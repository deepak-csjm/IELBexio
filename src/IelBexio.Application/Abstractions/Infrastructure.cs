using IelBexio.Domain.Audit;
using IelBexio.Domain.Common;

namespace IelBexio.Application.Abstractions;

/// <summary>Abstracts the clock so time-dependent behaviour (expiry, backoff, windows) is testable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// The tenant the current operation belongs to. Every repository query is filtered by this; there is
/// no code path that reads business data without it (§23).
/// </summary>
public interface ITenantContext
{
    Guid TenantId { get; }
    bool IsResolved { get; }
}

/// <summary>The authenticated actor, for audit and authorisation decisions.</summary>
public interface ICurrentUser
{
    string UserId { get; }
    string DisplayName { get; }
    bool IsInRole(string role);
}

/// <summary>Application roles (§24).</summary>
public static class AppRoles
{
    public const string Admin = "Admin";
    public const string Reviewer = "Reviewer";
    public const string Approver = "Approver";
    public const string IntegrationManager = "IntegrationManager";
    public const string ReadOnly = "ReadOnly";

    public static readonly string[] All = [Admin, Reviewer, Approver, IntegrationManager, ReadOnly];
}

/// <summary>Correlation id flowing through one invoice lifecycle (§27).</summary>
public interface ICorrelationContext
{
    string CorrelationId { get; }
    IDisposable Push(string correlationId);
}

/// <summary>Append-only audit writer. There is deliberately no update or delete operation.</summary>
public interface IAuditWriter
{
    Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);

    Task WriteAsync(
        string action,
        string entityType,
        Guid? entityId,
        object? oldValue = null,
        object? newValue = null,
        string? reason = null,
        bool aiInvolved = false,
        Guid? aiProposalId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Records where a canonical field's value came from (§22).</summary>
public interface IProvenanceWriter
{
    Task RecordAsync(FieldProvenance provenance, CancellationToken cancellationToken = default);

    Task RecordAsync(
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
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Private blob storage. The POC ships a local-filesystem implementation and an Azure Blob
/// implementation behind the same interface; neither ever produces a permanent public URL (§25).
/// </summary>
public interface IBlobStore
{
    /// <summary>Stores bytes under a generated key and returns the storage URI.</summary>
    Task<string> PutAsync(string container, string blobName, Stream content, string contentType, CancellationToken cancellationToken = default);

    Task<Stream?> GetAsync(string blobUri, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string blobUri, CancellationToken cancellationToken = default);

    Task DeleteAsync(string blobUri, CancellationToken cancellationToken = default);

    /// <summary>Short-lived read URL, issued only when a browser must fetch the bytes directly.</summary>
    Task<Uri?> CreateReadUrlAsync(string blobUri, TimeSpan lifetime, CancellationToken cancellationToken = default);
}

/// <summary>Encrypts and decrypts tokens at rest. Backed by ASP.NET Core Data Protection.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string? Unprotect(string? protectedValue);
}

/// <summary>Runs work inside a single database transaction, for the outbox pattern (§20).</summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default);

    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default);
}
