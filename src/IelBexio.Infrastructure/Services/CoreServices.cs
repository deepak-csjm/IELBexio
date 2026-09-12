using System.Text.Json;
using IelBexio.Application.Abstractions;
using IelBexio.Domain.Audit;
using IelBexio.Domain.Common;
using IelBexio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IelBexio.Infrastructure.Services;

/// <summary>Ambient tenant, settable once per scope by the request pipeline or a worker.</summary>
public sealed class AmbientTenantContext : ITenantContext
{
    private Guid _tenantId;

    public Guid TenantId => _tenantId;
    public bool IsResolved => _tenantId != Guid.Empty;

    public void Set(Guid tenantId) => _tenantId = tenantId;
}

/// <summary>Current actor. Replaced by an Entra-backed implementation when authentication is enabled.</summary>
public sealed class AmbientCurrentUser : ICurrentUser
{
    private string[] _roles = [];

    public string UserId { get; private set; } = "system";
    public string DisplayName { get; private set; } = "System";

    public bool IsInRole(string role) => _roles.Contains(role, StringComparer.OrdinalIgnoreCase);

    public void Set(string userId, string displayName, IEnumerable<string> roles)
    {
        UserId = userId;
        DisplayName = displayName;
        _roles = roles.ToArray();
    }
}

public sealed class AmbientCorrelationContext : ICorrelationContext
{
    private readonly Stack<string> _stack = new();

    public string CorrelationId => _stack.Count > 0 ? _stack.Peek() : _root;

    private readonly string _root = Guid.CreateVersion7().ToString("n");

    public IDisposable Push(string correlationId)
    {
        _stack.Push(correlationId);
        return new Pop(this);
    }

    private sealed class Pop : IDisposable
    {
        private readonly AmbientCorrelationContext _owner;
        private bool _disposed;

        public Pop(AmbientCorrelationContext owner) => _owner = owner;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_owner._stack.Count > 0)
            {
                _owner._stack.Pop();
            }
        }
    }
}

/// <summary>Serialisation settings shared by audit, provenance and proposal payloads.</summary>
internal static class AuditJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static string? Serialize(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value, Options);
}

/// <summary>Writes audit events. Append-only by construction: nothing here updates or deletes.</summary>
public sealed class AuditWriter : IAuditWriter
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;
    private readonly ICurrentUser _user;
    private readonly ICorrelationContext _correlation;

    public AuditWriter(AppDbContext db, IClock clock, ICurrentUser user, ICorrelationContext correlation)
    {
        _db = db;
        _clock = clock;
        _user = user;
        _correlation = correlation;
    }

    public async Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        if (auditEvent.OccurredAt == default)
        {
            auditEvent.OccurredAt = _clock.UtcNow;
        }

        if (string.IsNullOrEmpty(auditEvent.CorrelationId))
        {
            auditEvent.CorrelationId = _correlation.CorrelationId;
        }

        _db.AuditEvents.Add(auditEvent);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task WriteAsync(
        string action,
        string entityType,
        Guid? entityId,
        object? oldValue = null,
        object? newValue = null,
        string? reason = null,
        bool aiInvolved = false,
        Guid? aiProposalId = null,
        CancellationToken cancellationToken = default)
    {
        var evt = new AuditEvent
        {
            Actor = _user.UserId,
            ActorType = _user.UserId.StartsWith("system:", StringComparison.Ordinal) ? AuditActorType.System : AuditActorType.User,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            OldValueJson = AuditJson.Serialize(oldValue),
            NewValueJson = AuditJson.Serialize(newValue),
            Reason = reason,
            OccurredAt = _clock.UtcNow,
            CorrelationId = _correlation.CorrelationId,
            AiInvolved = aiInvolved,
            AiProposalId = aiProposalId,
        };

        return WriteAsync(evt, cancellationToken);
    }
}

/// <summary>Writes field provenance rows (§22).</summary>
public sealed class ProvenanceWriter : IProvenanceWriter
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;
    private readonly ICorrelationContext _correlation;

    public ProvenanceWriter(AppDbContext db, IClock clock, ICorrelationContext correlation)
    {
        _db = db;
        _clock = clock;
        _correlation = correlation;
    }

    public async Task RecordAsync(FieldProvenance provenance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provenance);

        if (provenance.RecordedAt == default)
        {
            provenance.RecordedAt = _clock.UtcNow;
        }

        if (string.IsNullOrEmpty(provenance.CorrelationId))
        {
            provenance.CorrelationId = _correlation.CorrelationId;
        }

        _db.FieldProvenances.Add(provenance);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task RecordAsync(
        string entityType,
        Guid entityId,
        string fieldPath,
        object? value,
        ValueOrigin origin,
        SourceSystem sourceSystem = SourceSystem.Unknown,
        string? sourceDocumentId = null,
        string? sourceFieldPath = null,
        string? transformation = null,
        string? modifiedBy = null,
        Guid? aiProposalId = null,
        CancellationToken cancellationToken = default) =>
        RecordAsync(
            new FieldProvenance
            {
                EntityType = entityType,
                EntityId = entityId,
                FieldPath = fieldPath,
                ValueJson = AuditJson.Serialize(value),
                Origin = origin,
                SourceSystem = sourceSystem,
                SourceDocumentId = sourceDocumentId,
                SourceFieldPath = sourceFieldPath,
                Transformation = transformation,
                ModifiedBy = modifiedBy,
                AiProposalId = aiProposalId,
                RecordedAt = _clock.UtcNow,
                CorrelationId = _correlation.CorrelationId,
            },
            cancellationToken);
}

/// <summary>EF Core unit of work. Transaction scope is explicit so the outbox write is atomic with its cause.</summary>
public sealed class EfUnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _db;

    public EfUnitOfWork(AppDbContext db) => _db = db;

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => _db.SaveChangesAsync(cancellationToken);

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Reuse an ambient transaction if the caller already opened one, so nesting stays correct.
        if (_db.Database.CurrentTransaction is not null)
        {
            return await action(cancellationToken);
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async ct =>
        {
            await using IDbContextTransaction tx = await _db.Database.BeginTransactionAsync(ct);
            var result = await action(ct);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        }, cancellationToken);
    }

    public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return ExecuteInTransactionAsync<object?>(async ct =>
        {
            await action(ct);
            return null;
        }, cancellationToken);
    }
}
