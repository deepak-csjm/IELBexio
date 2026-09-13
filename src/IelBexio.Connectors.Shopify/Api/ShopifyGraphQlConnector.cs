using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IelBexio.Application.Abstractions;
using IelBexio.Application.Sources;
using IelBexio.Connectors.Shopify.Normalization;
using IelBexio.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IelBexio.Connectors.Shopify.Api;

/// <summary>
/// Live Shopify Admin GraphQL connector.
/// <para>
/// <b>Verification status.</b> shopify.dev is unreachable from this build environment, so the query
/// below could not be validated against the current schema and no live call was made. It is written
/// against the documented Admin GraphQL <c>Order</c> object as described by secondary sources. The API
/// version is never hardcoded at a call site — it comes from <see cref="ShopifyOptions.ApiVersion"/>
/// and is interpolated in exactly one place (§4).
/// </para>
/// <para>
/// <b>Why GraphQL and not REST.</b> The specification forbids building a new integration on the
/// deprecated REST Admin API (§4). GraphQL also lets one round trip fetch the order, its line items,
/// tax lines, addresses and transactions together, which matters because Shopify bills by query cost.
/// </para>
/// </summary>
public sealed class ShopifyGraphQlConnector : IInvoiceSource, ISalesOrderSource
{
    private readonly HttpClient _http;
    private readonly ShopifyOptions _options;
    private readonly ShopifyOrderNormalizer _normalizer;
    private readonly ITenantContext _tenant;
    private readonly ILogger<ShopifyGraphQlConnector> _logger;

    public ShopifyGraphQlConnector(
        HttpClient http,
        IOptions<ShopifyOptions> options,
        ShopifyOrderNormalizer normalizer,
        ITenantContext tenant,
        ILogger<ShopifyGraphQlConnector> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options.Value;
        _normalizer = normalizer;
        _tenant = tenant;
        _logger = logger;
    }

    public SourceSystem SourceSystem => SourceSystem.Shopify;

    public string ModeName => $"Live Shopify Admin GraphQL ({_options.ShopDomain}, {_options.ApiVersion})";

    public SourceCapabilities Capabilities =>
        SourceCapabilities.Invoices | SourceCapabilities.SalesOrders | SourceCapabilities.Customers |
        SourceCapabilities.Products | SourceCapabilities.Payments | SourceCapabilities.Refunds |
        SourceCapabilities.TaxDetail | SourceCapabilities.IncrementalSync | SourceCapabilities.Webhooks;

    public IReadOnlyList<string> DocumentedRestrictions =>
    [
        "read_orders is required. Shopify restricts apps to the last 60 days of orders unless the " +
        "read_all_orders scope has been granted by Shopify for the app.",
        "read_customers is required for buyer name and email; without it the customer object is null.",
        "Buyer tax credentials (VAT/UID) are exposed through order localization extensions, not on the " +
        "customer, and are only populated for markets where Shopify collects them.",
        "The GraphQL Admin API is governed by a calculated query cost budget rather than a request " +
        "count; this connector reads the returned cost extensions and waits when the budget is low.",
        "Endpoint shapes are unverified: shopify.dev is unreachable from this build environment.",
    ];

