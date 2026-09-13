using System.Globalization;
using System.Text.Json;
using IelBexio.Application.Abstractions;
using IelBexio.Application.Common;
using IelBexio.Application.Invoices;
using IelBexio.Domain.Ai;
using IelBexio.Domain.Audit;
using IelBexio.Domain.Common;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Tax;
using IelBexio.Domain.Workflow;
using IelBexio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IelBexio.Infrastructure.Workflow;

/// <summary>
/// Human review operations (§15).
/// <para>
/// Everything here is audited and provenance-tracked, because these are the points at which a person
/// changes a financial record. The one rule that matters most: accepting an AI proposal records the
/// value's origin as a human decision informed by AI, not as an AI-authored value. The human is
/// accountable for the acceptance — that is the entire premise of §40.
/// </para>
/// </summary>
public sealed class ReviewService : IReviewService
{
    private readonly AppDbContext _db;
    private readonly IInvoiceProcessingService _processing;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditWriter _audit;
    private readonly IProvenanceWriter _provenance;
    private readonly ILogger<ReviewService> _logger;

    public ReviewService(
        AppDbContext db,
        IInvoiceProcessingService processing,
        ICurrentUser user,
        IClock clock,
        IAuditWriter audit,
        IProvenanceWriter provenance,
        ILogger<ReviewService> logger)
    {
        _db = db;
        _processing = processing;
        _user = user;
        _clock = clock;
        _audit = audit;
        _provenance = provenance;
        _logger = logger;
    }

    private bool CanReview => _user.IsInRole(AppRoles.Reviewer) || _user.IsInRole(AppRoles.Approver) || _user.IsInRole(AppRoles.Admin);

    public async Task<Result> ApplyCorrectionsAsync(Guid invoiceId, IReadOnlyList<FieldCorrection> corrections, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(corrections);

        if (!CanReview)
        {
            return Result.Failure("FORBIDDEN", "Correcting an invoice requires the Reviewer, Approver or Admin role.");
        }

        var invoice = await _db.Invoices.Include(i => i.Lines).Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);

        if (invoice is null)
        {
            return Result.Failure("NOT_FOUND", "The invoice does not exist.");
        }

        // Correcting an approved invoice would invalidate the approval. Refusing is clearer than
        // silently revoking it behind the reviewer's back.
        if (invoice.WorkflowState is InvoiceWorkflowState.Approved or InvoiceWorkflowState.QueuedForBexio
            or InvoiceWorkflowState.Syncing or InvoiceWorkflowState.Synced)
        {
            return Result.Failure("INVALID_STATE", $"An invoice in {invoice.WorkflowState} cannot be edited.");
        }

