using IelBexio.Application.Abstractions;
using IelBexio.Application.Sync;
using IelBexio.Domain.Sync;
using IelBexio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IelBexio.Infrastructure.Workflow;

/// <summary>
/// PostgreSQL-backed outbox store.
/// <para>
/// Claiming uses <c>SELECT … FOR UPDATE SKIP LOCKED</c>. That single clause is what lets several worker
/// instances share one outbox table without coordination: each transaction takes rows nobody else holds
/// and skips the rest instead of blocking on them. The alternative — a status flag updated without row
/// locks — races, and two workers can post the same invoice.
/// </para>
/// <para>
/// Leases are belt-and-braces on top: if a worker dies mid-flight its row stays claimed, so
/// <see cref="ReclaimExpiredLeasesAsync"/> returns it to the pool once the lease expires.
/// </para>
/// </summary>
public sealed class OutboxStore : IOutboxStore
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public OutboxStore(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimDueMessagesAsync(
        string workerId, int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var leaseExpiry = now.Add(leaseDuration);

        // A single statement that selects, locks and claims. Doing this as a read followed by a write
        // would leave a window in which another worker claims the same rows.
        var claimed = await _db.OutboxMessages
            .FromSqlInterpolated($"""
                UPDATE outbox_messages
                SET status = {(int)OutboxStatus.InProgress},
                    leased_by = {workerId},
                    lease_expires_at = {leaseExpiry}
                WHERE id IN (
                    SELECT id FROM outbox_messages
                    WHERE status = {(int)OutboxStatus.Pending}
                      AND next_attempt_at <= {now}
                    ORDER BY next_attempt_at
                    LIMIT {batchSize}
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING *
                """)
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);

        return claimed;
    }

    public async Task MarkProcessedAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        await _db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxStatus.Processed)
                .SetProperty(m => m.ProcessedAt, _clock.UtcNow)
                .SetProperty(m => m.LeasedBy, (string?)null)
                .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(m => m.UpdatedAt, _clock.UtcNow),
                cancellationToken);
    }

    public async Task MarkFailedAsync(
        Guid messageId, string error, SyncErrorCategory category, DateTimeOffset? nextAttemptAt, bool deadLetter,
        CancellationToken cancellationToken = default)
    {
        var truncated = error.Length > 4000 ? error[..4000] : error;

        await _db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, deadLetter ? OutboxStatus.DeadLettered : OutboxStatus.Pending)
                .SetProperty(m => m.AttemptCount, m => m.AttemptCount + 1)
                .SetProperty(m => m.LastError, truncated)
                .SetProperty(m => m.LastErrorCategory, category)
                .SetProperty(m => m.NextAttemptAt, nextAttemptAt ?? _clock.UtcNow)
                .SetProperty(m => m.LeasedBy, (string?)null)
                .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(m => m.UpdatedAt, _clock.UtcNow),
                cancellationToken);
    }

    public async Task<int> ReclaimExpiredLeasesAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;

        return await _db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.Status == OutboxStatus.InProgress && m.LeaseExpiresAt != null && m.LeaseExpiresAt < now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxStatus.Pending)
                .SetProperty(m => m.LeasedBy, (string?)null)
                .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(m => m.UpdatedAt, now),
                cancellationToken);
    }
}
