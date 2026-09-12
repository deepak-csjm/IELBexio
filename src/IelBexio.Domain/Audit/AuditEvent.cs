using IelBexio.Domain.Common;

namespace IelBexio.Domain.Audit;

/// <summary>
/// An immutable record of something that happened. Append-only: there is no update or delete path in
/// the application (§15, §27).
/// </summary>
public sealed class AuditEvent : TenantEntity
{
    /// <summary>User principal name, or a system actor such as "system:outbox-dispatcher".</summary>
    public string Actor { get; set; } = "system";

    public AuditActorType ActorType { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string Action { get; set; } = string.Empty;

    /// <summary>Previous value as JSON, where the action changed something.</summary>
    public string? OldValueJson { get; set; }

    public string? NewValueJson { get; set; }

    /// <summary>Human-supplied reason, where the action required one (rejections, overrides).</summary>
    public string? Reason { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public SourceSystem Source { get; set; }

    /// <summary>True when AI contributed to the value involved. Makes AI influence auditable at a glance.</summary>
    public bool AiInvolved { get; set; }

    public Guid? AiProposalId { get; set; }
}

public enum AuditActorType { System = 0, User = 1, Connector = 2, Worker = 3 }

/// <summary>Canonical action names, so audit queries are not guessing at free text.</summary>
public static class AuditActions
{
    public const string InvoiceImported = "invoice.imported";
    public const string InvoiceUpdatedFromSource = "invoice.updated_from_source";
    public const string InvoiceDuplicateSkipped = "invoice.duplicate_skipped";
    public const string InvoiceValidated = "invoice.validated";
    public const string InvoiceFieldCorrected = "invoice.field_corrected";
    public const string InvoiceVerified = "invoice.verified";
    public const string InvoiceSubmittedForApproval = "invoice.submitted_for_approval";
    public const string InvoiceApproved = "invoice.approved";
    public const string InvoiceRejected = "invoice.rejected";
    public const string InvoiceQueuedForSync = "invoice.queued_for_sync";
    public const string InvoiceSyncStarted = "invoice.sync_started";
    public const string InvoiceSyncSucceeded = "invoice.sync_succeeded";
    public const string InvoiceSyncFailed = "invoice.sync_failed";
    public const string InvoiceSyncSkippedDuplicate = "invoice.sync_skipped_duplicate";
    public const string PreflightFailed = "invoice.preflight_failed";
    public const string DocumentIngested = "document.ingested";
    public const string DocumentRejected = "document.rejected";
    public const string DocumentDuplicate = "document.duplicate";
    public const string AiProposalCreated = "ai.proposal_created";
    public const string AiProposalAccepted = "ai.proposal_accepted";
    public const string AiProposalRejected = "ai.proposal_rejected";
    public const string AiCallBlocked = "ai.call_blocked";
    public const string MappingCreated = "mapping.created";
    public const string MappingChanged = "mapping.changed";
    public const string BexioConnected = "bexio.connected";
    public const string BexioDisconnected = "bexio.disconnected";
    public const string BexioTokenRefreshed = "bexio.token_refreshed";
    public const string BexioReferenceDataRefreshed = "bexio.reference_data_refreshed";
    public const string ReconciliationChecked = "reconciliation.checked";
    public const string ReconciliationDiscrepancy = "reconciliation.discrepancy";
}