        foreach (var correction in corrections)
        {
            var applied = ApplyOne(invoice, correction);
            if (!applied.Succeeded)
            {
                return applied;
            }

            await _audit.WriteAsync(
                AuditActions.InvoiceFieldCorrected, nameof(Invoice), invoice.Id,
                newValue: new { correction.FieldPath, correction.NewValue },
                reason: correction.Reason, cancellationToken: cancellationToken);

            await _provenance.RecordAsync(
                nameof(Invoice), invoice.Id, correction.FieldPath, correction.NewValue,
                ValueOrigin.Human, invoice.SourceSystem, invoice.SourceDocumentId,
                transformation: "Corrected during human review", modifiedBy: _user.UserId,
                cancellationToken: cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);

        // Re-validate so the reviewer immediately sees whether the correction resolved the issue.
        await _processing.ProcessAsync(invoiceId, cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// Applies one correction. The set of correctable fields is an explicit allow-list rather than
    /// reflection: a reviewer must not be able to write to arbitrary properties by supplying a path.
    /// </summary>
    private static Result ApplyOne(Invoice invoice, FieldCorrection correction)
    {
        var path = correction.FieldPath;

        switch (path)
        {
            case "invoiceNumber":
                invoice.InvoiceNumber = correction.NewValue;
                return Result.Success();

            case "invoiceDate":
                if (!DateOnly.TryParse(correction.NewValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out var invoiceDate))
                {
                    return Result.Failure("INVALID_VALUE", $"'{correction.NewValue}' is not a valid date.");
                }

                invoice.InvoiceDate = invoiceDate;
                return Result.Success();

            case "dueDate":
                if (string.IsNullOrWhiteSpace(correction.NewValue))
                {
                    invoice.DueDate = null;
                    return Result.Success();
                }

                if (!DateOnly.TryParse(correction.NewValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dueDate))
                {
                    return Result.Failure("INVALID_VALUE", $"'{correction.NewValue}' is not a valid date.");
                }

                invoice.DueDate = dueDate;
                return Result.Success();

            case "customer.vatNumber":
                if (invoice.Customer is null)
                {
                    return Result.Failure("NO_CUSTOMER", "The invoice has no customer to correct.");
                }

                invoice.Customer.VatNumber = correction.NewValue;
                return Result.Success();

            case "customer.companyName":
                if (invoice.Customer is null)
                {
                    return Result.Failure("NO_CUSTOMER", "The invoice has no customer to correct.");
                }

                invoice.Customer.CompanyName = correction.NewValue;
                return Result.Success();

            case "billingAddress.countryCode":
                invoice.BillingAddress.CountryCode = correction.NewValue?.ToUpperInvariant();
                return Result.Success();

            default:
                // Monetary line corrections are handled explicitly so the arithmetic stays consistent.
                if (path.StartsWith("lines[", StringComparison.Ordinal))
                {
                    return ApplyLineCorrection(invoice, path, correction.NewValue);
                }

                return Result.Failure("UNSUPPORTED_FIELD", $"'{path}' is not a correctable field.");
        }
    }

    private static Result ApplyLineCorrection(Invoice invoice, string path, string? newValue)
    {
        // Expected shape: lines[N].field
        var closing = path.IndexOf(']', StringComparison.Ordinal);
        if (closing < 0 || !int.TryParse(path[6..closing], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNumber))
        {
            return Result.Failure("UNSUPPORTED_FIELD", $"'{path}' is not a recognised line field path.");
        }

        var line = invoice.Lines.FirstOrDefault(l => l.LineNumber == lineNumber);
        if (line is null)
        {
            return Result.Failure("NOT_FOUND", $"Line {lineNumber} does not exist.");
        }

        var field = path[(closing + 2)..];

        switch (field)
        {
            case "description":
                line.Description = newValue ?? line.Description;
                return Result.Success();

            case "sku":
                line.Sku = newValue;
                return Result.Success();

            case "quantity":
            case "unitPrice":
            case "taxRatePercent":
                if (!decimal.TryParse(newValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
                {
                    return Result.Failure("INVALID_VALUE", $"'{newValue}' is not a valid number.");
                }

                if (field == "quantity")
                {
                    line.Quantity = number;
                }
                else if (field == "unitPrice")
                {
                    line.UnitPrice = number;
                }
                else
                {
                    line.TaxRatePercent = number;
                }

                // Recompute the dependent amounts deterministically rather than letting a reviewer edit
                // net, tax and gross independently into an inconsistent state.
                Recalculate(line);
                RecalculateHeader(invoice);
                return Result.Success();

            default:
                return Result.Failure("UNSUPPORTED_FIELD", $"'{field}' is not a correctable line field.");
        }
    }

    private static void Recalculate(InvoiceLine line)
    {
        line.NetAmount = decimal.Round((line.Quantity * line.UnitPrice) - line.DiscountAmount, Money.StorageScale, MidpointRounding.ToEven);
        line.TaxAmount = decimal.Round(line.NetAmount * line.TaxRatePercent / 100m, Money.StorageScale, MidpointRounding.ToEven);
        line.GrossAmount = decimal.Round(line.NetAmount + line.TaxAmount, Money.StorageScale, MidpointRounding.ToEven);
    }

    private static void RecalculateHeader(Invoice invoice)
    {
        invoice.SubtotalAmount = decimal.Round(
            invoice.Lines.Where(l => l.Kind != LineKind.Shipping).Sum(l => l.NetAmount) + invoice.DiscountAmount,
            Money.StorageScale, MidpointRounding.ToEven);

        invoice.ShippingAmount = decimal.Round(
            invoice.Lines.Where(l => l.Kind == LineKind.Shipping).Sum(l => l.NetAmount),
            Money.StorageScale, MidpointRounding.ToEven);

        invoice.TaxAmount = decimal.Round(invoice.Lines.Sum(l => l.TaxAmount), Money.StorageScale, MidpointRounding.ToEven);

        invoice.TotalAmount = decimal.Round(
            invoice.SubtotalAmount + invoice.ShippingAmount + invoice.TaxAmount - invoice.DiscountAmount,
            Money.StorageScale, MidpointRounding.ToEven);
    }

    public async Task<Result> VerifyAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        if (!CanReview)
        {
            return Result.Failure("FORBIDDEN", "Verifying an invoice requires the Reviewer, Approver or Admin role.");
        }

        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);
        if (invoice is null)
        {
            return Result.Failure("NOT_FOUND", "The invoice does not exist.");
        }

        if (invoice.WorkflowState != InvoiceWorkflowState.NeedsReview)
        {
            return Result.Failure("INVALID_STATE", $"Only an invoice in NeedsReview may be verified; this one is {invoice.WorkflowState}.");
        }

        invoice.TransitionTo(InvoiceWorkflowState.HumanVerified);
        invoice.HumanVerified = true;
        invoice.VerifiedBy = _user.UserId;
        invoice.VerifiedAt = _clock.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync(AuditActions.InvoiceVerified, nameof(Invoice), invoice.Id,
            newValue: new { VerifiedBy = _user.UserId }, cancellationToken: cancellationToken);

        return Result.Success();
    }

    public async Task<Result> VerifyTaxAsync(Guid taxAssessmentId, string? bexioTaxId, CancellationToken cancellationToken = default)
    {
        if (!CanReview)
        {
            return Result.Failure("FORBIDDEN", "Verifying a tax conclusion requires the Reviewer, Approver or Admin role.");
        }

        var assessment = await _db.TaxAssessments.FirstOrDefaultAsync(a => a.Id == taxAssessmentId, cancellationToken);
        if (assessment is null)
        {
            return Result.Failure("NOT_FOUND", "The tax assessment does not exist.");
        }

        var before = new { assessment.ProposedBexioTaxId, assessment.DeterminationMethod, assessment.HumanVerified };

        if (!string.IsNullOrWhiteSpace(bexioTaxId))
        {
            assessment.ProposedBexioTaxId = bexioTaxId;

            var line = await _db.InvoiceLines.FirstOrDefaultAsync(l => l.Id == assessment.InvoiceLineId, cancellationToken);
            if (line is not null)
            {
                line.BexioTaxId = bexioTaxId;
            }
        }

        // A human verification is recorded as HumanEntered, never as AiProposed, whatever informed it.
        assessment.HumanVerified = true;
        assessment.VerifiedBy = _user.UserId;
        assessment.VerifiedAt = _clock.UtcNow;
        assessment.DeterminationMethod = TaxDeterminationMethod.HumanEntered;
        assessment.ConfidenceSignal = 1m;

        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            AuditActions.InvoiceVerified, nameof(TaxAssessment), assessment.Id,
            oldValue: before,
            newValue: new { assessment.ProposedBexioTaxId, assessment.DeterminationMethod, VerifiedBy = _user.UserId },
            cancellationToken: cancellationToken);

        await _provenance.RecordAsync(
            nameof(TaxAssessment), assessment.Id, "proposedBexioTaxId", bexioTaxId,
            ValueOrigin.Human, modifiedBy: _user.UserId,
            transformation: "Verified by a human reviewer", cancellationToken: cancellationToken);

        return Result.Success();
    }

    public async Task<Result> AcceptAiProposalAsync(Guid proposalId, CancellationToken cancellationToken = default)
    {
        if (!CanReview)
        {
            return Result.Failure("FORBIDDEN", "Accepting a proposal requires the Reviewer, Approver or Admin role.");
        }

        var proposal = await _db.AiProposals.FirstOrDefaultAsync(p => p.Id == proposalId, cancellationToken);
        if (proposal is null)
        {
            return Result.Failure("NOT_FOUND", "The proposal does not exist.");
        }

        if (proposal.Status != AiProposalStatus.Pending)
        {
            return Result.Failure("INVALID_STATE", $"The proposal is already {proposal.Status}.");
        }

        var invoice = await _db.Invoices.Include(i => i.Lines).Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == proposal.EntityId, cancellationToken);

        if (invoice is null)
        {
            return Result.Failure("NOT_FOUND", "The invoice the proposal refers to does not exist.");
        }

        var value = JsonSerializer.Deserialize<JsonElement>(proposal.ProposedValueJson);
        var newValue = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();

        // The proposed value goes through exactly the same validated correction path a human typing it
        // would use. There is no privileged write path for AI-originated values.
        var applied = ApplyOne(invoice, new FieldCorrection(proposal.FieldPath, newValue, "Accepted AI proposal"));
        if (applied.Failed)
        {
            return applied;
        }

        proposal.Status = AiProposalStatus.Accepted;
        proposal.ReviewedBy = _user.UserId;
        proposal.ReviewedAt = _clock.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            AuditActions.AiProposalAccepted, nameof(Invoice), invoice.Id,
            newValue: new { proposal.FieldPath, Value = newValue, proposal.Model, proposal.PromptVersion },
            reason: "Accepted by a human reviewer",
            aiInvolved: true, aiProposalId: proposal.Id, cancellationToken: cancellationToken);

