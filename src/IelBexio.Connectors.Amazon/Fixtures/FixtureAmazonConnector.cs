using System.Text.Json;
using IelBexio.Application.Abstractions;
using IelBexio.Application.Sources;
using IelBexio.Connectors.Amazon.Normalization;
using IelBexio.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IelBexio.Connectors.Amazon.Fixtures;

/// <summary>
/// Fixture-backed Amazon connector. Shares <see cref="AmazonOrderNormalizer"/> with the live
/// connector, so the Amazon path is genuinely demonstrable without Amazon credentials (§10: missing
/// Amazon credentials must not block the POC).
/// </summary>
public sealed class FixtureAmazonConnector : IInvoiceSource, ISalesOrderSource
{
    private readonly AmazonOptions _options;
    private readonly AmazonOrderNormalizer _normalizer;
    private readonly ITenantContext _tenant;
    private readonly ILogger<FixtureAmazonConnector> _logger;

    public FixtureAmazonConnector(
        IOptions<AmazonOptions> options,
        AmazonOrderNormalizer normalizer,
        ITenantContext tenant,
        ILogger<FixtureAmazonConnector> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _normalizer = normalizer;
        _tenant = tenant;
        _logger = logger;
    }

    public SourceSystem SourceSystem => SourceSystem.Amazon;

    public string ModeName => "Fixture (local JSON, no Amazon SP-API contacted)";

    /// <summary>
    /// Note what is absent compared with Shopify: no <see cref="SourceCapabilities.Payments"/> and no
    /// <see cref="SourceCapabilities.BuyerPersonalData"/>. Declaring the gap is the point (§10).
    /// </summary>
    public SourceCapabilities Capabilities =>
        SourceCapabilities.Invoices | SourceCapabilities.SalesOrders | SourceCapabilities.Customers |
        SourceCapabilities.IncrementalSync;

    public IReadOnlyList<string> DocumentedRestrictions => AmazonRestrictions.All;

    public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Directory.Exists(ResolveDirectory()));

    public Task<SourcePage<SourceInvoiceDocument>> GetInvoicesAsync(SourceQuery query, CancellationToken cancellationToken = default) =>
        LoadAsync(query, cancellationToken);

    public Task<SourcePage<SourceInvoiceDocument>> GetSalesOrdersAsync(SourceQuery query, CancellationToken cancellationToken = default) =>
        LoadAsync(query, cancellationToken);

    private async Task<SourcePage<SourceInvoiceDocument>> LoadAsync(SourceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var directory = ResolveDirectory();
        if (!Directory.Exists(directory))
        {
            _logger.LogWarning("Amazon fixture directory '{Directory}' does not exist; returning no orders.", directory);
            return new SourcePage<SourceInvoiceDocument>([], null, false);
        }

        var documents = new List<SourceInvoiceDocument>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var stream = File.OpenRead(file);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var document = _normalizer.Normalize(json.RootElement, _tenant.TenantId, null, "orders/v0");

            if (query.SourceDocumentId is { } wanted &&
                !string.Equals(document.SourceDocumentId, wanted, StringComparison.Ordinal))
            {
                continue;
            }

            documents.Add(document);
        }

        _logger.LogInformation("Loaded {Count} Amazon fixture order(s) from {Directory}.", documents.Count, directory);
        return new SourcePage<SourceInvoiceDocument>(documents, null, false);
    }

    private string ResolveDirectory() =>
        Path.IsPathRooted(_options.FixtureDirectory)
            ? _options.FixtureDirectory
            : Path.Combine(AppContext.BaseDirectory, _options.FixtureDirectory) is var probe && Directory.Exists(probe)
                ? probe
                : Path.GetFullPath(_options.FixtureDirectory);
}

/// <summary>
/// The Amazon restrictions the specification requires to be documented (§10). Kept as data so the UI
/// and the API can show them to an operator rather than burying them in a markdown file nobody reads
/// at the moment they matter.
/// </summary>
public static class AmazonRestrictions
{
    public static readonly IReadOnlyList<string> All =
    [
        "Buyer PII (name, email, full address) is a restricted data element. It requires an approved " +
        "PII data-access role for the application plus a Restricted Data Token from the Tokens API; " +
        "without both, those fields are absent or masked and the customer is created as anonymised.",
        "The Orders API reports tax amounts but not tax rates. Per-line rates shown by this system are " +
        "back-computed from amounts and must be verified by a human before booking.",
        "ItemPrice is tax-inclusive, unlike Shopify's net line totals. The connector derives net as " +
        "ItemPrice minus ItemTax.",
        "Amazon issues no invoice number; the AmazonOrderId is used as the document reference.",
        "Settlement-level financial detail (fees, refunds, reserves) is not in the Orders API. It " +
        "requires the Finances API or settlement reports via the Reports API, which are out of scope " +
        "for this POC.",
        "The Reports API is asynchronous: a report is requested, polled for completion, then downloaded. " +
        "It is not a synchronous query and would need its own worker.",
        "SP-API applies per-operation rate limits with a token-bucket burst allowance; the live " +
        "connector honours Retry-After and backs off.",
        "Endpoint and field shapes are unverified: developer-docs.amazon.com is unreachable from this " +
        "build environment.",
    ];
}
