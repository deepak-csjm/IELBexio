using IelBexio.Ai.Gateway;
using IelBexio.Ai.Services;
using IelBexio.Application.Abstractions;
using IelBexio.Application.Ai;
using IelBexio.Application.Bexio;
using IelBexio.Application.Documents;
using IelBexio.Application.Invoices;
using IelBexio.Application.Mapping;
using IelBexio.Application.Sources;
using IelBexio.Application.Sync;
using IelBexio.Application.Tax;
using IelBexio.Connectors.Amazon;
using IelBexio.Connectors.Amazon.Api;
using IelBexio.Connectors.Amazon.Fixtures;
using IelBexio.Connectors.Amazon.Normalization;
using IelBexio.Connectors.Bexio.Api;
using IelBexio.Connectors.Bexio.Auth;
using IelBexio.Connectors.Bexio.Configuration;
using Conformance = IelBexio.Connectors.Bexio.Conformance;
using IelBexio.Connectors.Bexio.Mock;
using IelBexio.Connectors.Shopify;
using IelBexio.Connectors.Shopify.Api;
using IelBexio.Connectors.Shopify.Fixtures;
using IelBexio.Connectors.Shopify.Normalization;
using IelBexio.Infrastructure.Blob;
using IelBexio.Infrastructure.Persistence;
using IelBexio.Infrastructure.Services;
using IelBexio.Infrastructure.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IelBexio.Infrastructure;

