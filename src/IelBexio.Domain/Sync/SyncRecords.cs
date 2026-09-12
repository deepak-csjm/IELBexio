using IelBexio.Domain.Common;

namespace IelBexio.Domain.Sync;

/// <summary>Error taxonomy required by §20. Drives whether a failure is retried and how.</summary>
public enum SyncErrorCategory
{
    None = 0,
    Authentication = 1,
    Authorization = 2,
    RateLimit = 3,
    Validation = 4,
    NotFound = 5,
    Conflict = 6,
    Transient = 7,
    Permanent = 8,
    Network = 9,
    Unknown = 10,
}

public static class SyncErrorPolicy
{
    /// <summary>
    /// Whether a category is worth retrying. Validation, authorization and permanent failures are
    /// never retried: retrying a 422 forever is how integrations melt down (§20).
    /// </summary>
    public static bool IsRetryable(SyncErrorCategory category) => category switch
    {
        SyncErrorCategory.RateLimit => true,
        SyncErrorCategory.Transient => true,
        SyncErrorCategory.Network => true,
        SyncErrorCategory.Authentication => true, // a refresh may fix it; capped by attempt count
        SyncErrorCategory.Unknown => true,
        _ => false,
    };
}

public enum SyncAttemptStatus { Pending = 0, InProgress = 1, Succeeded = 2, Failed = 3, PermanentlyFailed = 4, SkippedAlreadySynced = 5 }

/// <summary>
/// One attempt to push an entity to an external destination. The unique <see cref="IdempotencyKey"/>
/// is what makes duplicate posting structurally impossible (§19).
/// </summary>
public sealed class SynchronizationAttempt : TenantEntity
{
    public Guid EntityId { get; set; }
    public string EntityType { get; set; } = "Invoice";
    public string Destination { get; set; } = "Bexio";
    public string Operation { get; set; } = "CreateInvoice";

    /// <summary>SHA-256 of the canonical request body. Detects "same key, different payload".</summary>
    public string RequestHash { get; set; } = string.Empty;

    /// <summary>Deterministic key: tenant + source system + source document id + source version + operation.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public SyncAttemptStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Identifier assigned by the destination on success.</summary>
    public string? ExternalId { get; set; }

    public SyncErrorCategory ErrorCategory { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Non-sensitive response metadata (status code, request id, rate-limit headers).</summary>
    public string? ResponseMetadataJson { get; set; }

    public string CorrelationId { get; set; } = string.Empty;
}

public enum OutboxStatus { Pending = 0, InProgress = 1, Processed = 2, Failed = 3, DeadLettered = 4 }

/// <summary>
/// Transactional outbox message (§20). Written in the same database transaction as the state change
/// that caused it, so "approved" and "will be sent" can never disagree.
/// </summary>
public sealed class OutboxMessage : TenantEntity
{
    public string MessageType { get; set; } = string.Empty;
    public string Payload { get; set; } = "{}";
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Earliest time the dispatcher may pick this message up. Implements backoff.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    public string? LastError { get; set; }
    public SyncErrorCategory LastErrorCategory { get; set; }
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>Lease owner while a worker is processing, so a crashed worker's messages can be reclaimed.</summary>
    public string? LeasedBy { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }
}

/// <summary>Known outbox message types. A closed set: the dispatcher refuses anything else.</summary>
public static class OutboxMessageTypes
{
    public const string SyncInvoiceToBexio = "sync.invoice.bexio";
}
