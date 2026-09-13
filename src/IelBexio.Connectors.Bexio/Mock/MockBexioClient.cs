using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using IelBexio.Application.Bexio;
using IelBexio.Connectors.Bexio.Configuration;
using IelBexio.Domain.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IelBexio.Connectors.Bexio.Mock;

/// <summary>
/// In-memory Bexio. This is the default implementation so the POC runs, demonstrates and tests end to
/// end with no Bexio account (§30: most automated tests must not require a live Bexio account).
/// <para>
/// It is a genuine test double, not a stub: it holds reference data, assigns ids, enforces referential
/// integrity on invoice creation (unknown contact/tax/account are rejected the way a real API would),
/// honours the idempotency key, and can be driven into each failure mode of §30 and §32.
/// </para>
/// <para>
/// The reference data below uses obviously-synthetic ids (<c>mock-tax-1</c>, not a plausible Bexio
/// integer) precisely so that a value leaking into a mapping table is unmistakable, and so nobody can
/// confuse mock output for real Bexio configuration.
/// </para>
/// </summary>
public sealed class MockBexioClient : IBexioClient
{
    private readonly MockBexioOptions _options;
    private readonly ILogger<MockBexioClient> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<string, BexioContact> _contacts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BexioArticle> _articles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BexioInvoiceResult> _invoices = new(StringComparer.Ordinal);

    /// <summary>idempotency key → invoice id, so a repeated create returns the original (§19).</summary>
    private readonly ConcurrentDictionary<string, string> _idempotency = new(StringComparer.Ordinal);

    private readonly List<BexioTax> _taxes = [];
    private readonly List<BexioAccount> _accounts = [];
    private readonly List<BexioCurrency> _currencies = [];

    private int _callCount;
    private int _invoiceSequence;
    private int _contactSequence;

    public MockBexioClient(IOptions<MockBexioOptions> options, ILogger<MockBexioClient> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (_options.SeedSwissReferenceData)
        {
            SeedReferenceData();
        }
    }

    public string ModeName => "Mock (no Bexio account contacted)";

    /// <summary>Number of invoices the mock has actually created. Tests assert on this for duplicate prevention.</summary>
    public int CreatedInvoiceCount => _invoices.Count;

    public IReadOnlyCollection<BexioInvoiceResult> CreatedInvoices => _invoices.Values.ToList();

    private void SeedReferenceData()
    {
        // Synthetic Swiss-shaped configuration. Rates mirror published Swiss VAT rates so demo data is
        // coherent; ids are deliberately non-numeric so they cannot be mistaken for real Bexio ids.
        _taxes.AddRange(
        [
            new BexioTax("mock-tax-std-81", "MWST 8.1% (Umsatz)", 8.1m, true, "UN81", "sales_tax"),
            new BexioTax("mock-tax-red-26", "MWST 2.6% (Umsatz)", 2.6m, true, "UN26", "sales_tax"),
            new BexioTax("mock-tax-acc-38", "MWST 3.8% (Beherbergung)", 3.8m, true, "UN38", "sales_tax"),
            new BexioTax("mock-tax-zero", "Steuerfrei / 0%", 0m, true, "UN00", "sales_tax"),
            // An intentionally inactive tax, so the "inactive Bexio tax" negative test (§32) is real.
            new BexioTax("mock-tax-std-77", "MWST 7.7% (alt)", 7.7m, false, "UN77", "sales_tax"),
        ]);

        _accounts.AddRange(
        [
            new BexioAccount("mock-acct-3200", "3200", "Warenertrag", true, "revenue"),
            new BexioAccount("mock-acct-3400", "3400", "Dienstleistungsertrag", true, "revenue"),
            new BexioAccount("mock-acct-3700", "3700", "Versandertrag", true, "revenue"),
            new BexioAccount("mock-acct-1100", "1100", "Forderungen aus Lieferungen", true, "asset"),
        ]);

        _currencies.AddRange(
        [
            new BexioCurrency("mock-cur-chf", "CHF", true),
            new BexioCurrency("mock-cur-eur", "EUR", true),
            new BexioCurrency("mock-cur-usd", "USD", true),
        ]);

        AddContact(new BexioContact("mock-contact-1", "ABC Swiss GmbH", null, "buchhaltung@abc-swiss.example", "CH", "CHE-116.281.710", "Bahnhofstrasse 1", "8001", "Zürich"));
        AddContact(new BexioContact("mock-contact-2", "Helvetia Retail AG", null, "invoices@helvetia-retail.example", "CH", "CHE-105.980.910", "Seestrasse 40", "6300", "Zug"));
        AddContact(new BexioContact("mock-contact-3", "Alpine Traders SARL", null, "compta@alpine-traders.example", "CH", null, "Rue du Rhône 12", "1204", "Genève"));

        _articles["mock-article-1"] = new BexioArticle("mock-article-1", "SKU-ALPHA", "Alpha Widget", 250m, "mock-acct-3200", true);
        _articles["mock-article-2"] = new BexioArticle("mock-article-2", "SKU-BETA", "Beta Service", 500m, "mock-acct-3400", true);
        _articles["mock-article-3"] = new BexioArticle("mock-article-3", "SKU-BOOK", "Printed Manual", 40m, "mock-acct-3200", true);
    }

