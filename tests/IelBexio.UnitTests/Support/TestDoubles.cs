using IelBexio.Application.Abstractions;
using IelBexio.Connectors.Bexio.Auth;
using IelBexio.Domain.Audit;
using IelBexio.Domain.Bexio;
using IelBexio.Domain.Common;

namespace IelBexio.UnitTests.Support;

/// <summary>A clock the test drives explicitly, so token-expiry logic is deterministic.</summary>
public sealed class TestClock : IClock
{
    public TestClock(DateTimeOffset? start = null) => UtcNow = start ?? new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}

/// <summary>
/// A reversible stand-in for Data Protection. Deliberately NOT encryption — it exists so tests can
/// assert that the token that went in is the token that comes out, and that nothing else reads it.
/// </summary>
public sealed class ReversibleTestProtector : ISecretProtector
{
    public const string Prefix = "protected:";

    public string Protect(string plaintext) => Prefix + plaintext;

    public string? Unprotect(string? protectedValue) =>
        protectedValue is null ? null
        : protectedValue.StartsWith(Prefix, StringComparison.Ordinal) ? protectedValue[Prefix.Length..]
        : null;
}

/// <summary>In-memory Bexio connection store.</summary>
public sealed class InMemoryBexioConnectionStore : IBexioConnectionStore
{
    public BexioConnection? Connection { get; set; }

    public Task<BexioConnection?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Connection);

    public Task SaveAsync(BexioConnection connection, CancellationToken cancellationToken = default)
    {
        Connection = connection;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        Connection = null;
        return Task.CompletedTask;
    }
}

/// <summary>Captures audit events so tests can assert on the audit trail (§15).</summary>
public sealed class RecordingAuditWriter : IAuditWriter
{
    public List<AuditEvent> Events { get; } = [];

    public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(auditEvent);
        return Task.CompletedTask;
    }

    public Task WriteAsync(
        string action, string entityType, Guid? entityId,
        object? oldValue = null, object? newValue = null, string? reason = null,
        bool aiInvolved = false, Guid? aiProposalId = null, CancellationToken cancellationToken = default)
    {
        Events.Add(new AuditEvent
        {
            Action = action, EntityType = entityType, EntityId = entityId,
            Reason = reason, AiInvolved = aiInvolved, AiProposalId = aiProposalId,
        });
        return Task.CompletedTask;
    }

    public bool Contains(string action) => Events.Any(e => string.Equals(e.Action, action, StringComparison.Ordinal));
}

/// <summary>Captures provenance rows.</summary>
public sealed class RecordingProvenanceWriter : IProvenanceWriter
{
    public List<FieldProvenance> Records { get; } = [];

    public Task RecordAsync(FieldProvenance provenance, CancellationToken cancellationToken = default)
    {
        Records.Add(provenance);
        return Task.CompletedTask;
    }

    public Task RecordAsync(
        string entityType, Guid entityId, string fieldPath, object? value, ValueOrigin origin,
        SourceSystem sourceSystem = SourceSystem.Unknown, string? sourceDocumentId = null,
        string? sourceFieldPath = null, string? transformation = null, string? modifiedBy = null,
        Guid? aiProposalId = null, CancellationToken cancellationToken = default)
    {
        Records.Add(new FieldProvenance
        {
            EntityType = entityType, EntityId = entityId, FieldPath = fieldPath, Origin = origin,
            SourceSystem = sourceSystem, SourceDocumentId = sourceDocumentId, SourceFieldPath = sourceFieldPath,
            Transformation = transformation, ModifiedBy = modifiedBy, AiProposalId = aiProposalId,
        });
        return Task.CompletedTask;
    }
}

/// <summary>A fixed current user with a chosen role set.</summary>
public sealed class TestUser : ICurrentUser
{
    private readonly string[] _roles;

    public TestUser(string userId = "tester@example.test", params string[] roles)
    {
        UserId = userId;
        DisplayName = userId;
        _roles = roles.Length > 0 ? roles : [AppRoles.Admin];
    }

    public string UserId { get; }
    public string DisplayName { get; }
    public bool IsInRole(string role) => _roles.Contains(role, StringComparer.OrdinalIgnoreCase);
}

public sealed class TestCorrelationContext : ICorrelationContext
{
    public string CorrelationId { get; private set; } = "test-correlation";

    public IDisposable Push(string correlationId)
    {
        var previous = CorrelationId;
        CorrelationId = correlationId;
        return new Restore(() => CorrelationId = previous);
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
