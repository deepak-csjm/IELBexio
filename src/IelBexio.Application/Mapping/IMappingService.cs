using IelBexio.Application.Common;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Mapping;
using IelBexio.Domain.Tax;

namespace IelBexio.Application.Mapping;

/// <summary>
/// Resolves internal entities to Bexio identifiers (§17). Mappings live in dedicated tables, never in
/// the UI and never hardcoded, so they can be inspected, corrected and audited.
/// </summary>
public interface IMappingService
{
    Task<Result<CustomerMapping>> ResolveCustomerAsync(Customer customer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the Bexio tax id for an assessment. Fails when no mapping exists or the mapped Bexio
    /// tax is inactive — both are preflight blockers rather than silent fallbacks (§18).
    /// </summary>
    Task<Result<TaxMapping>> ResolveTaxAsync(TaxAssessment assessment, DateOnly onDate, CancellationToken cancellationToken = default);

    Task<Result<AccountMapping>> ResolveAccountAsync(string internalAccountCode, CancellationToken cancellationToken = default);

    Task<Result<ProductMapping>> ResolveProductAsync(string? sku, CancellationToken cancellationToken = default);

    Task<CustomerMapping> UpsertCustomerMappingAsync(Guid customerId, string bexioContactId, string? label, MappingOrigin origin, string? createdBy, CancellationToken cancellationToken = default);

    Task<TaxMapping> UpsertTaxMappingAsync(string internalTaxCode, string countryCode, decimal ratePercent, string bexioTaxId, string? bexioTaxName, decimal? bexioRate, bool bexioActive, MappingOrigin origin, string? createdBy, CancellationToken cancellationToken = default);

    Task<AccountMapping> UpsertAccountMappingAsync(string internalAccountCode, string bexioAccountId, string? number, string? name, MappingOrigin origin, string? createdBy, CancellationToken cancellationToken = default);

    Task<ProductMapping> UpsertProductMappingAsync(string sku, string? bexioArticleId, string? bexioAccountId, string? productName, MappingOrigin origin, string? createdBy, CancellationToken cancellationToken = default);
}

/// <summary>
/// Refreshes locally-cached Bexio reference data and derives tax mappings from what Bexio actually
/// reports. This is what makes tax mapping dynamic rather than hardcoded (acceptance criterion 17).
/// </summary>
public interface IBexioReferenceSyncService
{
    Task<Result<BexioReferenceSyncSummary>> RefreshAsync(CancellationToken cancellationToken = default);
}

public sealed record BexioReferenceSyncSummary(
    int Taxes,
    int Accounts,
    int Contacts,
    int Articles,
    int Currencies,
    int TaxMappingsCreated,
    int TaxMappingsUpdated,
    IReadOnlyList<string> Warnings);
