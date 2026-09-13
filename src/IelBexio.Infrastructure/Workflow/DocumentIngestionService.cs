using IelBexio.Application.Abstractions;
using IelBexio.Application.Common;
using IelBexio.Application.Documents;
using IelBexio.Application.Invoices;
using IelBexio.Domain.Audit;
using IelBexio.Domain.Common;
using IelBexio.Domain.Documents;
using IelBexio.Infrastructure.Blob;
using IelBexio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IelBexio.Infrastructure.Workflow;

/// <summary>
/// Implements the §11 document pipeline. Shared by the REST API and the Blazor UI so both behave
/// identically.
/// <para>
/// The ordering is deliberate: inspect before storing, deduplicate before storing, and record a
/// rejection even though the bytes are discarded. Storing first would mean an executable briefly lands
/// in the blob store; discarding a rejection silently would hide someone repeatedly trying to upload
/// one.
/// </para>
/// </summary>
public sealed class DocumentIngestionService : IDocumentIngestionService
{
    private readonly AppDbContext _db;
    private readonly FileInspector _inspector;
    private readonly IBlobStore _blobs;
    private readonly IEnumerable<IDeterministicDocumentExtractor> _extractors;
    private readonly IInvoiceProcessingService _processing;
    private readonly ITenantContext _tenant;
    private readonly IClock _clock;
    private readonly IAuditWriter _audit;
    private readonly IProvenanceWriter _provenance;
    private readonly ICurrentUser _user;
    private readonly ILogger<DocumentIngestionService> _logger;

    public DocumentIngestionService(
        AppDbContext db,
        FileInspector inspector,
        IBlobStore blobs,
        IEnumerable<IDeterministicDocumentExtractor> extractors,
        IInvoiceProcessingService processing,
        ITenantContext tenant,
        IClock clock,
        IAuditWriter audit,
        IProvenanceWriter provenance,
        ICurrentUser user,
        ILogger<DocumentIngestionService> logger)
    {
        _db = db;
        _inspector = inspector;
        _blobs = blobs;
        _extractors = extractors;
        _processing = processing;
        _tenant = tenant;
        _clock = clock;
        _audit = audit;
        _provenance = provenance;
        _user = user;
        _logger = logger;
    }

    public async Task<Result<DocumentIngestionResult>> IngestAsync(
        byte[] content, string fileName, string? declaredContentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!_user.IsInRole(AppRoles.Reviewer) && !_user.IsInRole(AppRoles.Admin) && !_user.IsInRole(AppRoles.IntegrationManager))
        {
            return Result<DocumentIngestionResult>.Failure("FORBIDDEN", "Uploading a document requires the Reviewer, IntegrationManager or Admin role.");
        }

        // 1. Inspect. The declared content type is a claim, not a fact.
        var inspection = _inspector.Inspect(content, declaredContentType, fileName);

        // 2. Deduplicate on content, before anything is stored.
        var existing = await _db.DocumentArtifacts.FirstOrDefaultAsync(d => d.Sha256 == inspection.Sha256, cancellationToken);
        if (existing is not null)
        {
            await _audit.WriteAsync(
                AuditActions.DocumentDuplicate, nameof(DocumentArtifact), existing.Id,
                newValue: new { inspection.Sha256, fileName }, cancellationToken: cancellationToken);

            return Result<DocumentIngestionResult>.Success(new DocumentIngestionResult(
                existing.Id, DocumentProcessingStatus.DuplicateOfExisting, inspection.Sha256,
                existing.ContentType, existing.Kind, existing.InvoiceId, null,
                $"Byte-for-byte identical to '{existing.OriginalFileName}', ingested on {existing.IngestedAt:yyyy-MM-dd}. Not ingested again.",
                existing.Id));
        }

        var artifact = new DocumentArtifact
        {
            TenantId = _tenant.TenantId,
            OriginalFileName = fileName,
            ContentType = inspection.DetectedContentType,
            SizeBytes = inspection.SizeBytes,
            Sha256 = inspection.Sha256,
            Kind = inspection.Kind,
            IngestedAt = _clock.UtcNow,
            SourceSystem = SourceSystem.DocumentUpload,
        };

        // 3. A rejected file is recorded but not stored. The record makes repeated attempts visible.
        if (!inspection.IsAccepted)
        {
            artifact.Status = DocumentProcessingStatus.Rejected;
            artifact.RejectionReason = inspection.RejectionReason;
            artifact.BlobUri = "(not stored)";

            _db.DocumentArtifacts.Add(artifact);
            await _db.SaveChangesAsync(cancellationToken);
            await _audit.WriteAsync(
                AuditActions.DocumentRejected, nameof(DocumentArtifact), artifact.Id,
                reason: inspection.RejectionReason, cancellationToken: cancellationToken);

            _logger.LogWarning("Rejected upload '{FileName}': {Reason}", fileName, inspection.RejectionReason);

            return Result<DocumentIngestionResult>.Success(new DocumentIngestionResult(
                artifact.Id, DocumentProcessingStatus.Rejected, inspection.Sha256,
                inspection.DetectedContentType, inspection.Kind, null, null,
                inspection.RejectionReason, null));
        }

