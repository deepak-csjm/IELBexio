using System.Security.Cryptography;
using System.Text;
using IelBexio.Application.Abstractions;
using IelBexio.Application.Common;
using IelBexio.Application.Invoices;
using IelBexio.Application.Sources;
using IelBexio.Domain.Audit;
using IelBexio.Domain.Common;
using IelBexio.Domain.Invoicing;
using IelBexio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IelBexio.Infrastructure.Workflow;

/// <summary>
/// Imports source documents into canonical records.
/// <para>
/// The idempotency rule is enforced here and again by a unique index: a source document already present
/// at the same version is <em>skipped</em>, not re-created and not overwritten. That is what makes
/// re-running an import safe and what makes the §31 step 20 requirement ("repeat the same import and
/// prove no duplicate Bexio invoice is created") hold at the ingestion layer rather than only at the
/// Bexio layer.
/// </para>
/// <para>
/// A document that reappears at a <em>new</em> version is a different story: the source changed it, so
/// a new canonical record is created and the old one keeps its own history. Silently mutating an
/// already-approved invoice because the source edited it would be far worse.
/// </para>
/// </summary>
public sealed class ImportService : IImportService
{
    private readonly AppDbContext _db;
    private readonly ISourceConnectorRegistry _connectors;
    private readonly ITenantContext _tenant;
    private readonly IClock _clock;
    private readonly IAuditWriter _audit;
    private readonly IProvenanceWriter _provenance;
    private readonly ICorrelationContext _correlation;
    private readonly ICurrentUser _user;
    private readonly ILogger<ImportService> _logger;

    public ImportService(
        AppDbContext db,
        ISourceConnectorRegistry connectors,
        ITenantContext tenant,
        IClock clock,
        IAuditWriter audit,
        IProvenanceWriter provenance,
        ICorrelationContext correlation,
        ICurrentUser user,
        ILogger<ImportService> logger)
    {
        _db = db;
        _connectors = connectors;
        _tenant = tenant;
        _clock = clock;
        _audit = audit;
        _provenance = provenance;
        _correlation = correlation;
        _user = user;
        _logger = logger;
    }

    public async Task<Result<ImportSummary>> ImportAsync(SourceSystem sourceSystem, SourceQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (_connectors.Get(sourceSystem) is not IInvoiceSource source)
        {
            return Result<ImportSummary>.Failure("NO_CONNECTOR", $"No invoice connector is registered for {sourceSystem}.");
        }

        var run = new ImportRun
        {
            TenantId = _tenant.TenantId,
            SourceSystem = sourceSystem,
            StartedAt = _clock.UtcNow,
            Status = ImportRunStatus.Running,
            CorrelationId = _correlation.CorrelationId,
            RequestDescription = query.UpdatedSince is { } since
                ? $"Incremental since {since:u}"
                : "Full import",
        };

        _db.ImportRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);

        var messages = new List<string>();
        var created = 0;
        var updated = 0;
        var duplicates = 0;
        var failures = 0;
        var seen = 0;

