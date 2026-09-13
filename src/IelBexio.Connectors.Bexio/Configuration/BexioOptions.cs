using System.ComponentModel.DataAnnotations;

namespace IelBexio.Connectors.Bexio.Configuration;

/// <summary>Which Bexio implementation is active. Switching modes changes no domain code (§5).</summary>
public enum BexioMode
{
    /// <summary>In-memory mock. The default, so the POC runs and tests pass with no credentials.</summary>
    Mock = 0,

    /// <summary>Live HTTP against the Bexio API using OAuth.</summary>
    Api = 1,
}

public sealed class BexioOptions
{
    public const string SectionName = "Bexio";

    public BexioMode Mode { get; set; } = BexioMode.Mock;

    [Url]
    public string ApiBaseUrl { get; set; } = BexioEndpoints.DefaultApiBaseUrl;

    [Url]
    public string AuthorizationEndpoint { get; set; } = BexioEndpoints.DefaultAuthorizationEndpoint;

    [Url]
    public string TokenEndpoint { get; set; } = BexioEndpoints.DefaultTokenEndpoint;

    /// <summary>Supplied by User Secrets, environment variables or Key Vault. Never committed.</summary>
    public string? ClientId { get; set; }

    /// <summary>Supplied by User Secrets, environment variables or Key Vault. Never committed, never logged.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Requested scopes. Overridable so an operator can drop a scope the sandbox rejects.</summary>
    public IList<string> Scopes { get; } = BexioScopes.Default.Select(s => s.Value).ToList();

    /// <summary>Refresh the access token this long before it actually expires, to avoid racing expiry.</summary>
    public TimeSpan TokenRefreshSkew { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Attempts per outbox delivery for retryable failures. Not a retry-forever loop (§20).</summary>
    public int MaxRetryAttempts { get; set; } = 5;

    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// When true, the API client refuses to POST anything. A safety catch for pointing the live client
    /// at a production tenant during evaluation.
    /// </summary>
    public bool ReadOnly { get; set; }

    public bool IsConfiguredForApi => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>Behaviour switches for the mock, so tests can drive failure scenarios (§30).</summary>
public sealed class MockBexioOptions
{
    public const string SectionName = "Bexio:Mock";

    /// <summary>Scenario forced on the next call. Set by tests, never by production configuration.</summary>
    public MockFailureScenario FailureScenario { get; set; } = MockFailureScenario.None;

    /// <summary>Trip the failure only on the Nth call, to exercise retry-then-succeed paths.</summary>
    public int FailOnCallNumber { get; set; }

    public TimeSpan SimulatedLatency { get; set; } = TimeSpan.Zero;

    /// <summary>Seeds the mock's reference data with a realistic Swiss configuration.</summary>
    public bool SeedSwissReferenceData { get; set; } = true;
}

public enum MockFailureScenario
{
    None = 0,
    AuthenticationFailure = 1,
    AuthorizationFailure = 2,
    ValidationFailure = 3,
    RateLimited = 4,
    Timeout = 5,
    Conflict = 6,
    ServerError = 7,
    NetworkFailure = 8,
    NotFound = 9,
}
