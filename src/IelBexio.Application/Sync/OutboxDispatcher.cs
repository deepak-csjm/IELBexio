using System.Text.Json;
using IelBexio.Application.Abstractions;
using IelBexio.Application.Invoices;
using IelBexio.Domain.Sync;

namespace IelBexio.Application.Sync;

/// <summary>The payload of a <see cref="OutboxMessageTypes.SyncInvoiceToBexio"/> message.</summary>
public sealed record SyncInvoicePayload(Guid InvoiceId, Guid TenantId, string CorrelationId);

/// <summary>
/// Storage operations the dispatcher needs. Kept as a port so the dispatcher's retry and backoff logic
/// is testable without a database, while the real implementation uses
/// <c>SELECT … FOR UPDATE SKIP LOCKED</c> so multiple workers can run without stepping on each other.
/// </summary>
public interface IOutboxStore
{
    /// <summary>Claims up to <paramref name="batchSize"/> due messages for this worker, leasing them.</summary>
    Task<IReadOnlyList<OutboxMessage>> ClaimDueMessagesAsync(string workerId, int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    Task MarkProcessedAsync(Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>Records a failed attempt and schedules the next one, or dead-letters the message.</summary>
    Task MarkFailedAsync(Guid messageId, string error, SyncErrorCategory category, DateTimeOffset? nextAttemptAt, bool deadLetter, CancellationToken cancellationToken = default);

    /// <summary>Releases leases whose worker died, so the messages become claimable again.</summary>
    Task<int> ReclaimExpiredLeasesAsync(CancellationToken cancellationToken = default);
}

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public int BatchSize { get; set; } = 10;

    /// <summary>How long a worker holds a claimed message before another worker may reclaim it.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Attempts before a message is dead-lettered for manual intervention (§20).</summary>
    public int MaxAttempts { get; set; } = 5;

    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Enables the dispatcher hosted service. Off in tests that drive it manually.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Computes retry delays. Exponential with full jitter (§20).
/// <para>
/// Full jitter rather than a fixed exponential: when a rate limit or an outage trips many messages at
/// once, an unjittered backoff makes them all retry at the same instant and re-trip the limit. Spreading
/// them is the difference between recovering and oscillating.
/// </para>
/// </summary>
public static class RetryBackoff
{
    public static TimeSpan Compute(int attemptNumber, OutboxOptions options, TimeSpan? retryAfter = null, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        // A server that tells us when to come back is obeyed rather than second-guessed.
        if (retryAfter is { } explicitDelay && explicitDelay > TimeSpan.Zero)
        {
            return explicitDelay > options.MaxBackoff ? options.MaxBackoff : explicitDelay;
        }

        var exponent = Math.Min(attemptNumber, 16);
        var ceiling = TimeSpan.FromMilliseconds(options.BaseBackoff.TotalMilliseconds * Math.Pow(2, exponent - 1));

        if (ceiling > options.MaxBackoff)
        {
            ceiling = options.MaxBackoff;
        }

        var rng = random ?? Random.Shared;
        var jittered = rng.NextDouble() * ceiling.TotalMilliseconds;

        // A floor keeps a "retry" from being indistinguishable from an immediate retry loop.
        return TimeSpan.FromMilliseconds(Math.Max(jittered, options.BaseBackoff.TotalMilliseconds / 2));
    }
}

/// <summary>
/// Processes outbox messages: claim, dispatch, and either mark processed or schedule the next attempt.
/// <para>
/// The hosted service is a thin loop around <see cref="ProcessBatchAsync"/> so tests can drive a single
/// batch deterministically rather than racing a background timer.
/// </para>
/// </summary>
public sealed class OutboxProcessor
{
    private readonly IOutboxStore _store;
    private readonly IInvoiceSynchronizationService _sync;
    private readonly OutboxOptions _options;
    private readonly IClock _clock;

    public OutboxProcessor(IOutboxStore store, IInvoiceSynchronizationService sync, OutboxOptions options, IClock clock)
    {
        _store = store;
        _sync = sync;
        _options = options;
        _clock = clock;
    }

    /// <summary>Identifies this worker in message leases, so a crashed worker's messages can be reclaimed.</summary>
    public string WorkerId { get; init; } = $"{Environment.MachineName}:{Environment.ProcessId}";

    public async Task<OutboxBatchResult> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        await _store.ReclaimExpiredLeasesAsync(cancellationToken);

        var messages = await _store.ClaimDueMessagesAsync(WorkerId, _options.BatchSize, _options.LeaseDuration, cancellationToken);

        var processed = 0;
        var failed = 0;
        var deadLettered = 0;

        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(message.MessageType, OutboxMessageTypes.SyncInvoiceToBexio, StringComparison.Ordinal))
            {
                // A closed set of message types: an unknown type is dead-lettered rather than guessed at.
                await _store.MarkFailedAsync(message.Id, $"Unknown outbox message type '{message.MessageType}'.", SyncErrorCategory.Permanent, null, true, cancellationToken);
                deadLettered++;
                continue;
            }

            SyncInvoicePayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<SyncInvoicePayload>(message.Payload);
            }
            catch (JsonException ex)
            {
                await _store.MarkFailedAsync(message.Id, $"Unreadable payload: {ex.Message}", SyncErrorCategory.Permanent, null, true, cancellationToken);
                deadLettered++;
                continue;
            }

            if (payload is null)
            {
                await _store.MarkFailedAsync(message.Id, "Empty payload.", SyncErrorCategory.Permanent, null, true, cancellationToken);
                deadLettered++;
                continue;
            }

            var result = await _sync.SynchronizeAsync(payload.InvoiceId, message.CorrelationId, cancellationToken);

            if (result.Succeeded)
            {
                await _store.MarkProcessedAsync(message.Id, cancellationToken);
                processed++;
                continue;
            }

            var category = Enum.TryParse<SyncErrorCategory>(result.ErrorCode, out var parsed) ? parsed : SyncErrorCategory.Unknown;
            var attempt = message.AttemptCount + 1;

            // Two independent reasons to stop retrying: the error class says retrying cannot help, or
            // we have simply tried enough times. Both end in a dead letter requiring a human, never in
            // an endless loop (§20).
            var retryable = SyncErrorPolicy.IsRetryable(category) && attempt < _options.MaxAttempts;

            if (retryable)
            {
                var delay = RetryBackoff.Compute(attempt, _options);
                await _store.MarkFailedAsync(message.Id, result.ErrorMessage ?? "Synchronisation failed.", category, _clock.UtcNow.Add(delay), false, cancellationToken);
                failed++;
            }
            else
            {
                await _store.MarkFailedAsync(message.Id, result.ErrorMessage ?? "Synchronisation failed.", category, null, true, cancellationToken);
                deadLettered++;
            }
        }

        return new OutboxBatchResult(messages.Count, processed, failed, deadLettered);
    }
}

public sealed record OutboxBatchResult(int Claimed, int Processed, int Failed, int DeadLettered);
