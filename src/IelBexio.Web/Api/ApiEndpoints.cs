using IelBexio.Application.Abstractions;
using IelBexio.Application.Bexio;
using IelBexio.Application.Common;
using IelBexio.Application.Invoices;
using IelBexio.Application.Mapping;
using IelBexio.Application.Sources;
using IelBexio.Application.Sync;
using IelBexio.Domain.Ai;
using IelBexio.Domain.Common;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Mapping;
using IelBexio.Domain.Sync;
using IelBexio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IelBexio.Web.Api;

/// <summary>
/// The REST surface of §28.
/// <para>
/// Two properties are worth stating because they are load-bearing rather than stylistic:
/// </para>
/// <list type="bullet">
/// <item><description>
/// There is no endpoint that posts to Bexio. The only way an invoice reaches Bexio is approval, which
/// writes an outbox message that a background worker picks up (§20: "never call Bexio directly from the
/// browser"). A reader looking for a "sync now" endpoint will not find one, by design.
/// </description></item>
/// <item><description>
/// Every handler returns the domain's own <c>Result</c> failures as problem details with the original
/// error code, so a client can distinguish "not allowed" from "wrong state" from "not found" without
/// parsing prose.
/// </description></item>
/// </list>
/// </summary>
public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapIelBexioApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var api = app.MapGroup("/api").WithTags("IelBexio");

        MapConnections(api);
        MapImports(api);
        MapInvoices(api);
        MapDocuments(api);
        MapMappings(api);
        MapSynchronizations(api);
        MapAuditAndAi(api);

        return app;
    }

    private static IResult FromResult(Result result) =>
        result.Succeeded
            ? Results.NoContent()
            : Results.Problem(
                title: result.ErrorCode,
                detail: result.ErrorMessage,
                statusCode: StatusFor(result.ErrorCode));

    private static IResult FromResult<T>(Result<T> result) =>
        result.Succeeded
            ? Results.Ok(result.Value)
            : Results.Problem(
                title: result.ErrorCode,
                detail: result.ErrorMessage,
                statusCode: StatusFor(result.ErrorCode));

    /// <summary>Maps domain error codes onto HTTP status codes so clients can react correctly.</summary>
    private static int StatusFor(string? errorCode) => errorCode switch
    {
        "NOT_FOUND" => StatusCodes.Status404NotFound,
        "FORBIDDEN" => StatusCodes.Status403Forbidden,
        "INVALID_STATE" or "PENDING_PROPOSALS" or "DUAL_CONTROL" or "REASON_REQUIRED" => StatusCodes.Status409Conflict,
        "UNSUPPORTED_FIELD" or "INVALID_VALUE" or "NO_CUSTOMER" => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status422UnprocessableEntity,
    };

    // ---- Connections -------------------------------------------------------------------------

    private static void MapConnections(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/connections");

        group.MapGet("/", (ISourceConnectorRegistry registry, IBexioClient bexio) =>
            Results.Ok(new
            {
                Bexio = new { Mode = bexio.ModeName, bexio.RequiresAuthorizedConnection },
                Sources = registry.All().Select(c => new
                {
                    SourceSystem = c.SourceSystem.ToString(),
                    Mode = c.ModeName,
                    Capabilities = c.Capabilities.ToString(),
                    c.DocumentedRestrictions,
                }),
            }))
            .WithSummary("Lists the configured connectors, their mode, capabilities and documented restrictions.");

        group.MapGet("/bexio", async (IBexioTokenProvider tokens, IBexioClient bexio, AppDbContext db, CancellationToken ct) =>
        {
            var connection = await db.BexioConnections.FirstOrDefaultAsync(ct);

            // Deliberately projects only non-secret fields. Tokens never leave the server (§24).
            return Results.Ok(new
            {
                Mode = bexio.ModeName,
                bexio.RequiresAuthorizedConnection,
                Status = connection?.Status.ToString() ?? "NotConnected",
                connection?.BexioCompanyName,
                connection?.ConnectedAt,
                connection?.LastRefreshedAt,
                connection?.LastReferenceRefreshAt,
                GrantedScopes = await tokens.GetGrantedScopesAsync(ct),
                connection?.LastError,
            });
        }).WithSummary("Reports the Bexio connection status. Never returns token material.");

        group.MapPost("/bexio/refresh-reference-data", async (IBexioReferenceSyncService sync, CancellationToken ct) =>
            FromResult(await sync.RefreshAsync(ct)))
            .WithSummary("Re-reads Bexio taxes, accounts, contacts and articles, and derives tax mappings from them.");

        group.MapPost("/bexio/disconnect", async (IBexioAuthorizationService auth, CancellationToken ct) =>
            FromResult(await auth.DisconnectAsync(ct)))
            .WithSummary("Disconnects Bexio and clears the stored token material.");
    }

    // ---- Imports -----------------------------------------------------------------------------

    private sealed record ImportRequest(string SourceSystem, DateTimeOffset? UpdatedSince, string? SourceDocumentId);

    private static void MapImports(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/imports");

        group.MapPost("/", async ([FromBody] ImportRequest request, IImportService import, CancellationToken ct) =>
        {
            if (!Enum.TryParse<SourceSystem>(request.SourceSystem, ignoreCase: true, out var sourceSystem))
            {
                return Results.Problem(title: "UNKNOWN_SOURCE", detail: $"'{request.SourceSystem}' is not a known source system.", statusCode: 400);
            }

            var result = await import.ImportAsync(
                sourceSystem,
                new SourceQuery { UpdatedSince = request.UpdatedSince, SourceDocumentId = request.SourceDocumentId },
                ct);

            return FromResult(result);
        }).WithSummary("Runs an import from a source system. Idempotent: an already-imported document version is skipped.");

        group.MapGet("/", async (AppDbContext db, int take, CancellationToken ct) =>
            Results.Ok(await db.ImportRuns
                .OrderByDescending(r => r.StartedAt)
                .Take(take <= 0 ? 25 : Math.Min(take, 200))
                .Select(r => new
                {
                    r.Id, SourceSystem = r.SourceSystem.ToString(), Status = r.Status.ToString(),
                    r.StartedAt, r.CompletedAt, r.DocumentsSeen, r.InvoicesCreated, r.InvoicesUpdated,
                    r.DuplicatesSkipped, r.Failures, r.CorrelationId, r.RequestDescription,
                })
                .ToListAsync(ct)));

        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.ImportRuns.FirstOrDefaultAsync(r => r.Id == id, ct) is { } run
                ? Results.Ok(run)
                : Results.NotFound());
    }

    // ---- Invoices ----------------------------------------------------------------------------

    private sealed record ApproveRequest(string? Comment);
    private sealed record RejectRequest(string Reason);
    private sealed record CorrectionsRequest(IReadOnlyList<FieldCorrection> Corrections);
    private sealed record VerifyTaxRequest(string? BexioTaxId);

    private static void MapInvoices(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/invoices");

        group.MapGet("/", async (AppDbContext db, string? state, int take, CancellationToken ct) =>
        {
            var query = db.Invoices.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(state) && Enum.TryParse<Domain.Workflow.InvoiceWorkflowState>(state, true, out var parsed))
            {
                query = query.Where(i => i.WorkflowState == parsed);
            }

            return Results.Ok(await query
                .OrderByDescending(i => i.CreatedAt)
                .Take(take <= 0 ? 50 : Math.Min(take, 200))
                .Select(i => new
                {
                    i.Id, i.InvoiceNumber, i.InvoiceDate, i.Currency, i.TotalAmount, i.TaxAmount,
                    SourceSystem = i.SourceSystem.ToString(),
                    State = i.WorkflowState.ToString(),
                    ValidationStatus = i.ValidationStatus.ToString(),
                    ApprovalStatus = i.ApprovalStatus.ToString(),
                    SyncStatus = i.SyncStatus.ToString(),
                    i.BexioInvoiceId, i.CorrelationId,
                    CustomerName = i.Customer != null ? i.Customer.CompanyName : null,
                })
                .ToListAsync(ct));
        }).WithSummary("Lists invoices, optionally filtered by workflow state.");

        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var invoice = await db.Invoices.AsNoTracking()
                .Include(i => i.Lines)
                .Include(i => i.Customer)
                .FirstOrDefaultAsync(i => i.Id == id, ct);

            if (invoice is null)
            {
                return Results.NotFound();
            }

            var assessments = await db.TaxAssessments.AsNoTracking().Where(a => a.InvoiceId == id).ToListAsync(ct);
            var proposals = await db.AiProposals.AsNoTracking().Where(p => p.EntityId == id).ToListAsync(ct);
            var provenance = await db.FieldProvenances.AsNoTracking().Where(p => p.EntityId == id).ToListAsync(ct);

            return Results.Ok(new
            {
                Invoice = new
                {
                    invoice.Id, invoice.InvoiceNumber, invoice.InvoiceDate, invoice.DueDate, invoice.Currency,
                    invoice.SubtotalAmount, invoice.DiscountAmount, invoice.ShippingAmount, invoice.TaxAmount, invoice.TotalAmount,
                    SourceSystem = invoice.SourceSystem.ToString(),
                    invoice.SourceDocumentId, invoice.SourceDocumentVersion,
                    State = invoice.WorkflowState.ToString(),
                    ValidationStatus = invoice.ValidationStatus.ToString(),
                    ApprovalStatus = invoice.ApprovalStatus.ToString(),
                    SyncStatus = invoice.SyncStatus.ToString(),
                    invoice.BexioInvoiceId, invoice.BexioContactId, invoice.CorrelationId,
                    invoice.HumanVerified, invoice.VerifiedBy, invoice.PreparedBy, invoice.ApprovedBy, invoice.ApprovedAt,
                },
                Customer = invoice.Customer is null ? null : new
                {
                    invoice.Customer.Id, invoice.Customer.CompanyName, invoice.Customer.Email,
                    invoice.Customer.VatNumber, invoice.Customer.CountryCode, invoice.Customer.IsAnonymised,
                },
                // Projected explicitly rather than returned as entities. EF populates the
                // InvoiceLine.Invoice back-reference, which is an object cycle that System.Text.Json
                // refuses — and because the response has already begun streaming when it fails, the
                // client sees a truncated body rather than an error. Projecting also keeps the wire
                // contract stable when the entity changes.
                Lines = invoice.Lines.OrderBy(l => l.LineNumber).Select(l => new
                {
                    l.Id, l.LineNumber, l.SourceLineId, l.Sku, l.ProductCode, l.Description,
                    l.Quantity, l.Unit, l.UnitPrice, l.DiscountAmount, l.NetAmount,
                    l.TaxRatePercent, l.TaxAmount, l.GrossAmount, l.Currency,
                    Kind = l.Kind.ToString(),
                    l.TaxAssessmentId, l.BexioArticleId, l.BexioAccountId, l.BexioTaxId,
                }),
                TaxAssessments = assessments.Select(a => new
                {
                    a.Id, a.InvoiceLineId, a.Jurisdiction, a.CountryCode,
                    TaxType = a.TaxType.ToString(),
                    a.RatePercent, a.TaxableAmount, a.TaxAmount, a.Currency,
                    a.SourceTaxCode, a.SourceTaxName, a.SourceTaxRatePercent,
                    a.InternalTaxCode, a.ProposedBexioTaxId, a.AppliedBexioTaxId,
                    DeterminationMethod = a.DeterminationMethod.ToString(),
                    a.DeterminationVersion, a.ConfidenceSignal,
                    a.HumanVerified, a.VerifiedBy, a.VerifiedAt, a.Rationale,
                }),
                AiProposals = proposals.Select(p => new
                {
                    p.Id, p.EntityType, p.FieldPath,
                    Operation = p.Operation.ToString(),
                    p.ProposedValueJson, p.CurrentValueJson, p.ConfidenceSignal,
                    p.Model, p.PromptVersion, p.SchemaVersion, p.EvidenceJson,
                    Status = p.Status.ToString(), p.ReviewedBy, p.ReviewedAt, p.ReviewComment,
                }),
                Provenance = provenance.Select(p => new
                {
                    p.FieldPath,
                    Origin = p.Origin.ToString(),
                    SourceSystem = p.SourceSystem.ToString(),
                    p.SourceDocumentId, p.SourceFieldPath, p.Transformation, p.ExtractionMethod,
                    p.AiProposalId, p.AiModel, p.ModifiedBy, p.RecordedAt, p.ValueJson,
                }),
            });
        }).WithSummary("Returns one invoice with its lines, tax assessments, AI proposals and field provenance.");

        group.MapPost("/{id:guid}/validate", async (Guid id, IInvoiceProcessingService processing, CancellationToken ct) =>
        {
            var result = await processing.ProcessAsync(id, ct);
            return result.Succeeded
                ? Results.Ok(new { Issues = result.Value!.Issues, result.Value.HasErrors, result.Value.HasWarnings })
                : Results.Problem(title: result.ErrorCode, detail: result.ErrorMessage, statusCode: StatusFor(result.ErrorCode));
        }).WithSummary("Re-runs deterministic validation and tax determination.");

        group.MapGet("/{id:guid}/preview", async (Guid id, AppDbContext db, BexioPreflightService preflight, CancellationToken ct) =>
        {
            var invoice = await db.Invoices.Include(i => i.Lines).Include(i => i.Customer).FirstOrDefaultAsync(i => i.Id == id, ct);
            if (invoice is null)
            {
                return Results.NotFound();
            }

            var assessments = await db.TaxAssessments.Where(a => a.InvoiceId == id).ToListAsync(ct);
            var preview = await preflight.BuildAsync(invoice, invoice.Lines.OrderBy(l => l.LineNumber).ToList(), assessments, true, ct);

            return Results.Ok(preview);
        }).WithSummary("Returns the Bexio pre-flight preview an approver sees before deciding.");

        group.MapPost("/{id:guid}/corrections", async (Guid id, [FromBody] CorrectionsRequest request, IReviewService review, CancellationToken ct) =>
            FromResult(await review.ApplyCorrectionsAsync(id, request.Corrections, ct)));

        group.MapPost("/{id:guid}/verify", async (Guid id, IReviewService review, CancellationToken ct) =>
            FromResult(await review.VerifyAsync(id, ct)));

        group.MapPost("/{id:guid}/submit", async (Guid id, IReviewService review, CancellationToken ct) =>
            FromResult(await review.SubmitForApprovalAsync(id, ct)));

        group.MapPost("/{id:guid}/approve", async (Guid id, [FromBody] ApproveRequest request, IApprovalService approval, CancellationToken ct) =>
            FromResult(await approval.ApproveAsync(id, request.Comment, ct)))
            .WithSummary("Approves an invoice. This is the ONLY way an invoice becomes eligible for Bexio synchronisation.");

        group.MapPost("/{id:guid}/reject", async (Guid id, [FromBody] RejectRequest request, IApprovalService approval, CancellationToken ct) =>
            FromResult(await approval.RejectAsync(id, request.Reason, ct)));

        group.MapPost("/tax-assessments/{assessmentId:guid}/verify", async (Guid assessmentId, [FromBody] VerifyTaxRequest request, IReviewService review, CancellationToken ct) =>
            FromResult(await review.VerifyTaxAsync(assessmentId, request.BexioTaxId, ct)));

        group.MapPost("/ai-proposals/{proposalId:guid}/accept", async (Guid proposalId, IReviewService review, CancellationToken ct) =>
            FromResult(await review.AcceptAiProposalAsync(proposalId, ct)));

        group.MapPost("/ai-proposals/{proposalId:guid}/reject", async (Guid proposalId, [FromBody] ApproveRequest request, IReviewService review, CancellationToken ct) =>
            FromResult(await review.RejectAiProposalAsync(proposalId, request.Comment, ct)));
    }

    // ---- Documents ---------------------------------------------------------------------------

    private static void MapDocuments(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/documents");

        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.DocumentArtifacts.AsNoTracking()
                .OrderByDescending(d => d.IngestedAt)
                .Take(100)
                .Select(d => new
                {
                    d.Id, d.OriginalFileName, d.ContentType, d.SizeBytes, d.Sha256,
                    Kind = d.Kind.ToString(), Status = d.Status.ToString(),
                    d.IngestedAt, d.RejectionReason, d.InvoiceId, d.DuplicateOfDocumentId,
                })
                .ToListAsync(ct)));

        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.DocumentArtifacts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) is { } document
                ? Results.Ok(document)
                : Results.NotFound());

        // The bytes are streamed through this authorised endpoint rather than exposed by a URL, so
        // there is never a permanent public document link (§25).
        group.MapGet("/{id:guid}/content", async (Guid id, AppDbContext db, IBlobStore blobs, CancellationToken ct) =>
        {
            var document = await db.DocumentArtifacts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
            if (document is null)
            {
                return Results.NotFound();
            }

            var stream = await blobs.GetAsync(document.BlobUri, ct);
            return stream is null
                ? Results.NotFound()
                : Results.File(stream, document.ContentType, document.OriginalFileName);
        });
    }

    // ---- Mappings ----------------------------------------------------------------------------

    private sealed record CustomerMappingRequest(Guid CustomerId, string BexioContactId, string? Label);
    private sealed record TaxMappingRequest(string InternalTaxCode, string CountryCode, decimal RatePercent, string BexioTaxId, string? BexioTaxName, decimal? BexioRate, bool BexioActive);
    private sealed record AccountMappingRequest(string InternalAccountCode, string BexioAccountId, string? Number, string? Name);
    private sealed record ProductMappingRequest(string Sku, string? BexioArticleId, string? BexioAccountId, string? ProductName);

    private static void MapMappings(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/mappings");

        group.MapGet("/customers", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.CustomerMappings.AsNoTracking().ToListAsync(ct)));

        group.MapPost("/customers", async ([FromBody] CustomerMappingRequest request, IMappingService mappings, ICurrentUser user, CancellationToken ct) =>
            Results.Ok(await mappings.UpsertCustomerMappingAsync(request.CustomerId, request.BexioContactId, request.Label, MappingOrigin.Manual, user.UserId, ct)));

        group.MapGet("/taxes", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.TaxMappings.AsNoTracking().ToListAsync(ct)));

        group.MapPost("/taxes", async ([FromBody] TaxMappingRequest request, IMappingService mappings, ICurrentUser user, CancellationToken ct) =>
            Results.Ok(await mappings.UpsertTaxMappingAsync(
                request.InternalTaxCode, request.CountryCode, request.RatePercent, request.BexioTaxId,
                request.BexioTaxName, request.BexioRate, request.BexioActive, MappingOrigin.Manual, user.UserId, ct)));

        group.MapGet("/accounts", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.AccountMappings.AsNoTracking().ToListAsync(ct)));

        group.MapPost("/accounts", async ([FromBody] AccountMappingRequest request, IMappingService mappings, ICurrentUser user, CancellationToken ct) =>
            Results.Ok(await mappings.UpsertAccountMappingAsync(
                request.InternalAccountCode, request.BexioAccountId, request.Number, request.Name, MappingOrigin.Manual, user.UserId, ct)));

        group.MapGet("/products", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.ProductMappings.AsNoTracking().ToListAsync(ct)));

        group.MapPost("/products", async ([FromBody] ProductMappingRequest request, IMappingService mappings, ICurrentUser user, CancellationToken ct) =>
            Results.Ok(await mappings.UpsertProductMappingAsync(
                request.Sku, request.BexioArticleId, request.BexioAccountId, request.ProductName, MappingOrigin.Manual, user.UserId, ct)));

        // The Bexio reference data a human chooses from when mapping manually.
        group.MapGet("/bexio-reference", async (AppDbContext db, string? kind, CancellationToken ct) =>
        {
            var query = db.BexioReferenceItems.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(kind) && Enum.TryParse<Domain.Bexio.BexioReferenceKind>(kind, true, out var parsed))
            {
                query = query.Where(r => r.Kind == parsed);
            }

            return Results.Ok(await query.OrderBy(r => r.Kind).ThenBy(r => r.Name).ToListAsync(ct));
        });
    }

    // ---- Synchronisations --------------------------------------------------------------------

    private static void MapSynchronizations(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/synchronizations");

        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.SynchronizationAttempts.AsNoTracking()
                .OrderByDescending(a => a.StartedAt)
                .Take(100)
                .Select(a => new
                {
                    a.Id, a.EntityId, a.Destination, a.Operation, a.IdempotencyKey,
                    Status = a.Status.ToString(), a.AttemptCount, a.StartedAt, a.CompletedAt,
                    a.ExternalId, ErrorCategory = a.ErrorCategory.ToString(), a.ErrorCode, a.ErrorMessage, a.CorrelationId,
                })
                .ToListAsync(ct)));

        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.SynchronizationAttempts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) is { } attempt
                ? Results.Ok(attempt)
                : Results.NotFound());

        group.MapGet("/outbox", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.OutboxMessages.AsNoTracking()
                .OrderByDescending(m => m.CreatedAt)
                .Take(100)
                .Select(m => new
                {
                    m.Id, m.MessageType, Status = m.Status.ToString(), m.AttemptCount,
                    m.NextAttemptAt, m.ProcessedAt, m.LastError,
                    LastErrorCategory = m.LastErrorCategory.ToString(), m.CorrelationId,
                })
                .ToListAsync(ct)));

        // Re-queues a dead-lettered message. This is a retry of an already-approved invoice, not a
        // second approval: the invoice's approval and its fingerprint are re-checked at dispatch.
        group.MapPost("/outbox/{id:guid}/retry", async (Guid id, AppDbContext db, IClock clock, ICurrentUser user, IAuditWriter audit, CancellationToken ct) =>
        {
            if (!user.IsInRole(AppRoles.Admin) && !user.IsInRole(AppRoles.IntegrationManager))
            {
                return Results.Problem(title: "FORBIDDEN", detail: "Retrying a synchronisation requires the Admin or IntegrationManager role.", statusCode: 403);
            }

            db.SuppressTenantFilter = true;
            var message = await db.OutboxMessages.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (message is null)
            {
                return Results.NotFound();
            }

            if (message.Status != OutboxStatus.DeadLettered)
            {
                return Results.Problem(title: "INVALID_STATE", detail: $"Only a dead-lettered message may be retried manually; this one is {message.Status}.", statusCode: 409);
            }

            message.Status = OutboxStatus.Pending;
            message.NextAttemptAt = clock.UtcNow;
            message.AttemptCount = 0;
            await db.SaveChangesAsync(ct);

            await audit.WriteAsync("outbox.manual_retry", nameof(OutboxMessage), message.Id, cancellationToken: ct);
            return Results.NoContent();
        });

        group.MapPost("/reconcile", async (IReconciliationService reconciliation, CancellationToken ct) =>
            FromResult(await reconciliation.ReconcileAsync(100, ct)));
    }

    // ---- Audit and AI usage --------------------------------------------------------------------

    private static void MapAuditAndAi(RouteGroupBuilder api)
    {
        api.MapGet("/audit", async (AppDbContext db, Guid? entityId, string? correlationId, int take, CancellationToken ct) =>
        {
            var query = db.AuditEvents.AsNoTracking();

            if (entityId is { } id)
            {
                query = query.Where(e => e.EntityId == id);
            }

            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                query = query.Where(e => e.CorrelationId == correlationId);
            }

            return Results.Ok(await query
                .OrderByDescending(e => e.OccurredAt)
                .Take(take <= 0 ? 100 : Math.Min(take, 500))
                .ToListAsync(ct));
        }).WithSummary("Reads the audit trail, filterable by entity or by correlation id.");

        api.MapGet("/provenance/{entityId:guid}", async (Guid entityId, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.FieldProvenances.AsNoTracking()
                .Where(p => p.EntityId == entityId)
                .OrderBy(p => p.FieldPath)
                .ToListAsync(ct)))
            .WithSummary("Traces each canonical field back to its source.");

        api.MapGet("/ai/usage", async (AppDbContext db, Ai.Gateway.IAiUsageLedger ledger, IClock clock, CancellationToken ct) =>
        {
            var records = await db.AiUsageRecords.AsNoTracking()
                .OrderByDescending(r => r.OccurredAt)
                .Take(200)
                .ToListAsync(ct);

            return Results.Ok(new
            {
                MonthToDateSpend = await ledger.GetMonthToDateSpendAsync(clock.UtcNow, ct),
                TotalCalls = records.Count,
                Failures = records.Count(r => !r.Succeeded),
                ServedFromCache = records.Count(r => r.ServedFromCache),
                ByOperation = records.GroupBy(r => r.Operation)
                    .Select(g => new { Operation = g.Key.ToString(), Calls = g.Count(), Cost = g.Sum(r => r.EstimatedCost) }),
                Records = records.Select(r => new
                {
                    r.Id, Operation = r.Operation.ToString(), r.Model, r.PromptVersion, r.OccurredAt,
                    r.LatencyMs, r.TotalTokens, r.EstimatedCost, r.CostCurrency, r.Succeeded,
                    FailureReason = r.FailureReason.ToString(), r.ServedFromCache, r.CorrelationId, r.InvoiceId,
                }),
            });
        }).WithSummary("AI usage, cost and refusals. Prompts and responses are deliberately not stored.");

        api.MapGet("/ai/proposals", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.AiProposals.AsNoTracking()
                .Where(p => p.Status == AiProposalStatus.Pending)
                .OrderByDescending(p => p.CreatedAt)
                .Take(100)
                .ToListAsync(ct)));
    }
}
