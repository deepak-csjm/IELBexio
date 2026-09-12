using IelBexio.Domain.Common;
using IelBexio.Domain.Workflow;

namespace IelBexio.Domain.Invoicing;

public enum PaymentStatus { Unknown = 0, Unpaid = 1, PartiallyPaid = 2, Paid = 3, Refunded = 4, PartiallyRefunded = 5, Voided = 6 }

public enum DocumentStatus { Unknown = 0, Draft = 1, Issued = 2, Cancelled = 3, CreditNote = 4 }

public enum ExtractionStatus { NotRequired = 0, Pending = 1, Succeeded = 2, Failed = 3, PartiallyExtracted = 4 }

public enum ValidationStatus { NotValidated = 0, Passed = 1, PassedWithWarnings = 2, Failed = 3 }

public enum ApprovalStatus { NotSubmitted = 0, PendingApproval = 1, Approved = 2, Rejected = 3 }

public enum SyncStatus { NotSynced = 0, Queued = 1, InProgress = 2, Synced = 3, Failed = 4, PermanentlyFailed = 5 }

/// <summary>
/// The canonical invoice — the internal source of truth. Shopify, Amazon and uploaded documents all
/// normalise into this type; Bexio is populated <em>from</em> it. No vendor field names appear here
/// (specification §7).
/// </summary>
public sealed class Invoice : TenantEntity
{
    // ---- Provenance / identity -------------------------------------------------
    public SourceSystem SourceSystem { get; set; }
    public Guid? SourceConnectionId { get; set; }

    /// <summary>The source's own identifier for this document (Shopify order id, Amazon order id, document hash).</summary>
    public string SourceDocumentId { get; set; } = string.Empty;

    /// <summary>
    /// Version of the source document. Together with tenant + source system + document id this forms
    /// the idempotency identity of the record (§19).
    /// </summary>
    public string SourceDocumentVersion { get; set; } = "1";

    // ---- Commercial content ----------------------------------------------------
    public string? InvoiceNumber { get; set; }
    public DateOnly? InvoiceDate { get; set; }
    public DateOnly? DueDate { get; set; }

    /// <summary>ISO-4217 code governing every monetary amount on this invoice and its lines.</summary>
    public string Currency { get; set; } = "CHF";

    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public Address BillingAddress { get; set; } = new();
    public Address ShippingAddress { get; set; } = new();

    public decimal SubtotalAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal ShippingAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal TotalAmount { get; set; }

    public Money Subtotal => new(SubtotalAmount, Currency);
    public Money Discount => new(DiscountAmount, Currency);
    public Money Shipping => new(ShippingAmount, Currency);
    public Money Tax => new(TaxAmount, Currency);
    public Money Total => new(TotalAmount, Currency);

    // ---- Status ----------------------------------------------------------------
    public PaymentStatus PaymentStatus { get; set; }
    public DocumentStatus DocumentStatus { get; set; } = DocumentStatus.Issued;
    public ExtractionStatus ExtractionStatus { get; set; } = ExtractionStatus.NotRequired;
    public ValidationStatus ValidationStatus { get; set; }
    public ApprovalStatus ApprovalStatus { get; set; }
    public SyncStatus SyncStatus { get; set; }

    /// <summary>The authoritative lifecycle position. Only <see cref="InvoiceWorkflow"/> may change it.</summary>
    public InvoiceWorkflowState WorkflowState { get; private set; } = InvoiceWorkflowState.Imported;

    // ---- Destination -----------------------------------------------------------
    public string? BexioInvoiceId { get; set; }
    public string? BexioContactId { get; set; }

    // ---- Review / approval -----------------------------------------------------
    public bool HumanVerified { get; set; }
    public string? VerifiedBy { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }

    /// <summary>Who prepared the record. Kept separate from the approver so dual control is a config change, not a migration (§16).</summary>
    public string? PreparedBy { get; set; }

    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }

    /// <summary>Correlation id linking every log, audit event and AI call for this invoice's lifecycle (§27).</summary>
    public string CorrelationId { get; set; } = Guid.CreateVersion7().ToString("n");

    public Guid? ImportRunId { get; set; }
    public Guid? PrimaryDocumentArtifactId { get; set; }

    public List<InvoiceLine> Lines { get; } = [];
    public List<Payment> Payments { get; } = [];

    /// <summary>
    /// The deterministic idempotency identity of this source document. Used as the unique key and as
    /// the basis of the Bexio idempotency key.
    /// </summary>
    public string SourceIdentity() => BuildSourceIdentity(TenantId, SourceSystem, SourceDocumentId, SourceDocumentVersion);

    public static string BuildSourceIdentity(Guid tenantId, SourceSystem source, string documentId, string version) =>
        $"{tenantId:n}|{source}|{documentId}|{version}";

    /// <summary>
    /// Moves the invoice to <paramref name="target"/>, refusing illegal transitions. This is the only
    /// mutator of <see cref="WorkflowState"/> in the entire system.
    /// </summary>
    public void TransitionTo(InvoiceWorkflowState target)
    {
        InvoiceWorkflow.Transition(WorkflowState, target);
        WorkflowState = target;
    }

    /// <summary>Restores persisted state without transition checks. For the persistence layer only.</summary>
    public void RestoreWorkflowState(InvoiceWorkflowState state) => WorkflowState = state;
}
