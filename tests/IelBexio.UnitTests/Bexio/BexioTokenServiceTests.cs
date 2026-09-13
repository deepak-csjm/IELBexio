using System.Net;
using IelBexio.Application.Bexio;
using IelBexio.Connectors.Bexio.Auth;
using IelBexio.Connectors.Bexio.Configuration;
using IelBexio.Domain.Audit;
using IelBexio.Domain.Bexio;
using IelBexio.Domain.Sync;
using IelBexio.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace IelBexio.UnitTests.Bexio;

/// <summary>
/// Contract tests for the Bexio OAuth flow against a WireMock.Net simulation of the documented token
/// endpoint.
/// <para>
/// <b>What these tests do and do not prove.</b> They prove our client implements the OAuth 2.0
/// authorization-code and refresh grants correctly, rotates refresh tokens, classifies failures, and
/// never leaks a token. They do <em>not</em> prove Bexio's issuer behaves as simulated — that host is
/// unreachable from this environment and no live call was made. If the live behaviour differs, these
/// tests are where the difference should first be encoded.
/// </para>
/// </summary>
public sealed class BexioTokenServiceTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly TestClock _clock = new();
    private readonly InMemoryBexioConnectionStore _store = new();
    private readonly RecordingAuditWriter _audit = new();
    private readonly ReversibleTestProtector _protector = new();

    private BexioTokenService CreateService(BexioOptions? overrides = null)
    {
        var options = overrides ?? new BexioOptions();
        options.ClientId = "test-client-id";
        options.ClientSecret = "test-client-secret";
        options.TokenEndpoint = $"{_server.Url}/realms/bexio/protocol/openid-connect/token";
        options.AuthorizationEndpoint = $"{_server.Url}/realms/bexio/protocol/openid-connect/auth";

        return new BexioTokenService(
            new HttpClient(),
            _store,
            _protector,
            Options.Create(options),
            _clock,
            _audit,
            NullLogger<BexioTokenService>.Instance);
    }

    private void StubToken(string accessToken, string? refreshToken, int expiresIn = 3600, int statusCode = 200)
    {
        _server.Reset();
        _server
            .Given(Request.Create().WithPath("/realms/bexio/protocol/openid-connect/token").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(statusCode)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                    {
                      "access_token": "{{accessToken}}",
                      "refresh_token": {{(refreshToken is null ? "null" : $"\"{refreshToken}\"")}},
                      "token_type": "Bearer",
                      "expires_in": {{expiresIn}},
                      "refresh_expires_in": 2592000,
                      "scope": "openid profile offline_access kb_invoice_edit"
                    }
                    """));
    }

    private void StubTokenError(string error, string description, int statusCode)
    {
        _server.Reset();
        _server
            .Given(Request.Create().WithPath("/realms/bexio/protocol/openid-connect/token").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(statusCode)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"error":"{{error}}","error_description":"{{description}}"}"""));
    }

    // ---- Authorization challenge -----------------------------------------------------------------

    [Fact]
    public void The_authorization_url_carries_pkce_state_and_the_configured_scopes()
    {
        var challenge = CreateService().CreateAuthorizationChallenge(new Uri("https://app.example.test/callback"));

        var query = challenge.AuthorizationUri.Query;
        query.Should().Contain("response_type=code");
        query.Should().Contain("client_id=test-client-id");
        query.Should().Contain("code_challenge_method=S256");
        query.Should().Contain("code_challenge=");
        query.Should().Contain("state=");
        query.Should().Contain("offline_access", "a refresh token is required for unattended synchronisation");

        challenge.State.Should().NotBeNullOrWhiteSpace();
        challenge.CodeVerifier.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_client_secret_never_appears_in_the_authorization_url()
    {
        var challenge = CreateService().CreateAuthorizationChallenge(new Uri("https://app.example.test/callback"));

        challenge.AuthorizationUri.ToString().Should().NotContain("test-client-secret");
    }

    [Fact]
    public void Each_challenge_uses_a_fresh_state_and_verifier()
    {
        var service = CreateService();
        var a = service.CreateAuthorizationChallenge(new Uri("https://app.example.test/callback"));
        var b = service.CreateAuthorizationChallenge(new Uri("https://app.example.test/callback"));

        a.State.Should().NotBe(b.State);
        a.CodeVerifier.Should().NotBe(b.CodeVerifier);
    }

    // ---- Code exchange ----------------------------------------------------------------------------

    [Fact]
    public async Task A_successful_code_exchange_stores_an_encrypted_token_and_records_the_granted_scopes()
    {
        StubToken("access-1", "refresh-1");
        var service = CreateService();

        var result = await service.CompleteAuthorizationAsync("code", "state", "verifier", new Uri("https://app.example.test/callback"));

        result.Succeeded.Should().BeTrue();
        _store.Connection!.Status.Should().Be(BexioConnectionStatus.Connected);
        _store.Connection.GrantedScopes.Should().Contain("kb_invoice_edit");
        _store.Connection.AccessTokenExpiresAt.Should().Be(_clock.UtcNow.AddSeconds(3600));
        _audit.Contains(AuditActions.BexioConnected).Should().BeTrue();
    }

    [Fact]
    public async Task Tokens_are_never_stored_in_plain_text()
    {
        StubToken("access-1", "refresh-1");

        await CreateService().CompleteAuthorizationAsync("code", "state", "verifier", new Uri("https://app.example.test/callback"));

        _store.Connection!.ProtectedAccessToken.Should().NotBe("access-1");
        _store.Connection.ProtectedAccessToken.Should().StartWith(ReversibleTestProtector.Prefix);
        _store.Connection.ProtectedRefreshToken.Should().NotBe("refresh-1");
    }

    [Fact]
    public async Task A_rejected_code_exchange_reports_the_oauth_error_without_connecting()
    {
        StubTokenError("invalid_grant", "Code not valid", 400);

        var result = await CreateService().CompleteAuthorizationAsync("bad", "state", "verifier", new Uri("https://app.example.test/callback"));

        result.Failed.Should().BeTrue();
        result.ErrorCode.Should().Be("invalid_grant");
        _store.Connection.Should().BeNull();
    }

    // ---- Token supply and refresh -----------------------------------------------------------------

    [Fact]
    public async Task A_valid_unexpired_token_is_returned_without_contacting_the_issuer()
    {
        StubToken("access-1", "refresh-1");
        var service = CreateService();
        await service.CompleteAuthorizationAsync("code", "state", "verifier", new Uri("https://app.example.test/callback"));

        _server.Reset(); // any further call to the issuer would now fail

        var token = await service.GetAccessTokenAsync();

        token.Should().Be("access-1");
    }

    [Fact]
    public async Task An_expired_token_is_refreshed_transparently_and_the_refresh_token_is_rotated()
    {
        StubToken("access-1", "refresh-1", expiresIn: 60);
        var service = CreateService();
        await service.CompleteAuthorizationAsync("code", "state", "verifier", new Uri("https://app.example.test/callback"));

        _clock.Advance(TimeSpan.FromMinutes(5));
        StubToken("access-2", "refresh-2");

        var token = await service.GetAccessTokenAsync();

        token.Should().Be("access-2");
        _protector.Unprotect(_store.Connection!.ProtectedRefreshToken).Should().Be("refresh-2", "a rotated refresh token must replace the old one");
        _audit.Contains(AuditActions.BexioTokenRefreshed).Should().BeTrue();
    }

    [Fact]
    public async Task The_token_is_refreshed_before_it_actually_expires_using_the_configured_skew()
    {
        StubToken("access-1", "refresh-1", expiresIn: 300);
        var service = CreateService(new BexioOptions { TokenRefreshSkew = TimeSpan.FromMinutes(10) });
        await service.CompleteAuthorizationAsync("code", "state", "verifier", new Uri("https://app.example.test/callback"));

        StubToken("access-2", "refresh-2");
        var token = await service.GetAccessTokenAsync();

        token.Should().Be("access-2", "the skew means a token expiring in 5 minutes is already considered stale");
    }

    [Fact]
    public async Task An_issuer_that_omits_a_new_refresh_token_leaves_the_existing_one_in_place()
    {
        StubToken("access-1", "refresh-1", expiresIn: 60);
        var service = CreateService();
        await service.CompleteAuthorizationAsync("code", "state", "verifier", new Uri("https://app.example.test/callback"));

        _clock.Advance(TimeSpan.FromMinutes(5));
        StubToken("access-2", refreshToken: null);
        await service.GetAccessTokenAsync();

        _protector.Unprotect(_store.Connection!.ProtectedRefreshToken).Should().Be("refresh-1");
    }

    [Fact]
    public async Task A_revoked_refresh_token_marks_the_connection_revoked_and_surfaces_as_an_authentication_error()
    {
        StubToken("access-1", "refresh-1", expiresIn: 60);
        var service = CreateService();
        await service.CompleteAuthorizationAsync("code", "state", "verifier", new Uri("https://app.example.test/callback"));

        _clock.Advance(TimeSpan.FromMinutes(5));
        StubTokenError("invalid_grant", "Refresh token revoked", 400);

        var act = async () => await service.GetAccessTokenAsync();

        var ex = await act.Should().ThrowAsync<BexioApiException>();
        ex.Which.Category.Should().Be(SyncErrorCategory.Authentication);
        _store.Connection!.Status.Should().Be(BexioConnectionStatus.Revoked);
    }

    [Fact]
    public async Task Requesting_a_token_with_no_connection_fails_as_authentication_rather_than_crashing()
    {
        var act = async () => await CreateService().GetAccessTokenAsync();

        (await act.Should().ThrowAsync<BexioApiException>()).Which.Category.Should().Be(SyncErrorCategory.Authentication);
    }

    [Fact]
    public async Task An_undecryptable_stored_token_demands_a_reconnect_rather_than_pretending_to_be_healthy()
    {
        // Simulates a rotated Data Protection key ring.
        _store.Connection = new BexioConnection
        {
            Status = BexioConnectionStatus.Connected,
            ProtectedAccessToken = "garbage-not-produced-by-this-protector",
            ProtectedRefreshToken = "also-garbage",
            AccessTokenExpiresAt = _clock.UtcNow.AddHours(-1),
        };

        var act = async () => await CreateService().GetAccessTokenAsync();

        await act.Should().ThrowAsync<BexioApiException>();
        _store.Connection.Status.Should().Be(BexioConnectionStatus.RefreshFailed);
    }

    [Fact]
    public async Task Disconnecting_clears_the_secret_material_rather_than_only_flipping_a_status()
    {
        StubToken("access-1", "refresh-1");
        var service = CreateService();
        await service.CompleteAuthorizationAsync("code", "state", "verifier", new Uri("https://app.example.test/callback"));

        await service.DisconnectAsync();

        _store.Connection!.ProtectedAccessToken.Should().BeNull();
        _store.Connection.ProtectedRefreshToken.Should().BeNull();
        _store.Connection.Status.Should().Be(BexioConnectionStatus.NotConnected);
        _audit.Contains(AuditActions.BexioDisconnected).Should().BeTrue();
    }

    [Fact]
    public async Task An_unreachable_issuer_is_reported_as_a_network_failure_not_a_crash()
    {
        var options = new BexioOptions
        {
            ClientId = "id",
            ClientSecret = "secret",
            // A port nothing is listening on.
            TokenEndpoint = "http://127.0.0.1:1/token",
        };

        var service = new BexioTokenService(
            new HttpClient(), _store, _protector, Options.Create(options), _clock, _audit, NullLogger<BexioTokenService>.Instance);

        var result = await service.CompleteAuthorizationAsync("code", "state", "verifier", new Uri("https://app.example.test/callback"));

        result.Failed.Should().BeTrue();
        result.ErrorCode.Should().Be("NETWORK");
    }

    [Fact]
    public async Task Granted_scopes_are_readable_for_the_preflight_scope_check()
    {
        StubToken("access-1", "refresh-1");
        var service = CreateService();
        await service.CompleteAuthorizationAsync("code", "state", "verifier", new Uri("https://app.example.test/callback"));

        var scopes = await service.GetGrantedScopesAsync();

        scopes.Should().Contain("kb_invoice_edit");
        scopes.Should().Contain("offline_access");
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }
}
