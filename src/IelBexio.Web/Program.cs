using IelBexio.Infrastructure;
using IelBexio.Infrastructure.Persistence;
using IelBexio.Web.Api;
using IelBexio.Web.Components;
using IelBexio.Web.Startup;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration ------------------------------------------------------------------------------
// Secrets come from User Secrets locally and environment variables or Key Vault in Azure; none are
// ever committed (§24). appsettings.json carries structure and non-secret defaults only.
builder.Configuration.AddEnvironmentVariables("IELBEXIO_");

// ---- Services -----------------------------------------------------------------------------------
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

builder.Services.AddDataProtection();
builder.Services.AddIelBexio(builder.Configuration);
builder.Services.AddIelBexioWorker();

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

// ---- Identity -----------------------------------------------------------------------------------
// Tenant and roles travel on the authenticated principal rather than in per-request ambient state,
// because that is the only thing ASP.NET Core flows into a Blazor circuit. See DevelopmentIdentity.cs
// for what this costs and why the alternative was wrong.
builder.Services.AddHttpContextAccessor();

if (!builder.Environment.IsDevelopment())
{
    // Fail at start-up rather than per request. The development identity trusts a header, so a
    // deployed instance running on it would hand Admin to anyone who asked; refusing to boot is the
    // only honest behaviour until Entra ID (§24) is configured here.
    throw new InvalidOperationException(
        "No production identity provider is configured. This build ships only the development " +
        "identity, which is not authentication. Configure Microsoft Entra ID before running outside " +
        "the Development environment.");
}

builder.Services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
        DevelopmentAuthenticationHandler.SchemeName, configureOptions: null);

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// Scoped, not singleton: the DbContext factory it needs is itself scoped.
builder.Services.AddScoped<IClaimsTransformation, TenantClaimsTransformation>();

// A circuit's scope is never touched by request middleware, so it is seeded from the principal here.
builder.Services.AddScoped<CircuitHandler, CircuitContextHandler>();

// Readiness and liveness are separated deliberately (§27): liveness says the process is up, readiness
// says it can actually serve — which for this application means the database is reachable.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: ["live"])
    .AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

var app = builder.Build();

// ---- Database -----------------------------------------------------------------------------------
// Migrating at startup suits a POC and a single-instance deployment. It is called out in
// docs/deployment.md as something to move into a release step before multi-instance production use,
// because concurrent instances racing to migrate is a real failure mode.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

await TenantSeeder.EnsureTenantAsync(app.Services, app.Configuration);

// ---- Pipeline -----------------------------------------------------------------------------------
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePages();
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// Binds the authenticated principal onto the ambient tenant and actor for the request scope. The
// circuit equivalent is CircuitContextHandler; see DevelopmentIdentity.cs for why both are needed.
app.UseMiddleware<RequestContextMiddleware>();

// Correlates every log line for one request, so an invoice's lifecycle is traceable (§27).
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? context.TraceIdentifier;
    context.Response.Headers["X-Correlation-Id"] = correlationId;

    using (app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
    {
        await next();
    }
});

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapIelBexioApi();
app.MapOpenApi();

app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
});

app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});

await app.RunAsync();

/// <summary>Exposed so integration tests can host the application with WebApplicationFactory.</summary>
public partial class Program;
