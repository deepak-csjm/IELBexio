using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using IelBexio.Application.Bexio;
using IelBexio.Connectors.Bexio.Configuration;
using IelBexio.Domain.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IelBexio.Connectors.Bexio.Api;

/// <summary>
/// The live Bexio adapter.
/// <para>
/// <b>Verification status — read this before trusting any field name below.</b> The Bexio
/// documentation hosts and the API host are unreachable from this build environment, so none of the
/// request/response shapes here were confirmed against primary documentation and no live call was
/// ever made. They are drawn from independent secondary sources (community SDKs) and are marked as
/// such in <see cref="BexioEndpoints"/>. Everything version- or path-specific is resolved through that
/// catalog, and the DTOs below are deliberately tolerant: unknown response fields are ignored, and
/// identifiers are read as either string or number, because the one thing worse than a wrong guess is
/// a wrong guess that throws at 3am.
/// </para>
/// </summary>
public sealed class BexioApiClient : IBexioClient
{
    private readonly HttpClient _http;
    private readonly IBexioTokenProvider _tokens;
    private readonly BexioOptions _options;
    private readonly ILogger<BexioApiClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public BexioApiClient(HttpClient http, IBexioTokenProvider tokens, IOptions<BexioOptions> options, ILogger<BexioApiClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _tokens = tokens;
        _options = options.Value;
        _logger = logger;
    }

    public string ModeName => $"Bexio API ({_options.ApiBaseUrl})";

    // ---- Reads -----------------------------------------------------------------------------------

    public async Task<BexioCompanyInfo> GetCompanyInfoAsync(CancellationToken cancellationToken = default)
    {
        var dto = await SendAsync<CompanyProfileDto>(HttpMethod.Get, BexioEndpoints.Path(BexioEndpoints.CompanyProfile), null, cancellationToken);
        return new BexioCompanyInfo(dto?.Id?.ToString(), dto?.Name, dto?.Country?.ToUpperInvariant(), dto?.Currency?.ToUpperInvariant());
    }

    public async Task<IReadOnlyList<BexioTax>> GetTaxesAsync(CancellationToken cancellationToken = default)
    {
        var dtos = await SendAsync<List<TaxDto>>(HttpMethod.Get, BexioEndpoints.Path(BexioEndpoints.Taxes), null, cancellationToken) ?? [];

        return dtos
            .Where(t => t.Uuid is not null || t.Id is not null)
            .Select(t => new BexioTax(
                Id: t.Uuid ?? t.Id!.ToString()!,
                Name: t.Name ?? t.DisplayName ?? "(unnamed tax)",
                RatePercent: ParseRate(t.Value ?? t.Percentage),
                IsActive: t.IsActive ?? true,
                Code: t.Code,
                Type: t.Type))
            .ToList();
    }

    public async Task<IReadOnlyList<BexioAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        var dtos = await SendAsync<List<AccountDto>>(HttpMethod.Get, BexioEndpoints.Path(BexioEndpoints.Accounts), null, cancellationToken) ?? [];