        try
        {
            var page = await source.GetInvoicesAsync(query, cancellationToken);

            foreach (var document in page.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                seen++;

                try
                {
                    var outcome = await ImportOneAsync(document, run, cancellationToken);

                    switch (outcome)
                    {
                        case ImportOutcome.Created:
                            created++;
                            break;
                        case ImportOutcome.Duplicate:
                            duplicates++;
                            messages.Add($"{document.SourceDocumentId} was already imported at version {document.SourceDocumentVersion}; skipped.");
                            break;
                        case ImportOutcome.NewVersion:
                            updated++;
                            messages.Add($"{document.SourceDocumentId} appeared at a new version ({document.SourceDocumentVersion}); a new record was created.");
                            break;
                        default:
                            break;
                    }

                    messages.AddRange(document.Notes.Select(n => $"{document.SourceDocumentId}: {n}"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad document must not abort the whole run.
                    failures++;
                    messages.Add($"{document.SourceDocumentId} failed: {ex.Message}");
                    _logger.LogWarning(ex, "Importing {SourceDocumentId} failed.", document.SourceDocumentId);
                }
            }

            run.Status = failures == 0 ? ImportRunStatus.Succeeded : ImportRunStatus.PartiallySucceeded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Status = ImportRunStatus.Failed;
            run.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Import run {RunId} failed.", run.Id);
            messages.Add($"The import failed: {ex.Message}");
        }

        run.CompletedAt = _clock.UtcNow;
        run.DocumentsSeen = seen;
        run.InvoicesCreated = created;
        run.InvoicesUpdated = updated;
        run.DuplicatesSkipped = duplicates;
        run.Failures = failures;
        await _db.SaveChangesAsync(cancellationToken);

        var summary = new ImportSummary(run.Id, run.CorrelationId, seen, created, updated, duplicates, failures, messages);

        return run.Status == ImportRunStatus.Failed
            ? Result<ImportSummary>.Failure("IMPORT_FAILED", run.ErrorMessage ?? "The import failed.")
            : Result<ImportSummary>.Success(summary);
    }

    private enum ImportOutcome { Created, Duplicate, NewVersion }

    private async Task<ImportOutcome> ImportOneAsync(SourceInvoiceDocument document, ImportRun run, CancellationToken cancellationToken)
    {
        var tenantId = _tenant.TenantId;

        // The duplicate check mirrors the unique index exactly, so the common case is a clean skip
        // rather than a caught constraint violation.
        var existing = await _db.Invoices.FirstOrDefaultAsync(
            i => i.SourceSystem == document.SourceSystem
                 && i.SourceDocumentId == document.SourceDocumentId
                 && i.SourceDocumentVersion == document.SourceDocumentVersion,
            cancellationToken);

        if (existing is not null)
        {
            await _audit.WriteAsync(
                AuditActions.InvoiceDuplicateSkipped, nameof(Invoice), existing.Id,
                newValue: new { document.SourceDocumentId, document.SourceDocumentVersion },
                cancellationToken: cancellationToken);

            return ImportOutcome.Duplicate;
        }

        var hasEarlierVersion = await _db.Invoices.AnyAsync(
            i => i.SourceSystem == document.SourceSystem && i.SourceDocumentId == document.SourceDocumentId,
            cancellationToken);

        // Retain the raw payload before anything is normalised, so provenance survives even if the
        // normalisation below later turns out to be wrong (§8).
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.RawPayload))).ToLowerInvariant();

        var payloadExists = await _db.RawSourcePayloads.AnyAsync(
            p => p.SourceSystem == document.SourceSystem
                 && p.SourceDocumentId == document.SourceDocumentId
                 && p.SourceDocumentVersion == document.SourceDocumentVersion,
            cancellationToken);

        if (!payloadExists)
        {
            _db.RawSourcePayloads.Add(new RawSourcePayload
            {
                TenantId = tenantId,
                SourceSystem = document.SourceSystem,
                ImportRunId = run.Id,
                SourceDocumentId = document.SourceDocumentId,
                SourceDocumentVersion = document.SourceDocumentVersion,
                Payload = document.RawPayload,
                PayloadSha256 = payloadHash,
                ApiVersion = document.ApiVersion,
                RetrievedAt = _clock.UtcNow,
            });
        }

        // Customers are deduplicated on their source identity so repeated imports do not multiply them.
        Customer? customer = null;
        if (document.Customer is { } incoming)
        {
            customer = await _db.Customers.FirstOrDefaultAsync(
                c => c.SourceSystem == incoming.SourceSystem && c.SourceCustomerId == incoming.SourceCustomerId,
                cancellationToken);

            if (customer is null)
            {
                customer = incoming;
                customer.TenantId = tenantId;
                _db.Customers.Add(customer);
            }
            else
            {
                // Refresh the mutable details; identity and mappings stay attached to the existing row.
                customer.CompanyName = incoming.CompanyName ?? customer.CompanyName;
                customer.Email = incoming.Email ?? customer.Email;
                customer.VatNumber = incoming.VatNumber ?? customer.VatNumber;
                customer.CountryCode = incoming.CountryCode ?? customer.CountryCode;
                customer.IsAnonymised = incoming.IsAnonymised;
            }
        }

        var invoice = document.Invoice;
        invoice.TenantId = tenantId;
        invoice.ImportRunId = run.Id;
        invoice.CorrelationId = run.CorrelationId;
        invoice.PreparedBy = _user.UserId;
        invoice.CustomerId = customer?.Id;
        invoice.Customer = null; // attached by id; avoids EF trying to insert the customer twice

        _db.Invoices.Add(invoice);

        foreach (var line in document.Lines)
        {
            line.TenantId = tenantId;
            line.InvoiceId = invoice.Id;
            _db.InvoiceLines.Add(line);
        }

        foreach (var payment in document.Payments)
        {
            payment.TenantId = tenantId;
            payment.InvoiceId = invoice.Id;
            _db.Payments.Add(payment);
        }

        await _db.SaveChangesAsync(cancellationToken);

        // Provenance for every canonical field the connector mapped (§22).
        foreach (var (canonicalField, sourceField) in document.FieldSourceMap)
        {
            await _provenance.RecordAsync(
                nameof(Invoice), invoice.Id, canonicalField, null, ValueOrigin.SourceApi,
                document.SourceSystem, document.SourceDocumentId, sourceField,
                transformation: $"Normalised by the {document.SourceSystem} connector",
                cancellationToken: cancellationToken);
        }

        await _audit.WriteAsync(
            hasEarlierVersion ? AuditActions.InvoiceUpdatedFromSource : AuditActions.InvoiceImported,
            nameof(Invoice), invoice.Id,
            newValue: new
            {
                document.SourceDocumentId,
                document.SourceDocumentVersion,
                invoice.InvoiceNumber,
                invoice.TotalAmount,
                invoice.Currency,
            },
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Imported {SourceSystem} document {DocumentId} v{Version} as invoice {InvoiceId}.",
            document.SourceSystem, document.SourceDocumentId, document.SourceDocumentVersion, invoice.Id);

        return hasEarlierVersion ? ImportOutcome.NewVersion : ImportOutcome.Created;
    }
}

/// <summary>Resolves the configured connector for a source system.</summary>
public sealed class SourceConnectorRegistry : ISourceConnectorRegistry
{
    private readonly IReadOnlyList<ISourceConnector> _connectors;

    public SourceConnectorRegistry(IEnumerable<ISourceConnector> connectors) => _connectors = connectors.ToList();

    public ISourceConnector? Get(SourceSystem sourceSystem) =>
        _connectors.FirstOrDefault(c => c.SourceSystem == sourceSystem);

    public IReadOnlyList<ISourceConnector> All() => _connectors;
}
