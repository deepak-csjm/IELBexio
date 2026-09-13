using IelBexio.Application.Abstractions;
using IelBexio.Application.Bexio;
using IelBexio.Application.Invoices;
using IelBexio.Application.Mapping;
using IelBexio.Application.Sources;
using IelBexio.Application.Sync;
using IelBexio.Connectors.Bexio.Configuration;
using IelBexio.Connectors.Bexio.Mock;
using IelBexio.Domain.Audit;
using IelBexio.Domain.Common;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Mapping;
using IelBexio.Domain.Sync;
using IelBexio.Domain.Validation;
using IelBexio.Domain.Workflow;
using IelBexio.Infrastructure.Persistence;
using IelBexio.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IelBexio.IntegrationTests;

/// <summary>
/// The negative scenarios of specification §32, exercised against a real database.
/// <para>
/// These matter more than the happy path. A financial integration is judged by what it does when
/// something is wrong: the requirement is that every one of these ends in a safe, visible, recorded
/// state — never in a silent success, a duplicate posting, or an endless retry.
/// </para>
/// </summary>
[Collection(SharedPostgresServer.Name)]
public sealed class NegativeScenarioTests
{
    private readonly PostgresFixture _postgres;

    public NegativeScenarioTests(PostgresFixture postgres) => _postgres = postgres;

    private const string ReferenceOrderId = "gid://shopify/Order/10001";
    private const string BrokenTotalOrderId = "gid://shopify/Order/10004";
    private const string MissingVatOrderId = "gid://shopify/Order/10005";

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>Imports one fixture order, refreshes Bexio reference data, and returns the invoice id.</summary>
    private static async Task<Guid> ImportAsync(TestHost host, string sourceDocumentId, bool withMappings = true)
    {
        return await host.AsAdminAsync(async sp =>
        {
            await sp.GetRequiredService<IBexioReferenceSyncService>().RefreshAsync();

            var import = sp.GetRequiredService<IImportService>();
            var result = await import.ImportAsync(SourceSystem.Shopify, new SourceQuery { SourceDocumentId = sourceDocumentId });
            result.Succeeded.Should().BeTrue(result.ErrorMessage);

            var db = sp.GetRequiredService<AppDbContext>();
            var invoice = await db.Invoices.Include(i => i.Customer).SingleAsync(i => i.SourceDocumentId == sourceDocumentId);

            await sp.GetRequiredService<IInvoiceProcessingService>().ProcessAsync(invoice.Id);

            if (withMappings && invoice.CustomerId is { } customerId)
            {
                var bexio = sp.GetRequiredService<IBexioClient>();
                var contacts = await bexio.SearchContactsAsync(null);
                var accounts = await bexio.GetAccountsAsync();
                var mappings = sp.GetRequiredService<IMappingService>();

                await mappings.UpsertCustomerMappingAsync(customerId, contacts[0].Id, contacts[0].Name, MappingOrigin.Manual, "test");
                await mappings.UpsertAccountMappingAsync(
                    InternalAccountCodes.RevenueGoods, accounts[0].Id, accounts[0].AccountNumber, accounts[0].Name, MappingOrigin.Manual, "test");
                await mappings.UpsertAccountMappingAsync(
                    InternalAccountCodes.RevenueShipping, accounts[0].Id, accounts[0].AccountNumber, accounts[0].Name, MappingOrigin.Manual, "test");
            }

            return invoice.Id;
        });
    }

