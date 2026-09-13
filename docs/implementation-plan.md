# Implementation Plan — Bexio Intelligent Invoice Integration POC

Status: **complete**. Written at Phase 1 and updated as each phase landed.
All ten phases are implemented, tested and verified — see
[`final-poc-report.md`](final-poc-report.md) for what was verified by execution and what was not.
Authoritative specification: `Claude_Code_Bexio_Invoice_Ingestion_POC_Instructions.md` (supplied by the product owner).

---

## 1. Current-state assessment

The repository was inspected before any code was written.

| Aspect | Finding |
|---|---|
| Repository contents | **Empty.** `git status` reports "No commits yet", `find` returns 0 files. |
| Branches | None. Working branch `claude/bexio-invoice-ingestion-poc-6eot5u` created, no upstream. |
| README / docs | None. |
| Solution / project files | None. |
| Existing architecture | None. |
| Existing tests | None. |
| CI/CD | None. |
| Configuration / secrets handling | None. |

**Conclusion:** this is a greenfield build. Principle 9 of the specification ("preserve good existing work", "do not blindly replace an established architecture") is vacuously satisfied — there is nothing to preserve. Every architectural decision is therefore ours, and is recorded as an ADR under `docs/adr/`.

### 1.1 Build environment assessment (material constraints)

The execution environment was probed because it constrains what can honestly be verified.

| Capability | Status | Consequence |
|---|---|---|
| .NET SDK | **Not preinstalled.** `dot.net` / `builds.dotnet.microsoft.com` blocked by the egress proxy. Obtained **.NET SDK 10.0.401** by extracting `mcr.microsoft.com/dotnet/sdk:10.0` (MCR is reachable). | .NET 10 is the current LTS (Nov 2025). Target framework `net10.0`. |
| PostgreSQL | **Installed natively** (PostgreSQL 16.15 from the Ubuntu archive) and running on `127.0.0.1:5432`. | Integration tests run against a real PostgreSQL. |
| Docker daemon | Available, started manually. | Container build is verifiable. |
| Docker Hub / ECR / GHCR / Quay **image blobs** | **Blocked** (403 from the registry CDNs). Only `mcr.microsoft.com` serves blobs. | **Testcontainers cannot pull `postgres:*` in this environment.** Mitigation: the integration-test fixture is provider-agnostic — it uses `IELBEXIO_TEST_POSTGRES` when set (CI/local native server) and falls back to Testcontainers when a Docker image is obtainable. See ADR-0009. |
| `docs.bexio.com`, `developer.bexio.com`, `api.bexio.com`, `auth.bexio.com` | **Blocked by egress policy** (HTTP 000 / CONNECT denied). | **Bexio API behaviour could not be verified against primary documentation, and no live call to Bexio was made.** See §11. |
| `shopify.dev`, `developer-docs.amazon.com` | **Blocked.** | Same treatment as Bexio. |
| Web search | Available (secondary sources only). | Used for corroboration, never treated as authoritative. |

This is the single most important honesty constraint in this POC and it is repeated in `docs/final-poc-report.md` and `docs/known-limitations.md`.

---

## 2. Target architecture

