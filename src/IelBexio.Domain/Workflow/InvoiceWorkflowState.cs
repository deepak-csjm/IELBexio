namespace IelBexio.Domain.Workflow;

/// <summary>
/// The invoice lifecycle required by specification §16. Numeric values are persisted, so they are
/// fixed forever; new states must take new numbers.
/// </summary>
public enum InvoiceWorkflowState
{
    Imported = 0,
    Extracted = 1,
    Validated = 2,
    NeedsReview = 3,
    HumanVerified = 4,
    ReadyForApproval = 5,
    Approved = 6,
    QueuedForBexio = 7,
    Syncing = 8,
    Synced = 9,

    ExtractionFailed = 100,
    ValidationFailed = 101,
    ApprovalRejected = 102,
    SyncFailed = 103,
    BexioRejected = 104,
    ReconciliationRequired = 105,
}
