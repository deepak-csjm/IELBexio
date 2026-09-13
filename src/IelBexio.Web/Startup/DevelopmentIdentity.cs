using System.Security.Claims;
using IelBexio.Application.Abstractions;
using IelBexio.Domain.Invoicing;
using IelBexio.Infrastructure.Persistence;
using IelBexio.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace IelBexio.Web.Startup;

/// <summary>
/// Resolves the tenant and the acting user for each request, then makes them available to every
/// service in the scope.
/// <para>
/// <b>Authentication posture, stated plainly.</b> The specification calls for Microsoft Entra ID (§24),
/// and the application is written against <see cref="ICurrentUser"/> so that swapping in Entra is a
/// registration change rather than a rewrite. This POC ships a <em>development</em> identity provider
/// that reads a role from configuration or a header, because no Entra tenant is available here.
/// <b>It is not authentication and must never be enabled outside local development.</b> The middleware
/// refuses to start in Development mode unless the environment is Development, so the unsafe path
/// cannot be reached by accident in a deployed environment.
/// </para>
/// </summary>
public sealed class RequestContextMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public RequestContextMiddleware(RequestDelegate next, IConfiguration configuration, IWebHostEnvironment environment)
    {
        _next = next;
        _configuration = configuration;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context, AppDbContext db, AmbientTenantContext tenantContext, AmbientCurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(db);

        // The POC operates one tenant, but nothing in the domain assumes that (§23): the tenant is
        // resolved per request and everything downstream is filtered by it.
        db.SuppressTenantFilter = true;
        var tenant = await db.Tenants.OrderBy(t => t.CreatedAt).FirstOrDefaultAsync(context.RequestAborted);
        db.SuppressTenantFilter = false;

        if (tenant is not null)
        {
            tenantContext.Set(tenant.Id);
        }

        var mode = _configuration["Authentication:Mode"] ?? "Development";

        if (string.Equals(mode, "EntraId", StringComparison.OrdinalIgnoreCase)
            && context.User.Identity?.IsAuthenticated == true)
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? context.User.FindFirstValue("oid")
                         ?? context.User.Identity.Name
                         ?? "unknown";

            var displayName = context.User.FindFirstValue("name") ?? context.User.Identity.Name ?? userId;
            var roles = context.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();

            currentUser.Set(userId, displayName, roles);
        }
        else
        {
            if (!_environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "Authentication:Mode is 'Development' outside the Development environment. " +
                    "Configure Entra ID before deploying: the development identity is not authentication.");
            }

            // A header lets the demo switch roles to show that approval is genuinely role-gated.
            var role = context.Request.Headers["X-Demo-Role"].FirstOrDefault()
                       ?? _configuration["Authentication:DevelopmentRole"]
                       ?? AppRoles.Admin;

            var roles = string.Equals(role, AppRoles.Admin, StringComparison.OrdinalIgnoreCase)
                ? new[] { AppRoles.Admin, AppRoles.Reviewer, AppRoles.Approver, AppRoles.IntegrationManager }
                : [role];

            var userId = context.Request.Headers["X-Demo-User"].FirstOrDefault() ?? "demo@local.dev";
            currentUser.Set(userId, userId, roles);
        }

        await _next(context);
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
