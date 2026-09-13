using IelBexio.Application.Abstractions;
using IelBexio.Application.Invoices;
using IelBexio.Application.Sync;
using IelBexio.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IelBexio.Infrastructure.Workflow;

/// <summary>
/// Runs the outbox loop in the background.
/// <para>
/// Two things worth noting. First, the loop creates a fresh DI scope per batch: the DbContext is
/// scoped, and holding one for the lifetime of a long-running service would accumulate tracked entities
/// and stale data. Second, it never lets an exception escape — a background service that crashes stops
/// processing the queue silently, which is the worst possible failure for a financial dispatcher.
/// </para>
/// </summary>
public sealed class OutboxHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxHostedService> _logger;

    public OutboxHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<OutboxOptions> options,
        ILogger<OutboxHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("The outbox dispatcher is disabled by configuration.");
            return;
        }

        _logger.LogInformation("The outbox dispatcher has started, polling every {Interval}.", _options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Log and continue. The queue must keep draining even after an unexpected fault.
                _logger.LogError(ex, "The outbox dispatcher encountered an unexpected error; continuing.");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("The outbox dispatcher has stopped.");
    }

    private async Task ProcessOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var provider = scope.ServiceProvider;

        // The worker is a system actor, not a user. Naming it explicitly keeps the audit trail honest
        // about who did what.
        if (provider.GetService<ICurrentUser>() is AmbientCurrentUser user)
        {
            user.Set("system:outbox-dispatcher", "Outbox dispatcher", [AppRoles.Admin]);
        }

        // The dispatcher spans tenants by design, so it suppresses the tenant filter and each message
        // carries the tenant its work belongs to.
        var db = provider.GetRequiredService<Persistence.AppDbContext>();
        db.SuppressTenantFilter = true;

        var tenantContext = provider.GetService<AmbientTenantContext>();

        var processor = new OutboxProcessor(
            provider.GetRequiredService<IOutboxStore>(),
            provider.GetRequiredService<IInvoiceSynchronizationService>(),
            _options,
            provider.GetRequiredService<IClock>(),
            applyTenant: tenantId => tenantContext?.Set(tenantId));

        var result = await processor.ProcessBatchAsync(cancellationToken);

        if (result.Claimed > 0)
        {
            _logger.LogInformation(
                "Outbox batch: claimed {Claimed}, processed {Processed}, retryable failures {Failed}, dead-lettered {DeadLettered}.",
                result.Claimed, result.Processed, result.Failed, result.DeadLettered);
        }
    }
}
