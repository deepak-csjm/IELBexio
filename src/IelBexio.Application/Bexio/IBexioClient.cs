using IelBexio.Application.Common;
using IelBexio.Domain.Sync;

namespace IelBexio.Application.Bexio;

// ---------------------------------------------------------------------------------------------
// Vendor-neutral DTOs. Bexio's wire shapes never cross this boundary — the adapter translates.
// ---------------------------------------------------------------------------------------------

public sealed record BexioCompanyInfo(string? CompanyId, string? Name, string? CountryCode, string? DefaultCurrency);

public sealed record BexioTax(string Id, string Name, decimal RatePercent, bool IsActive, string? Code, string? Type);

public sealed record BexioAccount(string Id, string? AccountNumber, string Name, bool IsActive, string? AccountType);

public sealed record BexioContact(
    string Id,
    string? Name,
    string? FirstName,
    string? Email,
    string? CountryCode,
    string? VatNumber,
    string? Address,
    string? PostalCode,
    string? City);

public sealed record BexioArticle(string Id, string? Code, string? Name, decimal? UnitPrice, string? AccountId, bool IsActive);

public sealed record BexioCurrency(string Id, string Code, bool IsActive);

/// <summary>A line to be created on a Bexio invoice. Amounts are decimals; ids are opaque strings.</summary>
public sealed record BexioInvoicePositionRequest(
    string Text,
    decimal Amount,
    decimal UnitPrice,
    string? TaxId,
    string? AccountId,
    string? ArticleId = null,
    string? UnitId = null,
    decimal DiscountInPercent = 0m);

public sealed record BexioInvoiceRequest(
    string ContactId,
    DateOnly InvoiceDate,
    DateOnly? DueDate,
    string CurrencyCode,
    string? CurrencyId,
    string? Title,
    string? ReferenceNumber,
    IReadOnlyList<BexioInvoicePositionRequest> Positions,
    string? UserId = null);

public sealed record BexioInvoiceResult(
    string Id,
    string? DocumentNumber,
    decimal? TotalGross,
    decimal? TotalNet,
    decimal? TotalTax,
    string? CurrencyCode,
    string? RawJson);

public sealed record BexioContactRequest(
    string? Name,
    string? FirstName,
    string? Email,
    string? CountryCode,
    string? Address,
    string? PostalCode,
    string? City,
    string? VatNumber,
    bool IsCompany);

/// <summary>
/// A Bexio failure, classified into the taxonomy of §20 so retry policy is a property of the error
/// rather than a guess at the call site.
/// </summary>
public sealed class BexioApiException : Exception
{
    public BexioApiException(SyncErrorCategory category, string? code, string message, int? statusCode = null, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        Category = category;
        Code = code;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public BexioApiException() : base("Bexio API error.") => Category = SyncErrorCategory.Unknown;

    public BexioApiException(string message) : base(message) => Category = SyncErrorCategory.Unknown;

    public BexioApiException(string message, Exception innerException) : base(message, innerException) => Category = SyncErrorCategory.Unknown;

    public SyncErrorCategory Category { get; }
    public string? Code { get; }
    public int? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }
    public bool IsRetryable => SyncErrorPolicy.IsRetryable(Category);
}

/// <summary>
/// The Bexio port (§5). Two implementations exist — <c>MockBexioClient</c> and <c>BexioApiClient</c> —
/// and the application switches between them by configuration alone. No domain or workflow code knows
/// which is in use.
/// <para>
/// Note there is deliberately no "approve" or "post without approval" capability, and the AI layer
/// has no reference to this interface at all (§14, §40).
/// </para>
/// </summary>
public interface IBexioClient
{
    /// <summary>Human-readable name of the active implementation, surfaced in the UI ("Mock" / "Bexio API").</summary>
    string ModeName { get; }

    Task<BexioCompanyInfo> GetCompanyInfoAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BexioTax>> GetTaxesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BexioAccount>> GetAccountsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BexioCurrency>> GetCurrenciesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BexioContact>> SearchContactsAsync(string? nameFragment, CancellationToken cancellationToken = default);

    Task<BexioContact?> GetContactAsync(string contactId, CancellationToken cancellationToken = default);

    Task<BexioContact> CreateContactAsync(BexioContactRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BexioArticle>> SearchArticlesAsync(string? codeOrName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an invoice. <paramref name="idempotencyKey"/> lets the adapter short-circuit a repeat of
    /// a request it has already performed — belt-and-braces alongside the database-level guard, because
    /// Bexio itself offers no idempotency header we have been able to verify (see docs/assumptions.md).
    /// </summary>
    Task<BexioInvoiceResult> CreateInvoiceAsync(BexioInvoiceRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<BexioInvoiceResult?> GetInvoiceAsync(string invoiceId, CancellationToken cancellationToken = default);
}

/// <summary>Supplies a valid Bexio access token, refreshing it when needed. Tokens never leave the server.</summary>
public interface IBexioTokenProvider
{
    /// <summary>Returns a currently-valid access token, refreshing transparently. Throws <see cref="BexioApiException"/> if unavailable.</summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetGrantedScopesAsync(CancellationToken cancellationToken = default);

    Task<bool> IsConnectedAsync(CancellationToken cancellationToken = default);
}

/// <summary>Drives the OAuth authorization-code flow and token lifecycle (§5).</summary>
public interface IBexioAuthorizationService
{
    /// <summary>Builds the authorization URL and the state/PKCE values the callback must verify.</summary>
    BexioAuthorizationChallenge CreateAuthorizationChallenge(Uri redirectUri);

    Task<Result> CompleteAuthorizationAsync(string code, string state, string codeVerifier, Uri redirectUri, CancellationToken cancellationToken = default);

    Task<Result> RefreshAsync(CancellationToken cancellationToken = default);

    Task<Result> DisconnectAsync(CancellationToken cancellationToken = default);
}

public sealed record BexioAuthorizationChallenge(Uri AuthorizationUri, string State, string CodeVerifier);
