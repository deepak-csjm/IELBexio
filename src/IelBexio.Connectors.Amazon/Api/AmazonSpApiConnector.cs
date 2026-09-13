using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using IelBexio.Application.Abstractions;
using IelBexio.Application.Sources;
using IelBexio.Connectors.Amazon.Fixtures;
using IelBexio.Connectors.Amazon.Normalization;
using IelBexio.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IelBexio.Connectors.Amazon.Api;

/// <summary>
/// Live Amazon Selling Partner API connector.
/// <para>
/// <b>Verification status.</b> developer-docs.amazon.com is unreachable from this build environment,
/// so the operation paths and response shapes could not be confirmed against primary documentation and
/// no live call was made. The flow implemented — exchange the seller's refresh token at the LWA token
/// endpoint for a short-lived access token, then call SP-API with it in <c>x-amz-access-token</c> — is
/// the documented shape per secondary sources.
/// </para>
/// <para>
/// <b>On buyer PII.</b> This connector does not request restricted data unless
/// <see cref="AmazonOptions.HasApprovedPiiDataAccess"/> is set. Requesting PII you are not approved for
/// produces denials and, worse, encourages working around them; declaring the capability up front means
/// the absence of buyer identity is handled as a first-class outcome instead of an error.
/// </para>
/// </summary>
public sealed class AmazonSpApiConnector : IInvoiceSource, ISalesOrderSource, IDisposable
{
    private readonly HttpClient _http;
    private readonly AmazonOptions _options;
    private readonly AmazonOrderNormalizer _normalizer;
    private readonly ITenantContext _tenant;
    private readonly ILogger<AmazonSpApiConnector> _logger;

    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public AmazonSpApiConnector(
        HttpClient http,
        IOptions<AmazonOptions> options,
        AmazonOrderNormalizer normalizer,
        ITenantContext tenant,
        ILogger<AmazonSpApiConnector> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options.Value;
        _normalizer = normalizer;
        _tenant = tenant;
        _logger = logger;
    }

    public SourceSystem SourceSystem => SourceSystem.Amazon;

    public string ModeName => $"Live Amazon SP-API ({_options.Endpoint})";

    public SourceCapabilities Capabilities
    {
        get
        {
            var capabilities = SourceCapabilities.Invoices | SourceCapabilities.SalesOrders |
                               SourceCapabilities.Customers | SourceCapabilities.IncrementalSync;

            // Only claim the capability when the application is actually approved for it.
            if (_options.HasApprovedPiiDataAccess)
            {
                capabilities |= SourceCapabilities.BuyerPersonalData;
            }

            return capabilities;
        }
    }

