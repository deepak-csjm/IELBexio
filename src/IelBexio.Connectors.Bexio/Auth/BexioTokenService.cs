using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using IelBexio.Application.Abstractions;
using IelBexio.Application.Bexio;
using IelBexio.Application.Common;
using IelBexio.Connectors.Bexio.Configuration;
using IelBexio.Domain.Bexio;
using IelBexio.Domain.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IelBexio.Connectors.Bexio.Auth;

/// <summary>Persists and retrieves the tenant's Bexio connection. Implemented in Infrastructure.</summary>
public interface IBexioConnectionStore
{
    Task<BexioConnection?> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(BexioConnection connection, CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>Token endpoint response. Field names follow RFC 6749.</summary>
internal sealed record TokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
    [JsonPropertyName("token_type")] public string? TokenType { get; init; }
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; init; }
    [JsonPropertyName("refresh_expires_in")] public int? RefreshExpiresIn { get; init; }
    [JsonPropertyName("scope")] public string? Scope { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; init; }
}

/// <summary>
/// Drives the Bexio OAuth 2.0 / OIDC authorization-code flow with PKCE, and owns the token lifecycle:
/// acquisition, expiry tracking, refresh with rotation, disconnect, and failure classification (§5).
/// <para>
/// <b>Token handling.</b> Tokens are encrypted at rest via <see cref="ISecretProtector"/>, are held in
/// memory only for the duration of a call, and are never returned to a caller outside this assembly,
/// never placed in an API response, and never written to a log — not even truncated. The only thing
/// that leaves this class is the <c>Authorization</c> header the HTTP handler attaches.
/// </para>
/// <para>
/// <b>Verification status.</b> The endpoints used here are those given verbatim in the project
/// specification §4. The flow itself is standard OIDC. Neither could be exercised against the live
/// Bexio issuer in this environment (host blocked), so it is covered by WireMock.Net contract tests
/// that simulate the documented flow, including refresh-token rotation and each failure mode.
/// </para>
/// </summary>
public sealed class BexioTokenService : IBexioTokenProvider, IBexioAuthorizationService, IDisposable
{
    private readonly HttpClient _http;
    private readonly IBexioConnectionStore _store;
    private readonly ISecretProtector _protector;
    private readonly BexioOptions _options;
    private readonly IClock _clock;
    private readonly IAuditWriter _audit;
    private readonly ILogger<BexioTokenService> _logger;

    /// <summary>Serialises refresh so a burst of worker calls performs one refresh, not N racing ones.</summary>
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public BexioTokenService(
        HttpClient http,
        IBexioConnectionStore store,
        ISecretProtector protector,
        IOptions<BexioOptions> options,
        IClock clock,
        IAuditWriter audit,
        ILogger<BexioTokenService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _store = store;
        _protector = protector;
        _options = options.Value;
        _clock = clock;
        _audit = audit;
        _logger = logger;
    }

    // ---- Authorization-code flow ---------------------------------------------------------------

