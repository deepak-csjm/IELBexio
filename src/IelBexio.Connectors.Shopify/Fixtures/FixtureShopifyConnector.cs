using System.Text.Json;
using IelBexio.Application.Abstractions;
using IelBexio.Application.Sources;
using IelBexio.Connectors.Shopify.Normalization;
using IelBexio.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IelBexio.Connectors.Shopify.Fixtures;

/// <summary>
/// Reads Shopify orders from JSON files on disk and normalises them with exactly the same code path
/// the live connector uses.
/// <para>
/// This matters more than it sounds: the fixture connector shares <see cref="ShopifyOrderNormalizer"/>
/// with the live connector, so the demo and the tests exercise the real normalisation logic. The only
/// thing swapped out is the transport. A fixture connector that had its own mapping would prove
/// nothing (§9, §29).
/// </para>
/// </summary>
public sealed class FixtureShopifyConnector : IInvoiceSource, ISalesOrderSource
{
    private readonly ShopifyOptions _options;
    private readonly ShopifyOrderNormalizer _normalizer;
    private readonly ITenantContext _tenant;
    private readonly ILogger<FixtureShopifyConnector> _logger;

    public FixtureShopifyConnector(
        IOptions<ShopifyOptions> options,
        ShopifyOrderNormalizer normalizer,
        ITenantContext tenant,
        ILogger<FixtureShopifyConnector> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _normalizer = normalizer;
        _tenant = tenant;
        _logger = logger;
    }

    public SourceSystem SourceSystem => SourceSystem.Shopify;

    /// <summary>Named so nobody can mistake fixture output for live store data in the UI.</summary>
    public string ModeName => "Fixture (local JSON, no Shopify store contacted)";

    public SourceCapabilities Capabilities =>
        SourceCapabilities.Invoices | SourceCapabilities.SalesOrders | SourceCapabilities.Customers |
        SourceCapabilities.Payments | SourceCapabilities.TaxDetail | SourceCapabilities.IncrementalSync |
        SourceCapabilities.Refunds;

    public IReadOnlyList<string> DocumentedRestrictions =>
    [
        "Fixture mode: orders are read from local JSON files, not from a Shopify store.",
        "The fixture shapes follow the Shopify Admin GraphQL Order object but could not be verified " +
        "against shopify.dev, which is unreachable from this build environment.",
    ];

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
            _logger.LogWarning("Shopify fixture directory '{Directory}' does not exist; returning no orders.", directory);
            return new SourcePage<SourceInvoiceDocument>([], null, false);
        }

        var documents = new List<SourceInvoiceDocument>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var stream = File.OpenRead(file);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var document = _normalizer.Normalize(json.RootElement, _tenant.TenantId, null, _options.ApiVersion);

            // Fixture files are filtered with the same semantics the live connector applies, so an
            // incremental import behaves identically in both modes.
            if (query.SourceDocumentId is { } wanted &&
                !string.Equals(document.SourceDocumentId, wanted, StringComparison.Ordinal))
            {
                continue;
            }

            if (query.UpdatedSince is { } since && document.Invoice.InvoiceDate is { } date &&
                date < DateOnly.FromDateTime(since.UtcDateTime))
            {
                continue;
            }

            documents.Add(document);
        }

        _logger.LogInformation("Loaded {Count} Shopify fixture order(s) from {Directory}.", documents.Count, directory);
        return new SourcePage<SourceInvoiceDocument>(documents, null, false);
    }

    private string ResolveDirectory() =>
        Path.IsPathRooted(_options.FixtureDirectory)
            ? _options.FixtureDirectory
            : Path.Combine(AppContext.BaseDirectory, _options.FixtureDirectory) is var probe && Directory.Exists(probe)
                ? probe
                : Path.GetFullPath(_options.FixtureDirectory);
}
