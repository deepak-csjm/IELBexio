using IelBexio.Application.Abstractions;
using IelBexio.Application.Common;
using IelBexio.Application.Invoices;
using IelBexio.Application.Mapping;
using IelBexio.Application.Tax;
using IelBexio.Domain.Audit;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Tax;
using IelBexio.Domain.Validation;
using IelBexio.Domain.Workflow;
using IelBexio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IelBexio.Infrastructure.Workflow;

/// <summary>
/// Runs deterministic validation and tax determination, then routes the invoice (§16).
/// <para>
/// Entirely deterministic: no AI is consulted, so this path produces identical results with AI enabled
/// or disabled. That is what makes acceptance criterion 9 verifiable rather than aspirational.
/// </para>
/// <para>
/// Routing rule: any validation <em>error</em> means <c>ValidationFailed</c>; any warning, or any tax
/// conclusion below the review threshold, means <c>NeedsReview</c>; a clean record goes straight to
/// <c>Validated</c>. A clean record still requires explicit human approval afterwards — "validated" is
/// never "approved".
/// </para>
/// </summary>
public sealed class InvoiceProcessingService : IInvoiceProcessingService
{
    private readonly AppDbContext _db;
    private readonly InvoiceValidator _validator;
    private readonly TaxDeterminationService _taxDetermination;
    private readonly IMappingService _mappings;
    private readonly IClock _clock;
    private readonly IAuditWriter _audit;
    private readonly ILogger<InvoiceProcessingService> _logger;

    public InvoiceProcessingService(
        AppDbContext db,
        InvoiceValidator validator,
        TaxDeterminationService taxDetermination,
        IMappingService mappings,
        IClock clock,
        IAuditWriter audit,
        ILogger<InvoiceProcessingService> logger)
    {
        _db = db;
        _validator = validator;
        _taxDetermination = taxDetermination;
        _mappings = mappings;
        _clock = clock;
        _audit = audit;
        _logger = logger;
    }

