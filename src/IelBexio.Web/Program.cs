using IelBexio.Infrastructure;
using IelBexio.Infrastructure.Persistence;
using IelBexio.Web.Api;
using IelBexio.Web.Components;
using IelBexio.Web.Startup;
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
app.UseAntiforgery();

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

app.UseMiddleware<RequestContextMiddleware>();

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
