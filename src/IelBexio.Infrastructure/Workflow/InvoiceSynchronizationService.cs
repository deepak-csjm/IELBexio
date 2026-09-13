using System.Text.Json;
using IelBexio.Application.Abstractions;
using IelBexio.Application.Bexio;
using IelBexio.Application.Common;
using IelBexio.Application.Invoices;
using IelBexio.Application.Sync;
using IelBexio.Domain.Audit;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Sync;
using IelBexio.Domain.Tax;
using IelBexio.Domain.Workflow;
using IelBexio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IelBexio.Infrastructure.Workflow;

/// <summary>
/// Performs one attempt to create an approved invoice in Bexio (§20).
/// <para>
/// The order of operations is the whole design, and it is deliberately paranoid:
/// </para>
/// <list type="number">
/// <item><description>Check whether this source document was already synchronised — by idempotency key,
/// not by our own row id, so a re-imported duplicate is caught too. This comes first so a replayed
/// message for finished work is acknowledged rather than being retried until it dead-letters.</description></item>
/// <item><description>Refuse anything not in an approved, sync-eligible state.</description></item>
/// <item><description>Re-run preflight. Configuration can change between approval and dispatch.</description></item>
/// <item><description>Verify the approval still matches the invoice's financial content.</description></item>
/// <item><description>Reserve the idempotency key in the database <em>before</em> calling Bexio.</description></item>
/// <item><description>Call Bexio, then record the outcome.</description></item>
/// </list>
/// <para>
/// Step 5 is the one that matters most. Reserving the key first means that if the process dies after
/// Bexio creates the invoice but before we record it, the retry finds the reserved key and reconciles
/// rather than creating a second invoice. Reserving it afterwards would leave exactly that window open.
/// </para>
/// </summary>
public sealed class InvoiceSynchronizationService : IInvoiceSynchronizationService
{
    private readonly AppDbContext _db;
    private readonly IBexioClient _bexio;
    private readonly BexioPreflightService _preflight;
    private readonly IClock _clock;
    private readonly IAuditWriter _audit;
    private readonly ILogger<InvoiceSynchronizationService> _logger;

    public InvoiceSynchronizationService(
        AppDbContext db,
        IBexioClient bexio,
        BexioPreflightService preflight,
        IClock clock,
        IAuditWriter audit,
        ILogger<InvoiceSynchronizationService> logger)
    {
        _db = db;
        _bexio = bexio;
        _preflight = preflight;
        _clock = clock;
        _audit = audit;
        _logger = logger;
    }

    public async Task<Result> SynchronizeAsync(Guid invoiceId, string correlationId, CancellationToken cancellationToken = default)
    {
        var invoice = await _db.Invoices
            .Include(i => i.Lines)
            .Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);

        if (invoice is null)
        {
            return Result.Failure(nameof(SyncErrorCategory.Permanent), $"Invoice {invoiceId} no longer exists.");
        }

        var idempotencyKey = IdempotencyKey.ForInvoiceSync(
            invoice.TenantId, invoice.SourceSystem, invoice.SourceDocumentId, invoice.SourceDocumentVersion);

        // ---- 1. Has this source document already been synchronised? (§19) ------------------------
        // Checked FIRST, before the eligibility gate. A replayed message for an already-synced invoice
        // is a success to acknowledge, not an error: the work it asked for is done. Ordering the
        // eligibility check ahead of this would make every duplicate delivery look like a failure and
        // would leave the message being retried until it dead-lettered.
        //
        // This cannot be used to smuggle an unapproved invoice through: a succeeded attempt row only
        // exists if a prior, approved dispatch created it.
        var existing = await _db.SynchronizationAttempts
            .FirstOrDefaultAsync(a => a.IdempotencyKey == idempotencyKey, cancellationToken);

