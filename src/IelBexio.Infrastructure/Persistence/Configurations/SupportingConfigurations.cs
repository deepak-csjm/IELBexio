using IelBexio.Domain.Ai;
using IelBexio.Domain.Audit;
using IelBexio.Domain.Bexio;
using IelBexio.Domain.Documents;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Mapping;
using IelBexio.Domain.Sync;
using IelBexio.Domain.Tax;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IelBexio.Infrastructure.Persistence.Configurations;

internal sealed class SourceConnectionConfiguration : IEntityTypeConfiguration<SourceConnection>
{
    public void Configure(EntityTypeBuilder<SourceConnection> b)
    {
        b.ToTable("source_connections");
        b.HasKey(x => x.Id);
        b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        b.Property(x => x.ExternalAccountId).HasMaxLength(300);
        b.Property(x => x.SecretReference).HasMaxLength(300);
        b.Property(x => x.LastSyncCursor).HasMaxLength(2000);
        b.Property(x => x.LastError).HasMaxLength(4000);
        b.HasIndex(x => new { x.TenantId, x.SourceSystem }).HasDatabaseName("ix_source_connections_tenant_system");
    }
}

internal sealed class ImportRunConfiguration : IEntityTypeConfiguration<ImportRun>
{
    public void Configure(EntityTypeBuilder<ImportRun> b)
    {
        b.ToTable("import_runs");
        b.HasKey(x => x.Id);
        b.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
        b.Property(x => x.ErrorMessage).HasMaxLength(4000);
        b.Property(x => x.RequestDescription).HasMaxLength(1000);
        b.HasIndex(x => new { x.TenantId, x.StartedAt }).HasDatabaseName("ix_import_runs_tenant_started");
    }
}

internal sealed class RawSourcePayloadConfiguration : IEntityTypeConfiguration<RawSourcePayload>
{
    public void Configure(EntityTypeBuilder<RawSourcePayload> b)
    {
        b.ToTable("raw_source_payloads");
        b.HasKey(x => x.Id);
        b.Property(x => x.SourceDocumentId).HasMaxLength(200).IsRequired();
        b.Property(x => x.SourceDocumentVersion).HasMaxLength(100).IsRequired();
        b.Property(x => x.Payload).HasColumnType(ColumnTypes.Json).IsRequired();
        b.Property(x => x.PayloadSha256).HasColumnType(ColumnTypes.Sha256).IsRequired();
        b.Property(x => x.ApiVersion).HasMaxLength(50);

        // One retained payload per source document version. A re-import with identical content is a
        // no-op; changed content for the same version is a signal worth surfacing.
        b.HasIndex(x => new { x.TenantId, x.SourceSystem, x.SourceDocumentId, x.SourceDocumentVersion })
            .IsUnique()
            .HasDatabaseName("ux_raw_payloads_source_identity");
    }
}

internal sealed class TaxAssessmentConfiguration : IEntityTypeConfiguration<TaxAssessment>
{
    public void Configure(EntityTypeBuilder<TaxAssessment> b)
    {
        b.ToTable("tax_assessments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Jurisdiction).HasMaxLength(60);
        b.Property(x => x.CountryCode).HasMaxLength(2);
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.SourceTaxCode).HasMaxLength(100);
        b.Property(x => x.SourceTaxName).HasMaxLength(300);
        b.Property(x => x.InternalTaxCode).HasMaxLength(100);
        b.Property(x => x.ProposedBexioTaxId).HasMaxLength(100);
        b.Property(x => x.AppliedBexioTaxId).HasMaxLength(100);
        b.Property(x => x.DeterminationVersion).HasMaxLength(30).IsRequired();
        b.Property(x => x.VerifiedBy).HasMaxLength(256);
        b.Property(x => x.Rationale).HasMaxLength(4000);

        b.Property(x => x.RatePercent).HasColumnType(ColumnTypes.Rate);
        b.Property(x => x.SourceTaxRatePercent).HasColumnType(ColumnTypes.Rate);
        b.Property(x => x.TaxableAmount).HasColumnType(ColumnTypes.Money);
        b.Property(x => x.TaxAmount).HasColumnType(ColumnTypes.Money);
        b.Property(x => x.ConfidenceSignal).HasColumnType(ColumnTypes.Signal);

        b.HasIndex(x => new { x.TenantId, x.InvoiceId }).HasDatabaseName("ix_tax_assessments_invoice");
        b.HasIndex(x => x.InvoiceLineId).HasDatabaseName("ix_tax_assessments_line");
    }
}

