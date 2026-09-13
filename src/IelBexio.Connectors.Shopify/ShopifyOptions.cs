namespace IelBexio.Connectors.Shopify;

public enum ShopifyMode
{
    /// <summary>Reads orders from JSON fixtures on disk. The default, so the POC runs with no Shopify store.</summary>
    Fixture = 0,

    /// <summary>Live Shopify Admin GraphQL API.</summary>
    Live = 1,
}

public sealed class ShopifyOptions
{
    public const string SectionName = "Shopify";

    public ShopifyMode Mode { get; set; } = ShopifyMode.Fixture;

    /// <summary>
    /// The Admin API version, e.g. "2025-10".
    /// <para>
    /// Held in exactly one place (§4: "do not hardcode the API version throughout the code"). The URL
    /// is built from it at the single call site, so a quarterly version bump is a configuration change.
    /// </para>
    /// </summary>
    public string ApiVersion { get; set; } = "2025-10";

    /// <summary>Shop domain, e.g. "example.myshopify.com". Not a secret.</summary>
    public string? ShopDomain { get; set; }

    /// <summary>Admin API access token. Secret: User Secrets, environment or Key Vault only.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Directory holding fixture orders when <see cref="Mode"/> is Fixture.</summary>
    public string FixtureDirectory { get; set; } = "fixtures/shopify";

    public int PageSize { get; set; } = 50;

    /// <summary>
    /// Shopify's GraphQL API is governed by a leaky-bucket cost budget rather than a request count.
    /// When the remaining budget drops below this, the connector waits for it to refill instead of
    /// hammering the endpoint into a throttle (§9).
    /// </summary>
    public int MinimumAvailableCostBeforeThrottle { get; set; } = 100;

    public int MaxRetryAttempts { get; set; } = 4;

    /// <summary>Secret used to verify inbound webhook HMAC signatures (§24 webhook spoofing).</summary>
    public string? WebhookSecret { get; set; }

    public bool IsConfiguredForLive => !string.IsNullOrWhiteSpace(ShopDomain) && !string.IsNullOrWhiteSpace(AccessToken);

    /// <summary>The single place a Shopify GraphQL endpoint URL is constructed.</summary>
    public Uri BuildGraphQlEndpoint() =>
        new($"https://{ShopDomain}/admin/api/{ApiVersion}/graphql.json");
}