    public BexioAuthorizationChallenge CreateAuthorizationChallenge(Uri redirectUri)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);

        if (string.IsNullOrWhiteSpace(_options.ClientId))
        {
            throw new InvalidOperationException("Bexio:ClientId is not configured; cannot start the authorization flow.");
        }

        // PKCE (RFC 7636). Even for a confidential client this closes the authorization-code
        // interception window, and costs nothing.
        var codeVerifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

        // Opaque, high-entropy state. Verified on callback to defeat CSRF against the redirect (§24).
        var state = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        var query = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = redirectUri.ToString(),
            ["scope"] = string.Join(' ', _options.Scopes),
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };

        var uri = new Uri(QueryHelpers.AddQueryString(_options.AuthorizationEndpoint, query));
        return new BexioAuthorizationChallenge(uri, state, codeVerifier);
    }

    public async Task<Result> CompleteAuthorizationAsync(string code, string state, string codeVerifier, Uri redirectUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri.ToString(),
            ["client_id"] = _options.ClientId ?? string.Empty,
            ["client_secret"] = _options.ClientSecret ?? string.Empty,
            ["code_verifier"] = codeVerifier,
        };

        var tokenResult = await PostTokenRequestAsync(form, cancellationToken);
        if (tokenResult.Failed)
        {
            return Result.Failure(tokenResult.ErrorCode!, tokenResult.ErrorMessage!);
        }

        var token = tokenResult.Value!;
        var connection = await _store.GetAsync(cancellationToken) ?? new BexioConnection();

        ApplyTokens(connection, token);
        connection.Status = BexioConnectionStatus.Connected;
        connection.ConnectedAt = _clock.UtcNow;
        connection.ClientIdFingerprint = Fingerprint(_options.ClientId);
        connection.LastError = null;

        await _store.SaveAsync(connection, cancellationToken);
        await _audit.WriteAsync(
            Domain.Audit.AuditActions.BexioConnected,
            nameof(BexioConnection),
            connection.Id,
            newValue: new { connection.Status, Scopes = connection.GrantedScopes },
            cancellationToken: cancellationToken);

        _logger.LogInformation("Bexio connection established. Granted scopes: {Scopes}", connection.GrantedScopes);
        return Result.Success();
    }

    public async Task<Result> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            return await RefreshCoreAsync(cancellationToken);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<Result> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var connection = await _store.GetAsync(cancellationToken);
        if (connection is null || !connection.HasUsableRefreshToken(_clock.UtcNow))
        {
            return Result.Failure("NO_REFRESH_TOKEN", "There is no usable refresh token; the tenant must reconnect to Bexio.");
        }

        var refreshToken = _protector.Unprotect(connection.ProtectedRefreshToken);
        if (string.IsNullOrEmpty(refreshToken))
        {
            // Undecryptable almost always means the data-protection key ring changed. Treat as a
            // reconnect-required condition rather than pretending the connection is healthy.
            connection.Status = BexioConnectionStatus.RefreshFailed;
            connection.LastError = "The stored refresh token could not be decrypted.";
            await _store.SaveAsync(connection, cancellationToken);
            return Result.Failure("TOKEN_UNPROTECT_FAILED", connection.LastError);
        }

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = _options.ClientId ?? string.Empty,
            ["client_secret"] = _options.ClientSecret ?? string.Empty,
        };

        var tokenResult = await PostTokenRequestAsync(form, cancellationToken);
        if (tokenResult.Failed)
        {
            connection.Status = tokenResult.ErrorCode == "invalid_grant"
                ? BexioConnectionStatus.Revoked
                : BexioConnectionStatus.RefreshFailed;
            connection.LastError = tokenResult.ErrorMessage;
            await _store.SaveAsync(connection, cancellationToken);
            return Result.Failure(tokenResult.ErrorCode!, tokenResult.ErrorMessage!);
        }

        ApplyTokens(connection, tokenResult.Value!);
        connection.Status = BexioConnectionStatus.Connected;
        connection.LastRefreshedAt = _clock.UtcNow;
        connection.LastError = null;

        await _store.SaveAsync(connection, cancellationToken);
        await _audit.WriteAsync(
            Domain.Audit.AuditActions.BexioTokenRefreshed,
            nameof(BexioConnection),
            connection.Id,
            cancellationToken: cancellationToken);

        return Result.Success();
    }

    public async Task<Result> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _store.GetAsync(cancellationToken);
        if (connection is null)
        {
            return Result.Success();
        }

        // Clear the secret material rather than only flipping a status flag.
        connection.ProtectedAccessToken = null;
        connection.ProtectedRefreshToken = null;
        connection.AccessTokenExpiresAt = null;
        connection.RefreshTokenExpiresAt = null;
        connection.GrantedScopes = null;
        connection.Status = BexioConnectionStatus.NotConnected;

        await _store.SaveAsync(connection, cancellationToken);
        await _audit.WriteAsync(
            Domain.Audit.AuditActions.BexioDisconnected,
            nameof(BexioConnection),
            connection.Id,
            cancellationToken: cancellationToken);

        return Result.Success();
    }

    // ---- Token supply ---------------------------------------------------------------------------

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _store.GetAsync(cancellationToken)
            ?? throw new BexioApiException(SyncErrorCategory.Authentication, "NOT_CONNECTED", "Bexio is not connected for this tenant.");

        if (connection.Status is BexioConnectionStatus.Revoked or BexioConnectionStatus.NotConnected)
        {
            throw new BexioApiException(SyncErrorCategory.Authentication, "NOT_CONNECTED", $"The Bexio connection is {connection.Status}; reconnect is required.");
        }

        var needsRefresh = connection.AccessTokenExpiresAt is null
            || connection.AccessTokenExpiresAt <= _clock.UtcNow.Add(_options.TokenRefreshSkew);

        if (needsRefresh)
        {
            var refresh = await RefreshAsync(cancellationToken);
            if (refresh.Failed)
            {
                throw new BexioApiException(SyncErrorCategory.Authentication, refresh.ErrorCode, refresh.ErrorMessage ?? "Token refresh failed.");
            }

            connection = await _store.GetAsync(cancellationToken)!;
        }

        var token = _protector.Unprotect(connection!.ProtectedAccessToken);
        return string.IsNullOrEmpty(token)
            ? throw new BexioApiException(SyncErrorCategory.Authentication, "NO_ACCESS_TOKEN", "No usable Bexio access token is available.")
            : token;
    }

    public async Task<IReadOnlyList<string>> GetGrantedScopesAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _store.GetAsync(cancellationToken);
        return connection?.GrantedScopes?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
    }

    public async Task<bool> IsConnectedAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _store.GetAsync(cancellationToken);
        return connection is { Status: BexioConnectionStatus.Connected } && !string.IsNullOrEmpty(connection.ProtectedAccessToken);
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private async Task<Result<TokenResponse>> PostTokenRequestAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await _http.PostAsync(new Uri(_options.TokenEndpoint), content, cancellationToken);
            var body = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);

            if (!response.IsSuccessStatusCode || body?.AccessToken is null)
            {
                // The error description may echo request parameters, so it is logged as a category, not
                // verbatim, and never includes the form (which holds the client secret).
                var code = body?.Error ?? response.StatusCode.ToString();
                var message = body?.ErrorDescription ?? $"The token endpoint returned {(int)response.StatusCode}.";
                _logger.LogWarning("Bexio token request failed with {Code}", code);
                return Result<TokenResponse>.Failure(code, message);
            }

            return Result<TokenResponse>.Success(body);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Network failure contacting the Bexio token endpoint.");
            return Result<TokenResponse>.Failure("NETWORK", "The Bexio token endpoint could not be reached.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result<TokenResponse>.Failure("TIMEOUT", "The Bexio token request timed out.");
        }
    }

    private void ApplyTokens(BexioConnection connection, TokenResponse token)
    {
        connection.ProtectedAccessToken = _protector.Protect(token.AccessToken!);

        // Rotation: keep the new refresh token when one is issued, otherwise retain the existing one.
        if (!string.IsNullOrEmpty(token.RefreshToken))
        {
            connection.ProtectedRefreshToken = _protector.Protect(token.RefreshToken);
        }

        var now = _clock.UtcNow;
        connection.AccessTokenExpiresAt = token.ExpiresIn is { } seconds ? now.AddSeconds(seconds) : now.AddMinutes(30);
        connection.RefreshTokenExpiresAt = token.RefreshExpiresIn is { } refreshSeconds ? now.AddSeconds(refreshSeconds) : null;

        if (!string.IsNullOrEmpty(token.Scope))
        {
            connection.GrantedScopes = token.Scope;
        }
    }

    /// <summary>Non-reversible fingerprint of the client id, so a rotated client invalidates stale rows.</summary>
    private static string Fingerprint(string? clientId) =>
        clientId is null
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientId)))[..32].ToLowerInvariant();

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => _refreshLock.Dispose();
}

/// <summary>Minimal query-string builder, avoiding a dependency on ASP.NET Core from this assembly.</summary>
internal static class QueryHelpers
{
    public static string AddQueryString(string uri, IReadOnlyDictionary<string, string?> parameters)
    {
        var builder = new StringBuilder(uri);
        var first = !uri.Contains('?', StringComparison.Ordinal);

        foreach (var (key, value) in parameters)
        {
            if (value is null)
            {
                continue;
            }

            builder.Append(first ? '?' : '&');
            first = false;
            builder.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }

        return builder.ToString();
    }
}