    public async Task<Result<ValidationReport>> ProcessAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        var invoice = await _db.Invoices
            .Include(i => i.Lines)
            .Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);

        if (invoice is null)
        {
            return Result<ValidationReport>.Failure("NOT_FOUND", "The invoice does not exist.");
        }

        // Re-processing an already-approved or synced invoice would invalidate the approval silently.
        if (invoice.WorkflowState is InvoiceWorkflowState.Approved or InvoiceWorkflowState.QueuedForBexio
            or InvoiceWorkflowState.Syncing or InvoiceWorkflowState.Synced)
        {
            return Result<ValidationReport>.Failure(
                "INVALID_STATE",
                $"An invoice in {invoice.WorkflowState} cannot be re-processed; it has already been approved.");
        }

        var lines = invoice.Lines.OrderBy(l => l.LineNumber).ToList();

        // ---- Tax determination (deterministic) --------------------------------------------------
        var assessments = _taxDetermination.Determine(invoice, lines);

        var existingAssessments = await _db.TaxAssessments.Where(a => a.InvoiceId == invoice.Id).ToListAsync(cancellationToken);

        foreach (var fresh in assessments)
        {
            var existing = existingAssessments.FirstOrDefault(a => a.InvoiceLineId == fresh.InvoiceLineId);

            // A human-verified tax conclusion is never overwritten by re-running determination. Losing
            // a reviewer's decision because a background job re-ran would be a serious defect.
            if (existing is { HumanVerified: true })
            {
                continue;
            }

            if (existing is null)
            {
                _db.TaxAssessments.Add(fresh);
                existing = fresh;
            }
            else
            {
                existing.CountryCode = fresh.CountryCode;
                existing.Jurisdiction = fresh.Jurisdiction;
                existing.TaxType = fresh.TaxType;
                existing.RatePercent = fresh.RatePercent;
                existing.TaxableAmount = fresh.TaxableAmount;
                existing.TaxAmount = fresh.TaxAmount;
                existing.Currency = fresh.Currency;
                existing.InternalTaxCode = fresh.InternalTaxCode;
                existing.DeterminationMethod = fresh.DeterminationMethod;
                existing.DeterminationVersion = fresh.DeterminationVersion;
                existing.ConfidenceSignal = fresh.ConfidenceSignal;
                existing.Rationale = fresh.Rationale;
            }

            // Propose the Bexio tax id from the mapping table, without applying it: proposing is
            // deterministic, applying happens only at synchronisation after approval.
            var onDate = invoice.InvoiceDate ?? DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
            var mapping = await _mappings.ResolveTaxAsync(existing, onDate, cancellationToken);
            existing.ProposedBexioTaxId = mapping.Succeeded ? mapping.Value!.BexioTaxId : null;

            var line = lines.FirstOrDefault(l => l.Id == existing.InvoiceLineId);
            if (line is not null)
            {
                line.TaxAssessmentId = existing.Id;
                line.BexioTaxId = existing.ProposedBexioTaxId;
            }
        }

        // ---- Deterministic validation -----------------------------------------------------------
        var report = _validator.Validate(invoice, lines);

        // Tax conclusions feed the same report, so a reviewer sees one list rather than two.
        foreach (var assessment in await _db.TaxAssessments.Where(a => a.InvoiceId == invoice.Id).ToListAsync(cancellationToken))
        {
            var lineNumber = lines.FirstOrDefault(l => l.Id == assessment.InvoiceLineId)?.LineNumber;

            if (assessment.DeterminationMethod == TaxDeterminationMethod.Undetermined)
            {
                report.Warning(
                    ValidationCodes.TaxUndetermined,
                    $"Line {lineNumber}: the tax treatment could not be determined automatically and needs a human decision.",
                    $"lines[{lineNumber}].tax");
            }
            else if (!assessment.HumanVerified && assessment.ConfidenceSignal < TaxDeterminationService.ReviewThreshold)
            {
                report.Warning(
                    ValidationCodes.TaxUnverified,
                    $"Line {lineNumber}: the tax conclusion needs human verification ({assessment.Rationale}).",
                    $"lines[{lineNumber}].tax");
            }

            if (assessment.ProposedBexioTaxId is null && assessment.InternalTaxCode is not null)
            {
                report.Warning(
                    ValidationCodes.TaxMappingMissing,
                    $"Line {lineNumber}: no Bexio tax is mapped for internal code '{assessment.InternalTaxCode}'.",
                    $"lines[{lineNumber}].tax");
            }
        }

        // ---- Route ---------------------------------------------------------------------------------
        invoice.ValidationStatus = report.HasErrors
            ? ValidationStatus.Failed
            : report.HasWarnings ? ValidationStatus.PassedWithWarnings : ValidationStatus.Passed;

        if (invoice.WorkflowState == InvoiceWorkflowState.Imported)
        {
            invoice.TransitionTo(InvoiceWorkflowState.Extracted);
        }

        var target = report.HasErrors
            ? InvoiceWorkflowState.ValidationFailed
            : report.HasWarnings
                ? InvoiceWorkflowState.NeedsReview
                : InvoiceWorkflowState.Validated;

        // From NeedsReview a clean re-validation may return to Validated; the state machine allows it.
        if (invoice.WorkflowState != target && InvoiceWorkflow.CanTransition(invoice.WorkflowState, target))
        {
            invoice.TransitionTo(target);
        }
        else if (invoice.WorkflowState == InvoiceWorkflowState.Extracted)
        {
            invoice.TransitionTo(target);
        }

        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            AuditActions.InvoiceValidated, nameof(Invoice), invoice.Id,
            newValue: new
            {
                invoice.ValidationStatus,
                State = invoice.WorkflowState.ToString(),
                Errors = report.Issues.Count(i => i.Severity == ValidationSeverity.Error),
                Warnings = report.Issues.Count(i => i.Severity == ValidationSeverity.Warning),
            },
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Invoice {InvoiceId} processed: {Status}, now in {State}.",
            invoice.Id, invoice.ValidationStatus, invoice.WorkflowState);

        return Result<ValidationReport>.Success(report);
    }
}
