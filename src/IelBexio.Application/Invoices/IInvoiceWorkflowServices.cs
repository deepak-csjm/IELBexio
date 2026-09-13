using IelBexio.Application.Common;
using IelBexio.Application.Sources;
using IelBexio.Domain.Common;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Tax;
using IelBexio.Domain.Validation;
using IelBexio.Domain.Workflow;

namespace IelBexio.Application.Invoices;

/// <summary>Summary of one import run.</summary>
public sealed record ImportSummary(
    Guid ImportRunId,
    string CorrelationId,
    int DocumentsSeen,
    int Created,
    int Updated,
    int DuplicatesSkipped,
    int Failures,
    IReadOnlyList<string> Messages);

/// <summary>Imports source documents into canonical records, idempotently (§19).</summary>
public interface IImportService
{
    Task<Result<ImportSummary>> ImportAsync(SourceSystem sourceSystem, SourceQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Runs deterministic validation and tax determination, then routes the record (§16).</summary>
public interface IInvoiceProcessingService
{
    /// <summary>
    /// Validates, determines tax, and moves the invoice to <c>Validated</c>, <c>NeedsReview</c> or
    /// <c>ValidationFailed</c>. Safe to re-run after a human correction.
    /// </summary>
    Task<Result<ValidationReport>> ProcessAsync(Guid invoiceId, CancellationToken cancellationToken = default);
}

/// <summary>A field correction made by a human reviewer.</summary>
public sealed record FieldCorrection(string FieldPath, string? NewValue, string? Reason);

/// <summary>Human review operations. Every one of these writes an audit event (§15).</summary>
public interface IReviewService
{
    Task<Result> ApplyCorrectionsAsync(Guid invoiceId, IReadOnlyList<FieldCorrection> corrections, CancellationToken cancellationToken = default);

    Task<Result> VerifyAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    Task<Result> VerifyTaxAsync(Guid taxAssessmentId, string? bexioTaxId, CancellationToken cancellationToken = default);

    Task<Result> AcceptAiProposalAsync(Guid proposalId, CancellationToken cancellationToken = default);

    Task<Result> RejectAiProposalAsync(Guid proposalId, string? comment, CancellationToken cancellationToken = default);

    Task<Result> SubmitForApprovalAsync(Guid invoiceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Approval and rejection. This is the only place an invoice may become eligible for Bexio
/// synchronisation, and the approval and the outbox message are written in one transaction (§20).
/// </summary>
public interface IApprovalService
{
    Task<Result> ApproveAsync(Guid invoiceId, string? comment, CancellationToken cancellationToken = default);

    Task<Result> RejectAsync(Guid invoiceId, string reason, CancellationToken cancellationToken = default);
}

/// <summary>Performs one Bexio synchronisation attempt for an outbox message.</summary>
public interface IInvoiceSynchronizationService
{
    Task<Result> SynchronizeAsync(Guid invoiceId, string correlationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Compares this system's record of what was posted against what Bexio actually holds (§14, §20).
/// A discrepancy moves the invoice to <see cref="InvoiceWorkflowState.ReconciliationRequired"/>.
/// </summary>
public interface IReconciliationService
{
    Task<Result<ReconciliationSummary>> ReconcileAsync(int maxInvoices = 100, CancellationToken cancellationToken = default);
}

public sealed record ReconciliationSummary(int Checked, int Matched, int Missing, int Mismatched, IReadOnlyList<string> Findings);

/// <summary>Read model for the review queue and invoice detail screens.</summary>
public sealed record InvoiceDetail(
    Invoice Invoice,
    IReadOnlyList<InvoiceLine> Lines,
    IReadOnlyList<TaxAssessment> TaxAssessments,
    ValidationReport Report);
