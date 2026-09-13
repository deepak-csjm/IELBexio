using IelBexio.Application.Abstractions;
using IelBexio.Application.Common;
using IelBexio.Application.Mapping;
using IelBexio.Domain.Audit;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Mapping;
using IelBexio.Domain.Tax;
using IelBexio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IelBexio.Infrastructure.Workflow;

/// <summary>
/// Resolves internal entities to Bexio identifiers from the mapping tables (§17).
/// <para>
/// Every failure here is an explicit, named failure rather than a fallback. There is no "use the first
/// tax that looks about right" path, because a plausible-but-wrong tax id produces a wrong ledger entry
/// that nobody notices until an audit.
/// </para>
/// </summary>
public sealed class MappingService : IMappingService
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;

    public MappingService(AppDbContext db, IClock clock, IAuditWriter audit, ICurrentUser user)
    {
        _db = db;
        _clock = clock;
        _audit = audit;
        _user = user;
    }

    public async Task<Result<CustomerMapping>> ResolveCustomerAsync(Customer customer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customer);

        var mapping = await _db.CustomerMappings
            .FirstOrDefaultAsync(m => m.CustomerId == customer.Id && m.IsActive, cancellationToken);

        return mapping is null
            ? Result<CustomerMapping>.Failure("NO_MAPPING", $"No active Bexio contact mapping exists for customer '{customer.DisplayName}'.")
            : Result<CustomerMapping>.Success(mapping);
    }

    public async Task<Result<TaxMapping>> ResolveTaxAsync(TaxAssessment assessment, DateOnly onDate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        if (string.IsNullOrWhiteSpace(assessment.InternalTaxCode))
        {
            return Result<TaxMapping>.Failure(
                "NO_TAX_CODE",
                "The tax assessment has no internal tax code, so there is nothing to map. The tax treatment must be determined first.");
        }

        var country = assessment.CountryCode ?? "CH";

        var candidates = await _db.TaxMappings
            .Where(m => m.IsActive && m.InternalTaxCode == assessment.InternalTaxCode && m.CountryCode == country)
            .ToListAsync(cancellationToken);

        // Validity windows are evaluated in memory: the comparison involves a nullable DateOnly range
        // that translates awkwardly, and the candidate set is tiny.
        var mapping = candidates
            .Where(m => m.IsValidOn(onDate))
            .OrderByDescending(m => m.ValidFrom)
            .FirstOrDefault();

        return mapping is null
            ? Result<TaxMapping>.Failure(
                "NO_MAPPING",
                $"No active Bexio tax is mapped for internal code '{assessment.InternalTaxCode}' in {country} on {onDate:yyyy-MM-dd}. " +
                "Refresh the Bexio reference data or create the mapping manually.")
            : Result<TaxMapping>.Success(mapping);
    }

    public async Task<Result<AccountMapping>> ResolveAccountAsync(string internalAccountCode, CancellationToken cancellationToken = default)
    {
        var mapping = await _db.AccountMappings
            .FirstOrDefaultAsync(m => m.InternalAccountCode == internalAccountCode && m.IsActive, cancellationToken);

        return mapping is null
            ? Result<AccountMapping>.Failure("NO_MAPPING", $"No active Bexio account is mapped for '{internalAccountCode}'.")
            : Result<AccountMapping>.Success(mapping);
    }

    public async Task<Result<ProductMapping>> ResolveProductAsync(string? sku, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            return Result<ProductMapping>.Failure("NO_SKU", "The line has no SKU to map.");
        }

        var mapping = await _db.ProductMappings.FirstOrDefaultAsync(m => m.Sku == sku && m.IsActive, cancellationToken);

        return mapping is null
            ? Result<ProductMapping>.Failure("NO_MAPPING", $"No active Bexio article is mapped for SKU '{sku}'.")
            : Result<ProductMapping>.Success(mapping);
    }

    public async Task<CustomerMapping> UpsertCustomerMappingAsync(
        Guid customerId, string bexioContactId, string? label, MappingOrigin origin, string? createdBy, CancellationToken cancellationToken = default)
    {
        var existing = await _db.CustomerMappings.FirstOrDefaultAsync(m => m.CustomerId == customerId && m.IsActive, cancellationToken);
        var before = existing is null ? null : new { existing.BexioContactId, existing.Origin };

        if (existing is null)
        {
            existing = new CustomerMapping { CustomerId = customerId };
            _db.CustomerMappings.Add(existing);
        }

        existing.BexioContactId = bexioContactId;
        existing.BexioContactName = label;
        existing.Origin = origin;
        existing.CreatedBy = createdBy ?? _user.UserId;
        existing.IsActive = true;
        existing.ConfidenceSignal = origin == MappingOrigin.AiSuggested ? 0.5m : 1m;

        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync(
            before is null ? AuditActions.MappingCreated : AuditActions.MappingChanged,
            nameof(CustomerMapping), existing.Id,
            oldValue: before,
            newValue: new { existing.BexioContactId, existing.Origin },
            aiInvolved: origin == MappingOrigin.AiSuggested,
            cancellationToken: cancellationToken);

        return existing;
    }

    public async Task<TaxMapping> UpsertTaxMappingAsync(
        string internalTaxCode, string countryCode, decimal ratePercent, string bexioTaxId, string? bexioTaxName,
        decimal? bexioRate, bool bexioActive, MappingOrigin origin, string? createdBy, CancellationToken cancellationToken = default)
    {
        var existing = await _db.TaxMappings.FirstOrDefaultAsync(
            m => m.InternalTaxCode == internalTaxCode && m.CountryCode == countryCode && m.IsActive, cancellationToken);

        var before = existing is null ? null : new { existing.BexioTaxId, existing.BexioTaxIsActive };

        if (existing is null)
        {
            existing = new TaxMapping
            {
                InternalTaxCode = internalTaxCode,
                CountryCode = countryCode,
                ValidFrom = new DateOnly(2000, 1, 1),
            };

            _db.TaxMappings.Add(existing);
        }

        existing.RatePercent = ratePercent;
        existing.BexioTaxId = bexioTaxId;
        existing.BexioTaxName = bexioTaxName;
        existing.BexioTaxRatePercent = bexioRate;
        existing.BexioTaxIsActive = bexioActive;
        existing.Origin = origin;
        existing.CreatedBy = createdBy ?? _user.UserId;
        existing.IsActive = true;

        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync(
            before is null ? AuditActions.MappingCreated : AuditActions.MappingChanged,
            nameof(TaxMapping), existing.Id,
            oldValue: before,
            newValue: new { existing.BexioTaxId, existing.BexioTaxIsActive, existing.BexioTaxRatePercent },
            cancellationToken: cancellationToken);

        return existing;
    }

    public async Task<AccountMapping> UpsertAccountMappingAsync(
        string internalAccountCode, string bexioAccountId, string? number, string? name, MappingOrigin origin, string? createdBy, CancellationToken cancellationToken = default)
    {
        var existing = await _db.AccountMappings.FirstOrDefaultAsync(m => m.InternalAccountCode == internalAccountCode && m.IsActive, cancellationToken);
        var before = existing is null ? null : new { existing.BexioAccountId };

        if (existing is null)
        {
            existing = new AccountMapping { InternalAccountCode = internalAccountCode };
            _db.AccountMappings.Add(existing);
        }

        existing.BexioAccountId = bexioAccountId;
        existing.BexioAccountNumber = number;
        existing.BexioAccountName = name;
        existing.Origin = origin;
        existing.CreatedBy = createdBy ?? _user.UserId;
        existing.IsActive = true;

        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync(
            before is null ? AuditActions.MappingCreated : AuditActions.MappingChanged,
            nameof(AccountMapping), existing.Id, oldValue: before, newValue: new { existing.BexioAccountId },
            cancellationToken: cancellationToken);

        return existing;
    }

    public async Task<ProductMapping> UpsertProductMappingAsync(
        string sku, string? bexioArticleId, string? bexioAccountId, string? productName, MappingOrigin origin, string? createdBy, CancellationToken cancellationToken = default)
    {
        var existing = await _db.ProductMappings.FirstOrDefaultAsync(m => m.Sku == sku && m.IsActive, cancellationToken);
        var before = existing is null ? null : new { existing.BexioArticleId, existing.BexioAccountId };

        if (existing is null)
        {
            existing = new ProductMapping { Sku = sku };
            _db.ProductMappings.Add(existing);
        }

        existing.BexioArticleId = bexioArticleId;
        existing.BexioAccountId = bexioAccountId;
        existing.ProductName = productName;
        existing.Origin = origin;
        existing.CreatedBy = createdBy ?? _user.UserId;
        existing.IsActive = true;
        existing.ConfidenceSignal = origin == MappingOrigin.AiSuggested ? 0.5m : 1m;

        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync(
            before is null ? AuditActions.MappingCreated : AuditActions.MappingChanged,
            nameof(ProductMapping), existing.Id, oldValue: before,
            newValue: new { existing.BexioArticleId, existing.BexioAccountId },
            aiInvolved: origin == MappingOrigin.AiSuggested,
            cancellationToken: cancellationToken);

        return existing;
    }
}