        // 4. Store under a generated name — never one derived from the upload (§25).
        var blobName = BlobNaming.Create(_clock.UtcNow, fileName);
        using (var stream = new MemoryStream(content))
        {
            artifact.BlobUri = await _blobs.PutAsync("documents", blobName, stream, inspection.DetectedContentType, cancellationToken);
        }

        artifact.Status = DocumentProcessingStatus.Validated;
        _db.DocumentArtifacts.Add(artifact);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync(AuditActions.DocumentIngested, nameof(DocumentArtifact), artifact.Id, cancellationToken: cancellationToken);

        // 5. Deterministic extraction where the format allows it. No AI involved.
        var extractor = _extractors.FirstOrDefault(x => x.CanExtract(inspection.Kind, inspection.DetectedContentType));

        if (extractor is null)
        {
            artifact.Status = DocumentProcessingStatus.Classified;
            await _db.SaveChangesAsync(cancellationToken);

            return Result<DocumentIngestionResult>.Success(new DocumentIngestionResult(
                artifact.Id, artifact.Status, inspection.Sha256, inspection.DetectedContentType, inspection.Kind, null, null,
                $"Stored. No deterministic extractor handles {inspection.DetectedContentType}; a PDF or image needs AI extraction.",
                null));
        }

        var extraction = extractor.Extract(content, _tenant.TenantId, $"document:{inspection.Sha256[..16]}");

        if (!extraction.Succeeded)
        {
            // A refusal is not a failure: the document is intact and simply needs a human or a model.
            // Recording it as Classified keeps genuine corruption visible instead of burying it among
            // documents that are merely unsupported.
            artifact.Status = extraction.WasRefused
                ? DocumentProcessingStatus.Classified
                : DocumentProcessingStatus.Failed;

            artifact.RejectionReason = extraction.FailureReason;
            await _db.SaveChangesAsync(cancellationToken);

            var message = extraction.WasRefused
                ? $"Stored and classified, not extracted: {extraction.FailureReason}"
                : $"Stored, but extraction failed: {extraction.FailureReason}";

            return Result<DocumentIngestionResult>.Success(new DocumentIngestionResult(
                artifact.Id, artifact.Status, inspection.Sha256, inspection.DetectedContentType, inspection.Kind,
                null, extraction.Method, message, null));
        }

        // 6. Persist the canonical invoice the extractor produced.
        var invoice = extraction.Invoice!;
        invoice.TenantId = _tenant.TenantId;
        invoice.PrimaryDocumentArtifactId = artifact.Id;
        invoice.PreparedBy = _user.UserId;
        invoice.ExtractionStatus = Domain.Invoicing.ExtractionStatus.Succeeded;

        if (extraction.Customer is { } customer)
        {
            customer.TenantId = _tenant.TenantId;
            _db.Customers.Add(customer);
            invoice.CustomerId = customer.Id;
        }

        invoice.Customer = null;
        _db.Invoices.Add(invoice);

        foreach (var line in extraction.Lines)
        {
            line.TenantId = _tenant.TenantId;
            line.InvoiceId = invoice.Id;
            _db.InvoiceLines.Add(line);
        }

        _db.ExtractionResults.Add(new ExtractionResult
        {
            TenantId = _tenant.TenantId,
            DocumentArtifactId = artifact.Id,
            InvoiceId = invoice.Id,
            ExtractorVersion = extraction.ExtractorVersion,
            Method = extraction.Method,
            ExtractedJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                invoice.InvoiceNumber, invoice.InvoiceDate, invoice.Currency,
                invoice.SubtotalAmount, invoice.TaxAmount, invoice.TotalAmount,
                Lines = extraction.Lines.Count,
            }),
            Succeeded = true,
            ProducedAt = _clock.UtcNow,

            // Deterministic extraction is cached by content and extractor version. There is no model or
            // prompt version, which is precisely what distinguishes it from an AI extraction.
            CacheKey = $"deterministic|{extraction.Method}|{extraction.ExtractorVersion}|{inspection.Sha256}",
        });

        artifact.InvoiceId = invoice.Id;
        artifact.Status = DocumentProcessingStatus.Extracted;

        await _db.SaveChangesAsync(cancellationToken);

        await _provenance.RecordAsync(
            nameof(Domain.Invoicing.Invoice), invoice.Id, "totalAmount", invoice.TotalAmount,
            ValueOrigin.DocumentParser, SourceSystem.DocumentUpload, artifact.Sha256,
            sourceFieldPath: fileName,
            transformation: $"Extracted deterministically by {extraction.Method} v{extraction.ExtractorVersion}",
            cancellationToken: cancellationToken);

        // 7. Validate immediately, so anything needing attention reaches the review queue rather than
        // sitting silently.
        await _processing.ProcessAsync(invoice.Id, cancellationToken);

        _logger.LogInformation(
            "Ingested '{FileName}' ({Method}) as invoice {InvoiceId}.", fileName, extraction.Method, invoice.Id);

        return Result<DocumentIngestionResult>.Success(new DocumentIngestionResult(
            artifact.Id, DocumentProcessingStatus.Extracted, inspection.Sha256,
            inspection.DetectedContentType, inspection.Kind, invoice.Id, extraction.Method,
            $"Extracted deterministically ({extraction.Method}) into invoice {invoice.InvoiceNumber ?? invoice.Id.ToString()[..8]}.",
            null));
    }
}
