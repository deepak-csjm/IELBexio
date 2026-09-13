using System.Diagnostics;
using Microsoft.Playwright;
using Npgsql;

namespace IelBexio.E2ETests.Support;

/// <summary>
/// Starts the real application against a real PostgreSQL database and gives tests a real browser.
/// <para>
/// Deliberately launches the published host as a separate process rather than using
/// <c>WebApplicationFactory</c>: an in-process test server would not exercise static asset serving,
/// the Blazor circuit over a real WebSocket, or the background worker on its own timer — which are
/// exactly the things a UI test is for. If the app cannot start for real, these tests should fail.
/// </para>
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    private Process? _app;
    private IPlaywright? _playwright;
    private string _databaseName = string.Empty;

    public IBrowser Browser { get; private set; } = null!;
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>Where a failing test drops its screenshot, so a CI failure is diagnosable.</summary>
    public string ArtifactDirectory { get; private set; } = string.Empty;

    /// <summary>The configuration these tests were built in, so the application is run in the same one.</summary>
    private static string BuildConfiguration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("IELBEXIO_TEST_POSTGRES")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    public async Task InitializeAsync()
    {
        var repoRoot = FindRepositoryRoot();
        ArtifactDirectory = Path.Combine(repoRoot, "artifacts", "e2e");
        Directory.CreateDirectory(ArtifactDirectory);

        _databaseName = $"ielbexio_e2e_{Guid.NewGuid():n}"[..40];

        await using (var admin = new NpgsqlConnection(AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = _databaseName };

        // A per-fixture port, so a developer's own running instance does not collide with the tests.
        var port = 5300 + Random.Shared.Next(1, 400);
        BaseUrl = $"http://127.0.0.1:{port}";

        _app = StartApplication(repoRoot, builder.ConnectionString, BaseUrl);

        await WaitForReadyAsync();

        _playwright = await Playwright.CreateAsync();

        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            // Null means "use the build Playwright downloaded for itself", which is the normal case.
            // It is only non-null where the machine has a pre-installed Chromium of a different build,
            // as the container images used here do.
            ExecutablePath = ResolvePreinstalledChromium(),
            Args = ["--no-sandbox", "--disable-dev-shm-usage"],
        });
    }

    /// <summary>
    /// Finds a Chromium that is already on the machine, when it is not the exact build this version of
    /// Playwright downloads for itself.
    /// <para>
    /// Sandboxed build environments frequently pre-install one Chromium and forbid the download of any
    /// other, so Playwright's own resolution fails on a build-number mismatch even though a perfectly
    /// usable browser is present. Returning <see langword="null"/> — the common case on a developer
    /// machine and in CI that runs <c>playwright install</c> — leaves Playwright's resolution untouched.
    /// </para>
    /// </summary>
    private static string? ResolvePreinstalledChromium()
    {
        var explicitPath = Environment.GetEnvironmentVariable("IELBEXIO_E2E_CHROMIUM");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return File.Exists(explicitPath)
                ? explicitPath
                : throw new FileNotFoundException(
                    $"IELBEXIO_E2E_CHROMIUM points at '{explicitPath}', which does not exist.", explicitPath);
        }

        var browsersPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (string.IsNullOrWhiteSpace(browsersPath) || !Directory.Exists(browsersPath))
        {
            return null;
        }

        // Highest build number first: if several are present, the newest is the closest match to the
        // driver, which is what compatibility depends on.
        var candidates = Directory.EnumerateDirectories(browsersPath, "chromium-*")
            .OrderByDescending(directory => directory, StringComparer.Ordinal)
            .Select(directory => Path.Combine(directory, "chrome-linux", "chrome"))
            .Where(File.Exists);

        return candidates.FirstOrDefault();
    }

    private static Process StartApplication(string repoRoot, string connectionString, string baseUrl)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.Combine(repoRoot, "src", "IelBexio.Web"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--no-launch-profile");
        startInfo.ArgumentList.Add("--no-build");

        // Must match how the solution was built, or --no-build finds nothing. CI builds Release.
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(BuildConfiguration);

        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_URLS"] = baseUrl;
        startInfo.Environment["IELBEXIO_ConnectionStrings__Postgres"] = connectionString;
        startInfo.Environment["IELBEXIO_Shopify__FixtureDirectory"] = Path.Combine(repoRoot, "fixtures", "shopify");
        startInfo.Environment["IELBEXIO_Amazon__FixtureDirectory"] = Path.Combine(repoRoot, "fixtures", "amazon");
        startInfo.Environment["IELBEXIO_BlobStorage__LocalRootPath"] = Path.Combine(Path.GetTempPath(), $"ielbexio-e2e-{Guid.NewGuid():n}");
        startInfo.Environment["IELBEXIO_Logging__LogLevel__Default"] = "Warning";

        // A short poll interval so a test does not wait seconds for the worker to notice the outbox.
        startInfo.Environment["IELBEXIO_Outbox__PollIntervalSeconds"] = "1";

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the application.");

        // Drained so the child never blocks on a full pipe, which would hang the whole test run.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    private async Task WaitForReadyAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (_app?.HasExited == true)
            {
                throw new InvalidOperationException($"The application exited with code {_app.ExitCode} before becoming ready.");
            }

            try
            {
                var response = await client.GetAsync(new Uri($"{BaseUrl}/health/ready"));
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (TaskCanceledException)
            {
                // Slow first response.
            }

            await Task.Delay(500);
        }

        throw new InvalidOperationException($"The application at {BaseUrl} did not become ready.");
    }

    /// <summary>A fresh browser context per test, so cookies and circuits never leak between tests.</summary>
    public async Task<IPage> NewPageAsync()
    {
        var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            ViewportSize = new ViewportSize { Width = 1600, Height = 1000 },
            IgnoreHTTPSErrors = true,
        });

        return await context.NewPageAsync();
    }

    /// <summary>
    /// Runs one browser interaction, and on failure saves a screenshot and the page's HTML.
    /// <para>
    /// A UI test that fails in CI with nothing but a selector timeout is close to useless; the two
    /// artifacts this writes are what turn such a failure into a five-minute diagnosis.
    /// </para>
    /// </summary>
    public async Task WithPageAsync(string name, Func<IPage, Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var page = await NewPageAsync();

        try
        {
            await body(page);
        }
        catch
        {
            await CaptureAsync(page, name);
            throw;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>
    /// Waits until the page's controls are wired to a live circuit.
    /// <para>
    /// Without this a test can click a pre-rendered button before its event handler exists and see
    /// nothing happen — the same trap a real user falls into, which is why the application marks the
    /// state rather than the test guessing at it (see ActionGate.razor).
    /// </para>
    /// </summary>
    public static Task WaitUntilInteractiveAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return page.WaitForSelectorAsync("[data-interactive='true']");
    }

    /// <summary>Saves a screenshot and the rendered HTML under the artifact directory.</summary>
    public async Task CaptureAsync(IPage page, string name)
    {
        ArgumentNullException.ThrowIfNull(page);

        try
        {
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(ArtifactDirectory, $"{name}.png"),
                FullPage = true,
            });

            await File.WriteAllTextAsync(Path.Combine(ArtifactDirectory, $"{name}.html"), await page.ContentAsync());
        }
        catch (PlaywrightException)
        {
            // Capturing diagnostics must never replace the real failure with one of its own.
        }
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.CloseAsync();
        }

        _playwright?.Dispose();

        if (_app is { HasExited: false })
        {
            _app.Kill(entireProcessTree: true);
            await _app.WaitForExitAsync();
        }

        _app?.Dispose();

        if (!string.IsNullOrEmpty(_databaseName))
        {
            try
            {
                await using var admin = new NpgsqlConnection(AdminConnectionString);
                await admin.OpenAsync();
                await using var command = admin.CreateCommand();
                command.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
                await command.ExecuteNonQueryAsync();
            }
            catch (NpgsqlException)
            {
                // A leftover test database is untidy, not a test failure.
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "IelBexio.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate the repository root above {AppContext.BaseDirectory}.");
    }
}

/// <summary>
/// Runs every test in this assembly one at a time.
/// <para>
/// Each test gets its own application and its own database, which is only safe because they do not
/// run concurrently: several copies of the app fighting over ports and PostgreSQL connections would
/// produce failures that have nothing to do with the code under test.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialE2E
{
    public const string Name = "e2e";
}