    /// <summary>
    /// The single GraphQL document this connector sends. Keeping it as one constant makes it reviewable
    /// and makes a schema change a one-place edit.
    /// </summary>
    private const string OrdersQuery = """
        query ImportOrders($first: Int!, $after: String, $query: String) {
          orders(first: $first, after: $after, query: $query, sortKey: UPDATED_AT) {
            pageInfo { hasNextPage endCursor }
            edges {
              node {
                id
                name
                createdAt
                updatedAt
                processedAt
                currencyCode
                displayFinancialStatus
                test
                customer { id firstName lastName email phone }
                billingAddress { company address1 address2 zip city province countryCodeV2 }
                shippingAddress { company address1 address2 zip city province countryCodeV2 }
                localizationExtensions(first: 5) { edges { node { key value } } }
                subtotalPriceSet { shopMoney { amount currencyCode } }
                totalDiscountsSet { shopMoney { amount currencyCode } }
                totalShippingPriceSet { shopMoney { amount currencyCode } }
                totalTaxSet { shopMoney { amount currencyCode } }
                currentTotalPriceSet { shopMoney { amount currencyCode } }
                shippingLine {
                  title
                  originalPriceSet { shopMoney { amount currencyCode } }
                  taxLines { title rate ratePercentage priceSet { shopMoney { amount currencyCode } } }
                }
                lineItems(first: 100) {
                  edges {
                    node {
                      id
                      name
                      quantity
                      sku
                      variant { id sku }
                      originalUnitPriceSet { shopMoney { amount currencyCode } }
                      discountedTotalSet { shopMoney { amount currencyCode } }
                      totalDiscountSet { shopMoney { amount currencyCode } }
                      taxLines { title rate ratePercentage priceSet { shopMoney { amount currencyCode } } }
                    }
                  }
                }
                transactions { id kind status gateway processedAt amountSet { shopMoney { amount currencyCode } } }
              }
            }
          }
        }
        """;

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfiguredForLive)
        {
            return false;
        }

        try
        {
            var response = await ExecuteAsync("{ shop { name } }", new Dictionary<string, object?>(StringComparer.Ordinal), cancellationToken);
            return response.RootElement.TryGetProperty("data", out _);
        }
        catch (ShopifyApiException ex)
        {
            _logger.LogWarning(ex, "Shopify connection test failed.");
            return false;
        }
    }

    public Task<SourcePage<SourceInvoiceDocument>> GetInvoicesAsync(SourceQuery query, CancellationToken cancellationToken = default) =>
        FetchOrdersAsync(query, cancellationToken);

    public Task<SourcePage<SourceInvoiceDocument>> GetSalesOrdersAsync(SourceQuery query, CancellationToken cancellationToken = default) =>
        FetchOrdersAsync(query, cancellationToken);

    private async Task<SourcePage<SourceInvoiceDocument>> FetchOrdersAsync(SourceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!_options.IsConfiguredForLive)
        {
            throw new ShopifyApiException("Shopify live mode is selected but ShopDomain/AccessToken are not configured.");
        }

        var variables = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["first"] = Math.Clamp(query.PageSize, 1, 250),
            ["after"] = query.Cursor,
            ["query"] = BuildSearchQuery(query),
        };

        using var document = await ExecuteAsync(OrdersQuery, variables, cancellationToken);
        var root = document.RootElement;

        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("orders", out var orders))
        {
            throw new ShopifyApiException("The Shopify response contained no 'data.orders' node.");
        }

        var documents = new List<SourceInvoiceDocument>();
        if (orders.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array)
        {
            foreach (var edge in edges.EnumerateArray())
            {
                if (edge.TryGetProperty("node", out var node))
                {
                    documents.Add(_normalizer.Normalize(node, _tenant.TenantId, null, _options.ApiVersion));
                }
            }
        }

        var pageInfo = orders.TryGetProperty("pageInfo", out var info) ? info : default;
        var hasNext = pageInfo.ValueKind == JsonValueKind.Object
                      && pageInfo.TryGetProperty("hasNextPage", out var hasNextProperty)
                      && hasNextProperty.ValueKind == JsonValueKind.True;
        var endCursor = pageInfo.ValueKind == JsonValueKind.Object && pageInfo.TryGetProperty("endCursor", out var cursor)
            ? cursor.GetString()
            : null;

        return new SourcePage<SourceInvoiceDocument>(documents, endCursor, hasNext);
    }

    /// <summary>
    /// Builds Shopify's search-syntax filter. Incremental sync is expressed as <c>updated_at:&gt;=…</c>
    /// so a re-import fetches only what changed rather than the whole store (§9).
    /// </summary>
    private static string? BuildSearchQuery(SourceQuery query)
    {
        var clauses = new List<string>();

        if (query.UpdatedSince is { } since)
        {
            clauses.Add($"updated_at:>='{since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}'");
        }

        if (!string.IsNullOrWhiteSpace(query.SourceDocumentId))
        {
            var numericId = query.SourceDocumentId.Split('/').LastOrDefault();
            if (!string.IsNullOrWhiteSpace(numericId))
            {
                clauses.Add($"id:{numericId}");
            }
        }

        return clauses.Count == 0 ? null : string.Join(" AND ", clauses);
    }

    private async Task<JsonDocument> ExecuteAsync(string query, Dictionary<string, object?> variables, CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (true)
        {
            attempt++;
            cancellationToken.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.BuildGraphQlEndpoint())
            {
                Content = JsonContent.Create(new { query, variables }),
            };

            // Shopify authenticates the Admin API with this header, not with an OAuth bearer token.
            request.Headers.TryAddWithoutValidation("X-Shopify-Access-Token", _options.AccessToken);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < _options.MaxRetryAttempts)
            {
                await DelayForAttemptAsync(attempt, null, cancellationToken);
                _logger.LogWarning(ex, "Shopify request failed on attempt {Attempt}; retrying.", attempt);
                continue;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < _options.MaxRetryAttempts)
                {
                    await DelayForAttemptAsync(attempt, response.Headers.RetryAfter?.Delta, cancellationToken);
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new ShopifyApiException(
                        $"Shopify rejected the request with {(int)response.StatusCode}. The access token is missing a required scope " +
                        "(read_orders, and read_customers for buyer details) or has been revoked.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (attempt < _options.MaxRetryAttempts && (int)response.StatusCode >= 500)
                    {
                        await DelayForAttemptAsync(attempt, null, cancellationToken);
                        continue;
                    }

                    throw new ShopifyApiException($"Shopify returned {(int)response.StatusCode}.");
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var document = JsonDocument.Parse(body);

                // A GraphQL endpoint returns HTTP 200 with an "errors" array for query-level failures,
                // so a status check alone is not enough to know the call succeeded.
                if (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                {
                    var throttled = errors.EnumerateArray().Any(e =>
                        e.TryGetProperty("extensions", out var ext) &&
                        ext.TryGetProperty("code", out var code) &&
                        string.Equals(code.GetString(), "THROTTLED", StringComparison.OrdinalIgnoreCase));

                    if (throttled && attempt < _options.MaxRetryAttempts)
                    {
                        document.Dispose();
                        await DelayForAttemptAsync(attempt, null, cancellationToken);
                        continue;
                    }

                    var message = string.Join("; ", errors.EnumerateArray()
                        .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : null)
                        .Where(m => m is not null));

                    document.Dispose();
                    throw new ShopifyApiException($"Shopify GraphQL returned errors: {message}");
                }

                await HonourCostBudgetAsync(document, cancellationToken);
                return document;
            }
        }
    }

    /// <summary>
    /// Reads the query-cost extensions Shopify returns and pauses when the remaining budget is low.
    /// Backing off before being throttled is cheaper than being throttled and retrying.
    /// </summary>
    private async Task HonourCostBudgetAsync(JsonDocument document, CancellationToken cancellationToken)
    {
        if (!document.RootElement.TryGetProperty("extensions", out var extensions) ||
            !extensions.TryGetProperty("cost", out var cost) ||
            !cost.TryGetProperty("throttleStatus", out var throttle))
        {
            return;
        }

        var available = throttle.TryGetProperty("currentlyAvailable", out var currentlyAvailable) ? currentlyAvailable.GetDouble() : double.MaxValue;
        var restoreRate = throttle.TryGetProperty("restoreRate", out var rate) ? rate.GetDouble() : 50d;

        if (available >= _options.MinimumAvailableCostBeforeThrottle || restoreRate <= 0)
        {
            return;
        }

        var deficit = _options.MinimumAvailableCostBeforeThrottle - available;
        var waitSeconds = Math.Min(deficit / restoreRate, 10d);

        _logger.LogInformation(
            "Shopify cost budget low ({Available} available); waiting {Seconds:F1}s for it to refill.", available, waitSeconds);

        await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
    }

    /// <summary>Exponential backoff with jitter, honouring Retry-After when the server supplies one (§20).</summary>
    private static Task DelayForAttemptAsync(int attempt, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        if (retryAfter is { } explicitDelay)
        {
            return Task.Delay(explicitDelay, cancellationToken);
        }

        var baseDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
        return Task.Delay(baseDelay + jitter, cancellationToken);
    }
}

public sealed class ShopifyApiException : Exception
{
    public ShopifyApiException(string message) : base(message) { }
    public ShopifyApiException() : base("Shopify API error.") { }
    public ShopifyApiException(string message, Exception innerException) : base(message, innerException) { }
}
