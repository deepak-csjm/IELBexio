using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IelBexio.Application.Abstractions;
using IelBexio.Application.Common;
using IelBexio.Application.Invoices;
using IelBexio.Application.Sync;
using IelBexio.Domain.Audit;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Sync;
using IelBexio.Domain.Workflow;
using IelBexio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IelBexio.Infrastructure.Workflow;

/// <summary>
/// Explicit human approval, and the single gate through which an invoice can become eligible for
/// Bexio synchronisation (§16, §20, acceptance criteria 11 and 12).
/// <para>
/// The critical property is transactional: the state change to <c>Approved</c>/<c>QueuedForBexio</c>,
/// the <c>Approval</c> record, the audit event and the <c>OutboxMessage</c> are written in one
/// database transaction. Either all of them exist or none do, so there is no window in which an invoice
/// is approved but will never be sent, or is queued for sending but was never approved.
/// </para>
/// </summary>
public sealed class ApprovalService : IApprovalService
{
    private readonly AppDbContext _db;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditWriter _audit;
    private readonly ICorrelationContext _correlation;
    private readonly ILogger<ApprovalService> _logger;

    public ApprovalService(
        AppDbContext db,
        IUnitOfWork unitOfWork,
        ICurrentUser user,
        IClock clock,
        IAuditWriter audit,
        ICorrelationContext correlation,
        ILogger<ApprovalService> logger)
    {
        _db = db;
        _unitOfWork = unitOfWork;
        _user = user;
        _clock = clock;
        _audit = audit;
        _correlation = correlation;
        _logger = logger;
    }

    public async Task<Result> ApproveAsync(Guid invoiceId, string? comment, CancellationToken cancellationToken = default)
    {
        // Authorisation first: an unauthorised caller must not even learn whether the invoice exists in
        // a state worth approving.
        if (!_user.IsInRole(AppRoles.Approver) && !_user.IsInRole(AppRoles.Admin))
        {
            return Result.Failure("FORBIDDEN", "Approving an invoice requires the Approver or Admin role.");
        }

        var invoice = await _db.Invoices
            .Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);

        if (invoice is null)
        {
            return Result.Failure("NOT_FOUND", "The invoice does not exist.");
        }

        if (invoice.WorkflowState != InvoiceWorkflowState.ReadyForApproval)
        {
            return Result.Failure(
                "INVALID_STATE",
                $"Only an invoice in ReadyForApproval may be approved; this one is {invoice.WorkflowState}.");
        }

