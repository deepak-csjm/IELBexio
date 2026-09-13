using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using IelBexio.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IelBexio.IntegrationTests.Support;

/// <summary>
/// Provides a real PostgreSQL database for integration tests.
/// <para>
/// <b>Why this is not simply Testcontainers.</b> The specification asks for Testcontainers, and it is
/// supported and preferred here. But it requires pulling <c>postgres:*</c> from a container registry,
/// and in some environments — including the one this POC was built in — registry image blobs are
/// blocked by egress policy. Rather than let that turn into "integration tests are skipped", the
/// fixture resolves a database in order of preference:
/// </para>
/// <list type="number">
/// <item><description><c>IELBEXIO_TEST_POSTGRES</c>, if set — an explicitly supplied server.</description></item>
/// <item><description>Testcontainers, if Docker can start a PostgreSQL container.</description></item>
/// <item><description>A PostgreSQL already listening on localhost:5432.</description></item>
/// </list>
/// <para>
/// Every path runs the same migrations against a real PostgreSQL. Nothing here falls back to an
/// in-memory provider: these tests exist specifically to verify behaviour that only a real database
/// has — unique constraints, <c>numeric</c> arithmetic, transactions and <c>SKIP LOCKED</c>.
/// </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _adminConnectionString;

    /// <summary>How the database was obtained, reported in the test output so nobody has to guess.</summary>
    public string ProvisioningMode { get; private set; } = "(not provisioned)";

    public string ConnectionString => _adminConnectionString
        ?? throw new InvalidOperationException("The PostgreSQL fixture has not been initialised.");

    public async Task InitializeAsync()
    {
        var explicitConnection = Environment.GetEnvironmentVariable("IELBEXIO_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(explicitConnection) && await CanConnectAsync(explicitConnection))
        {
            _adminConnectionString = explicitConnection;
            ProvisioningMode = "IELBEXIO_TEST_POSTGRES";
            return;
        }

        if (await TryStartContainerAsync())
        {
            return;
        }

        const string local = "Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=postgres";
        if (await CanConnectAsync(local))
        {
            _adminConnectionString = local;
            ProvisioningMode = "local PostgreSQL on 127.0.0.1:5432";
            return;
        }

        throw new InvalidOperationException(
            "No PostgreSQL is available for integration tests. Set IELBEXIO_TEST_POSTGRES to a connection " +
            "string, ensure Docker can pull postgres:16-alpine, or run a local PostgreSQL on port 5432.");
    }

    private async Task<bool> TryStartContainerAsync()
    {
        try
        {
            var container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("ielbexio_tests")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await container.StartAsync(timeout.Token);

            _container = container;
            _adminConnectionString = container.GetConnectionString();
            ProvisioningMode = "Testcontainers (postgres:16-alpine)";
            return true;
        }
        catch (Exception ex)
        {
            // Deliberately swallowed and reported: an unavailable container runtime is an environment
            // fact, not a test failure, and the fixture has other ways to get a database.
            Debug.WriteLine($"Testcontainers is unavailable ({ex.GetType().Name}: {ex.Message}); falling back.");
            return false;
        }
    }

    private static async Task<bool> CanConnectAsync(string connectionString)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await connection.OpenAsync(timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a uniquely named database and applies migrations to it. Every test class gets its own
    /// database so tests never see each other's rows and can run in parallel.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string namePrefix)
    {
        var databaseName = $"{namePrefix}_{Guid.NewGuid():n}"[..Math.Min(60, namePrefix.Length + 33)].ToLowerInvariant();

        await using (var admin = new NpgsqlConnection(ConnectionString))
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = databaseName };
        var connectionString = builder.ConnectionString;

        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
        await using var context = new AppDbContext(options);
        await context.Database.MigrateAsync();

        return connectionString;
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>Shares one PostgreSQL server across every integration test class.</summary>
[CollectionDefinition(Name)]
public sealed class SharedPostgresServer : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