internal sealed class DocumentArtifactConfiguration : IEntityTypeConfiguration<DocumentArtifact>
{
    public void Configure(EntityTypeBuilder<DocumentArtifact> b)
    {
        b.ToTable("document_artifacts");
        b.HasKey(x => x.Id);
        b.Property(x => x.BlobUri).HasMaxLength(1000).IsRequired();
        b.Property(x => x.OriginalFileName).HasMaxLength(300).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(150).IsRequired();
        b.Property(x => x.Sha256).HasColumnType(ColumnTypes.Sha256).IsRequired();
        b.Property(x => x.SourceReference).HasMaxLength(300);
        b.Property(x => x.ProcessingVersion).HasMaxLength(30).IsRequired();
        b.Property(x => x.RejectionReason).HasMaxLength(2000);
        b.Property(x => x.MalwareScanDetail).HasMaxLength(2000);
        b.Property(x => x.CorrelationId).HasMaxLength(64);

        // Content-addressed duplicate detection (§11).
        b.HasIndex(x => new { x.TenantId, x.Sha256 }).IsUnique().HasDatabaseName("ux_document_artifacts_hash");
        b.HasIndex(x => new { x.TenantId, x.InvoiceId }).HasDatabaseName("ix_document_artifacts_invoice");
    }
}

internal sealed class ExtractionResultConfiguration : IEntityTypeConfiguration<ExtractionResult>
{
    public void Configure(EntityTypeBuilder<ExtractionResult> b)
    {
        b.ToTable("extraction_results");
        b.HasKey(x => x.Id);
        b.Property(x => x.ExtractorVersion).HasMaxLength(30).IsRequired();
        b.Property(x => x.Model).HasMaxLength(150);
        b.Property(x => x.PromptVersion).HasMaxLength(50);
        b.Property(x => x.ExtractedJson).HasColumnType(ColumnTypes.Json).IsRequired();
        b.Property(x => x.FieldConfidenceJson).HasColumnType(ColumnTypes.Json);
        b.Property(x => x.ValidationErrorsJson).HasColumnType(ColumnTypes.Json);
        b.Property(x => x.FailureReason).HasMaxLength(2000);
        b.Property(x => x.CorrelationId).HasMaxLength(64);
        b.Property(x => x.CacheKey).HasMaxLength(200).IsRequired();

        // The extraction cache key of §13: document hash + extractor version + prompt version + model.
        b.HasIndex(x => new { x.TenantId, x.CacheKey }).IsUnique().HasDatabaseName("ux_extraction_results_cache_key");
        b.HasIndex(x => new { x.TenantId, x.DocumentArtifactId }).HasDatabaseName("ix_extraction_results_document");
    }
}