Modular monolith, one deployable ASP.NET Core host, hard module boundaries enforced by project references (a module cannot reference a sibling module's internals — only `Application` ports).

```
                    ┌───────────────────────────────────────────┐
Sources             │ Connectors.Shopify   Connectors.Amazon     │  (adapters, replaceable)
                    │   ShopifyGraphQL…      AmazonSpApi…        │
                    │   FixtureShopify…      FixtureAmazon…      │
                    └──────────────────┬────────────────────────┘
                                       │ ISourceConnector / IInvoiceSource / …
                    ┌──────────────────▼────────────────────────┐
Ingestion           │ ImportService  DocumentIngestionService    │
                    │ raw payload persisted verbatim (provenance)│
                    └──────────────────┬────────────────────────┘
                    ┌──────────────────▼────────────────────────┐
Normalization       │ canonical Invoice / InvoiceLine / Customer │
                    │ deterministic mapping, Money value object  │
                    └──────────────────┬────────────────────────┘
                    ┌──────────────────▼────────────────────────┐
Validation          │ InvoiceValidator (deterministic rules)     │
                    │ TaxDeterminationService (rule table)       │
                    └──────────────────┬────────────────────────┘
                    ┌──────────────────▼────────────────────────┐
AI (optional)       │ IAiService gateway → AiProposal rows only  │ ◄── can be fully disabled
                    │ budget, schema, injection defence, cache   │
                    └──────────────────┬────────────────────────┘
                    ┌──────────────────▼────────────────────────┐
Human               │ Blazor review UI → corrections → verify    │
                    │ → explicit approval (Approver role)        │
                    └──────────────────┬────────────────────────┘
                    ┌──────────────────▼────────────────────────┐
Dispatch            │ same DB transaction: state=APPROVED        │
                    │                    + OutboxMessage         │
                    └──────────────────┬────────────────────────┘
                    ┌──────────────────▼────────────────────────┐
Worker              │ OutboxDispatcher (BackgroundService)       │
                    │ FOR UPDATE SKIP LOCKED, backoff + jitter   │
                    └──────────────────┬────────────────────────┘
                    ┌──────────────────▼────────────────────────┐
Bexio               │ preflight → IBexioClient                   │
                    │ MockBexioClient | BexioApiClient (OAuth)   │
                    └──────────────────┬────────────────────────┘
                    ┌──────────────────▼────────────────────────┐
Audit               │ AuditEvent, SynchronizationAttempt,        │
                    │ FieldProvenance, reconciliation            │
                    └───────────────────────────────────────────┘
```

PostgreSQL is the internal source of truth. Bexio is a downstream destination.

## 3. Modules (projects)

| Project | Responsibility | May reference |
|---|---|---|
| `IelBexio.Domain` | Canonical entities, value objects (`Money`), enums, workflow state machine, domain rules. **No I/O, no EF, no HTTP.** | — |
| `IelBexio.Application` | Ports (interfaces) + use-case services: import, normalization, validation, tax determination, mapping, review, approval, sync orchestration, preflight. | Domain |
| `IelBexio.Infrastructure` | EF Core + Npgsql, `AppDbContext`, migrations, repositories, outbox store & dispatcher, blob storage abstraction, audit writer, clock, tenant context. | Domain, Application |
| `IelBexio.Connectors.Bexio` | `IBexioClient` implementations, OAuth token service, error classification, DTOs, endpoint catalog. | Domain, Application |
| `IelBexio.Connectors.Shopify` | Shopify Admin GraphQL connector + fixture connector. | Domain, Application |
| `IelBexio.Connectors.Amazon` | Amazon SP-API connector + fixture connector. | Domain, Application |
| `IelBexio.Ai` | `IAiService` gateway, Azure OpenAI client, policy enforcement, disabled implementation. | Domain, Application |
| `IelBexio.Web` | ASP.NET Core host: REST API (OpenAPI), Blazor Web App UI, hosted outbox worker, composition root. | all |
| `IelBexio.UnitTests` | Pure unit tests (no DB, no network). | all |
| `IelBexio.IntegrationTests` | Real PostgreSQL, WireMock.Net Bexio simulation, full E2E workflow. | all |

Boundary rule: connectors and AI never reference each other or `Infrastructure`. They depend only on `Application` ports. Composition happens in `IelBexio.Web`.

## 4. Database model

PostgreSQL. EF Core migrations. `decimal` in .NET ↔ `numeric` in PostgreSQL. Never `float`/`double` for money.

Core tables: `tenants`, `source_connections`, `import_runs`, `raw_source_payloads`, `customers`, `invoices`, `invoice_lines`, `tax_assessments`, `payments`, `document_artifacts`, `extraction_results`, `ai_proposals`, `ai_usage_records`, `approvals`, `synchronization_attempts`, `audit_events`, `outbox_messages`, `field_provenance`, plus mapping tables `customer_mappings`, `product_mappings`, `tax_mappings`, `account_mappings`, and `bexio_connections` / `bexio_reference_cache`.

Key constraints (duplicate prevention is a correctness requirement, not a nicety):

| Constraint | Purpose |
|---|---|
| `UX invoices (tenant_id, source_system, source_document_id, source_document_version)` | One canonical invoice per source document version → repeated imports cannot duplicate. |
| `UX synchronization_attempts (idempotency_key)` | One sync per logical operation. |
| `UX invoices (tenant_id, bexio_invoice_id) WHERE bexio_invoice_id IS NOT NULL` | One canonical invoice per Bexio invoice. |
| `UX document_artifacts (tenant_id, sha256)` | Document deduplication. |
| `UX tax_mappings (tenant_id, internal_tax_code, country, valid_from)` | Deterministic tax mapping lookup. |
| Indexes on `tenant_id`, `workflow_state`, `invoice_number`, `created_at`, `bexio_invoice_id`, outbox `(status, next_attempt_at)` | Query paths in the spec §26. |

Multi-tenancy: every business table carries `tenant_id`; EF global query filters bound to `ITenantContext` enforce isolation at the data-access boundary.

## 5. Integration plan

| Integration | POC mode | Real mode | Blocking? |
|---|---|---|---|
| Bexio | `MockBexioClient` (default) — in-memory reference data + scriptable failures | `BexioApiClient` — OAuth 2.0 authorization-code + refresh against `auth.bexio.com`, REST against `api.bexio.com` | **Yes for live verification.** No credentials, host blocked. Fully implemented and unit/WireMock tested. |
| Shopify | `FixtureShopifyConnector` reading `fixtures/shopify/*.json` | `ShopifyGraphQlConnector` — Admin GraphQL, configurable API version, cursor pagination, cost-aware throttling | Yes for live. Fixture path fully working. |
| Amazon | `FixtureAmazonConnector` reading `fixtures/amazon/*.json` | `AmazonSpApiConnector` — LWA token exchange, Orders API, RDT for PII | Yes for live. Fixture path fully working. |
| Azure OpenAI | `DisabledAiService` (default) / `StubAiService` (deterministic, for tests) | `AzureOpenAiService` | No — AI is optional by design (acceptance criterion 9). |
| Blob storage | `LocalFileBlobStore` (private directory) | `AzureBlobStore` | No. |

Switching is pure configuration (`Bexio:Mode=Mock|Api`), no domain code changes — acceptance criterion for §5 of the spec.

## 6. AI architecture

One gateway: `IAiService`. Specialised facades `IAiDocumentExtractor`, `IAiClassificationService`, `IAiMappingSuggestionService`, `IAiAnomalyService` all route through it. No business code constructs an Azure OpenAI client.

Enforced per call: allowed model allow-list, allowed operation, max prompt/document bytes, max output tokens, temperature ceiling, timeout, retry policy, JSON-schema validation of output, prompt version, per-invoice call cap, monthly budget, correlation id, PII minimisation, cost estimate recorded.

Hard boundary: AI output is written **only** to `ai_proposals`. There is no code path from an AI response to a canonical field, to an approval, or to Bexio. Enforced by architecture and asserted by tests.

Prompt injection: document text is passed as a delimited, clearly-labelled untrusted data block; the system prompt is a constant; the AI has no tools; output is schema-validated. An injection attempt in invoice text produces a proposal (or a rejection) and nothing else.

## 7. Security risks (summary; full analysis in `docs/threat-model.md`)

Top risks and mitigations: unauthorised approval (role-gated + audit + state machine), duplicate financial posting (idempotency key + unique constraints + preflight), token leakage (tokens never leave the server, never logged, redacting logger), prompt injection (above), cross-tenant access (global query filters + tenant assertions), malicious upload (signature + MIME + size validation, private storage, generated names), SSRF (no user-supplied URLs fetched), replay (source document version + idempotency), webhook spoofing (HMAC verification).

## 8. Assumptions

Recorded and tracked in `docs/assumptions.md`. The load-bearing ones:

- A1 — Bexio endpoint paths/fields are taken from secondary sources; **unverified**. Isolated in `BexioEndpoints` + DTOs so a single file changes if wrong.
- A2 — Bexio scopes likewise unverified; declared in configuration with a documented justification per scope.
- A3 — Swiss standard VAT 8.1% (effective 2024-01-01) is used in demo data as a *demo value*, not a tax ruling.
- A4 — One tenant operationally, multi-tenant in the model.
- A5 — Single approver in the POC; the schema already separates `prepared_by` from `approved_by`.

## 9. External credentials required

None are required to run, test, or demo the POC. All are required only for live mode:

| Credential | Needed for | Where it goes |
|---|---|---|
| `Bexio:ClientId` / `ClientSecret` | Bexio OAuth | User Secrets / env / Key Vault |
| Shopify shop domain + Admin API access token | Live Shopify | same |
| Amazon LWA client id/secret + refresh token | Live Amazon | same |
| `AzureOpenAI:Endpoint` / `ApiKey` (or Managed Identity) | AI features | same |
| Azure Storage connection | Azure blob mode | same |
| Entra ID tenant/client id | Entra authentication mode | same |

`.env.example` and `appsettings.Development.json` contain placeholders only. A CI secret-scan job guards this.

## 10. Implementation phases

Executed in this order, with `dotnet build` + `dotnet test` + a commit at each phase boundary (spec §38).

| # | Phase | Status |
|---|---|---|
| 1 | Repo assessment, environment, plan, solution skeleton | ✅ complete |
| 2 | Domain model + EF Core + PostgreSQL migrations | ✅ complete |
| 3 | Bexio abstraction + mock + OAuth API client + error classification | ✅ complete |
| 4 | Source connectors (Shopify, Amazon) + fixtures | ✅ complete |
| 5 | Document ingestion + deterministic validation + tax determination + mappings | ✅ complete |
| 6 | AI gateway + controls + injection defence | ✅ complete |
| 7 | Workflow state machine + approval + outbox + sync worker + preflight + reconciliation | ✅ complete |
| 8 | REST API + OpenAPI + Blazor review UI | ✅ complete |
| 9 | Tests: unit, integration, negative (§32), E2E happy path (§31) | ✅ complete — 221 passing |
| 10 | Docker, compose, demo scripts, CI, documentation, final report | ✅ complete |

## 11. Acceptance criteria → how each is demonstrated

Mapped 1:1 to spec §37 in `docs/final-poc-report.md`. Criterion 1 ("Bexio sandbox can be connected using the current OAuth flow") is the one criterion that **cannot be closed in this environment** — `auth.bexio.com` and `api.bexio.com` are unreachable and no credentials exist. It is implemented and tested against a WireMock.Net simulation of the documented flow; it is reported as *implemented, not live-verified*.

## 12. Known uncertainties

| # | Uncertainty | Containment |
|---|---|---|
| U1 | Exact Bexio endpoint paths and version prefixes (`/2.0` vs `/3.0`) per resource. | Single `BexioEndpoints` catalog; each entry carries a `Verification` marker. |
| U2 | Exact Bexio request/response field names for `kb_invoice` and positions. | DTOs in one file; WireMock contract tests pin our assumption so a change is a one-file edit + test update. |
| U3 | Exact Bexio scope strings and whether write implies read. | Configured list + documented rationale; failure surfaces as a classified `AUTHORIZATION` error, not a crash. |
| U4 | Bexio rate-limit headers and 429 semantics. | Backoff honours `Retry-After` when present, else exponential + jitter. |
| U5 | Shopify API version lifecycle. | Version is a single configuration value, never hardcoded at call sites. |
| U6 | Amazon SP-API buyer-PII availability per marketplace/authorisation. | Connector declares capabilities; missing buyer data is a first-class `NEEDS_REVIEW` outcome, not an exception. |
| U7 | Swiss VAT treatment of edge cases (export, reverse charge, mixed rates). | Deliberately **out of scope** — the system proposes and requires human verification; it is not a tax engine (spec §39). |