    private static async Task ApproveAsync(TestHost host, Guid invoiceId)
    {
        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var invoice = await db.Invoices.SingleAsync(i => i.Id == invoiceId);
            var review = sp.GetRequiredService<IReviewService>();

            if (invoice.WorkflowState == InvoiceWorkflowState.NeedsReview)
            {
                foreach (var assessment in await db.TaxAssessments.Where(a => a.InvoiceId == invoiceId).ToListAsync())
                {
                    await review.VerifyTaxAsync(assessment.Id, assessment.ProposedBexioTaxId);
                }

                (await review.VerifyAsync(invoiceId)).Succeeded.Should().BeTrue();
            }

            (await review.SubmitForApprovalAsync(invoiceId)).Succeeded.Should().BeTrue();
            var approved = await sp.GetRequiredService<IApprovalService>().ApproveAsync(invoiceId, "test approval");
            approved.Succeeded.Should().BeTrue(approved.ErrorMessage);
        });
    }

    private static Task<OutboxBatchResult> RunOutboxAsync(TestHost host, int maxAttempts = 3) =>
        host.AsUserAsync("system:outbox-dispatcher", [AppRoles.Admin], async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.SuppressTenantFilter = true;

            var processor = new OutboxProcessor(
                sp.GetRequiredService<IOutboxStore>(),
                sp.GetRequiredService<IInvoiceSynchronizationService>(),
                new OutboxOptions { MaxAttempts = maxAttempts, BaseBackoff = TimeSpan.FromMilliseconds(1), MaxBackoff = TimeSpan.FromMilliseconds(5) },
                sp.GetRequiredService<IClock>());

            return await processor.ProcessBatchAsync();
        });

    private static void SetMockFailure(TestHost host, MockFailureScenario scenario)
    {
        // The mock options object is a singleton, so a test can change the scenario mid-flight.
        host.GetSingleton<IOptions<MockBexioOptions>>().Value.FailureScenario = scenario;
    }

    // ---------------------------------------------------------------------------------------------
    // Validation and data-quality failures
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_invoice_whose_totals_do_not_balance_is_blocked_and_never_reaches_approval()
    {
        await using var host = await TestHost.CreateAsync(_postgres, "neg_totals");
        var invoiceId = await ImportAsync(host, BrokenTotalOrderId);

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var invoice = await db.Invoices.SingleAsync(i => i.Id == invoiceId);

            invoice.WorkflowState.Should().Be(InvoiceWorkflowState.ValidationFailed);
            invoice.ValidationStatus.Should().Be(ValidationStatus.Failed);

            // It cannot be pushed forward, because the state machine forbids the transition.
            var submitted = await sp.GetRequiredService<IReviewService>().SubmitForApprovalAsync(invoiceId);
            submitted.Failed.Should().BeTrue();
            submitted.ErrorCode.Should().Be("INVALID_STATE");
        });
    }

    [Fact]
    public async Task An_unapproved_invoice_cannot_be_synchronised_even_if_the_worker_is_asked_to_directly()
    {
        // Acceptance criterion 12, tested by attacking it rather than by assuming the UI prevents it.
        await using var host = await TestHost.CreateAsync(_postgres, "neg_unapproved");
        var invoiceId = await ImportAsync(host, ReferenceOrderId);

        var result = await host.AsAdminAsync(sp =>
            sp.GetRequiredService<IInvoiceSynchronizationService>().SynchronizeAsync(invoiceId, "test-correlation"));

        result.Failed.Should().BeTrue();
        result.ErrorMessage.Should().Contain("not eligible");
        host.GetSingleton<MockBexioClient>().CreatedInvoiceCount.Should().Be(0);
    }

    [Fact]
    public async Task A_missing_customer_mapping_fails_pre_flight_rather_than_posting_to_a_guessed_contact()
    {
        await using var host = await TestHost.CreateAsync(_postgres, "neg_customer_map");
        var invoiceId = await ImportAsync(host, ReferenceOrderId, withMappings: false);

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var invoice = await db.Invoices.Include(i => i.Lines).Include(i => i.Customer).SingleAsync(i => i.Id == invoiceId);
            var assessments = await db.TaxAssessments.Where(a => a.InvoiceId == invoiceId).ToListAsync();

            var preview = await sp.GetRequiredService<BexioPreflightService>()
                .BuildAsync(invoice, invoice.Lines.ToList(), assessments);

            preview.CanSynchronize.Should().BeFalse();
            preview.Report.Contains(ValidationCodes.CustomerMappingMissing).Should().BeTrue();
        });
    }

    [Fact]
    public async Task A_missing_tax_mapping_fails_pre_flight()
    {
        await using var host = await TestHost.CreateAsync(_postgres, "neg_tax_map");
        var invoiceId = await ImportAsync(host, ReferenceOrderId);

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();

            // Remove every tax mapping, simulating an unconfigured Bexio account.
            await db.TaxMappings.ExecuteDeleteAsync();

            var invoice = await db.Invoices.Include(i => i.Lines).Include(i => i.Customer).SingleAsync(i => i.Id == invoiceId);
            foreach (var line in invoice.Lines)
            {
                line.BexioTaxId = null;
            }

            await db.SaveChangesAsync();

            var assessments = await db.TaxAssessments.Where(a => a.InvoiceId == invoiceId).ToListAsync();
            var preview = await sp.GetRequiredService<BexioPreflightService>()
                .BuildAsync(invoice, invoice.Lines.ToList(), assessments);

            preview.CanSynchronize.Should().BeFalse();
            preview.Report.Contains(ValidationCodes.TaxMappingMissing).Should().BeTrue();
        });
    }

    [Fact]
    public async Task An_inactive_bexio_tax_is_refused_by_pre_flight()
    {
        await using var host = await TestHost.CreateAsync(_postgres, "neg_inactive_tax");
        var invoiceId = await ImportAsync(host, ReferenceOrderId);

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();

            // Point the mapping at the mock's deliberately inactive 7.7% tax.
            var mapping = await db.TaxMappings.FirstAsync(m => m.InternalTaxCode == "CH-VAT-STD-8.1");
            mapping.BexioTaxId = "mock-tax-std-77";
            mapping.BexioTaxIsActive = false;
            mapping.BexioTaxRatePercent = 8.1m;
            await db.SaveChangesAsync();

            var invoice = await db.Invoices.Include(i => i.Lines).Include(i => i.Customer).SingleAsync(i => i.Id == invoiceId);
            foreach (var line in invoice.Lines)
            {
                line.BexioTaxId = null;
            }

            await db.SaveChangesAsync();

            var assessments = await db.TaxAssessments.Where(a => a.InvoiceId == invoiceId).ToListAsync();
            var preview = await sp.GetRequiredService<BexioPreflightService>()
                .BuildAsync(invoice, invoice.Lines.ToList(), assessments);

            preview.CanSynchronize.Should().BeFalse();
            preview.Report.Contains(ValidationCodes.TaxMappingInactive).Should().BeTrue();
        });
    }

    [Fact]
    public async Task A_bexio_tax_whose_rate_contradicts_our_determination_is_refused()
    {
        // Posting here would book a different tax amount than the one that was validated and approved.
        await using var host = await TestHost.CreateAsync(_postgres, "neg_rate_mismatch");
        var invoiceId = await ImportAsync(host, ReferenceOrderId);

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var mapping = await db.TaxMappings.FirstAsync(m => m.InternalTaxCode == "CH-VAT-STD-8.1");
            mapping.BexioTaxRatePercent = 2.6m;
            await db.SaveChangesAsync();

            var invoice = await db.Invoices.Include(i => i.Lines).Include(i => i.Customer).SingleAsync(i => i.Id == invoiceId);
            var assessments = await db.TaxAssessments.Where(a => a.InvoiceId == invoiceId).ToListAsync();

            var preview = await sp.GetRequiredService<BexioPreflightService>()
                .BuildAsync(invoice, invoice.Lines.ToList(), assessments);

            preview.CanSynchronize.Should().BeFalse();
            preview.Report.Contains(ValidationCodes.TaxRateUnexpected).Should().BeTrue();
        });
    }

    [Fact]
    public async Task A_missing_vat_number_routes_to_review_but_does_not_block_the_import()
    {
        await using var host = await TestHost.CreateAsync(_postgres, "neg_missing_vat");
        var invoiceId = await ImportAsync(host, MissingVatOrderId);

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var invoice = await db.Invoices.SingleAsync(i => i.Id == invoiceId);

            invoice.WorkflowState.Should().Be(InvoiceWorkflowState.NeedsReview);
            invoice.ValidationStatus.Should().Be(ValidationStatus.PassedWithWarnings);
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Duplicate prevention
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_repeated_import_of_the_same_source_document_creates_no_second_invoice()
    {
        await using var host = await TestHost.CreateAsync(_postgres, "neg_dup_import");
        await ImportAsync(host, ReferenceOrderId);

        var second = await host.AsAdminAsync(sp =>
            sp.GetRequiredService<IImportService>().ImportAsync(SourceSystem.Shopify, new SourceQuery { SourceDocumentId = ReferenceOrderId }));

        second.Value!.DuplicatesSkipped.Should().Be(1);
        second.Value.Created.Should().Be(0);

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.Invoices.CountAsync(i => i.SourceDocumentId == ReferenceOrderId)).Should().Be(1);
        });
    }

    [Fact]
    public async Task The_database_itself_refuses_a_duplicate_source_document_even_if_application_code_is_bypassed()
    {
        // Belt and braces: the unique index is the real guarantee, not the application check above it.
        await using var host = await TestHost.CreateAsync(_postgres, "neg_dup_constraint");
        await ImportAsync(host, ReferenceOrderId);

        var act = () => host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var original = await db.Invoices.AsNoTracking().SingleAsync(i => i.SourceDocumentId == ReferenceOrderId);

            db.Invoices.Add(new Invoice
            {
                TenantId = original.TenantId,
                SourceSystem = original.SourceSystem,
                SourceDocumentId = original.SourceDocumentId,
                SourceDocumentVersion = original.SourceDocumentVersion,
                Currency = "CHF",
            });

            await db.SaveChangesAsync();
        });

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Processing_the_same_outbox_message_twice_does_not_create_a_second_bexio_invoice()
    {
        await using var host = await TestHost.CreateAsync(_postgres, "neg_dup_sync");
        var invoiceId = await ImportAsync(host, ReferenceOrderId);
        await ApproveAsync(host, invoiceId);

        (await RunOutboxAsync(host)).Processed.Should().Be(1);

        // Simulate a worker restart that re-queues an already-processed message.
        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.SuppressTenantFilter = true;

            await db.OutboxMessages.ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxStatus.Pending)
                .SetProperty(m => m.NextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        });

        var second = await RunOutboxAsync(host);
        second.Claimed.Should().Be(1);
        second.Processed.Should().Be(1, "the replay succeeds by recognising the prior synchronisation");

        host.GetSingleton<MockBexioClient>().CreatedInvoiceCount.Should().Be(1, "no duplicate may be created");

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.SuppressTenantFilter = true;

            (await db.SynchronizationAttempts.CountAsync()).Should().Be(1);
            (await db.AuditEvents.CountAsync(e => e.Action == AuditActions.InvoiceSyncSkippedDuplicate)).Should().Be(1);
        });
    }

    [Fact]
    public async Task A_double_submitted_approval_approves_once_and_refuses_the_second()
    {
        // The browser double-click / double-submit case from §19.
        await using var host = await TestHost.CreateAsync(_postgres, "neg_double_approve");
        var invoiceId = await ImportAsync(host, ReferenceOrderId);

        await host.AsAdminAsync(async sp =>
        {
            (await sp.GetRequiredService<IReviewService>().SubmitForApprovalAsync(invoiceId)).Succeeded.Should().BeTrue();

            var approval = sp.GetRequiredService<IApprovalService>();
            (await approval.ApproveAsync(invoiceId, "first")).Succeeded.Should().BeTrue();

            var second = await approval.ApproveAsync(invoiceId, "second");
            second.Failed.Should().BeTrue();
            second.ErrorCode.Should().Be("INVALID_STATE");
        });

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.SuppressTenantFilter = true;

            (await db.OutboxMessages.CountAsync()).Should().Be(1, "a double submit must not queue two synchronisations");
            (await db.Approvals.CountAsync(a => a.EntityId == invoiceId && a.Decision == ApprovalDecision.Approved)).Should().Be(1);
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Bexio failures
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(MockFailureScenario.ValidationFailure, InvoiceWorkflowState.BexioRejected, true)]
    [InlineData(MockFailureScenario.AuthorizationFailure, InvoiceWorkflowState.SyncFailed, true)]
    [InlineData(MockFailureScenario.Conflict, InvoiceWorkflowState.BexioRejected, true)]
    public async Task A_non_retryable_bexio_failure_dead_letters_the_message_instead_of_retrying_forever(
        MockFailureScenario scenario, InvoiceWorkflowState expectedState, bool expectDeadLetter)
    {
        await using var host = await TestHost.CreateAsync(_postgres, "neg_bexio_perm");
        var invoiceId = await ImportAsync(host, ReferenceOrderId);
        await ApproveAsync(host, invoiceId);

        SetMockFailure(host, scenario);
        var batch = await RunOutboxAsync(host);

        batch.Processed.Should().Be(0);
        batch.DeadLettered.Should().Be(expectDeadLetter ? 1 : 0);

        host.GetSingleton<MockBexioClient>().CreatedInvoiceCount.Should().Be(0);

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.SuppressTenantFilter = true;

            var invoice = await db.Invoices.SingleAsync(i => i.Id == invoiceId);
            invoice.WorkflowState.Should().Be(expectedState);

            var message = await db.OutboxMessages.SingleAsync();
            message.Status.Should().Be(OutboxStatus.DeadLettered);
            message.LastError.Should().NotBeNullOrWhiteSpace();

            (await db.AuditEvents.CountAsync(e => e.Action == AuditActions.InvoiceSyncFailed)).Should().BeGreaterThan(0);
        });
    }

    [Theory]
    [InlineData(MockFailureScenario.RateLimited)]
    [InlineData(MockFailureScenario.ServerError)]
    [InlineData(MockFailureScenario.NetworkFailure)]
    [InlineData(MockFailureScenario.Timeout)]
    public async Task A_retryable_bexio_failure_is_rescheduled_rather_than_dead_lettered(MockFailureScenario scenario)
    {
        await using var host = await TestHost.CreateAsync(_postgres, "neg_bexio_retry");
        var invoiceId = await ImportAsync(host, ReferenceOrderId);
        await ApproveAsync(host, invoiceId);

        SetMockFailure(host, scenario);
        var batch = await RunOutboxAsync(host, maxAttempts: 5);

        batch.Failed.Should().Be(1);
        batch.DeadLettered.Should().Be(0);

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.SuppressTenantFilter = true;

            var message = await db.OutboxMessages.SingleAsync();
            message.Status.Should().Be(OutboxStatus.Pending, "a transient failure must remain retryable");
            message.AttemptCount.Should().Be(1);
            message.NextAttemptAt.Should().BeAfter(DateTimeOffset.UtcNow.AddSeconds(-1));

            var invoice = await db.Invoices.SingleAsync(i => i.Id == invoiceId);
            invoice.WorkflowState.Should().Be(InvoiceWorkflowState.QueuedForBexio);
        });
    }

    [Fact]
    public async Task A_transient_failure_that_clears_succeeds_on_a_later_attempt()
    {
        await using var host = await TestHost.CreateAsync(_postgres, "neg_retry_recovers");
        var invoiceId = await ImportAsync(host, ReferenceOrderId);
        await ApproveAsync(host, invoiceId);

        SetMockFailure(host, MockFailureScenario.ServerError);
        (await RunOutboxAsync(host, maxAttempts: 5)).Failed.Should().Be(1);

        SetMockFailure(host, MockFailureScenario.None);

        // Bring the retry forward so the test does not wait for the backoff.
        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.SuppressTenantFilter = true;
            await db.OutboxMessages.ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        });

        (await RunOutboxAsync(host, maxAttempts: 5)).Processed.Should().Be(1);
        host.GetSingleton<MockBexioClient>().CreatedInvoiceCount.Should().Be(1);
    }

    [Fact]
    public async Task Retrying_a_failure_beyond_the_attempt_limit_dead_letters_it_rather_than_looping_forever()
    {
        await using var host = await TestHost.CreateAsync(_postgres, "neg_retry_exhausted");
        var invoiceId = await ImportAsync(host, ReferenceOrderId);
        await ApproveAsync(host, invoiceId);

        SetMockFailure(host, MockFailureScenario.ServerError);

        for (var i = 0; i < 3; i++)
        {
            await RunOutboxAsync(host, maxAttempts: 2);
            await host.AsAdminAsync(async sp =>
            {
                var db = sp.GetRequiredService<AppDbContext>();
                db.SuppressTenantFilter = true;
                await db.OutboxMessages.ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
            });
        }

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.SuppressTenantFilter = true;

            var message = await db.OutboxMessages.SingleAsync();
            message.Status.Should().Be(OutboxStatus.DeadLettered);
        });

        host.GetSingleton<MockBexioClient>().CreatedInvoiceCount.Should().Be(0);
    }

    // ---------------------------------------------------------------------------------------------
    // Authorisation and tampering
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_reviewer_without_the_approver_role_cannot_approve()
    {
        await using var host = await TestHost.CreateAsync(_postgres, "neg_role");
        var invoiceId = await ImportAsync(host, ReferenceOrderId);

        await host.AsAdminAsync(sp => sp.GetRequiredService<IReviewService>().SubmitForApprovalAsync(invoiceId));

        var result = await host.AsUserAsync("reviewer@test.example", [AppRoles.Reviewer], sp =>
            sp.GetRequiredService<IApprovalService>().ApproveAsync(invoiceId, "attempting"));

        result.Failed.Should().BeTrue();
        result.ErrorCode.Should().Be("FORBIDDEN");
        host.GetSingleton<MockBexioClient>().CreatedInvoiceCount.Should().Be(0);
    }

    [Fact]
    public async Task A_read_only_user_cannot_correct_an_invoice()
    {
        await using var host = await TestHost.CreateAsync(_postgres, "neg_readonly");
        var invoiceId = await ImportAsync(host, ReferenceOrderId);

        var result = await host.AsUserAsync("viewer@test.example", [AppRoles.ReadOnly], sp =>
            sp.GetRequiredService<IReviewService>().ApplyCorrectionsAsync(
                invoiceId, [new FieldCorrection("invoiceNumber", "TAMPERED", "nope")]));

        result.Failed.Should().BeTrue();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task An_invoice_edited_after_approval_is_refused_at_dispatch_and_returned_to_review()
    {
        // The approve-then-edit-then-post attack. The approval no longer describes what would be sent.
        await using var host = await TestHost.CreateAsync(_postgres, "neg_stale_approval");
        var invoiceId = await ImportAsync(host, ReferenceOrderId);
        await ApproveAsync(host, invoiceId);

        await host.AsAdminAsync(async sp =>
        {
            // Tamper directly with the database, bypassing the review service's own guard, and do it
            // *consistently* — every amount still balances, so deterministic validation and pre-flight
            // both pass. Only the approval fingerprint can catch this, which is exactly the point:
            // an inconsistent tamper would be caught by arithmetic and would not test the guard at all.
            var db = sp.GetRequiredService<AppDbContext>();
            var invoice = await db.Invoices.Include(i => i.Lines).SingleAsync(i => i.Id == invoiceId);

            var line = invoice.Lines[0];
            line.UnitPrice *= 10m;                  // 250 -> 2500
            line.NetAmount = line.Quantity * line.UnitPrice;
            line.TaxAmount = decimal.Round(line.NetAmount * line.TaxRatePercent / 100m, 4, MidpointRounding.ToEven);
            line.GrossAmount = line.NetAmount + line.TaxAmount;

            invoice.SubtotalAmount = line.NetAmount;
            invoice.TaxAmount = line.TaxAmount;
            invoice.TotalAmount = line.GrossAmount;

            await db.SaveChangesAsync();
        });

        var batch = await RunOutboxAsync(host);

        batch.Processed.Should().Be(0);
        host.GetSingleton<MockBexioClient>().CreatedInvoiceCount.Should().Be(0, "a changed invoice must not be posted under an old approval");

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.SuppressTenantFilter = true;

            var invoice = await db.Invoices.SingleAsync(i => i.Id == invoiceId);
            invoice.WorkflowState.Should().Be(InvoiceWorkflowState.NeedsReview);
            invoice.ApprovalStatus.Should().Be(ApprovalStatus.NotSubmitted, "the approval no longer stands");
        });
    }

    [Fact]
    public async Task A_correction_is_refused_once_an_invoice_has_been_approved()
    {
        await using var host = await TestHost.CreateAsync(_postgres, "neg_edit_approved");
        var invoiceId = await ImportAsync(host, ReferenceOrderId);
        await ApproveAsync(host, invoiceId);

        var result = await host.AsAdminAsync(sp =>
            sp.GetRequiredService<IReviewService>().ApplyCorrectionsAsync(
                invoiceId, [new FieldCorrection("invoiceNumber", "CHANGED", "late edit")]));

        result.Failed.Should().BeTrue();
        result.ErrorCode.Should().Be("INVALID_STATE");
    }

    [Fact]
    public async Task A_correction_to_an_unsupported_field_path_is_refused()
    {
        // Guards against a caller writing to arbitrary properties by supplying a path.
        await using var host = await TestHost.CreateAsync(_postgres, "neg_bad_field");
        var invoiceId = await ImportAsync(host, ReferenceOrderId);

        var result = await host.AsAdminAsync(sp =>
            sp.GetRequiredService<IReviewService>().ApplyCorrectionsAsync(
                invoiceId, [new FieldCorrection("WorkflowState", "Synced", "nice try")]));

        result.Failed.Should().BeTrue();
        result.ErrorCode.Should().Be("UNSUPPORTED_FIELD");
    }

    // ---------------------------------------------------------------------------------------------
    // Tenant isolation
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task One_tenant_cannot_see_another_tenants_invoices()
    {
        await using var host = await TestHost.CreateAsync(_postgres, "neg_tenant");
        await ImportAsync(host, ReferenceOrderId);

        // A different tenant id in the same database must see nothing.
        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.Invoices.CountAsync()).Should().Be(1);
        });

        using var scope = host.GetSingleton<IServiceScopeFactory>().CreateScope();
        scope.ServiceProvider.GetRequiredService<Infrastructure.Services.AmbientTenantContext>().Set(Guid.CreateVersion7());
        var otherTenantDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        (await otherTenantDb.Invoices.CountAsync()).Should().Be(0, "the global query filter isolates tenants");
        (await otherTenantDb.TaxAssessments.CountAsync()).Should().Be(0);
        (await otherTenantDb.AuditEvents.CountAsync()).Should().Be(0);
    }

    // ---------------------------------------------------------------------------------------------
    // Reconciliation
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_invoice_that_vanishes_from_bexio_is_flagged_for_reconciliation()
    {
        await using var host = await TestHost.CreateAsync(_postgres, "neg_reconcile");
        var invoiceId = await ImportAsync(host, ReferenceOrderId);
        await ApproveAsync(host, invoiceId);
        (await RunOutboxAsync(host)).Processed.Should().Be(1);

        await host.AsAdminAsync(async sp =>
        {
            // Point at a Bexio id the mock does not hold, simulating deletion in Bexio.
            var db = sp.GetRequiredService<AppDbContext>();
            var invoice = await db.Invoices.SingleAsync(i => i.Id == invoiceId);
            invoice.BexioInvoiceId = "mock-invoice-deleted";
            await db.SaveChangesAsync();

            var summary = await sp.GetRequiredService<IReconciliationService>().ReconcileAsync();
            summary.Succeeded.Should().BeTrue();
            summary.Value!.Missing.Should().Be(1);

            var reloaded = await db.Invoices.SingleAsync(i => i.Id == invoiceId);
            reloaded.WorkflowState.Should().Be(InvoiceWorkflowState.ReconciliationRequired);
        });
    }
}
