using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IelBexio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_mappings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    internal_account_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    bexio_account_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    bexio_account_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    bexio_account_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    origin = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    confidence_signal = table.Column<decimal>(type: "numeric(5,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_mappings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_proposals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    field_path = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    operation = table.Column<int>(type: "integer", nullable: false),
                    proposed_value_json = table.Column<string>(type: "jsonb", nullable: false),
                    current_value_json = table.Column<string>(type: "jsonb", nullable: true),
                    confidence_signal = table.Column<decimal>(type: "numeric(5,4)", nullable: false),
                    model = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    prompt_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    schema_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    evidence_json = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    reviewed_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review_comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_proposals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_usage_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<int>(type: "integer", nullable: false),
                    model = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    prompt_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: false),
                    completion_tokens = table.Column<int>(type: "integer", nullable: false),
                    total_tokens = table.Column<int>(type: "integer", nullable: false),
                    estimated_cost = table.Column<decimal>(type: "numeric(12,6)", nullable: false),
                    cost_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    failure_reason = table.Column<int>(type: "integer", nullable: false),
                    failure_detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    served_from_cache = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_usage_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    decision = table.Column<int>(type: "integer", nullable: false),
                    prepared_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    approved_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approval_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approvals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor_type = table.Column<int>(type: "integer", nullable: false),
                    entity_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    old_value_json = table.Column<string>(type: "jsonb", nullable: true),
                    new_value_json = table.Column<string>(type: "jsonb", nullable: true),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source = table.Column<int>(type: "integer", nullable: false),
                    ai_involved = table.Column<bool>(type: "boolean", nullable: false),
                    ai_proposal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bexio_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    protected_access_token = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    protected_refresh_token = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    access_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    refresh_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    granted_scopes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    bexio_company_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    bexio_company_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    connected_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    connected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_refreshed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_reference_refresh_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    client_id_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bexio_connections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bexio_reference_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    bexio_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    rate_percent = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    raw_json = table.Column<string>(type: "jsonb", nullable: true),
                    discovered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bexio_reference_items", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "customer_mappings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bexio_contact_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    bexio_contact_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    origin = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    confidence_signal = table.Column<decimal>(type: "numeric(5,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_mappings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_system = table.Column<int>(type: "integer", nullable: false),
                    source_connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_customer_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    company_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    first_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    last_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    phone = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    vat_number = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    line1 = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    line2 = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    postal_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    city = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    region = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    address_country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    external_identifiers_json = table.Column<string>(type: "jsonb", nullable: true),
                    is_anonymised = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "document_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    blob_uri = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    content_type = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    source_system = table.Column<int>(type: "integer", nullable: false),
                    source_reference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    processing_version = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ingested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rejection_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    duplicate_of_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    malware_scan_state = table.Column<int>(type: "integer", nullable: false),
                    malware_scan_detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_artifacts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "extraction_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    extractor_version = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    model = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    prompt_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    method = table.Column<int>(type: "integer", nullable: false),
                    extracted_json = table.Column<string>(type: "jsonb", nullable: false),
                    field_confidence_json = table.Column<string>(type: "jsonb", nullable: true),
                    validation_errors_json = table.Column<string>(type: "jsonb", nullable: true),
                    succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    produced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    cache_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_extraction_results", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "field_provenance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_path = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    origin = table.Column<int>(type: "integer", nullable: false),
                    source_system = table.Column<int>(type: "integer", nullable: false),
                    source_document_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    source_field_path = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    transformation = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    extraction_method = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    ai_proposal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ai_model = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    ai_prompt_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    modified_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    value_json = table.Column<string>(type: "jsonb", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_field_provenance", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "import_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_system = table.Column<int>(type: "integer", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    documents_seen = table.Column<int>(type: "integer", nullable: false),
                    invoices_created = table.Column<int>(type: "integer", nullable: false),
                    invoices_updated = table.Column<int>(type: "integer", nullable: false),
                    duplicates_skipped = table.Column<int>(type: "integer", nullable: false),
                    failures = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    request_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    last_error_category = table.Column<int>(type: "integer", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    leased_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "product_mappings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    product_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    bexio_article_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    bexio_account_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    origin = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    confidence_signal = table.Column<decimal>(type: "numeric(5,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_mappings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "raw_source_payloads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_system = table.Column<int>(type: "integer", nullable: false),
                    import_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_document_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_document_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    payload_sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    api_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    retrieved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_raw_source_payloads", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "source_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_system = table.Column<int>(type: "integer", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    external_account_id = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    secret_reference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    last_synced_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_sync_cursor = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    last_successful_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_connections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "synchronization_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    destination = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    operation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<string>(type: "char(64)", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    external_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    error_category = table.Column<int>(type: "integer", nullable: false),
                    error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    error_message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    response_metadata_json = table.Column<string>(type: "jsonb", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_synchronization_attempts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tax_assessments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    jurisdiction = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    tax_type = table.Column<int>(type: "integer", nullable: false),
                    rate_percent = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    taxable_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    source_tax_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    source_tax_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    source_tax_rate_percent = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    internal_tax_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    proposed_bexio_tax_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    applied_bexio_tax_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    determination_method = table.Column<int>(type: "integer", nullable: false),
                    determination_version = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    confidence_signal = table.Column<decimal>(type: "numeric(5,4)", nullable: false),
                    human_verified = table.Column<bool>(type: "boolean", nullable: false),
                    verified_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tax_assessments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tax_mappings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    internal_tax_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    rate_percent = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    bexio_tax_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    bexio_tax_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    bexio_tax_rate_percent = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    bexio_tax_is_active = table.Column<bool>(type: "boolean", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    origin = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    confidence_signal = table.Column<decimal>(type: "numeric(5,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tax_mappings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    default_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_system = table.Column<int>(type: "integer", nullable: false),
                    source_connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_document_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_document_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    invoice_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    invoice_date = table.Column<DateOnly>(type: "date", nullable: true),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    billing_line1 = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    billing_line2 = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    billing_postal_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    billing_city = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    billing_region = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    billing_country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    shipping_line1 = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    shipping_line2 = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    shipping_postal_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    shipping_city = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    shipping_region = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    shipping_country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    subtotal_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    shipping_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    payment_status = table.Column<int>(type: "integer", nullable: false),
                    document_status = table.Column<int>(type: "integer", nullable: false),
                    extraction_status = table.Column<int>(type: "integer", nullable: false),
                    validation_status = table.Column<int>(type: "integer", nullable: false),
                    approval_status = table.Column<int>(type: "integer", nullable: false),
                    sync_status = table.Column<int>(type: "integer", nullable: false),
                    workflow_state = table.Column<int>(type: "integer", nullable: false),
                    bexio_invoice_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    bexio_contact_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    human_verified = table.Column<bool>(type: "boolean", nullable: false),
                    verified_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    prepared_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    approved_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    import_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    primary_document_artifact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoices", x => x.id);
                    table.ForeignKey(
                        name: "fk_invoices_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invoice_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    source_line_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    sku = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    product_code = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    unit = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    unit_price = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    net_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    tax_rate_percent = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    gross_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    tax_assessment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_mapping_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bexio_article_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    bexio_account_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    bexio_tax_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_invoice_lines_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_payment_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    method = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    transaction_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.ForeignKey(
                        name: "fk_payments_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_account_mappings_code",
                table: "account_mappings",
                columns: new[] { "tenant_id", "internal_account_code" },
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_ai_proposals_entity",
                table: "ai_proposals",
                columns: new[] { "tenant_id", "entity_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_invoice",
                table: "ai_usage_records",
                columns: new[] { "tenant_id", "invoice_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_tenant_time",
                table: "ai_usage_records",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_approvals_entity",
                table: "approvals",
                columns: new[] { "tenant_id", "entity_id", "approved_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_correlation",
                table: "audit_events",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_entity",
                table: "audit_events",
                columns: new[] { "tenant_id", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_tenant_time",
                table: "audit_events",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ux_bexio_connections_tenant",
                table: "bexio_connections",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_bexio_reference_kind_id",
                table: "bexio_reference_items",
                columns: new[] { "tenant_id", "kind", "bexio_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_customer_mappings_customer",
                table: "customer_mappings",
                columns: new[] { "tenant_id", "customer_id" },
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_customers_company",
                table: "customers",
                columns: new[] { "tenant_id", "company_name" });

            migrationBuilder.CreateIndex(
                name: "ix_customers_email",
                table: "customers",
                columns: new[] { "tenant_id", "email" });

            migrationBuilder.CreateIndex(
                name: "ux_customers_source_identity",
                table: "customers",
                columns: new[] { "tenant_id", "source_system", "source_customer_id" },
                unique: true,
                filter: "source_customer_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_document_artifacts_invoice",
                table: "document_artifacts",
                columns: new[] { "tenant_id", "invoice_id" });

            migrationBuilder.CreateIndex(
                name: "ux_document_artifacts_hash",
                table: "document_artifacts",
                columns: new[] { "tenant_id", "sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_extraction_results_document",
                table: "extraction_results",
                columns: new[] { "tenant_id", "document_artifact_id" });

            migrationBuilder.CreateIndex(
                name: "ux_extraction_results_cache_key",
                table: "extraction_results",
                columns: new[] { "tenant_id", "cache_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_provenance_entity_field",
                table: "field_provenance",
                columns: new[] { "tenant_id", "entity_id", "field_path" });

            migrationBuilder.CreateIndex(
                name: "ix_import_runs_tenant_started",
                table: "import_runs",
                columns: new[] { "tenant_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_invoice",
                table: "invoice_lines",
                columns: new[] { "tenant_id", "invoice_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_invoice_id",
                table: "invoice_lines",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_sku",
                table: "invoice_lines",
                columns: new[] { "tenant_id", "sku" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_correlation",
                table: "invoices",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_customer_id",
                table: "invoices",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_created",
                table: "invoices",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_number",
                table: "invoices",
                columns: new[] { "tenant_id", "invoice_number" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_state",
                table: "invoices",
                columns: new[] { "tenant_id", "workflow_state" });

            migrationBuilder.CreateIndex(
                name: "ux_invoices_bexio_invoice_id",
                table: "invoices",
                columns: new[] { "tenant_id", "bexio_invoice_id" },
                unique: true,
                filter: "bexio_invoice_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_invoices_source_identity",
                table: "invoices",
                columns: new[] { "tenant_id", "source_system", "source_document_id", "source_document_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_status_next_attempt",
                table: "outbox_messages",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_tenant_status",
                table: "outbox_messages",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_invoice",
                table: "payments",
                columns: new[] { "tenant_id", "invoice_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_invoice_id",
                table: "payments",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ux_product_mappings_sku",
                table: "product_mappings",
                columns: new[] { "tenant_id", "sku" },
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ux_raw_payloads_source_identity",
                table: "raw_source_payloads",
                columns: new[] { "tenant_id", "source_system", "source_document_id", "source_document_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_source_connections_tenant_system",
                table: "source_connections",
                columns: new[] { "tenant_id", "source_system" });

            migrationBuilder.CreateIndex(
                name: "ix_sync_attempts_entity",
                table: "synchronization_attempts",
                columns: new[] { "tenant_id", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sync_attempts_status",
                table: "synchronization_attempts",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_sync_attempts_idempotency_key",
                table: "synchronization_attempts",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tax_assessments_invoice",
                table: "tax_assessments",
                columns: new[] { "tenant_id", "invoice_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tax_assessments_line",
                table: "tax_assessments",
                column: "invoice_line_id");

            migrationBuilder.CreateIndex(
                name: "ux_tax_mappings_code_country_from",
                table: "tax_mappings",
                columns: new[] { "tenant_id", "internal_tax_code", "country_code", "valid_from" },
                unique: true,
                filter: "is_active");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_mappings");

            migrationBuilder.DropTable(
                name: "ai_proposals");

            migrationBuilder.DropTable(
                name: "ai_usage_records");

            migrationBuilder.DropTable(
                name: "approvals");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "bexio_connections");

            migrationBuilder.DropTable(
                name: "bexio_reference_items");

            migrationBuilder.DropTable(
                name: "customer_mappings");

            migrationBuilder.DropTable(
                name: "document_artifacts");

            migrationBuilder.DropTable(
                name: "extraction_results");

            migrationBuilder.DropTable(
                name: "field_provenance");

            migrationBuilder.DropTable(
                name: "import_runs");

            migrationBuilder.DropTable(
                name: "invoice_lines");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "payments");

            migrationBuilder.DropTable(
                name: "product_mappings");

            migrationBuilder.DropTable(
                name: "raw_source_payloads");

            migrationBuilder.DropTable(
                name: "source_connections");

            migrationBuilder.DropTable(
                name: "synchronization_attempts");

            migrationBuilder.DropTable(
                name: "tax_assessments");

            migrationBuilder.DropTable(
                name: "tax_mappings");

            migrationBuilder.DropTable(
                name: "tenants");

            migrationBuilder.DropTable(
                name: "invoices");

            migrationBuilder.DropTable(
                name: "customers");
        }
    }
}