        return dtos
            .Where(a => a.Id is not null)
            .Select(a => new BexioAccount(a.Id!.ToString()!, a.AccountNo, a.Name ?? "(unnamed account)", a.IsActive ?? true, a.AccountType?.ToString()))
            .ToList();
    }

    public async Task<IReadOnlyList<BexioCurrency>> GetCurrenciesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var dtos = await SendAsync<List<CurrencyDto>>(HttpMethod.Get, BexioEndpoints.Path(BexioEndpoints.Currencies), null, cancellationToken) ?? [];
            return dtos
                .Where(c => c.Id is not null && c.Name is not null)
                .Select(c => new BexioCurrency(c.Id!.ToString()!, c.Name!.ToUpperInvariant(), true))
                .ToList();
        }
        catch (BexioApiException ex) when (ex.Category is SyncErrorCategory.NotFound or SyncErrorCategory.Authorization)
        {
            // This endpoint carries the lowest verification confidence in the catalog. Its absence
            // must degrade the currency preflight check to a warning, never fail an entire sync.
            _logger.LogWarning("Bexio currency endpoint unavailable ({Category}); currency validation will be skipped.", ex.Category);
            return [];
        }
    }

    public async Task<IReadOnlyList<BexioContact>> SearchContactsAsync(string? nameFragment, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nameFragment))
        {
            var all = await SendAsync<List<ContactDto>>(HttpMethod.Get, BexioEndpoints.Path(BexioEndpoints.ContactList), null, cancellationToken) ?? [];
            return all.Select(ToContact).ToList();
        }

        var criteria = new[]
        {
            new { field = "name_1", value = nameFragment, criteria = "like" },
        };

        var results = await SendAsync<List<ContactDto>>(HttpMethod.Post, BexioEndpoints.Path(BexioEndpoints.ContactSearch), criteria, cancellationToken) ?? [];
        return results.Select(ToContact).ToList();
    }

    public async Task<BexioContact?> GetContactAsync(string contactId, CancellationToken cancellationToken = default)
    {
        try
        {
            var dto = await SendAsync<ContactDto>(HttpMethod.Get, BexioEndpoints.Path(BexioEndpoints.ContactById, contactId), null, cancellationToken);
            return dto is null ? null : ToContact(dto);
        }
        catch (BexioApiException ex) when (ex.Category == SyncErrorCategory.NotFound)
        {
            return null;
        }
    }

    public async Task<BexioContact> CreateContactAsync(BexioContactRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AssertWritesAllowed();

        var payload = new
        {
            contact_type_id = request.IsCompany ? 1 : 2,
            name_1 = request.Name,
            name_2 = request.FirstName,
            mail = request.Email,
            address = request.Address,
            postcode = request.PostalCode,
            city = request.City,
            country_id = (int?)null,
        };

        var dto = await SendAsync<ContactDto>(HttpMethod.Post, BexioEndpoints.Path(BexioEndpoints.ContactCreate), payload, cancellationToken)
            ?? throw new BexioApiException(SyncErrorCategory.Unknown, null, "Bexio returned no body when creating a contact.");

        return ToContact(dto);
    }

    public async Task<IReadOnlyList<BexioArticle>> SearchArticlesAsync(string? codeOrName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(codeOrName))
        {
            var all = await SendAsync<List<ArticleDto>>(HttpMethod.Get, BexioEndpoints.Path(BexioEndpoints.ArticleList), null, cancellationToken) ?? [];
            return all.Select(ToArticle).ToList();
        }

        var criteria = new[]
        {
            new { field = "intern_code", value = codeOrName, criteria = "like" },
        };

        var results = await SendAsync<List<ArticleDto>>(HttpMethod.Post, BexioEndpoints.Path(BexioEndpoints.ArticleSearch), criteria, cancellationToken) ?? [];
        return results.Select(ToArticle).ToList();
    }

    // ---- The write --------------------------------------------------------------------------------

    public async Task<BexioInvoiceResult> CreateInvoiceAsync(BexioInvoiceRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AssertWritesAllowed();

        var payload = new
        {
            title = request.Title,
            contact_id = ParseIdOrThrow(request.ContactId, nameof(request.ContactId)),
            is_valid_from = request.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            is_valid_to = request.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            api_reference = request.ReferenceNumber,
            positions = request.Positions.Select(p => new
            {
                type = "KbPositionCustom",
                text = p.Text,
                amount = p.Amount.ToString(CultureInfo.InvariantCulture),
                unit_price = p.UnitPrice.ToString(CultureInfo.InvariantCulture),
                discount_in_percent = p.DiscountInPercent.ToString(CultureInfo.InvariantCulture),
                tax_id = p.TaxId,
                account_id = p.AccountId,
                article_id = p.ArticleId,
                unit_id = p.UnitId,
            }).ToList(),
        };

        // The idempotency key is sent as a header as well as being enforced in our own database. We
        // have not been able to verify that Bexio honours such a header, so the database-level guard
        // and the pre-sync "already synchronised?" check remain the real defence (§19).
        var dto = await SendAsync<InvoiceDto>(
            HttpMethod.Post,
            BexioEndpoints.Path(BexioEndpoints.InvoiceCreate),
            payload,
            cancellationToken,
            idempotencyKey)
            ?? throw new BexioApiException(SyncErrorCategory.Unknown, null, "Bexio returned no body when creating an invoice.");

        return ToInvoiceResult(dto);
    }

    public async Task<BexioInvoiceResult?> GetInvoiceAsync(string invoiceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var dto = await SendAsync<InvoiceDto>(HttpMethod.Get, BexioEndpoints.Path(BexioEndpoints.InvoiceById, invoiceId), null, cancellationToken);
            return dto is null ? null : ToInvoiceResult(dto);
        }
        catch (BexioApiException ex) when (ex.Category == SyncErrorCategory.NotFound)
        {
            return null;
        }
    }

    private void AssertWritesAllowed()
    {
        if (_options.ReadOnly)
        {
            throw new BexioApiException(SyncErrorCategory.Permanent, "READ_ONLY", "Bexio:ReadOnly is enabled; write operations are refused.");
        }
    }

    // ---- Transport ---------------------------------------------------------------------------------

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken, string? idempotencyKey = null)
    {
        var token = await _tokens.GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(method, new Uri(_options.ApiBaseUrl.TrimEnd('/') + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new BexioApiException(SyncErrorCategory.Network, "NETWORK", "The Bexio API could not be reached.", null, null, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BexioApiException(SyncErrorCategory.Network, "TIMEOUT", "The Bexio API request timed out.", null, null, ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await BuildExceptionAsync(response, cancellationToken);
            }

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return default;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                return default;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(content, JsonOptions);
            }
            catch (JsonException ex)
            {
                // A shape mismatch is exactly the failure mode our unverified DTOs risk, so it is
                // classified as Permanent: retrying an unparseable response is pointless, and the
                // operator needs to see it rather than watch it retry silently.
                throw new BexioApiException(
                    SyncErrorCategory.Permanent,
                    "RESPONSE_SHAPE",
                    $"The Bexio response for {method} {path} did not match the expected shape. " +
                    "See docs/bexio-integration.md — these DTOs are not verified against primary documentation.",
                    (int)response.StatusCode, null, ex);
            }
        }
    }

    /// <summary>Maps an HTTP failure onto the §20 error taxonomy, which decides whether it is retried.</summary>
    private static async Task<BexioApiException> BuildExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        string? detail = null;

        try
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            // Truncated: an error body may echo request content, and we do not want it all in logs.
            detail = raw.Length > 500 ? raw[..500] : raw;
        }
        catch (IOException)
        {
            // A body we cannot read does not change the classification.
        }

        var retryAfter = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);

        var category = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => SyncErrorCategory.Authentication,
            HttpStatusCode.Forbidden => SyncErrorCategory.Authorization,
            HttpStatusCode.NotFound => SyncErrorCategory.NotFound,
            HttpStatusCode.Conflict => SyncErrorCategory.Conflict,
            HttpStatusCode.TooManyRequests => SyncErrorCategory.RateLimit,
            HttpStatusCode.UnprocessableEntity => SyncErrorCategory.Validation,
            HttpStatusCode.BadRequest => SyncErrorCategory.Validation,
            HttpStatusCode.RequestTimeout => SyncErrorCategory.Transient,
            >= HttpStatusCode.InternalServerError => SyncErrorCategory.Transient,
            _ => SyncErrorCategory.Unknown,
        };

        return new BexioApiException(category, status.ToString(CultureInfo.InvariantCulture), $"Bexio returned {status}. {detail}", status, retryAfter);
    }

    private static decimal ParseRate(string? value) =>
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;

    private static int ParseIdOrThrow(string value, string name) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new BexioApiException(SyncErrorCategory.Validation, "BAD_ID", $"'{name}' value '{value}' is not a Bexio numeric id.");

    private static BexioContact ToContact(ContactDto d) => new(
        d.Id?.ToString() ?? string.Empty, d.Name1, d.Name2, d.Mail, d.CountryCode, d.VatNumber, d.Address, d.Postcode, d.City);

    private static BexioArticle ToArticle(ArticleDto d) => new(
        d.Id?.ToString() ?? string.Empty, d.InternCode, d.InternName, d.SalePrice, d.AccountId?.ToString(), d.IsStockArticle ?? true);

    private static BexioInvoiceResult ToInvoiceResult(InvoiceDto d) => new(
        d.Id?.ToString() ?? string.Empty, d.DocumentNr, d.TotalGross, d.TotalNet, d.TotalTaxes, d.CurrencyCode, JsonSerializer.Serialize(d, JsonOptions));

    // ---- DTOs. Tolerant by design: unknown fields are ignored, ids accept string or number. --------

    private sealed record CompanyProfileDto
    {
        [JsonPropertyName("id")] public JsonElement? Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("country")] public string? Country { get; init; }
        [JsonPropertyName("currency")] public string? Currency { get; init; }
    }

    private sealed record TaxDto
    {
        [JsonPropertyName("id")] public JsonElement? Id { get; init; }
        [JsonPropertyName("uuid")] public string? Uuid { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
        [JsonPropertyName("code")] public string? Code { get; init; }
        [JsonPropertyName("type")] public string? Type { get; init; }
        [JsonPropertyName("value")] public string? Value { get; init; }
        [JsonPropertyName("percentage")] public string? Percentage { get; init; }
        [JsonPropertyName("is_active")] public bool? IsActive { get; init; }
    }

    private sealed record AccountDto
    {
        [JsonPropertyName("id")] public JsonElement? Id { get; init; }
        [JsonPropertyName("account_no")] public string? AccountNo { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("account_type")] public JsonElement? AccountType { get; init; }
        [JsonPropertyName("is_active")] public bool? IsActive { get; init; }
    }

    private sealed record CurrencyDto
    {
        [JsonPropertyName("id")] public JsonElement? Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
    }

    private sealed record ContactDto
    {
        [JsonPropertyName("id")] public JsonElement? Id { get; init; }
        [JsonPropertyName("name_1")] public string? Name1 { get; init; }
        [JsonPropertyName("name_2")] public string? Name2 { get; init; }
        [JsonPropertyName("mail")] public string? Mail { get; init; }
        [JsonPropertyName("address")] public string? Address { get; init; }
        [JsonPropertyName("postcode")] public string? Postcode { get; init; }
        [JsonPropertyName("city")] public string? City { get; init; }
        [JsonPropertyName("country_code")] public string? CountryCode { get; init; }
        [JsonPropertyName("vat_number")] public string? VatNumber { get; init; }
    }

    private sealed record ArticleDto
    {
        [JsonPropertyName("id")] public JsonElement? Id { get; init; }
        [JsonPropertyName("intern_code")] public string? InternCode { get; init; }
        [JsonPropertyName("intern_name")] public string? InternName { get; init; }
        [JsonPropertyName("sale_price")] public decimal? SalePrice { get; init; }
        [JsonPropertyName("account_id")] public JsonElement? AccountId { get; init; }
        [JsonPropertyName("is_stock")] public bool? IsStockArticle { get; init; }
    }

    private sealed record InvoiceDto
    {
        [JsonPropertyName("id")] public JsonElement? Id { get; init; }
        [JsonPropertyName("document_nr")] public string? DocumentNr { get; init; }
        [JsonPropertyName("total_gross")] public decimal? TotalGross { get; init; }
        [JsonPropertyName("total_net")] public decimal? TotalNet { get; init; }
        [JsonPropertyName("total_taxes")] public decimal? TotalTaxes { get; init; }
        [JsonPropertyName("currency_code")] public string? CurrencyCode { get; init; }
    }
}
