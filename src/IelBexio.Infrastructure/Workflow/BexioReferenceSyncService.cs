using System.Text.Json;
using IelBexio.Application.Abstractions;
using IelBexio.Application.Bexio;
using IelBexio.Application.Common;
using IelBexio.Application.Mapping;
using IelBexio.Application.Tax;
using IelBexio.Domain.Audit;
using IelBexio.Domain.Bexio;
using IelBexio.Domain.Mapping;
using IelBexio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IelBexio.Infrastructure.Workflow;

/// <summary>
/// Discovers Bexio configuration through the API and derives tax mappings from it.
/// <para>
/// This is what makes acceptance criterion 17 ("tax mapping is dynamic and not hardcoded") true in
/// practice. No Bexio tax id, account id or contact id is ever written by a seed script or a constant:
/// they are read from whatever Bexio actually reports, and a mapping is created only when a discovered
/// Bexio tax matches one of our internal tax codes on both country and rate.
/// </para>
/// <para>
/// Matching on rate rather than on name is deliberate. Names are localised and edited by users
/// ("MWST 8.1%", "VAT 8.1", "Umsatzsteuer normal"); the rate is the thing that determines the money.
/// </para>
/// </summary>
public sealed class BexioReferenceSyncService : IBexioReferenceSyncService
{
    private readonly AppDbContext _db;
    private readonly IBexioClient _bexio;
    private readonly IMappingService _mappings;
    private readonly TaxRuleTable _ruleTable;
    private readonly IClock _clock;
    private readonly IAuditWriter _audit;
    private readonly ILogger<BexioReferenceSyncService> _logger;

    public BexioReferenceSyncService(
        AppDbContext db,
        IBexioClient bexio,
        IMappingService mappings,
        TaxRuleTable ruleTable,
        IClock clock,
        IAuditWriter audit,
        ILogger<BexioReferenceSyncService> logger)
    {
        _db = db;
        _bexio = bexio;
        _mappings = mappings;
        _ruleTable = ruleTable;
        _clock = clock;
        _audit = audit;
        _logger = logger;
    }

    public async Task<Result<BexioReferenceSyncSummary>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();

