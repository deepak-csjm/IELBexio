using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using IelBexio.Application.Abstractions;
using IelBexio.Domain.Invoicing;
using IelBexio.Infrastructure.Persistence;
using IelBexio.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IelBexio.Web.Startup;

/// <summary>Claim types this application owns.</summary>
public static class IelBexioClaimTypes
{
    /// <summary>The tenant whose data the actor may see. Carried on the principal so it survives
    /// everywhere the principal does — including into a Blazor circuit.</summary>
    public const string TenantId = "ielbexio:tenant_id";
}

/// <summary>
/// Identity for local development, issued as a real authentication scheme.
/// <para>
/// <b>Why a scheme and not middleware.</b> An earlier version of this file set an ambient per-request
/// tenant and user. That is correct for HTTP requests and silently wrong for interactive Blazor
/// components: a circuit has its own dependency-injection scope that no request middleware ever
/// touches, so every interactive re-render ran with no tenant and no roles — pages that had rendered
/// correctly during pre-render blanked out the moment the circuit connected. ASP.NET Core flows
/// exactly one thing into a circuit, the authenticated principal, so tenant and roles have to travel
/// on it.
/// </para>
/// <para>
/// <b>Posture.</b> The specification calls for Microsoft Entra ID (§24). This scheme is a development
/// stand-in and <b>is not authentication</b>: it trusts a header. It refuses to run outside the
/// Development environment, so the unsafe path cannot be reached by accident in a deployed
/// environment. Because identity now arrives as a principal, swapping in Entra is a registration
/// change — nothing downstream reads a header.
/// </para>
/// </summary>
public sealed class DevelopmentAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name, used wherever the development identity is registered.</summary>
    public const string SchemeName = "Development";

    private readonly IWebHostEnvironment _environment;

    public DevelopmentAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IWebHostEnvironment environment)
        : base(options, logger, encoder)
    {
        _environment = environment;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!_environment.IsDevelopment())
        {
            // Fail loudly rather than quietly granting Admin in a deployed environment.
            throw new InvalidOperationException(
                $"The '{SchemeName}' authentication scheme is registered outside the Development " +
                "environment. Configure Entra ID before deploying: it is not authentication.");
        }

        // A header lets the demo switch roles, to show that approval is genuinely role-gated rather
        // than merely hidden in the UI.
        var role = Context.Request.Headers["X-Demo-Role"].FirstOrDefault()
                   ?? Context.RequestServices.GetRequiredService<IConfiguration>()["Authentication:DevelopmentRole"]
                   ?? AppRoles.Admin;

        // Admin is a superset: a single header value is enough to drive the whole demo.
        string[] roles = string.Equals(role, AppRoles.Admin, StringComparison.OrdinalIgnoreCase)
            ? [AppRoles.Admin, AppRoles.Reviewer, AppRoles.Approver, AppRoles.IntegrationManager]
            : [role];

        var userId = Context.Request.Headers["X-Demo-User"].FirstOrDefault() ?? "demo@local.dev";

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, userId),
        };

        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

/// <summary>
/// Attaches the tenant to the authenticated principal.
/// <para>
/// Deliberately separate from the authentication handler: the tenant is a property of the
/// installation, not of the identity provider, so it is resolved the same way whether the principal
/// came from the development scheme or from Entra ID. Being a claim is what makes it survive into a
/// Blazor circuit, and it is server-derived — never read from anything the client can set.
/// </para>
/// </summary>
public sealed class TenantClaimsTransformation : IClaimsTransformation
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public TenantClaimsTransformation(IDbContextFactory<AppDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity?.IsAuthenticated != true || principal.HasClaim(c => c.Type == IelBexioClaimTypes.TenantId))
        {
            return principal;
        }

        // The POC operates one tenant, but nothing in the domain assumes that (§23): resolution
        // happens here, and everything downstream is filtered by whatever this returns.
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.SuppressTenantFilter = true;

        var tenantId = await db.Tenants.OrderBy(t => t.CreatedAt).Select(t => t.Id).FirstOrDefaultAsync();

        if (tenantId != Guid.Empty && principal.Identity is ClaimsIdentity identity)
        {
            identity.AddClaim(new Claim(IelBexioClaimTypes.TenantId, tenantId.ToString()));
        }

        return principal;
    }
}

/// <summary>Maps an authenticated principal onto the ambient tenant and actor a scope works with.</summary>
/// <remarks>
/// One place does this mapping, because an HTTP request and a Blazor circuit reach it by different
/// routes and must not be able to disagree about who the caller is.
/// </remarks>
public static class PrincipalContextBinder
{
    public static void Apply(ClaimsPrincipal? principal, AmbientTenantContext tenant, AmbientCurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(user);

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return;
        }

        if (Guid.TryParse(principal.FindFirst(IelBexioClaimTypes.TenantId)?.Value, CultureInfo.InvariantCulture, out var tenantId))
        {
            tenant.Set(tenantId);
        }

        var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? principal.Identity.Name
                     ?? "unknown";

        var displayName = principal.FindFirst(ClaimTypes.Name)?.Value ?? userId;
        var roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();

        user.Set(userId, displayName, roles);
    }
}

/// <summary>Binds the authenticated principal onto the ambient context for one HTTP request.</summary>
public sealed class RequestContextMiddleware
{
    private readonly RequestDelegate _next;

    public RequestContextMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, AmbientTenantContext tenantContext, AmbientCurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(context);

        PrincipalContextBinder.Apply(context.User, tenantContext, currentUser);

        await _next(context);
    }
}

/// <summary>
/// Binds the authenticated principal onto the ambient context for one Blazor circuit.
/// <para>
/// A circuit gets its own dependency-injection scope, created once when the circuit opens and reused
/// for every interactive render after that. No request middleware ever runs in it, so without this the
/// scope would carry no tenant and no roles — which is exactly the defect that made pre-rendered pages
/// blank out the instant the circuit connected. The authentication state is read here rather than on
/// demand because <see cref="AuthenticationStateProvider"/> may only be used from a component scope.
/// </para>
/// </summary>
public sealed class CircuitContextHandler : CircuitHandler
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly AmbientTenantContext _tenantContext;
    private readonly AmbientCurrentUser _currentUser;

    public CircuitContextHandler(
        AuthenticationStateProvider authenticationStateProvider,
        AmbientTenantContext tenantContext,
        AmbientCurrentUser currentUser)
    {
        _authenticationStateProvider = authenticationStateProvider;
        _tenantContext = tenantContext;
        _currentUser = currentUser;
    }

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync();

        PrincipalContextBinder.Apply(state.User, _tenantContext, _currentUser);

        await base.OnCircuitOpenedAsync(circuit, cancellationToken);
    }
}

/// <summary>Creates the single demo tenant on first run so the application is usable immediately.</summary>
public static class TenantSeeder
{
    public static async Task EnsureTenantAsync(IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.SuppressTenantFilter = true;
        if (await db.Tenants.AnyAsync(cancellationToken))
        {
            return;
        }

        db.Tenants.Add(new Tenant
        {
            Name = configuration["Tenant:DefaultTenantName"] ?? "Demo Tenant AG",
            CountryCode = configuration["Tenant:DefaultCountryCode"] ?? "CH",
            DefaultCurrency = configuration["Tenant:DefaultCurrency"] ?? "CHF",
        });

        await db.SaveChangesAsync(cancellationToken);
    }
}