        if (existing is { Status: SyncAttemptStatus.Succeeded })
        {
            _logger.LogInformation(
                "Invoice {InvoiceId} was already synchronised as Bexio invoice {ExternalId}; not creating a duplicate.",
                invoiceId, existing.ExternalId);

            // Converge our state onto the truth rather than leaving a stuck record behind.
            if (string.IsNullOrEmpty(invoice.BexioInvoiceId))
            {
                invoice.BexioInvoiceId = existing.ExternalId;
            }

            if (invoice.WorkflowState != InvoiceWorkflowState.Synced)
            {
                if (invoice.WorkflowState == InvoiceWorkflowState.QueuedForBexio)
                {
                    invoice.TransitionTo(InvoiceWorkflowState.Syncing);
                }

                if (InvoiceWorkflow.CanTransition(invoice.WorkflowState, InvoiceWorkflowState.Synced))
                {
                    invoice.TransitionTo(InvoiceWorkflowState.Synced);
                    invoice.SyncStatus = SyncStatus.Synced;
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            await _audit.WriteAsync(AuditActions.InvoiceSyncSkippedDuplicate, nameof(Invoice), invoice.Id,
                newValue: new { existing.ExternalId, idempotencyKey }, cancellationToken: cancellationToken);

            return Result.Success();
        }

        // ---- 2. Only approved invoices may be posted (acceptance criterion 12) --------------------
        if (!InvoiceWorkflow.SyncEligible.Contains(invoice.WorkflowState))
        {
            return Result.Failure(
                nameof(SyncErrorCategory.Permanent),
                $"Invoice {invoiceId} is in state {invoice.WorkflowState}, which is not eligible for synchronisation. " +
                "Only an approved invoice may be sent to Bexio.");
        }

        if (invoice.ApprovalStatus != ApprovalStatus.Approved)
        {
            return Result.Failure(nameof(SyncErrorCategory.Permanent), $"Invoice {invoiceId} has not been approved.");
        }

        // ---- 3. Mark in flight -----------------------------------------------------------------
        if (invoice.WorkflowState == InvoiceWorkflowState.QueuedForBexio)
        {
            invoice.TransitionTo(InvoiceWorkflowState.Syncing);
            invoice.SyncStatus = SyncStatus.InProgress;
            await _db.SaveChangesAsync(cancellationToken);
            await _audit.WriteAsync(AuditActions.InvoiceSyncStarted, nameof(Invoice), invoice.Id, cancellationToken: cancellationToken);
        }

        // ---- 4. Preflight, again --------------------------------------------------------------------
        var assessments = await _db.TaxAssessments.Where(a => a.InvoiceId == invoice.Id).ToListAsync(cancellationToken);
        var lines = invoice.Lines.OrderBy(l => l.LineNumber).ToList();

        var preview = await _preflight.BuildAsync(invoice, lines, assessments, requireConnection: true, cancellationToken);

        if (!preview.CanSynchronize)
        {
            var reasons = string.Join("; ", preview.Report.Issues.Where(i => i.Severity == Domain.Validation.ValidationSeverity.Error).Select(i => i.Message));
            await FailAsync(invoice, idempotencyKey, SyncErrorCategory.Validation, "PREFLIGHT", reasons, correlationId, cancellationToken);
            await _audit.WriteAsync(AuditActions.PreflightFailed, nameof(Invoice), invoice.Id, reason: reasons, cancellationToken: cancellationToken);
            return Result.Failure(nameof(SyncErrorCategory.Validation), $"Pre-flight checks failed: {reasons}");
        }

        // ---- 5. The approval must still describe this invoice ---------------------------------------
        var approval = await _db.Approvals
            .Where(a => a.EntityId == invoice.Id && a.Decision == ApprovalDecision.Approved)
            .OrderByDescending(a => a.ApprovedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (approval is null)
        {
            await FailAsync(invoice, idempotencyKey, SyncErrorCategory.Permanent, "NO_APPROVAL", "No approval record exists.", correlationId, cancellationToken);
            return Result.Failure(nameof(SyncErrorCategory.Permanent), "No approval record exists for this invoice.");
        }

        var currentVersion = ApprovalService.ComputeApprovalVersion(invoice);
        if (!string.Equals(approval.ApprovalVersion, currentVersion, StringComparison.Ordinal))
        {
            // Approve-then-edit-then-post. The approval no longer covers what would be sent.
            const string message =
                "The invoice has changed since it was approved, so the approval no longer covers what would be " +
                "posted. It must be reviewed and approved again.";

            // FailAsync has already moved the invoice out of Syncing into its failure state; from there
            // it is returned to review so the change can be looked at and re-approved. Re-asserting the
            // failure state here would be an illegal self-transition.
            await FailAsync(invoice, idempotencyKey, SyncErrorCategory.Permanent, "APPROVAL_STALE", message, correlationId, cancellationToken);

            if (InvoiceWorkflow.CanTransition(invoice.WorkflowState, InvoiceWorkflowState.NeedsReview))
            {
                invoice.TransitionTo(InvoiceWorkflowState.NeedsReview);
            }

            invoice.ApprovalStatus = ApprovalStatus.NotSubmitted;
            await _db.SaveChangesAsync(cancellationToken);

            return Result.Failure(nameof(SyncErrorCategory.Permanent), message);
        }

        // ---- 6. Build the request and reserve the key BEFORE calling Bexio ---------------------------
        var request = new BexioInvoiceRequest(
            ContactId: preview.BexioContactId!,
            InvoiceDate: invoice.InvoiceDate ?? DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime),
            DueDate: invoice.DueDate,
            CurrencyCode: invoice.Currency,
            CurrencyId: null,
            Title: invoice.InvoiceNumber,
            ReferenceNumber: invoice.InvoiceNumber,
            Positions: preview.Lines.Select(l => new BexioInvoicePositionRequest(
                Text: l.Description,
                Amount: l.Quantity,
                UnitPrice: l.Quantity == 0m ? l.NetAmount : decimal.Round(l.NetAmount / l.Quantity, 4, MidpointRounding.ToEven),
                TaxId: l.BexioTaxId,
                AccountId: l.BexioAccountId,
                ArticleId: l.BexioArticleId)).ToList());

        var requestHash = IdempotencyKey.HashRequest(request);

        var attempt = existing ?? new SynchronizationAttempt
        {
            TenantId = invoice.TenantId,
            EntityId = invoice.Id,
            EntityType = nameof(Invoice),
            Destination = "Bexio",
            Operation = IdempotencyKey.CreateInvoiceOperation,
            IdempotencyKey = idempotencyKey,
            CorrelationId = correlationId,
        };

        attempt.RequestHash = requestHash;
        attempt.Status = SyncAttemptStatus.InProgress;
        attempt.AttemptCount++;
        attempt.StartedAt = _clock.UtcNow;

        if (existing is null)
        {
            _db.SynchronizationAttempts.Add(attempt);
        }

        try
        {
            // The unique index on idempotency_key is what actually enforces "one sync per document".
            // If a concurrent worker inserted the same key first, this throws and we do not call Bexio.
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Another worker already reserved idempotency key {Key}; abandoning this attempt.", idempotencyKey);
            return Result.Failure(nameof(SyncErrorCategory.Conflict), "Another worker is already synchronising this invoice.");
        }

        // ---- 7. Call Bexio ----------------------------------------------------------------------------
        try
        {
            var result = await _bexio.CreateInvoiceAsync(request, idempotencyKey, cancellationToken);

            attempt.Status = SyncAttemptStatus.Succeeded;
            attempt.ExternalId = result.Id;
            attempt.CompletedAt = _clock.UtcNow;
            attempt.ErrorCategory = SyncErrorCategory.None;
            attempt.ErrorCode = null;
            attempt.ErrorMessage = null;
            attempt.ResponseMetadataJson = JsonSerializer.Serialize(new
            {
                result.DocumentNumber,
                result.TotalGross,
                result.TotalNet,
                result.TotalTax,
                result.CurrencyCode,
            });

            invoice.BexioInvoiceId = result.Id;
            invoice.BexioContactId = preview.BexioContactId;
            invoice.TransitionTo(InvoiceWorkflowState.Synced);
            invoice.SyncStatus = SyncStatus.Synced;

            // Record which Bexio tax was actually used, so the audit trail shows what was booked rather
            // than only what was proposed.
            foreach (var assessment in assessments)
            {
                var previewLine = preview.Lines.FirstOrDefault(l =>
                    lines.FirstOrDefault(x => x.Id == assessment.InvoiceLineId)?.LineNumber == l.LineNumber);

                if (previewLine?.BexioTaxId is { } appliedTaxId)
                {
                    assessment.AppliedBexioTaxId = appliedTaxId;
                }
            }

            await _db.SaveChangesAsync(cancellationToken);

            await _audit.WriteAsync(
                AuditActions.InvoiceSyncSucceeded, nameof(Invoice), invoice.Id,
                newValue: new { BexioInvoiceId = result.Id, result.DocumentNumber, idempotencyKey },
                cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Invoice {InvoiceId} created in Bexio as {BexioId} ({DocumentNumber}). Correlation {CorrelationId}.",
                invoice.Id, result.Id, result.DocumentNumber, correlationId);

            return Result.Success();
        }
        catch (BexioApiException ex)
        {
            await FailAsync(invoice, idempotencyKey, ex.Category, ex.Code, ex.Message, correlationId, cancellationToken, attempt);
            return Result.Failure(ex.Category.ToString(), ex.Message);
        }
        catch (Exception ex)
        {
            // An unclassified failure becomes a classified one here rather than escaping the worker.
            await FailAsync(invoice, idempotencyKey, SyncErrorCategory.Unknown, ex.GetType().Name, ex.Message, correlationId, cancellationToken, attempt);
            return Result.Failure(nameof(SyncErrorCategory.Unknown), ex.Message);
        }
    }

    private async Task FailAsync(
        Invoice invoice, string idempotencyKey, SyncErrorCategory category, string? code, string message,
        string correlationId, CancellationToken cancellationToken, SynchronizationAttempt? attempt = null)
    {
        attempt ??= await _db.SynchronizationAttempts.FirstOrDefaultAsync(a => a.IdempotencyKey == idempotencyKey, cancellationToken);

        if (attempt is null)
        {
            attempt = new SynchronizationAttempt
            {
                TenantId = invoice.TenantId,
                EntityId = invoice.Id,
                EntityType = nameof(Invoice),
                Destination = "Bexio",
                Operation = IdempotencyKey.CreateInvoiceOperation,
                IdempotencyKey = idempotencyKey,
                CorrelationId = correlationId,
                StartedAt = _clock.UtcNow,
                AttemptCount = 1,
                RequestHash = string.Empty,
            };

            _db.SynchronizationAttempts.Add(attempt);
        }

        var retryable = SyncErrorPolicy.IsRetryable(category);

        attempt.Status = retryable ? SyncAttemptStatus.Failed : SyncAttemptStatus.PermanentlyFailed;
        attempt.ErrorCategory = category;
        attempt.ErrorCode = code;
        attempt.ErrorMessage = message.Length > 4000 ? message[..4000] : message;
        attempt.CompletedAt = _clock.UtcNow;

        // A validation rejection from Bexio is a different workflow outcome from a transient failure:
        // one needs a human, the other needs patience.
        if (invoice.WorkflowState == InvoiceWorkflowState.Syncing)
        {
            var target = category switch
            {
                SyncErrorCategory.Validation or SyncErrorCategory.Conflict => InvoiceWorkflowState.BexioRejected,
                _ when retryable => InvoiceWorkflowState.QueuedForBexio,
                _ => InvoiceWorkflowState.SyncFailed,
            };

            invoice.TransitionTo(target);
            invoice.SyncStatus = retryable ? SyncStatus.Queued : SyncStatus.Failed;
        }

        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            AuditActions.InvoiceSyncFailed, nameof(Invoice), invoice.Id,
            newValue: new { Category = category.ToString(), Code = code, Retryable = retryable },
            reason: message, cancellationToken: cancellationToken);

        _logger.LogWarning(
            "Invoice {InvoiceId} synchronisation failed: {Category}/{Code} — {Message}",
            invoice.Id, category, code, message);
    }
}