    public IReadOnlyList<string> DocumentedRestrictions => AmazonRestrictions.All;

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfiguredForLive)
        {
            return false;
        }

        try
        {
            await GetAccessTokenAsync(cancellationToken);
            return true;
        }
        catch (AmazonApiException ex)
        {
            _logger.LogWarning(ex, "Amazon SP-API connection test failed.");
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
            throw new AmazonApiException("Amazon live mode is selected but the LWA client id/secret and refresh token are not configured.");
        }

        if (_options.MarketplaceIds.Count == 0)
        {
            throw new AmazonApiException("At least one Amazon marketplace id must be configured.");
        }

        var parameters = new List<string>
        {
            $"MarketplaceIds={Uri.EscapeDataString(string.Join(',', _options.MarketplaceIds))}",
            $"MaxResultsPerPage={Math.Clamp(query.PageSize, 1, 100)}",
        };

        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            parameters.Add($"NextToken={Uri.EscapeDataString(query.Cursor)}");
        }
        else
        {
            // SP-API requires a lower bound on the first page. Incremental sync uses the watermark; a
            // first-ever import falls back to a bounded window rather than the whole history.
            var since = query.UpdatedSince ?? DateTimeOffset.UtcNow.AddDays(-30);
            parameters.Add($"LastUpdatedAfter={Uri.EscapeDataString(since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}");
        }

        using var ordersDocument = await SendAsync($"/orders/v0/orders?{string.Join('&', parameters)}", cancellationToken);

        if (!ordersDocument.RootElement.TryGetProperty("payload", out var payload))
        {
            throw new AmazonApiException("The SP-API orders response contained no 'payload' node.");
        }

        var documents = new List<SourceInvoiceDocument>();

        if (payload.TryGetProperty("Orders", out var orders) && orders.ValueKind == JsonValueKind.Array)
        {
            foreach (var order in orders.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var amazonOrderId = order.TryGetProperty("AmazonOrderId", out var idElement) ? idElement.GetString() : null;
                if (amazonOrderId is null)
                {
                    continue;
                }

                // Order items are a separate operation; an order without them cannot be booked, so a
                // failure here is surfaced per order rather than failing the whole page.
                JsonElement itemsElement;
                try
                {
                    using var itemsDocument = await SendAsync(
                        $"/orders/v0/orders/{Uri.EscapeDataString(amazonOrderId)}/orderItems", cancellationToken);

                    itemsElement = itemsDocument.RootElement.TryGetProperty("payload", out var itemsPayload)
                                   && itemsPayload.TryGetProperty("OrderItems", out var orderItems)
                        ? orderItems.Clone()
                        : default;
                }
                catch (AmazonApiException ex)
                {
                    _logger.LogWarning(ex, "Could not retrieve order items for Amazon order {OrderId}; skipping it.", amazonOrderId);
                    continue;
                }

                using var combined = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    order = JsonSerializer.Deserialize<JsonElement>(order.GetRawText()),
                    orderItems = itemsElement.ValueKind == JsonValueKind.Array
                        ? JsonSerializer.Deserialize<JsonElement>(itemsElement.GetRawText())
                        : JsonSerializer.Deserialize<JsonElement>("[]"),
                }));

                documents.Add(_normalizer.Normalize(combined.RootElement, _tenant.TenantId, null, "orders/v0"));
            }
        }

        var nextToken = payload.TryGetProperty("NextToken", out var next) ? next.GetString() : null;
        return new SourcePage<SourceInvoiceDocument>(documents, nextToken, !string.IsNullOrEmpty(nextToken));
    }

    /// <summary>Exchanges the seller's refresh token for a short-lived access token, caching it until expiry.</summary>
    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is not null && _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return _accessToken;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return _accessToken;
            }

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _options.RefreshToken!,
                ["client_id"] = _options.LwaClientId!,
                ["client_secret"] = _options.LwaClientSecret!,
            });

            using var response = await _http.PostAsync(new Uri(_options.LwaTokenEndpoint), content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Deliberately does not echo the response body: it can contain credential material.
                throw new AmazonApiException($"The Amazon LWA token endpoint returned {(int)response.StatusCode}.");
            }

            var token = await response.Content.ReadFromJsonAsync<LwaTokenResponse>(cancellationToken)
                ?? throw new AmazonApiException("The Amazon LWA token endpoint returned no body.");

            _accessToken = token.AccessToken ?? throw new AmazonApiException("The Amazon LWA response contained no access_token.");
            _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn ?? 3600);

            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<JsonDocument> SendAsync(string path, CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (true)
        {
            attempt++;
            var token = await GetAccessTokenAsync(cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.Endpoint.TrimEnd('/') + path));
            request.Headers.TryAddWithoutValidation("x-amz-access-token", token);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < _options.MaxRetryAttempts)
            {
                _logger.LogWarning(ex, "Amazon SP-API request failed on attempt {Attempt}; retrying.", attempt);
                await DelayAsync(attempt, null, cancellationToken);
                continue;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < _options.MaxRetryAttempts)
                {
                    await DelayAsync(attempt, response.Headers.RetryAfter?.Delta, cancellationToken);
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new AmazonApiException(
                        $"Amazon SP-API returned {(int)response.StatusCode}. This usually means the application lacks the " +
                        "required role for this operation, or a restricted data element was requested without a Restricted Data Token.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (attempt < _options.MaxRetryAttempts && (int)response.StatusCode >= 500)
                    {
                        await DelayAsync(attempt, null, cancellationToken);
                        continue;
                    }

                    throw new AmazonApiException($"Amazon SP-API returned {(int)response.StatusCode} for {path}.");
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonDocument.Parse(body);
            }
        }
    }

    private static Task DelayAsync(int attempt, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        if (retryAfter is { } explicitDelay)
        {
            return Task.Delay(explicitDelay, cancellationToken);
        }

        var baseDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
        return Task.Delay(baseDelay + jitter, cancellationToken);
    }

    public void Dispose() => _tokenLock.Dispose();

    private sealed record LwaTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [JsonPropertyName("expires_in")] public int? ExpiresIn { get; init; }
        [JsonPropertyName("token_type")] public string? TokenType { get; init; }
    }
}

public sealed class AmazonApiException : Exception
{
    public AmazonApiException(string message) : base(message) { }
    public AmazonApiException() : base("Amazon SP-API error.") { }
    public AmazonApiException(string message, Exception innerException) : base(message, innerException) { }
}
