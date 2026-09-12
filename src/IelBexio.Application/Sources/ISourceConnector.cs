using IelBexio.Domain.Common;
using IelBexio.Domain.Invoicing;

namespace IelBexio.Application.Sources;

/// <summary>
/// A canonical invoice as produced by a connector, together with the raw payload it was derived from
/// and the field-level provenance of the mapping. Connectors return this rather than touching the
/// database, which keeps them pure and trivially testable against fixtures.
/// </summary>
public sealed class SourceInvoiceDocument
{
    public required string SourceDocumentId { get; init; }
    public required string SourceDocumentVersion { get; init; }
    public required SourceSystem SourceSystem { get; init; }

    /// <summary>Verbatim source payload (JSON). Persisted for provenance and replay (§8).</summary>
    public required string RawPayload { get; init; }

    public string? ApiVersion { get; init; }
    public required Invoice Invoice { get; init; }
    public Customer? Customer { get; init; }
    public IReadOnlyList<InvoiceLine> Lines { get; init; } = [];
    public IReadOnlyList<Payment> Payments { get; init; } = [];

    /// <summary>Canonical field path → source field path, used to write provenance rows (§22).</summary>
    public IReadOnlyDictionary<string, string> FieldSourceMap { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Connector-level notes, e.g. "buyer PII unavailable for this marketplace".</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>A page of results plus the cursor needed to fetch the next one.</summary>
public sealed record SourcePage<T>(IReadOnlyList<T> Items, string? NextCursor, bool HasMore);

/// <summary>Window for an incremental fetch (§9: prefer incremental over repeated full downloads).</summary>
public sealed record SourceQuery
{
    /// <summary>Only return documents updated at or after this instant. Null means "from the beginning".</summary>
    public DateTimeOffset? UpdatedSince { get; init; }

    public string? Cursor { get; init; }
    public int PageSize { get; init; } = 50;

    /// <summary>Restrict to a single source document; used for replay and for targeted re-import.</summary>
    public string? SourceDocumentId { get; init; }
}

/// <summary>
/// What a connector can actually do. Declaring capabilities explicitly means the application can
/// degrade gracefully instead of catching NotSupportedException (§10: Amazon exposes less than Shopify).
/// </summary>
[Flags]
public enum SourceCapabilities
{
    None = 0,
    Invoices = 1 << 0,
    SalesOrders = 1 << 1,
    Customers = 1 << 2,
    Products = 1 << 3,
    Payments = 1 << 4,
    Documents = 1 << 5,
    Refunds = 1 << 6,
    IncrementalSync = 1 << 7,
    Webhooks = 1 << 8,
    BuyerPersonalData = 1 << 9,
    TaxDetail = 1 << 10,
}

/// <summary>Base capability every connector has: identity, capability declaration, health check.</summary>
public interface ISourceConnector
{
    SourceSystem SourceSystem { get; }

    /// <summary>"Fixture" or "Live" — surfaced in the UI so nobody mistakes fixture data for real data.</summary>
    string ModeName { get; }

    SourceCapabilities Capabilities { get; }

    /// <summary>Restrictions worth showing a human, e.g. missing Shopify scopes or Amazon PII limits.</summary>
    IReadOnlyList<string> DocumentedRestrictions { get; }

    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>Retrieves documents that normalise into canonical invoices.</summary>
public interface IInvoiceSource : ISourceConnector
{
    Task<SourcePage<SourceInvoiceDocument>> GetInvoicesAsync(SourceQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Retrieves sales orders where the source models orders rather than invoices.</summary>
public interface ISalesOrderSource : ISourceConnector
{
    Task<SourcePage<SourceInvoiceDocument>> GetSalesOrdersAsync(SourceQuery query, CancellationToken cancellationToken = default);
}

public interface ICustomerSource : ISourceConnector
{
    Task<SourcePage<Customer>> GetCustomersAsync(SourceQuery query, CancellationToken cancellationToken = default);
}

public sealed record SourceProduct(string SourceProductId, string? Sku, string? Name, decimal? UnitPrice, string? Currency);

public interface IProductSource : ISourceConnector
{
    Task<SourcePage<SourceProduct>> GetProductsAsync(SourceQuery query, CancellationToken cancellationToken = default);
}

public interface IPaymentSource : ISourceConnector
{
    Task<SourcePage<Payment>> GetPaymentsAsync(SourceQuery query, CancellationToken cancellationToken = default);
}

public sealed record SourceDocumentFile(string FileName, string ContentType, byte[] Content, string? SourceReference);

public interface IDocumentSource : ISourceConnector
{
    Task<SourcePage<SourceDocumentFile>> GetDocumentsAsync(SourceQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Resolves the connector configured for a source system. Registered per source in DI.</summary>
public interface ISourceConnectorRegistry
{
    ISourceConnector? Get(SourceSystem sourceSystem);
    IReadOnlyList<ISourceConnector> All();
}
