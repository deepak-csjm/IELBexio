namespace IelBexio.Connectors.Amazon;

public enum AmazonMode
{
    /// <summary>Reads orders from JSON fixtures. The default — the POC must not be blocked on Amazon credentials (§10).</summary>
    Fixture = 0,

    /// <summary>Live Amazon Selling Partner API.</summary>
    Live = 1,
}

public sealed class AmazonOptions
{
    public const string SectionName = "Amazon";

    public AmazonMode Mode { get; set; } = AmazonMode.Fixture;

    /// <summary>SP-API regional endpoint, e.g. https://sellingpartnerapi-eu.amazon.com.</summary>
    public string Endpoint { get; set; } = "https://sellingpartnerapi-eu.amazon.com";

    /// <summary>Login with Amazon token endpoint used to exchange the refresh token for an access token.</summary>
    public string LwaTokenEndpoint { get; set; } = "https://api.amazon.com/auth/o2/token";

    /// <summary>LWA client id. Not itself a secret, but kept out of source alongside the rest.</summary>
    public string? LwaClientId { get; set; }

    /// <summary>LWA client secret. Secret: User Secrets, environment or Key Vault only.</summary>
    public string? LwaClientSecret { get; set; }

    /// <summary>Seller's long-lived refresh token. Secret.</summary>
    public string? RefreshToken { get; set; }

    /// <summary>Marketplace ids to query, e.g. A1PA6795UKMFR9 (DE).</summary>
    public IList<string> MarketplaceIds { get; } = [];

    /// <summary>
    /// Whether this application holds an approved PII data-access role. When false the connector does
    /// not even attempt to request buyer information, rather than calling and being denied.
    /// </summary>
    public bool HasApprovedPiiDataAccess { get; set; }

    public string FixtureDirectory { get; set; } = "fixtures/amazon";

    public int PageSize { get; set; } = 50;
    public int MaxRetryAttempts { get; set; } = 4;

    public bool IsConfiguredForLive =>
        !string.IsNullOrWhiteSpace(LwaClientId) &&
        !string.IsNullOrWhiteSpace(LwaClientSecret) &&
        !string.IsNullOrWhiteSpace(RefreshToken);
}
