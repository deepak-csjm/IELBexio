using System.Text;
using IelBexio.Application.Bexio;
using IelBexio.Application.Documents;
using IelBexio.Application.Invoices;
using IelBexio.Application.Mapping;
using IelBexio.Application.Sync;
using IelBexio.Connectors.Bexio.Mock;
using IelBexio.Domain.Audit;
using IelBexio.Domain.Documents;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Mapping;
using IelBexio.Domain.Workflow;
using IelBexio.Infrastructure.Persistence;
using IelBexio.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IelBexio.IntegrationTests;

/// <summary>
/// The document ingestion path of §11, from upload through to a Bexio invoice — proving that
/// specification §37 criterion 5 ("structured *and* document ingestion are both supported") holds for
/// the document half as well as the structured half.
/// </summary>
[Collection(SharedPostgresServer.Name)]
public sealed class DocumentIngestionTests
{
    private readonly PostgresFixture _postgres;

    public DocumentIngestionTests(PostgresFixture postgres) => _postgres = postgres;

    private const string ValidCsv = """
        invoiceNumber,invoiceDate,currency,customerName,customerVatNumber,customerCountry,sku,description,quantity,unitPrice,taxRatePercent
        DOC-5001,2026-03-15,CHF,Helvetia Retail AG,CHE-105.980.910,CH,SKU-ALPHA,"Alpha Widget, boxed",4,250.00,8.1
        DOC-5001,2026-03-15,CHF,Helvetia Retail AG,CHE-105.980.910,CH,SKU-BOOK,Printed Manual,2,50.00,2.6
        """;

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task An_uploaded_csv_invoice_travels_all_the_way_to_a_bexio_invoice_with_ai_disabled()
    {
        await using var host = await TestHost.CreateAsync(_postgres, "doc_e2e");

        var ingestion = await host.AsAdminAsync(async sp =>
        {
            await sp.GetRequiredService<IBexioReferenceSyncService>().RefreshAsync();

            var result = await sp.GetRequiredService<IDocumentIngestionService>()
                .IngestAsync(Bytes(ValidCsv), "invoice-DOC-5001.csv", "text/csv");

            result.Succeeded.Should().BeTrue(result.ErrorMessage);
            return result.Value!;
        });

        // Extracted deterministically — no AI is configured in this host.
        ingestion.Status.Should().Be(DocumentProcessingStatus.Extracted);
        ingestion.ExtractionMethod.Should().Be(ExtractionMethod.CsvParser);
        ingestion.ContentType.Should().Be("text/csv");
        ingestion.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
        ingestion.InvoiceId.Should().NotBeNull();

        var invoiceId = ingestion.InvoiceId!.Value;

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var invoice = await db.Invoices.Include(i => i.Lines).SingleAsync(i => i.Id == invoiceId);

            invoice.InvoiceNumber.Should().Be("DOC-5001");
            invoice.Currency.Should().Be("CHF");
            invoice.Lines.Should().HaveCount(2);

            // 1000.00 at 8.1% = 81.00; 100.00 at 2.6% = 2.60.
            invoice.SubtotalAmount.Should().Be(1100.00m);
            invoice.TaxAmount.Should().Be(83.60m);
            invoice.TotalAmount.Should().Be(1183.60m);

            // The quoted description containing commas survived intact.
            invoice.Lines.Should().Contain(l => l.Description == "Alpha Widget, boxed");

            // Validation ran automatically at ingestion.
            invoice.ValidationStatus.Should().NotBe(ValidationStatus.NotValidated);
            invoice.ExtractionStatus.Should().Be(ExtractionStatus.Succeeded);

            // The document is linked both ways, and an extraction result was recorded.
            var artifact = await db.DocumentArtifacts.SingleAsync(d => d.Id == ingestion.DocumentId);
            artifact.InvoiceId.Should().Be(invoiceId);
            invoice.PrimaryDocumentArtifactId.Should().Be(artifact.Id);

            (await db.ExtractionResults.CountAsync(r => r.DocumentArtifactId == artifact.Id)).Should().Be(1);

            // Provenance records that this came from a document parser, not from a source API.
            var provenance = await db.FieldProvenances.Where(p => p.EntityId == invoiceId).ToListAsync();
            provenance.Should().Contain(p => p.Origin == Domain.Common.ValueOrigin.DocumentParser);
        });

        // Map, verify, approve and synchronise — the same path a Shopify invoice takes.
        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var invoice = await db.Invoices.SingleAsync(i => i.Id == invoiceId);

            var bexio = sp.GetRequiredService<IBexioClient>();
            var contacts = await bexio.SearchContactsAsync(null);
            var accounts = await bexio.GetAccountsAsync();
            var mappings = sp.GetRequiredService<IMappingService>();

            await mappings.UpsertCustomerMappingAsync(invoice.CustomerId!.Value, contacts[0].Id, contacts[0].Name, MappingOrigin.Manual, "test");
            await mappings.UpsertAccountMappingAsync(InternalAccountCodes.RevenueGoods, accounts[0].Id, accounts[0].AccountNumber, accounts[0].Name, MappingOrigin.Manual, "test");

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

            var approved = await sp.GetRequiredService<IApprovalService>().ApproveAsync(invoiceId, "Checked against the uploaded document.");
            approved.Succeeded.Should().BeTrue(approved.ErrorMessage);
        });

        var batch = await host.AsUserAsync("system:outbox-dispatcher", [Application.Abstractions.AppRoles.Admin], async sp =>
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

        batch.Processed.Should().Be(1, "a document-sourced invoice follows exactly the same approval and sync path");
        host.GetSingleton<MockBexioClient>().CreatedInvoiceCount.Should().Be(1);

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.SuppressTenantFilter = true;

            var invoice = await db.Invoices.SingleAsync(i => i.Id == invoiceId);
            invoice.WorkflowState.Should().Be(InvoiceWorkflowState.Synced);
            invoice.BexioInvoiceId.Should().NotBeNullOrWhiteSpace();
        });
    }

    [Fact]
    public async Task Uploading_identical_content_twice_ingests_it_once()
    {
        await using var host = await TestHost.CreateAsync(_postgres, "doc_dupe");

        var first = await host.AsAdminAsync(sp =>
            sp.GetRequiredService<IDocumentIngestionService>().IngestAsync(Bytes(ValidCsv), "a.csv", "text/csv"));

        // A different filename, byte-identical content: still a duplicate, because the hash identifies
        // content rather than name.
        var second = await host.AsAdminAsync(sp =>
            sp.GetRequiredService<IDocumentIngestionService>().IngestAsync(Bytes(ValidCsv), "renamed-copy.csv", "text/csv"));

        second.Value!.Status.Should().Be(DocumentProcessingStatus.DuplicateOfExisting);
        second.Value.DuplicateOfDocumentId.Should().Be(first.Value!.DocumentId);

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.DocumentArtifacts.CountAsync()).Should().Be(1);
            (await db.Invoices.CountAsync()).Should().Be(1, "a duplicate document must not create a second invoice");
            (await db.AuditEvents.CountAsync(e => e.Action == AuditActions.DocumentDuplicate)).Should().Be(1);
        });
    }

    [Fact]
    public async Task An_executable_disguised_as_a_pdf_is_rejected_and_never_stored()
    {
        await using var host = await TestHost.CreateAsync(_postgres, "doc_exe");

        byte[] executable = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00];

        var result = await host.AsAdminAsync(sp =>
            sp.GetRequiredService<IDocumentIngestionService>().IngestAsync(executable, "invoice.pdf", "application/pdf"));

        result.Value!.Status.Should().Be(DocumentProcessingStatus.Rejected);
        result.Value.Message.Should().Contain("executable");

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();

            var artifact = await db.DocumentArtifacts.SingleAsync();
            artifact.BlobUri.Should().Be("(not stored)", "rejected content must not reach the blob store");
            artifact.Sha256.Should().MatchRegex("^[0-9a-f]{64}$", "the hash is kept so repeat attempts are traceable");

            (await db.Invoices.CountAsync()).Should().Be(0);
            (await db.AuditEvents.CountAsync(e => e.Action == AuditActions.DocumentRejected)).Should().Be(1);
        });
    }

    [Fact]
    public async Task A_malformed_csv_is_stored_but_reports_extraction_failure_rather_than_a_partial_invoice()
    {
        await using var host = await TestHost.CreateAsync(_postgres, "doc_bad_csv");

        const string broken = """
            invoiceNumber,currency,description,quantity,unitPrice
            DOC-6001,CHF,Widget,2,not-a-number
            """;

        var result = await host.AsAdminAsync(sp =>
            sp.GetRequiredService<IDocumentIngestionService>().IngestAsync(Bytes(broken), "broken.csv", "text/csv"));

        result.Value!.Status.Should().Be(DocumentProcessingStatus.Failed);
        result.Value.InvoiceId.Should().BeNull("a half-extracted invoice is more dangerous than none");

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.Invoices.CountAsync()).Should().Be(0);
        });
    }

    [Fact]
    public async Task A_printed_pdf_is_stored_and_classified_but_refused_rather_than_partially_extracted()
    {
        // A printed page is a design, not a format. The document is retained and classified, and the
        // caller is told exactly why nothing was read from it — never handed a half-filled invoice.
        await using var host = await TestHost.CreateAsync(_postgres, "doc_pdf");

        var pdf = await File.ReadAllBytesAsync(Path.Combine(FixturePaths.Directory("documents"), "printed-invoice.pdf"));

        var result = await host.AsAdminAsync(sp =>
            sp.GetRequiredService<IDocumentIngestionService>().IngestAsync(pdf, "scan.pdf", "application/pdf"));

        result.Value!.Status.Should().Be(DocumentProcessingStatus.Classified);
        result.Value.Kind.Should().Be(DocumentKind.InvoicePdf);
        result.Value.InvoiceId.Should().BeNull();
        result.Value.Message.Should().Contain("AI extraction");

        await host.AsAdminAsync(async sp =>
        {
            var artifact = await sp.GetRequiredService<AppDbContext>().DocumentArtifacts.SingleAsync();
            artifact.BlobUri.Should().StartWith("local://", "an accepted document is stored even when it cannot be extracted");
        });
    }

    [Fact]
    public async Task A_factur_x_pdf_reaches_a_canonical_invoice_with_no_ai_involved()
    {
        // The one PDF that can be read exactly: the invoice travels inside it as XML. This runs with AI
        // disabled, which is what proves no model was consulted.
        await using var host = await TestHost.CreateAsync(_postgres, "doc_facturx");

        var pdf = await File.ReadAllBytesAsync(Path.Combine(FixturePaths.Directory("documents"), "facturx-invoice.pdf"));

        var result = await host.AsAdminAsync(sp =>
            sp.GetRequiredService<IDocumentIngestionService>().IngestAsync(pdf, "invoice.pdf", "application/pdf"));

        result.Value!.Status.Should().Be(DocumentProcessingStatus.Extracted);
        result.Value.ExtractionMethod.Should().Be(ExtractionMethod.EmbeddedXmlParser);
        result.Value.InvoiceId.Should().NotBeNull();

        await host.AsAdminAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();

            var invoice = await db.Invoices.Include(i => i.Lines).SingleAsync();
            invoice.InvoiceNumber.Should().Be("FX-2026-0042");
            invoice.Currency.Should().Be("CHF");
            invoice.TotalAmount.Should().Be(1081.00m);
            invoice.Lines.Should().HaveCount(2);

            // No AI was involved, so nothing may have been recorded against the AI budget.
            (await db.AiUsageRecords.CountAsync()).Should().Be(0);
            (await db.AiProposals.CountAsync()).Should().Be(0);
        });
    }

    [Fact]
    public async Task A_malformed_pdf_fails_rather_than_being_reported_as_merely_unsupported()
    {
        // The distinction that makes the refusal above meaningful: a broken document is a failure, so
        // real corruption stays visible instead of hiding among files that are simply not readable here.
        await using var host = await TestHost.CreateAsync(_postgres, "doc_badpdf");

        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\ntrailer\n%%EOF");

        var result = await host.AsAdminAsync(sp =>
            sp.GetRequiredService<IDocumentIngestionService>().IngestAsync(pdf, "broken.pdf", "application/pdf"));

        result.Value!.Status.Should().Be(DocumentProcessingStatus.Failed);
        result.Value.Message.Should().Contain("could not be read");
    }

    [Fact]
    public async Task A_read_only_user_cannot_upload_a_document()
    {
        await using var host = await TestHost.CreateAsync(_postgres, "doc_role");

        var result = await host.AsUserAsync("viewer@test.example", [Application.Abstractions.AppRoles.ReadOnly], sp =>
            sp.GetRequiredService<IDocumentIngestionService>().IngestAsync(Bytes(ValidCsv), "a.csv", "text/csv"));

        result.Failed.Should().BeTrue();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }
}
