using IelBexio.Application.Abstractions;
using IelBexio.Application.Bexio;
using IelBexio.Application.Common;
using IelBexio.Application.Invoices;
using IelBexio.Domain.Audit;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Workflow;
using IelBexio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IelBexio.Infrastructure.Workflow;

/// <summary>
/// Verifies that what this system believes it posted matches what Bexio actually holds (§20, §14).
/// <para>
/// Worth doing because "we recorded a success" and "the invoice exists in Bexio with the right numbers"
/// are different claims. A network failure after Bexio committed, a later manual deletion, or an edit
/// in Bexio all break the second without touching the first. A discrepancy moves the invoice to
/// <see cref="InvoiceWorkflowState.ReconciliationRequired"/> and raises an audit event; it deliberately
/// does not try to repair anything automatically, because guessing which side is right is exactly the
/// judgement a human should make.
/// </para>
/// </summary>
public sealed class ReconciliationService : IReconciliationService
{
    private readonly AppDbContext _db;
    private readonly IBexioClient _bexio;
    private readonly IAuditWriter _audit;
    private readonly ILogger<ReconciliationService> _logger;

    /// <summary>Tolerance when comparing our total against Bexio's, allowing for its own rounding.</summary>
    private const decimal TotalTolerance = 0.05m;

    public ReconciliationService(AppDbContext db, IBexioClient bexio, IAuditWriter audit, ILogger<ReconciliationService> logger)
    {
        _db = db;
        _bexio = bexio;
        _audit = audit;
        _logger = logger;
    }

    public async Task<Result<ReconciliationSummary>> ReconcileAsync(int maxInvoices = 100, CancellationToken cancellationToken = default)
    {
        var invoices = await _db.Invoices
            .Where(i => i.WorkflowState == InvoiceWorkflowState.Synced && i.BexioInvoiceId != null)
            .OrderByDescending(i => i.UpdatedAt)
            .Take(maxInvoices)
            .ToListAsync(cancellationToken);

        var findings = new List<string>();
        var matched = 0;
        var missing = 0;
        var mismatched = 0;

        foreach (var invoice in invoices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            BexioInvoiceResult? remote;
            try
            {
                remote = await _bexio.GetInvoiceAsync(invoice.BexioInvoiceId!, cancellationToken);
            }
            catch (BexioApiException ex)
            {
                // An unreachable Bexio is not a discrepancy; it is a failed check. Saying otherwise
                // would flood the queue with false positives during an outage.
                findings.Add($"Invoice {invoice.InvoiceNumber}: could not be checked ({ex.Category}).");
                continue;
            }

            if (remote is null)
            {
                missing++;
                findings.Add(
                    $"Invoice {invoice.InvoiceNumber} is recorded as Bexio invoice {invoice.BexioInvoiceId}, " +
                    "but Bexio no longer returns it. It may have been deleted there.");

                await FlagAsync(invoice, "The invoice no longer exists in Bexio.", cancellationToken);
                continue;
            }

            if (remote.TotalGross is { } remoteTotal && Math.Abs(remoteTotal - invoice.TotalAmount) > TotalTolerance)
            {
                mismatched++;
                findings.Add(
                    $"Invoice {invoice.InvoiceNumber}: this system holds {invoice.TotalAmount} {invoice.Currency} " +
                    $"but Bexio reports {remoteTotal}. The two records disagree.");

                await FlagAsync(invoice, $"Total mismatch: local {invoice.TotalAmount}, Bexio {remoteTotal}.", cancellationToken);
                continue;
            }

            matched++;
        }

        await _audit.WriteAsync(
            AuditActions.ReconciliationChecked, nameof(Invoice), null,
            newValue: new { Checked = invoices.Count, matched, missing, mismatched },
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Reconciliation checked {Checked} invoice(s): {Matched} matched, {Missing} missing, {Mismatched} mismatched.",
            invoices.Count, matched, missing, mismatched);

        return Result<ReconciliationSummary>.Success(
            new ReconciliationSummary(invoices.Count, matched, missing, mismatched, findings));
    }

    private async Task FlagAsync(Invoice invoice, string reason, CancellationToken cancellationToken)
    {
        if (InvoiceWorkflow.CanTransition(invoice.WorkflowState, InvoiceWorkflowState.ReconciliationRequired))
        {
            invoice.TransitionTo(InvoiceWorkflowState.ReconciliationRequired);
            await _db.SaveChangesAsync(cancellationToken);
        }

        await _audit.WriteAsync(
            AuditActions.ReconciliationDiscrepancy, nameof(Invoice), invoice.Id,
            reason: reason, cancellationToken: cancellationToken);
    }
}