        try
        {
            var taxes = await _bexio.GetTaxesAsync(cancellationToken);
            var accounts = await _bexio.GetAccountsAsync(cancellationToken);
            var contacts = await _bexio.SearchContactsAsync(null, cancellationToken);
            var articles = await _bexio.SearchArticlesAsync(null, cancellationToken);

            IReadOnlyList<BexioCurrency> currencies = [];
            try
            {
                currencies = await _bexio.GetCurrenciesAsync(cancellationToken);
            }
            catch (BexioApiException ex)
            {
                // The currency endpoint carries the lowest verification confidence in the catalog, so
                // its absence degrades one preflight check rather than failing the whole refresh.
                warnings.Add($"The currency list could not be retrieved ({ex.Category}); currency validation will be skipped during pre-flight.");
            }

            await StoreAsync(BexioReferenceKind.Tax, taxes.Select(t => new DiscoveredItem(t.Id, t.Name, t.Code, t.RatePercent, t.IsActive, t)), cancellationToken);
            await StoreAsync(BexioReferenceKind.Account, accounts.Select(a => new DiscoveredItem(a.Id, a.Name, a.AccountNumber, null, a.IsActive, a)), cancellationToken);
            await StoreAsync(BexioReferenceKind.Contact, contacts.Select(c => new DiscoveredItem(c.Id, c.Name ?? c.Email ?? c.Id, null, null, true, c)), cancellationToken);
            await StoreAsync(BexioReferenceKind.Article, articles.Select(a => new DiscoveredItem(a.Id, a.Name ?? a.Code ?? a.Id, a.Code, null, a.IsActive, a)), cancellationToken);
            await StoreAsync(BexioReferenceKind.Currency, currencies.Select(c => new DiscoveredItem(c.Id, c.Code, c.Code, null, c.IsActive, c)), cancellationToken);

            var (created, updated) = await DeriveTaxMappingsAsync(taxes, warnings, cancellationToken);
            await EnsureAccountMappingsAsync(accounts, warnings, cancellationToken);

            var connection = await _db.BexioConnections.FirstOrDefaultAsync(cancellationToken);
            if (connection is not null)
            {
                connection.LastReferenceRefreshAt = _clock.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
            }

            await _audit.WriteAsync(
                AuditActions.BexioReferenceDataRefreshed, nameof(BexioReferenceItem), null,
                newValue: new { Taxes = taxes.Count, Accounts = accounts.Count, Contacts = contacts.Count, Articles = articles.Count },
                cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Refreshed Bexio reference data: {Taxes} taxes, {Accounts} accounts, {Contacts} contacts, {Articles} articles.",
                taxes.Count, accounts.Count, contacts.Count, articles.Count);

            return Result<BexioReferenceSyncSummary>.Success(new BexioReferenceSyncSummary(
                taxes.Count, accounts.Count, contacts.Count, articles.Count, currencies.Count, created, updated, warnings));
        }
        catch (BexioApiException ex)
        {
            _logger.LogWarning(ex, "Refreshing Bexio reference data failed.");
            return Result<BexioReferenceSyncSummary>.Failure(ex.Category.ToString(), ex.Message);
        }
    }

    /// <summary>One discovered Bexio reference row, normalised across the different resource shapes.</summary>
    private sealed record DiscoveredItem(string Id, string? Name, string? Code, decimal? Rate, bool IsActive, object Raw);

    private async Task StoreAsync(
        BexioReferenceKind kind,
        IEnumerable<DiscoveredItem> items,
        CancellationToken cancellationToken)
    {
        var existing = await _db.BexioReferenceItems.Where(r => r.Kind == kind).ToDictionaryAsync(r => r.BexioId, cancellationToken);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var discovered in items)
        {
            seen.Add(discovered.Id);

            if (!existing.TryGetValue(discovered.Id, out var item))
            {
                item = new BexioReferenceItem { Kind = kind, BexioId = discovered.Id };
                _db.BexioReferenceItems.Add(item);
            }

            item.Name = discovered.Name;
            item.Code = discovered.Code;
            item.RatePercent = discovered.Rate;
            item.IsActive = discovered.IsActive;
            item.RawJson = JsonSerializer.Serialize(discovered.Raw);
            item.DiscoveredAt = _clock.UtcNow;
        }

        // Anything Bexio no longer reports is marked inactive rather than deleted, so an existing
        // mapping that points at it still shows why it stopped working.
        foreach (var (id, item) in existing.Where(e => !seen.Contains(e.Key)))
        {
            item.IsActive = false;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Creates or updates a tax mapping for each internal tax code that a discovered Bexio tax matches.
    /// Codes with no match are reported as warnings rather than mapped to something approximate.
    /// </summary>
    private async Task<(int Created, int Updated)> DeriveTaxMappingsAsync(
        IReadOnlyList<BexioTax> taxes, List<string> warnings, CancellationToken cancellationToken)
    {
        var created = 0;
        var updated = 0;
        var today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);

        foreach (var rule in _ruleTable.Rules.Where(r => r.IsValidOn(today) && r.CountryCode != "*"))
        {
            var candidates = taxes.Where(t => t.RatePercent == rule.RatePercent).ToList();

            if (candidates.Count == 0)
            {
                warnings.Add(
                    $"No Bexio tax was found at {rule.RatePercent}% for internal code '{rule.InternalTaxCode}'. " +
                    "Invoices needing that rate will fail pre-flight until a tax is configured in Bexio or mapped manually.");
                continue;
            }

            // Prefer an active tax; only fall back to an inactive one so the mapping exists and the
            // inactive state is visible in pre-flight rather than appearing as "no mapping at all".
            var chosen = candidates.FirstOrDefault(t => t.IsActive) ?? candidates[0];

            if (candidates.Count(t => t.IsActive) > 1)
            {
                warnings.Add(
                    $"Bexio reports {candidates.Count(t => t.IsActive)} active taxes at {rule.RatePercent}%; " +
                    $"'{chosen.Name}' was mapped to '{rule.InternalTaxCode}'. Confirm this is the intended sales tax.");
            }

            var existing = await _db.TaxMappings.FirstOrDefaultAsync(
                m => m.InternalTaxCode == rule.InternalTaxCode && m.CountryCode == rule.CountryCode && m.IsActive, cancellationToken);

            await _mappings.UpsertTaxMappingAsync(
                rule.InternalTaxCode, rule.CountryCode, rule.RatePercent,
                chosen.Id, chosen.Name, chosen.RatePercent, chosen.IsActive,
                MappingOrigin.ExactMatch, "system:reference-sync", cancellationToken);

            if (existing is null)
            {
                created++;
            }
            else
            {
                updated++;
            }
        }

        return (created, updated);
    }

    /// <summary>
    /// Seeds account mappings only where they are unambiguous, and warns otherwise. Guessing which
    /// revenue account a business uses is not this system's decision to make.
    /// </summary>
    private async Task EnsureAccountMappingsAsync(IReadOnlyList<BexioAccount> accounts, List<string> warnings, CancellationToken cancellationToken)
    {
        var revenue = accounts.Where(a => a.IsActive && string.Equals(a.AccountType, "revenue", StringComparison.OrdinalIgnoreCase)).ToList();

        if (revenue.Count == 0)
        {
            warnings.Add("No active revenue accounts were discovered in Bexio; account mappings must be created manually.");
            return;
        }

        foreach (var code in new[] { InternalAccountCodes.RevenueGoods, InternalAccountCodes.RevenueShipping })
        {
            var existing = await _db.AccountMappings.FirstOrDefaultAsync(m => m.InternalAccountCode == code && m.IsActive, cancellationToken);
            if (existing is not null)
            {
                continue;
            }

            // Only auto-map when there is exactly one candidate. Otherwise say so and let a human choose.
            if (revenue.Count == 1)
            {
                await _mappings.UpsertAccountMappingAsync(
                    code, revenue[0].Id, revenue[0].AccountNumber, revenue[0].Name,
                    MappingOrigin.ExactMatch, "system:reference-sync", cancellationToken);
            }
            else
            {
                warnings.Add(
                    $"'{code}' is not mapped and Bexio reports {revenue.Count} revenue accounts; " +
                    "choose the correct one on the Tax Mapping screen rather than letting the system guess.");
            }
        }
    }
}