internal sealed class AiProposalConfiguration : IEntityTypeConfiguration<AiProposal>
{
    public void Configure(EntityTypeBuilder<AiProposal> b)
    {
        b.ToTable("ai_proposals");
        b.HasKey(x => x.Id);
        b.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
        b.Property(x => x.FieldPath).HasMaxLength(300).IsRequired();
        b.Property(x => x.ProposedValueJson).HasColumnType(ColumnTypes.Json).IsRequired();
        b.Property(x => x.CurrentValueJson).HasColumnType(ColumnTypes.Json);
        b.Property(x => x.EvidenceJson).HasColumnType(ColumnTypes.Json);
        b.Property(x => x.Model).HasMaxLength(150).IsRequired();
        b.Property(x => x.PromptVersion).HasMaxLength(50).IsRequired();
        b.Property(x => x.SchemaVersion).HasMaxLength(50);
        b.Property(x => x.ReviewedBy).HasMaxLength(256);
        b.Property(x => x.ReviewComment).HasMaxLength(2000);
        b.Property(x => x.CorrelationId).HasMaxLength(64);
        b.Property(x => x.ConfidenceSignal).HasColumnType(ColumnTypes.Signal);

        b.HasIndex(x => new { x.TenantId, x.EntityId, x.Status }).HasDatabaseName("ix_ai_proposals_entity");
    }
}

internal sealed class AiUsageRecordConfiguration : IEntityTypeConfiguration<AiUsageRecord>
{
    public void Configure(EntityTypeBuilder<AiUsageRecord> b)
    {
        b.ToTable("ai_usage_records");
        b.HasKey(x => x.Id);
        b.Property(x => x.Model).HasMaxLength(150).IsRequired();
        b.Property(x => x.PromptVersion).HasMaxLength(50);
        b.Property(x => x.FailureDetail).HasMaxLength(2000);
        b.Property(x => x.CorrelationId).HasMaxLength(64);
        b.Property(x => x.CostCurrency).HasMaxLength(3).IsRequired();
        b.Property(x => x.EstimatedCost).HasColumnType("numeric(12,6)");
        b.HasIndex(x => new { x.TenantId, x.OccurredAt }).HasDatabaseName("ix_ai_usage_tenant_time");
        b.HasIndex(x => new { x.TenantId, x.InvoiceId }).HasDatabaseName("ix_ai_usage_invoice");
    }
}

internal sealed class SynchronizationAttemptConfiguration : IEntityTypeConfiguration<SynchronizationAttempt>
{
    public void Configure(EntityTypeBuilder<SynchronizationAttempt> b)
    {
        b.ToTable("synchronization_attempts");
        b.HasKey(x => x.Id);
        b.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
        b.Property(x => x.Destination).HasMaxLength(100).IsRequired();
        b.Property(x => x.Operation).HasMaxLength(100).IsRequired();
        b.Property(x => x.RequestHash).HasColumnType(ColumnTypes.Sha256).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
        b.Property(x => x.ExternalId).HasMaxLength(100);
        b.Property(x => x.ErrorCode).HasMaxLength(100);
        b.Property(x => x.ErrorMessage).HasMaxLength(4000);
        b.Property(x => x.ResponseMetadataJson).HasColumnType(ColumnTypes.Json);
        b.Property(x => x.CorrelationId).HasMaxLength(64);

        // The single most important constraint in the system: one synchronisation per logical
        // operation, enforced by the database rather than by application logic (§19).
        b.HasIndex(x => x.IdempotencyKey).IsUnique().HasDatabaseName("ux_sync_attempts_idempotency_key");
        b.HasIndex(x => new { x.TenantId, x.EntityId }).HasDatabaseName("ix_sync_attempts_entity");
        b.HasIndex(x => new { x.TenantId, x.Status }).HasDatabaseName("ix_sync_attempts_status");
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("outbox_messages");
        b.HasKey(x => x.Id);
        b.Property(x => x.MessageType).HasMaxLength(100).IsRequired();
        b.Property(x => x.Payload).HasColumnType(ColumnTypes.Json).IsRequired();
        b.Property(x => x.LastError).HasMaxLength(4000);
        b.Property(x => x.CorrelationId).HasMaxLength(64);
        b.Property(x => x.LeasedBy).HasMaxLength(200);

        // The dispatcher's hot path: pending messages whose backoff has elapsed.
        b.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("ix_outbox_status_next_attempt");
        b.HasIndex(x => new { x.TenantId, x.Status }).HasDatabaseName("ix_outbox_tenant_status");
    }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.ToTable("audit_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.Actor).HasMaxLength(256).IsRequired();
        b.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
        b.Property(x => x.Action).HasMaxLength(100).IsRequired();
        b.Property(x => x.OldValueJson).HasColumnType(ColumnTypes.Json);
        b.Property(x => x.NewValueJson).HasColumnType(ColumnTypes.Json);
        b.Property(x => x.Reason).HasMaxLength(2000);
        b.Property(x => x.CorrelationId).HasMaxLength(64);

        b.HasIndex(x => new { x.TenantId, x.OccurredAt }).HasDatabaseName("ix_audit_tenant_time");
        b.HasIndex(x => new { x.TenantId, x.EntityId }).HasDatabaseName("ix_audit_entity");
        b.HasIndex(x => x.CorrelationId).HasDatabaseName("ix_audit_correlation");
    }
}

