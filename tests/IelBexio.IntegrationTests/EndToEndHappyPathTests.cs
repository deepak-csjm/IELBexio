using IelBexio.Application.Bexio;
using IelBexio.Application.Invoices;
using IelBexio.Application.Mapping;
using IelBexio.Application.Sources;
using IelBexio.Application.Sync;
using IelBexio.Connectors.Bexio.Mock;
using IelBexio.Domain.Audit;
using IelBexio.Domain.Common;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Mapping;
using IelBexio.Domain.Sync;
using IelBexio.Domain.Workflow;
using IelBexio.Infrastructure.Persistence;
using IelBexio.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IelBexio.IntegrationTests;

/// <summary>
/// The end-to-end scenario the specification requires in §31, executed against a real PostgreSQL
/// database and the real service composition — all twenty steps, in order, including step 20's
/// requirement to prove that repeating the import creates no duplicate Bexio invoice.
/// </summary>
[Collection(SharedPostgresServer.Name)]
public sealed class EndToEndHappyPathTests
{
    private readonly PostgresFixture _postgres;

    public EndToEndHappyPathTests(PostgresFixture postgres) => _postgres = postgres;

    private const string ReferenceOrderId = "gid://shopify/Order/10001";

    [Fact]
    public async Task The_complete_specified_happy_path_runs_from_shopify_import_to_bexio_synchronisation()
    {
        await using var host = await TestHost.CreateAsync(_postgres, "e2e_happy");

        // ---- Steps 2-3: import the Shopify order, persisting the raw payload --------------------
        var summary = await host.AsAdminAsync(async sp =>
        {
            var reference = sp.GetRequiredService<IBexioReferenceSyncService>();
            var refreshed = await reference.RefreshAsync();
            refreshed.Succeeded.Should().BeTrue("tax mappings are derived from Bexio's own configuration, never hardcoded");

            var import = sp.GetRequiredService<IImportService>();
            var result = await import.ImportAsync(SourceSystem.Shopify, new SourceQuery { SourceDocumentId = ReferenceOrderId });
            result.Succeeded.Should().BeTrue(result.ErrorMessage);
            return result.Value!;
        });

        summary.Created.Should().Be(1);

        var invoiceId = await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var invoice = await db.Invoices.SingleAsync(i => i.SourceDocumentId == ReferenceOrderId);

            // Step 4: normalised into the canonical model with the amounts §31 specifies.
            invoice.InvoiceNumber.Should().Be("INV-10001");
            invoice.Currency.Should().Be("CHF");
            invoice.SubtotalAmount.Should().Be(1000.00m);
            invoice.TaxAmount.Should().Be(81.00m);
            invoice.TotalAmount.Should().Be(1081.00m);
            invoice.WorkflowState.Should().Be(InvoiceWorkflowState.Imported);

            // Step 3: the raw payload is retained for provenance and replay.
            var raw = await db.RawSourcePayloads.SingleAsync(p => p.SourceDocumentId == ReferenceOrderId);
            raw.Payload.Should().Contain("INV-10001");
            raw.PayloadSha256.Should().MatchRegex("^[0-9a-f]{64}$");

            // Step 6: the customer was resolved.
            invoice.CustomerId.Should().NotBeNull();

            return invoice.Id;
        });

        // ---- Steps 5, 7, 8: validate, determine tax, propose Bexio mappings -----------------------
        await host.AsAdminAsync(async sp =>
        {
            var processing = sp.GetRequiredService<IInvoiceProcessingService>();
            var result = await processing.ProcessAsync(invoiceId);
            result.Succeeded.Should().BeTrue();

            result.Value!.HasErrors.Should().BeFalse(
                because: string.Join("; ", result.Value.Issues));

            var db = sp.GetRequiredService<AppDbContext>();
            var assessment = await db.TaxAssessments.SingleAsync(a => a.InvoiceId == invoiceId);

            assessment.InternalTaxCode.Should().Be("CH-VAT-STD-8.1");
            assessment.RatePercent.Should().Be(8.1m);
            assessment.TaxAmount.Should().Be(81.00m);

            // Step 8: a Bexio tax id was proposed, and it came from discovered reference data.
            assessment.ProposedBexioTaxId.Should().NotBeNullOrWhiteSpace();
            assessment.ProposedBexioTaxId.Should().StartWith("mock-tax-");
        });

        // ---- Step 10: the human supplies the customer mapping the preflight requires -------------
        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var invoice = await db.Invoices.Include(i => i.Customer).SingleAsync(i => i.Id == invoiceId);

            var bexio = sp.GetRequiredService<IBexioClient>();
            var contacts = await bexio.SearchContactsAsync("ABC Swiss");
            contacts.Should().NotBeEmpty("the reviewer chooses from what Bexio actually reports");

            var mappings = sp.GetRequiredService<IMappingService>();
            await mappings.UpsertCustomerMappingAsync(
                invoice.CustomerId!.Value, contacts[0].Id, contacts[0].Name,
                MappingOrigin.Manual, "reviewer@test.example");

            // The demo Bexio account reports several revenue accounts, so the reviewer picks one
            // rather than the system guessing.
            var accounts = await bexio.GetAccountsAsync();
            var goods = accounts.First(a => a.AccountNumber == "3200");
            await mappings.UpsertAccountMappingAsync(
                InternalAccountCodes.RevenueGoods, goods.Id, goods.AccountNumber, goods.Name,
                MappingOrigin.Manual, "reviewer@test.example");
        });

        // ---- Step 9 and 15 (preview): the review UI's pre-flight now passes -----------------------
        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var invoice = await db.Invoices.Include(i => i.Lines).Include(i => i.Customer).SingleAsync(i => i.Id == invoiceId);
            var lines = invoice.Lines.OrderBy(l => l.LineNumber).ToList();
            var assessments = await db.TaxAssessments.Where(a => a.InvoiceId == invoiceId).ToListAsync();

            var preflight = sp.GetRequiredService<BexioPreflightService>();
            var preview = await preflight.BuildAsync(invoice, lines, assessments, requireConnection: false);

            preview.CanSynchronize.Should().BeTrue(
                because: string.Join("; ", preview.Report.Issues.Select(i => i.ToString())));

            preview.BexioContactId.Should().NotBeNullOrWhiteSpace();
            preview.TotalNet.Should().Be(1000.00m);
            preview.TotalTax.Should().Be(81.00m);
            preview.TotalGross.Should().Be(1081.00m);
            preview.Lines.Should().ContainSingle();
            preview.Lines[0].BexioTaxId.Should().NotBeNullOrWhiteSpace();
            preview.Lines[0].BexioAccountId.Should().NotBeNullOrWhiteSpace();
        });

        // ---- Steps 11-12: a human verifies and then explicitly approves ---------------------------
        await host.AsAdminAsync(async sp =>
        {
            var review = sp.GetRequiredService<IReviewService>();

            // The reference invoice validates cleanly, so it is Validated rather than NeedsReview and
            // goes straight to submission.
            (await review.SubmitForApprovalAsync(invoiceId)).Succeeded.Should().BeTrue();

            var approval = sp.GetRequiredService<IApprovalService>();
            var approved = await approval.ApproveAsync(invoiceId, "Verified against the source order.");
            approved.Succeeded.Should().BeTrue(approved.ErrorMessage);
        });

        // ---- Step 13: the approval wrote an outbox event in the same transaction ------------------
        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.SuppressTenantFilter = true;

            var invoice = await db.Invoices.SingleAsync(i => i.Id == invoiceId);
            invoice.WorkflowState.Should().Be(InvoiceWorkflowState.QueuedForBexio);
            invoice.ApprovalStatus.Should().Be(ApprovalStatus.Approved);

            var outbox = await db.OutboxMessages.SingleAsync(m => m.Status == OutboxStatus.Pending);
            outbox.MessageType.Should().Be(OutboxMessageTypes.SyncInvoiceToBexio);
            outbox.CorrelationId.Should().Be(invoice.CorrelationId);

            (await db.Approvals.CountAsync(a => a.EntityId == invoiceId && a.Decision == ApprovalDecision.Approved))
                .Should().Be(1);
        });

        // ---- Steps 14-18: the worker processes it and creates the Bexio invoice --------------------
        var batch = await RunOutboxBatchAsync(host);
        batch.Processed.Should().Be(1, "the queued invoice should have been synchronised");

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.SuppressTenantFilter = true;

            var invoice = await db.Invoices.SingleAsync(i => i.Id == invoiceId);

            // Steps 17-18.
            invoice.BexioInvoiceId.Should().NotBeNullOrWhiteSpace();
            invoice.WorkflowState.Should().Be(InvoiceWorkflowState.Synced);
            invoice.SyncStatus.Should().Be(SyncStatus.Synced);

            var attempt = await db.SynchronizationAttempts.SingleAsync(a => a.EntityId == invoiceId);
            attempt.Status.Should().Be(SyncAttemptStatus.Succeeded);
            attempt.ExternalId.Should().Be(invoice.BexioInvoiceId);
            attempt.IdempotencyKey.Should().NotBeNullOrWhiteSpace();
            attempt.RequestHash.Should().MatchRegex("^[0-9a-f]{64}$");

            // The tax actually applied is recorded, not only the one proposed.
            var assessment = await db.TaxAssessments.SingleAsync(a => a.InvoiceId == invoiceId);
            assessment.AppliedBexioTaxId.Should().NotBeNullOrWhiteSpace();
        });

        // The invoice really exists in the Bexio mock, with the right money.
        var mock = host.GetSingleton<MockBexioClient>();
        mock.CreatedInvoiceCount.Should().Be(1);
        mock.CreatedInvoices.Single().TotalGross.Should().Be(1081.00m);
        mock.CreatedInvoices.Single().TotalNet.Should().Be(1000.00m);
        mock.CreatedInvoices.Single().TotalTax.Should().Be(81.00m);

        // ---- Step 19: the whole lifecycle is auditable under one correlation id ---------------------
        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.SuppressTenantFilter = true;

            var invoice = await db.Invoices.SingleAsync(i => i.Id == invoiceId);
            var actions = await db.AuditEvents
                .Where(e => e.EntityId == invoiceId)
                .Select(e => e.Action)
                .ToListAsync();

            actions.Should().Contain(AuditActions.InvoiceImported);
            actions.Should().Contain(AuditActions.InvoiceValidated);
            actions.Should().Contain(AuditActions.InvoiceSubmittedForApproval);
            actions.Should().Contain(AuditActions.InvoiceApproved);
            actions.Should().Contain(AuditActions.InvoiceQueuedForSync);
            actions.Should().Contain(AuditActions.InvoiceSyncSucceeded);

            // Regression: the worker once wrote its audit events with an empty tenant id, because the
            // dispatcher spans tenants and never set one. The two most important records in the system —
            // "we posted this to an accounting system" — were therefore invisible to the tenant that
            // owned them. These must be readable through the tenant-filtered query, not just present
            // in the table.
            var tenantScopedSyncEvents = await db.AuditEvents
                .Where(e => e.TenantId == invoice.TenantId
                            && (e.Action == AuditActions.InvoiceSyncStarted || e.Action == AuditActions.InvoiceSyncSucceeded))
                .CountAsync();

            tenantScopedSyncEvents.Should().Be(2, "worker-written audit events must belong to the invoice's tenant");

            (await db.AuditEvents.CountAsync(e => e.TenantId == Guid.Empty))
                .Should().Be(0, "no audit event may be orphaned without a tenant");

            // A complete lifecycle must be traceable with one correlation id (§27).
            var byCorrelation = await db.AuditEvents.CountAsync(e => e.CorrelationId == invoice.CorrelationId);
            byCorrelation.Should().BeGreaterThan(0);

            // Provenance links canonical fields back to their Shopify source fields (§22).
            var provenance = await db.FieldProvenances.Where(p => p.EntityId == invoiceId).ToListAsync();
            provenance.Should().Contain(p => p.FieldPath == "totalAmount" && p.SourceFieldPath!.Contains("currentTotalPriceSet"));
        });

        // ---- Step 20: repeat the import and prove no duplicate Bexio invoice is created -------------
        var second = await host.AsAdminAsync(async sp =>
        {
            var import = sp.GetRequiredService<IImportService>();
            var result = await import.ImportAsync(SourceSystem.Shopify, new SourceQuery { SourceDocumentId = ReferenceOrderId });
            result.Succeeded.Should().BeTrue();
            return result.Value!;
        });

        second.Created.Should().Be(0, "the source document was already imported at this version");
        second.DuplicatesSkipped.Should().Be(1);

        var secondBatch = await RunOutboxBatchAsync(host);
        secondBatch.Claimed.Should().Be(0, "a duplicate import must not queue a second synchronisation");

        mock.CreatedInvoiceCount.Should().Be(1, "no duplicate invoice may be created in Bexio");

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.SuppressTenantFilter = true;

            (await db.Invoices.CountAsync(i => i.SourceDocumentId == ReferenceOrderId)).Should().Be(1);
            (await db.SynchronizationAttempts.CountAsync()).Should().Be(1);
        });
    }

    /// <summary>Drives exactly one outbox batch, so the test controls timing rather than a background timer.</summary>
    private static Task<OutboxBatchResult> RunOutboxBatchAsync(TestHost host) =>
        host.AsUserAsync("system:outbox-dispatcher", [Application.Abstractions.AppRoles.Admin], async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.SuppressTenantFilter = true;

            var processor = new OutboxProcessor(
                sp.GetRequiredService<IOutboxStore>(),
                sp.GetRequiredService<IInvoiceSynchronizationService>(),
                new OutboxOptions { MaxAttempts = 3, BaseBackoff = TimeSpan.FromMilliseconds(10) },
                sp.GetRequiredService<Application.Abstractions.IClock>(),
                applyTenant: tenantId => sp.GetRequiredService<Infrastructure.Services.AmbientTenantContext>().Set(tenantId));

            return await processor.ProcessBatchAsync();
        });
}