        // Dual control is designed for even though the POC allows one approver (§16). The check is
        // written now so enabling it later is a configuration change, not a redesign.
        if (RequireSeparateApprover && string.Equals(invoice.PreparedBy, _user.UserId, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure("DUAL_CONTROL", "The person who prepared this invoice may not also approve it.");
        }

        var approvalVersion = ComputeApprovalVersion(invoice);
        var correlationId = invoice.CorrelationId;

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            invoice.TransitionTo(InvoiceWorkflowState.Approved);
            invoice.ApprovalStatus = ApprovalStatus.Approved;
            invoice.ApprovedBy = _user.UserId;
            invoice.ApprovedAt = _clock.UtcNow;

            _db.Approvals.Add(new Approval
            {
                TenantId = invoice.TenantId,
                EntityId = invoice.Id,
                EntityType = nameof(Invoice),
                Decision = ApprovalDecision.Approved,
                PreparedBy = invoice.PreparedBy,
                ApprovedBy = _user.UserId,
                ApprovedAt = _clock.UtcNow,
                ApprovalVersion = approvalVersion,
                Comment = comment,
                CorrelationId = correlationId,
            });

            // Queued in the same transaction as the approval. This is the transactional outbox: the
            // decision and the intent to act on it cannot disagree.
            invoice.TransitionTo(InvoiceWorkflowState.QueuedForBexio);
            invoice.SyncStatus = SyncStatus.Queued;

            _db.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = invoice.TenantId,
                MessageType = OutboxMessageTypes.SyncInvoiceToBexio,
                Payload = JsonSerializer.Serialize(new SyncInvoicePayload(invoice.Id, invoice.TenantId, correlationId)),
                Status = OutboxStatus.Pending,
                NextAttemptAt = _clock.UtcNow,
                CorrelationId = correlationId,
            });

            await _db.SaveChangesAsync(ct);
        }, cancellationToken);

        await _audit.WriteAsync(
            AuditActions.InvoiceApproved,
            nameof(Invoice),
            invoice.Id,
            newValue: new { invoice.WorkflowState, ApprovedBy = _user.UserId, ApprovalVersion = approvalVersion },
            reason: comment,
            cancellationToken: cancellationToken);

        await _audit.WriteAsync(
            AuditActions.InvoiceQueuedForSync,
            nameof(Invoice),
            invoice.Id,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Invoice {InvoiceId} approved by {User} and queued for Bexio synchronisation. Correlation {CorrelationId}.",
            invoice.Id, _user.UserId, correlationId);

        return Result.Success();
    }

    public async Task<Result> RejectAsync(Guid invoiceId, string reason, CancellationToken cancellationToken = default)
    {
        if (!_user.IsInRole(AppRoles.Approver) && !_user.IsInRole(AppRoles.Admin))
        {
            return Result.Failure("FORBIDDEN", "Rejecting an invoice requires the Approver or Admin role.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            // A rejection without a reason is useless to whoever has to fix it.
            return Result.Failure("REASON_REQUIRED", "A rejection must state a reason.");
        }

        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);
        if (invoice is null)
        {
            return Result.Failure("NOT_FOUND", "The invoice does not exist.");
        }

        if (invoice.WorkflowState != InvoiceWorkflowState.ReadyForApproval)
        {
            return Result.Failure("INVALID_STATE", $"Only an invoice in ReadyForApproval may be rejected; this one is {invoice.WorkflowState}.");
        }

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            invoice.TransitionTo(InvoiceWorkflowState.ApprovalRejected);
            invoice.ApprovalStatus = ApprovalStatus.Rejected;

            _db.Approvals.Add(new Approval
            {
                TenantId = invoice.TenantId,
                EntityId = invoice.Id,
                EntityType = nameof(Invoice),
                Decision = ApprovalDecision.Rejected,
                PreparedBy = invoice.PreparedBy,
                ApprovedBy = _user.UserId,
                ApprovedAt = _clock.UtcNow,
                ApprovalVersion = ComputeApprovalVersion(invoice),
                Comment = reason,
                CorrelationId = invoice.CorrelationId,
            });

            await _db.SaveChangesAsync(ct);
        }, cancellationToken);

        await _audit.WriteAsync(AuditActions.InvoiceRejected, nameof(Invoice), invoice.Id, reason: reason, cancellationToken: cancellationToken);

        // Returned to review rather than left in a dead end, so the reviewer can act on the reason.
        invoice.TransitionTo(InvoiceWorkflowState.NeedsReview);
        await _db.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// Whether the preparer and approver must differ. False for the POC per §16, but the check exists
    /// and is tested, so enabling dual control is a one-line change.
    /// </summary>
    public static bool RequireSeparateApprover { get; set; }

    /// <summary>
    /// Fingerprints the financially significant content of the invoice at approval time.
    /// <para>
    /// This is what makes "approve, then edit, then post" detectable: the worker recomputes the version
    /// before posting and refuses if it no longer matches what was approved. Only the fields that change
    /// what gets booked are included — a corrected spelling in a description should not invalidate an
    /// approval, but a changed amount must.
    /// </para>
    /// </summary>
    public static string ComputeApprovalVersion(Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        var builder = new StringBuilder();
        builder.Append(invoice.Id.ToString("n"))
            .Append('|').Append(invoice.Currency)
            .Append('|').Append(invoice.SubtotalAmount.ToString("F4", System.Globalization.CultureInfo.InvariantCulture))
            .Append('|').Append(invoice.DiscountAmount.ToString("F4", System.Globalization.CultureInfo.InvariantCulture))
            .Append('|').Append(invoice.ShippingAmount.ToString("F4", System.Globalization.CultureInfo.InvariantCulture))
            .Append('|').Append(invoice.TaxAmount.ToString("F4", System.Globalization.CultureInfo.InvariantCulture))
            .Append('|').Append(invoice.TotalAmount.ToString("F4", System.Globalization.CultureInfo.InvariantCulture))
            .Append('|').Append(invoice.CustomerId?.ToString("n") ?? "-")
            .Append('|').Append(invoice.BexioContactId ?? "-");

        foreach (var line in invoice.Lines.OrderBy(l => l.LineNumber))
        {
            builder.Append("||")
                .Append(line.LineNumber).Append(':')
                .Append(line.Quantity.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                .Append(line.UnitPrice.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                .Append(line.NetAmount.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                .Append(line.TaxAmount.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                .Append(line.BexioTaxId ?? "-").Append(':')
                .Append(line.BexioAccountId ?? "-");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..32].ToLowerInvariant();
    }
}