    private void AddContact(BexioContact contact) => _contacts[contact.Id] = contact;

    // ---- Failure simulation -------------------------------------------------------------------

    private async Task GateAsync(CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _callCount);

        if (_options.SimulatedLatency > TimeSpan.Zero)
        {
            await Task.Delay(_options.SimulatedLatency, _timeProvider, cancellationToken);
        }

        if (_options.FailureScenario == MockFailureScenario.None)
        {
            return;
        }

        if (_options.FailOnCallNumber > 0 && call != _options.FailOnCallNumber)
        {
            return;
        }

        _logger.LogWarning("MockBexioClient injecting {Scenario} on call {Call}", _options.FailureScenario, call);

        throw _options.FailureScenario switch
        {
            MockFailureScenario.AuthenticationFailure =>
                new BexioApiException(SyncErrorCategory.Authentication, "401", "Simulated: access token is invalid or expired.", 401),
            MockFailureScenario.AuthorizationFailure =>
                new BexioApiException(SyncErrorCategory.Authorization, "403", "Simulated: the granted scopes do not permit this operation.", 403),
            MockFailureScenario.ValidationFailure =>
                new BexioApiException(SyncErrorCategory.Validation, "422", "Simulated: the request payload was rejected as invalid.", 422),
            MockFailureScenario.RateLimited =>
                new BexioApiException(SyncErrorCategory.RateLimit, "429", "Simulated: rate limit exceeded.", 429, TimeSpan.FromSeconds(2)),
            MockFailureScenario.Timeout =>
                new BexioApiException(SyncErrorCategory.Network, "TIMEOUT", "Simulated: the request timed out.", null),
            MockFailureScenario.Conflict =>
                new BexioApiException(SyncErrorCategory.Conflict, "409", "Simulated: a conflicting resource already exists.", 409),
            MockFailureScenario.ServerError =>
                new BexioApiException(SyncErrorCategory.Transient, "500", "Simulated: upstream server error.", 500),
            MockFailureScenario.NetworkFailure =>
                new BexioApiException(SyncErrorCategory.Network, "NETWORK", "Simulated: the connection was reset.", null),
            MockFailureScenario.NotFound =>
                new BexioApiException(SyncErrorCategory.NotFound, "404", "Simulated: the resource does not exist.", 404),
            _ => new BexioApiException(SyncErrorCategory.Unknown, "UNKNOWN", "Simulated: unclassified failure."),
        };
    }

    // ---- Reads ---------------------------------------------------------------------------------

    public async Task<BexioCompanyInfo> GetCompanyInfoAsync(CancellationToken cancellationToken = default)
    {
        await GateAsync(cancellationToken);
        return new BexioCompanyInfo("mock-company-1", "Mock Bexio Company AG", "CH", "CHF");
    }

    public async Task<IReadOnlyList<BexioTax>> GetTaxesAsync(CancellationToken cancellationToken = default)
    {
        await GateAsync(cancellationToken);
        return _taxes.ToList();
    }

    public async Task<IReadOnlyList<BexioAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        await GateAsync(cancellationToken);
        return _accounts.ToList();
    }

    public async Task<IReadOnlyList<BexioCurrency>> GetCurrenciesAsync(CancellationToken cancellationToken = default)
    {
        await GateAsync(cancellationToken);
        return _currencies.ToList();
    }

    public async Task<IReadOnlyList<BexioContact>> SearchContactsAsync(string? nameFragment, CancellationToken cancellationToken = default)
    {
        await GateAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(nameFragment))
        {
            return _contacts.Values.ToList();
        }

        return _contacts.Values
            .Where(c => (c.Name ?? string.Empty).Contains(nameFragment, StringComparison.OrdinalIgnoreCase)
                        || (c.Email ?? string.Empty).Contains(nameFragment, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task<BexioContact?> GetContactAsync(string contactId, CancellationToken cancellationToken = default)
    {
        await GateAsync(cancellationToken);
        return _contacts.GetValueOrDefault(contactId);
    }

    public async Task<BexioContact> CreateContactAsync(BexioContactRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await GateAsync(cancellationToken);

        var id = $"mock-contact-{Interlocked.Increment(ref _contactSequence) + 100}";
        var contact = new BexioContact(id, request.Name, request.FirstName, request.Email, request.CountryCode, request.VatNumber, request.Address, request.PostalCode, request.City);
        _contacts[id] = contact;
        return contact;
    }

    public async Task<IReadOnlyList<BexioArticle>> SearchArticlesAsync(string? codeOrName, CancellationToken cancellationToken = default)
    {
        await GateAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(codeOrName))
        {
            return _articles.Values.ToList();
        }

        return _articles.Values
            .Where(a => (a.Code ?? string.Empty).Contains(codeOrName, StringComparison.OrdinalIgnoreCase)
                        || (a.Name ?? string.Empty).Contains(codeOrName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // ---- The write ------------------------------------------------------------------------------

    public async Task<BexioInvoiceResult> CreateInvoiceAsync(BexioInvoiceRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        // Idempotency is checked BEFORE the failure gate, so a replay of an already-created invoice is
        // returned even under an injected failure — which is exactly what a correct idempotent API does.
        if (_idempotency.TryGetValue(idempotencyKey, out var existingId) && _invoices.TryGetValue(existingId, out var existing))
        {
            _logger.LogInformation("Mock: idempotency key {Key} already created invoice {Id}; returning the original.", idempotencyKey, existingId);
            return existing;
        }

        await GateAsync(cancellationToken);

        // Referential checks a real API would perform. Silently accepting an unknown tax id would make
        // the mock useless as a safety net.
        var contact = _contacts.GetValueOrDefault(request.ContactId)
            ?? throw new BexioApiException(SyncErrorCategory.Validation, "422", $"Unknown contact id '{request.ContactId}'.", 422);

        if (request.Positions.Count == 0)
        {
            throw new BexioApiException(SyncErrorCategory.Validation, "422", "An invoice must have at least one position.", 422);
        }

        foreach (var position in request.Positions)
        {
            if (position.TaxId is not null)
            {
                var tax = _taxes.FirstOrDefault(t => string.Equals(t.Id, position.TaxId, StringComparison.Ordinal))
                    ?? throw new BexioApiException(SyncErrorCategory.Validation, "422", $"Unknown tax id '{position.TaxId}'.", 422);

                if (!tax.IsActive)
                {
                    throw new BexioApiException(SyncErrorCategory.Validation, "422", $"Tax '{tax.Name}' is not active and cannot be used.", 422);
                }
            }

            if (position.AccountId is not null && !_accounts.Any(a => string.Equals(a.Id, position.AccountId, StringComparison.Ordinal)))
            {
                throw new BexioApiException(SyncErrorCategory.Validation, "422", $"Unknown account id '{position.AccountId}'.", 422);
            }
        }

        if (!_currencies.Any(c => string.Equals(c.Code, request.CurrencyCode, StringComparison.OrdinalIgnoreCase)))
        {
            throw new BexioApiException(SyncErrorCategory.Validation, "422", $"Currency '{request.CurrencyCode}' is not configured.", 422);
        }

        var net = request.Positions.Sum(p => decimal.Round(p.Amount * p.UnitPrice * (1 - (p.DiscountInPercent / 100m)), 4, MidpointRounding.ToEven));
        var taxTotal = request.Positions.Sum(p =>
        {
            var rate = _taxes.FirstOrDefault(t => string.Equals(t.Id, p.TaxId, StringComparison.Ordinal))?.RatePercent ?? 0m;
            var lineNet = decimal.Round(p.Amount * p.UnitPrice * (1 - (p.DiscountInPercent / 100m)), 4, MidpointRounding.ToEven);
            return decimal.Round(lineNet * rate / 100m, 4, MidpointRounding.ToEven);
        });

        var sequence = Interlocked.Increment(ref _invoiceSequence);
        var id = $"mock-invoice-{sequence}";
        var result = new BexioInvoiceResult(
            id,
            $"MOCK-{sequence.ToString("D5", CultureInfo.InvariantCulture)}",
            decimal.Round(net + taxTotal, 2, MidpointRounding.ToEven),
            decimal.Round(net, 2, MidpointRounding.ToEven),
            decimal.Round(taxTotal, 2, MidpointRounding.ToEven),
            request.CurrencyCode,
            JsonSerializer.Serialize(new { id, contact = contact.Id, positions = request.Positions.Count }));

        _invoices[id] = result;
        _idempotency[idempotencyKey] = id;

        _logger.LogInformation("Mock: created invoice {Id} for contact {Contact} with {Positions} positions.", id, contact.Id, request.Positions.Count);
        return result;
    }

    public async Task<BexioInvoiceResult?> GetInvoiceAsync(string invoiceId, CancellationToken cancellationToken = default)
    {
        await GateAsync(cancellationToken);
        return _invoices.GetValueOrDefault(invoiceId);
    }
}