internal sealed class FieldProvenanceConfiguration : IEntityTypeConfiguration<FieldProvenance>
{
    public void Configure(EntityTypeBuilder<FieldProvenance> b)
    {
        b.ToTable("field_provenance");
        b.HasKey(x => x.Id);
        b.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
        b.Property(x => x.FieldPath).HasMaxLength(300).IsRequired();
        b.Property(x => x.SourceDocumentId).HasMaxLength(200);
        b.Property(x => x.SourceFieldPath).HasMaxLength(400);
        b.Property(x => x.Transformation).HasMaxLength(1000);
        b.Property(x => x.ExtractionMethod).HasMaxLength(60);
        b.Property(x => x.AiModel).HasMaxLength(150);
        b.Property(x => x.AiPromptVersion).HasMaxLength(50);
        b.Property(x => x.ModifiedBy).HasMaxLength(256);
        b.Property(x => x.ValueJson).HasColumnType(ColumnTypes.Json);
        b.Property(x => x.CorrelationId).HasMaxLength(64);

        b.HasIndex(x => new { x.TenantId, x.EntityId, x.FieldPath }).HasDatabaseName("ix_provenance_entity_field");
    }
}

internal sealed class CustomerMappingConfiguration : IEntityTypeConfiguration<CustomerMapping>
{
    public void Configure(EntityTypeBuilder<CustomerMapping> b)
    {
        b.ToTable("customer_mappings");
        b.HasKey(x => x.Id);
        b.Property(x => x.BexioContactId).HasMaxLength(100).IsRequired();
        b.Property(x => x.BexioContactName).HasMaxLength(300);
        b.Property(x => x.CreatedBy).HasMaxLength(256);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.ConfidenceSignal).HasColumnType(ColumnTypes.Signal);
        b.HasIndex(x => new { x.TenantId, x.CustomerId }).IsUnique().HasFilter("is_active").HasDatabaseName("ux_customer_mappings_customer");
    }
}

internal sealed class ProductMappingConfiguration : IEntityTypeConfiguration<ProductMapping>
{
    public void Configure(EntityTypeBuilder<ProductMapping> b)
    {
        b.ToTable("product_mappings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Sku).HasMaxLength(200).IsRequired();
        b.Property(x => x.ProductName).HasMaxLength(300);
        b.Property(x => x.BexioArticleId).HasMaxLength(100);
        b.Property(x => x.BexioAccountId).HasMaxLength(100);
        b.Property(x => x.CreatedBy).HasMaxLength(256);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.ConfidenceSignal).HasColumnType(ColumnTypes.Signal);
        b.HasIndex(x => new { x.TenantId, x.Sku }).IsUnique().HasFilter("is_active").HasDatabaseName("ux_product_mappings_sku");
    }
}

