using IelBexio.Application.Abstractions;
using IelBexio.Infrastructure;
using IelBexio.Infrastructure.Persistence;
using IelBexio.Infrastructure.Services;
using IelBexio.Domain.Invoicing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IelBexio.IntegrationTests.Support;

/// <summary>
/// Composes the real application services against a real PostgreSQL database.
/// <para>
/// Deliberately uses the production <c>AddIelBexio</c> composition root rather than hand-registering
/// test doubles. If these tests wired up their own object graph they would verify a configuration that
/// never ships. Only the outbox background loop is left out, so tests can drive it a batch at a time
/// instead of racing a timer.
/// </para>
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private TestHost(ServiceProvider provider, Guid tenantId)
    {
        _provider = provider;
        TenantId = tenantId;
    }

    public Guid TenantId { get; }

    public static async Task<TestHost> CreateAsync(
        PostgresFixture fixture,
        string databasePrefix,
        Dictionary<string, string?>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var connectionString = await fixture.CreateDatabaseAsync(databasePrefix);

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:Postgres"] = connectionString,
            ["Bexio:Mode"] = "Mock",
            ["Shopify:Mode"] = "Fixture",
            ["Shopify:FixtureDirectory"] = FixturePaths.Directory("shopify"),
            ["Amazon:Mode"] = "Fixture",
            ["Amazon:FixtureDirectory"] = FixturePaths.Directory("amazon"),
            ["Ai:Enabled"] = "false",
            ["BlobStorage:Provider"] = "Local",
            ["BlobStorage:LocalRootPath"] = Path.Combine(Path.GetTempPath(), "ielbexio-tests", Guid.NewGuid().ToString("n")),
            ["Outbox:Enabled"] = "false",
        };

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                settings[key] = value;
            }
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDataProtection();
        services.AddIelBexio(configuration);

        var provider = services.BuildServiceProvider();

        // Seed the tenant. Everything else in the system requires one, by design.
        var tenantId = Guid.CreateVersion7();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Integration Test Tenant", CountryCode = "CH", DefaultCurrency = "CHF" });
            await db.SaveChangesAsync();
        }

        return new TestHost(provider, tenantId);
    }

    /// <summary>
    /// Runs an operation in a fresh DI scope as a given user and role set, exactly as a web request
    /// would. Role-based refusals are therefore exercised by the same code path the application uses.
    /// </summary>
    public async Task<T> AsUserAsync<T>(string userId, string[] roles, Func<IServiceProvider, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<AmbientTenantContext>().Set(TenantId);
        sp.GetRequiredService<AmbientCurrentUser>().Set(userId, userId, roles);

        return await action(sp);
    }

    public Task AsUserAsync(string userId, string[] roles, Func<IServiceProvider, Task> action) =>
        AsUserAsync(userId, roles, async sp =>
        {
            await action(sp);
            return true;
        });

    /// <summary>Runs as an administrator; the common case for arranging test state.</summary>
    public Task<T> AsAdminAsync<T>(Func<IServiceProvider, Task<T>> action) =>
        AsUserAsync("admin@test.example", [AppRoles.Admin, AppRoles.Reviewer, AppRoles.Approver, AppRoles.IntegrationManager], action);

    public Task AsAdminAsync(Func<IServiceProvider, Task> action) =>
        AsUserAsync("admin@test.example", [AppRoles.Admin, AppRoles.Reviewer, AppRoles.Approver, AppRoles.IntegrationManager], action);

    /// <summary>Resolves a singleton (such as the Bexio mock) outside any scope.</summary>
    public TService GetSingleton<TService>() where TService : notnull => _provider.GetRequiredService<TService>();

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();
}

internal static class FixturePaths
{
    public static string Directory(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "fixtures", name);
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate fixtures/{name} above {AppContext.BaseDirectory}.");
    }
}
