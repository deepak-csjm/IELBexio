using IelBexio.Domain.Common;

namespace IelBexio.Domain.Bexio;

public enum BexioConnectionStatus { NotConnected = 0, Connected = 1, TokenExpired = 2, RefreshFailed = 3, Revoked = 4, ScopeInsufficient = 5 }

/// <summary>
/// A tenant's OAuth link to Bexio.
/// <para>
/// Security note (§24): the access and refresh tokens are stored <em>encrypted</em> via the configured
/// data-protection provider, and are exposed only through the token service running on the server.
/// They are never projected into an API response, never rendered in the UI, and never logged.
/// </para>
/// </summary>
public sealed class BexioConnection : TenantEntity
{
    public BexioConnectionStatus Status { get; set; } = BexioConnectionStatus.NotConnected;

    /// <summary>Protected (encrypted) access token. Never read outside the token service.</summary>
    public string? ProtectedAccessToken { get; set; }

    /// <summary>Protected (encrypted) refresh token. Never read outside the token service.</summary>
    public string? ProtectedRefreshToken { get; set; }

    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    public DateTimeOffset? RefreshTokenExpiresAt { get; set; }

    /// <summary>Scopes actually granted, as returned by the token endpoint (space-separated).</summary>
    public string? GrantedScopes { get; set; }

    /// <summary>Bexio company/org identifier discovered after connecting. Never hardcoded.</summary>
    public string? BexioCompanyId { get; set; }

    public string? BexioCompanyName { get; set; }
    public string? ConnectedBy { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public DateTimeOffset? LastRefreshedAt { get; set; }
    public DateTimeOffset? LastReferenceRefreshAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>Which client the connection belongs to, so rotating client ids invalidates stale rows.</summary>
    public string? ClientIdFingerprint { get; set; }

    public bool HasUsableRefreshToken =>
        !string.IsNullOrEmpty(ProtectedRefreshToken) &&
        (RefreshTokenExpiresAt is null || RefreshTokenExpiresAt > DateTimeOffset.UtcNow);
}

/// <summary>
/// Locally cached Bexio reference data (taxes, accounts, contacts, articles) discovered through the
/// API. Cached so preflight and mapping do not need a live call per invoice, and so the UI can show
/// what was available at mapping time. Never seeded with hardcoded Bexio ids (§5, §6).
/// </summary>
public sealed class BexioReferenceItem : TenantEntity
{
    public BexioReferenceKind Kind { get; set; }

    /// <summary>Bexio's own identifier, as a string so numeric/string id differences do not leak into the domain.</summary>
    public string BexioId { get; set; } = string.Empty;

    public string? Name { get; set; }
    public string? Code { get; set; }

    /// <summary>Percentage for taxes (8.1 = 8.1%); null for other kinds.</summary>
    public decimal? RatePercent { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Raw Bexio payload for this item, retained for provenance and field discovery.</summary>
    public string? RawJson { get; set; }

    public DateTimeOffset DiscoveredAt { get; set; }
}

public enum BexioReferenceKind { Unknown = 0, Tax = 1, Account = 2, Contact = 3, Article = 4, Currency = 5, Unit = 6, PaymentType = 7 }