        // Origin is AiProposalAccepted: a human made the decision, informed by AI. The distinction
        // matters for accountability and is visible in the provenance trail.
        await _provenance.RecordAsync(
            nameof(Invoice), invoice.Id, proposal.FieldPath, newValue,
            ValueOrigin.AiProposalAccepted, invoice.SourceSystem, invoice.SourceDocumentId,
            transformation: $"AI proposal accepted by {_user.UserId}",
            modifiedBy: _user.UserId, aiProposalId: proposal.Id, cancellationToken: cancellationToken);

        await _processing.ProcessAsync(invoice.Id, cancellationToken);

        _logger.LogInformation("AI proposal {ProposalId} for {FieldPath} accepted by {User}.", proposal.Id, proposal.FieldPath, _user.UserId);

        return Result.Success();
    }

    public async Task<Result> RejectAiProposalAsync(Guid proposalId, string? comment, CancellationToken cancellationToken = default)
    {
        if (!CanReview)
        {
            return Result.Failure("FORBIDDEN", "Rejecting a proposal requires the Reviewer, Approver or Admin role.");
        }

        var proposal = await _db.AiProposals.FirstOrDefaultAsync(p => p.Id == proposalId, cancellationToken);
        if (proposal is null)
        {
            return Result.Failure("NOT_FOUND", "The proposal does not exist.");
        }

        proposal.Status = AiProposalStatus.Rejected;
        proposal.ReviewedBy = _user.UserId;
        proposal.ReviewedAt = _clock.UtcNow;
        proposal.ReviewComment = comment;

        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync(
            AuditActions.AiProposalRejected, nameof(AiProposal), proposal.Id,
            reason: comment, aiInvolved: true, aiProposalId: proposal.Id, cancellationToken: cancellationToken);

        return Result.Success();
    }

    public async Task<Result> SubmitForApprovalAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        if (!CanReview)
        {
            return Result.Failure("FORBIDDEN", "Submitting for approval requires the Reviewer, Approver or Admin role.");
        }

        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);
        if (invoice is null)
        {
            return Result.Failure("NOT_FOUND", "The invoice does not exist.");
        }

        if (invoice.WorkflowState is not (InvoiceWorkflowState.Validated or InvoiceWorkflowState.HumanVerified))
        {
            return Result.Failure(
                "INVALID_STATE",
                $"Only a Validated or HumanVerified invoice may be submitted for approval; this one is {invoice.WorkflowState}.");
        }

        // A record with outstanding AI proposals is not ready for a decision: the reviewer has not yet
        // said what they think of them.
        var pending = await _db.AiProposals.CountAsync(
            p => p.EntityId == invoice.Id && p.Status == AiProposalStatus.Pending, cancellationToken);

        if (pending > 0)
        {
            return Result.Failure(
                "PENDING_PROPOSALS",
                $"{pending} AI proposal(s) are still awaiting a decision. Accept or reject them before submitting for approval.");
        }

        invoice.TransitionTo(InvoiceWorkflowState.ReadyForApproval);
        invoice.ApprovalStatus = ApprovalStatus.PendingApproval;

        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync(AuditActions.InvoiceSubmittedForApproval, nameof(Invoice), invoice.Id, cancellationToken: cancellationToken);

        return Result.Success();
    }
}