/// <summary>
/// The composition root. Every mode switch in the system lives here, which is what makes
/// "switch between Mock and Sandbox without changing domain logic" (§5) literally true: the domain
/// depends on interfaces, and this file decides which implementation satisfies them.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIelBexio(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<BexioOptions>().Bind(configuration.GetSection(BexioOptions.SectionName)).ValidateOnStart();
        services.Configure<MockBexioOptions>(configuration.GetSection(MockBexioOptions.SectionName));
        services.Configure<ShopifyOptions>(configuration.GetSection(ShopifyOptions.SectionName));
        services.Configure<AmazonOptions>(configuration.GetSection(AmazonOptions.SectionName));
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));
        services.Configure<BlobStorageOptions>(configuration.GetSection(BlobStorageOptions.SectionName));
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));
        services.Configure<FileSecurityOptions>(configuration.GetSection(FileSecurityOptions.SectionName));

        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<AmbientTenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<AmbientTenantContext>());
        services.AddScoped<AmbientCurrentUser>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<AmbientCurrentUser>());
        services.AddScoped<ICorrelationContext, AmbientCorrelationContext>();

        services.AddDbContext<AppDbContext>((sp, options) =>
            options.UseNpgsql(
                configuration.GetConnectionString("Postgres")
                    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required."),
                npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName)));

        // Blazor components outlive a single request, so they resolve their own short-lived context
        // from a factory rather than sharing the request-scoped one — which would otherwise be disposed
        // under them, or mutated concurrently by two renders of the same circuit.
        //
        // The factory is Scoped rather than Singleton on purpose: AddDbContext above registers
        // DbContextOptions as scoped, and a singleton factory cannot consume a scoped dependency. In a
        // Blazor Server circuit the scope lives as long as the circuit, which is exactly the lifetime a
        // component needs. Contexts the factory creates receive no ITenantContext, so every component
        // filters by tenant explicitly rather than relying on the global filter.
        services.AddDbContextFactory<AppDbContext>((sp, options) =>
            options.UseNpgsql(
                configuration.GetConnectionString("Postgres")
                    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required."),
                npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName)),
            lifetime: ServiceLifetime.Scoped);

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IProvenanceWriter, ProvenanceWriter>();

        AddBlobStorage(services, configuration);
        AddBexio(services, configuration);
        AddSources(services, configuration);
        AddAi(services, configuration);

        // Deterministic services. Constructed from options rather than resolved per call, since they
        // are pure and stateless.
        services.AddSingleton(sp => new TaxRuleTable());
        services.AddSingleton(sp => new TaxDeterminationService(sp.GetRequiredService<TaxRuleTable>()));
        services.AddSingleton(sp => new InvoiceValidator());
        services.AddSingleton(sp => new FileInspector(sp.GetRequiredService<IOptions<FileSecurityOptions>>().Value));
        services.AddSingleton<IDeterministicDocumentExtractor, JsonInvoiceExtractor>();
        services.AddSingleton<IDeterministicDocumentExtractor, CsvInvoiceExtractor>();

        services.AddScoped<IMappingService, MappingService>();
        services.AddScoped<IBexioReferenceSyncService, BexioReferenceSyncService>();
        services.AddScoped<BexioPreflightService>();
        services.AddScoped<IImportService, ImportService>();
        services.AddScoped<IInvoiceProcessingService, InvoiceProcessingService>();
        services.AddScoped<IDocumentIngestionService, DocumentIngestionService>();
        services.AddScoped<IReviewService, ReviewService>();
        services.AddScoped<IApprovalService, ApprovalService>();
        services.AddScoped<IInvoiceSynchronizationService, InvoiceSynchronizationService>();
        services.AddScoped<IReconciliationService, ReconciliationService>();
        services.AddScoped<IOutboxStore, OutboxStore>();
        services.AddScoped<ISourceConnectorRegistry, SourceConnectorRegistry>();

        return services;
    }

    /// <summary>Registers the outbox worker. Separated so tests can compose services without a background loop.</summary>
    public static IServiceCollection AddIelBexioWorker(this IServiceCollection services)
    {
        services.AddHostedService<OutboxHostedService>();
        return services;
    }

    private static void AddBlobStorage(IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration[$"{BlobStorageOptions.SectionName}:Provider"] ?? "Local";

        if (string.Equals(provider, "Azure", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IBlobStore, AzureBlobStore>();
        }
        else
        {
            services.AddSingleton<IBlobStore, LocalFileBlobStore>();
        }
    }

    private static void AddBexio(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IBexioConnectionStore, BexioConnectionStore>();
        services.AddScoped<ISecretProtector, DataProtectionSecretProtector>();

        services.AddHttpClient<BexioTokenService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddScoped<IBexioTokenProvider>(sp => sp.GetRequiredService<BexioTokenService>());

        // The conformance check works against whichever IBexioClient is configured. Run against the
        // mock it proves the harness itself; run against the API it verifies the real assumptions.
        services.AddScoped(sp => new Conformance.BexioConformanceCheck(
            sp.GetRequiredService<IBexioClient>(),
            sp.GetRequiredService<IBexioTokenProvider>(),
            sp.GetRequiredService<IOptions<BexioOptions>>().Value,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Conformance.BexioConformanceCheck>>()));
        services.AddScoped<IBexioAuthorizationService>(sp => sp.GetRequiredService<BexioTokenService>());

        var mode = configuration[$"{BexioOptions.SectionName}:Mode"] ?? nameof(BexioMode.Mock);

        if (string.Equals(mode, nameof(BexioMode.Api), StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<BexioApiClient>((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<BexioOptions>>().Value;
                client.Timeout = options.RequestTimeout;
            });

            services.AddScoped<IBexioClient>(sp => sp.GetRequiredService<BexioApiClient>());
        }
        else
        {
            // The mock is a singleton so its created-invoice state survives across scopes, which is
            // what lets a demo and an integration test observe "no duplicate was created".
            services.AddSingleton<MockBexioClient>();
            services.AddSingleton<IBexioClient>(sp => sp.GetRequiredService<MockBexioClient>());
        }
    }

    private static void AddSources(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ShopifyOrderNormalizer>();
        services.AddSingleton<AmazonOrderNormalizer>();

        var shopifyMode = configuration[$"{ShopifyOptions.SectionName}:Mode"] ?? nameof(ShopifyMode.Fixture);
        if (string.Equals(shopifyMode, nameof(ShopifyMode.Live), StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<ShopifyGraphQlConnector>();
            services.AddScoped<ISourceConnector>(sp => sp.GetRequiredService<ShopifyGraphQlConnector>());
        }
        else
        {
            services.AddScoped<FixtureShopifyConnector>();
            services.AddScoped<ISourceConnector>(sp => sp.GetRequiredService<FixtureShopifyConnector>());
        }

        var amazonMode = configuration[$"{AmazonOptions.SectionName}:Mode"] ?? nameof(AmazonMode.Fixture);
        if (string.Equals(amazonMode, nameof(AmazonMode.Live), StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<AmazonSpApiConnector>();
            services.AddScoped<ISourceConnector>(sp => sp.GetRequiredService<AmazonSpApiConnector>());
        }
        else
        {
            services.AddScoped<FixtureAmazonConnector>();
            services.AddScoped<ISourceConnector>(sp => sp.GetRequiredService<FixtureAmazonConnector>());
        }
    }

    private static void AddAi(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IAiUsageLedger, AiUsageLedger>();

        var enabled = bool.TryParse(configuration[$"{AiOptions.SectionName}:Enabled"], out var parsed) && parsed;
        var configured = !string.IsNullOrWhiteSpace(configuration[$"{AiOptions.SectionName}:Endpoint"])
                         && !string.IsNullOrWhiteSpace(configuration[$"{AiOptions.SectionName}:Deployment"]);

        if (enabled && configured)
        {
            services.AddSingleton<IAiCompletionClient, AzureOpenAiCompletionClient>();
            services.AddScoped(sp => new AiGuard(sp.GetRequiredService<IOptions<AiOptions>>().Value, sp.GetRequiredService<IAiUsageLedger>()));
            services.AddScoped<IAiService, AiGateway>();
        }
        else
        {
            // AI off by default. The rest of the application is unaware, because it depends only on
            // IAiService (acceptance criterion 9).
            services.AddScoped<IAiService, DisabledAiService>();
        }

        services.AddScoped<IAiDocumentExtractor, AiDocumentExtractor>();
        services.AddScoped<IAiClassificationService, AiClassificationService>();
        services.AddScoped<IAiMappingSuggestionService, AiMappingSuggestionService>();
        services.AddScoped<IAiAnomalyService, AiAnomalyService>();
    }
}