internal sealed class TaxMappingConfiguration : IEntityTypeConfiguration<TaxMapping>
{
    public void Configure(EntityTypeBuilder<TaxMapping> b)
    {
        b.ToTable("tax_mappings");
        b.HasKey(x => x.Id);
        b.Property(x => x.InternalTaxCode).HasMaxLength(100).IsRequired();
        b.Property(x => x.CountryCode).HasMaxLength(2).IsRequired();
        b.Property(x => x.BexioTaxId).HasMaxLength(100).IsRequired();
        b.Property(x => x.BexioTaxName).HasMaxLength(300);
        b.Property(x => x.CreatedBy).HasMaxLength(256);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.RatePercent).HasColumnType(ColumnTypes.Rate);
        b.Property(x => x.BexioTaxRatePercent).HasColumnType(ColumnTypes.Rate);
        b.Property(x => x.ConfidenceSignal).HasColumnType(ColumnTypes.Signal);

        b.HasIndex(x => new { x.TenantId, x.InternalTaxCode, x.CountryCode, x.ValidFrom })
            .IsUnique()
            .HasFilter("is_active")
            .HasDatabaseName("ux_tax_mappings_code_country_from");
    }
}

internal sealed class AccountMappingConfiguration : IEntityTypeConfiguration<AccountMapping>
{
    public void Configure(EntityTypeBuilder<AccountMapping> b)
    {
        b.ToTable("account_mappings");
        b.HasKey(x => x.Id);
        b.Property(x => x.InternalAccountCode).HasMaxLength(100).IsRequired();
        b.Property(x => x.BexioAccountId).HasMaxLength(100).IsRequired();
        b.Property(x => x.BexioAccountNumber).HasMaxLength(50);
        b.Property(x => x.BexioAccountName).HasMaxLength(300);
        b.Property(x => x.CreatedBy).HasMaxLength(256);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.ConfidenceSignal).HasColumnType(ColumnTypes.Signal);
        b.HasIndex(x => new { x.TenantId, x.InternalAccountCode }).IsUnique().HasFilter("is_active").HasDatabaseName("ux_account_mappings_code");
    }
}

internal sealed class BexioConnectionConfiguration : IEntityTypeConfiguration<BexioConnection>
{
    public void Configure(EntityTypeBuilder<BexioConnection> b)
    {
        b.ToTable("bexio_connections");
        b.HasKey(x => x.Id);

        // Encrypted at rest by ISecretProtector before it ever reaches this column, and never
        // projected out of the token service (§24).
        b.Property(x => x.ProtectedAccessToken).HasMaxLength(8000);
        b.Property(x => x.ProtectedRefreshToken).HasMaxLength(8000);
        b.Property(x => x.GrantedScopes).HasMaxLength(2000);
        b.Property(x => x.BexioCompanyId).HasMaxLength(100);
        b.Property(x => x.BexioCompanyName).HasMaxLength(300);
        b.Property(x => x.ConnectedBy).HasMaxLength(256);
        b.Property(x => x.LastError).HasMaxLength(4000);
        b.Property(x => x.ClientIdFingerprint).HasMaxLength(64);

        b.HasIndex(x => x.TenantId).IsUnique().HasDatabaseName("ux_bexio_connections_tenant");
    }
}

internal sealed class BexioReferenceItemConfiguration : IEntityTypeConfiguration<BexioReferenceItem>
{
    public void Configure(EntityTypeBuilder<BexioReferenceItem> b)
    {
        b.ToTable("bexio_reference_items");
        b.HasKey(x => x.Id);
        b.Property(x => x.BexioId).HasMaxLength(100).IsRequired();
        b.Property(x => x.Name).HasMaxLength(300);
        b.Property(x => x.Code).HasMaxLength(100);
        b.Property(x => x.RatePercent).HasColumnType(ColumnTypes.Rate);
        b.Property(x => x.RawJson).HasColumnType(ColumnTypes.Json);
        b.HasIndex(x => new { x.TenantId, x.Kind, x.BexioId }).IsUnique().HasDatabaseName("ux_bexio_reference_kind_id");
    }
}
