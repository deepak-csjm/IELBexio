# Canonical domain model

Source-independent by design (§7). Shopify, Amazon and uploaded documents all normalise into these
types, and Bexio is populated *from* them. No vendor field name appears in the domain.

## Money

`Money` is a value object carrying a `decimal` amount and a mandatory ISO-4217 currency. Adding two
currencies throws `CurrencyMismatchException` — mixing them is impossible by construction rather than
by convention. Amounts round half-to-even at a 4-decimal storage scale; presentation scale is
per-currency (2 for CHF/EUR/USD, 0 for JPY, 3 for KWD).

Every monetary column is PostgreSQL `numeric`, never `float` or `double`. A test demonstrates that
0.1 + 0.2 = 0.3 exactly and that a hundred rappen sum to exactly one franc.

## Core entities

| Entity | Purpose |
|---|---|
| `Tenant` | A tenant of the platform |
| `SourceConnection` | A configured link to a source. Holds a **secret reference**, never a secret |
| `ImportRun` | One import execution, with counts and a correlation id |
| `RawSourcePayload` | The verbatim source payload, retained for provenance and replay |
| `Customer` | Canonical customer, with `IsAnonymised` for sources that withhold identity |
| `Invoice` | The canonical invoice. `WorkflowState` has a **private setter** |
| `InvoiceLine` | Canonical line, with `LineKind` distinguishing product/shipping/discount |
| `Payment` | A payment as reported by the source; refunds carry a negative amount |
| `TaxAssessment` | Source tax, our conclusion, proposed and applied Bexio ids, verification — all separate |
| `DocumentArtifact` | Stored document: hash, MIME type, size, source, scan state |
| `ExtractionResult` | Extraction output and the cache key of §13 |
| `AiProposal` | **The only place AI output is written** |
| `AiUsageRecord` | Safe AI telemetry. Deliberately excludes prompts and responses |
| `Approval` | An explicit decision, with the approval version it covered |
| `SynchronizationAttempt` | One attempt, with the idempotency key and request hash |
| `OutboxMessage` | Transactional outbox, with lease and backoff |
| `AuditEvent` | Append-only. Records AI involvement explicitly |
| `FieldProvenance` | Traces one canonical field to its origin |
| `CustomerMapping` / `ProductMapping` / `TaxMapping` / `AccountMapping` | Internal → Bexio |
| `BexioConnection` | OAuth connection. Tokens encrypted at rest |
| `BexioReferenceItem` | Bexio configuration discovered through the API — never seeded |

## Constraints that carry the guarantees

| Constraint | What it prevents |
|---|---|
| `UX invoices (tenant, source_system, source_document_id, source_document_version)` | A repeated import creating a second canonical invoice |
| `UX synchronization_attempts (idempotency_key)` | Two workers posting the same invoice |
| `UX invoices (tenant, bexio_invoice_id) WHERE NOT NULL` | Two canonical invoices claiming one Bexio invoice |
| `UX document_artifacts (tenant, sha256)` | The same document ingested twice |
| `UX raw_payloads (tenant, source_system, doc_id, version)` | Duplicate retained payloads |
| `UX tax_mappings (tenant, code, country, valid_from) WHERE is_active` | Ambiguous tax resolution |
| `UX customer_mappings (tenant, customer_id) WHERE is_active` | Ambiguous contact resolution |

These are database constraints, not application checks. A test bypasses the application entirely and
confirms the database still refuses.

## Multi-tenancy

Every business entity derives from `TenantEntity`, so `TenantId` cannot be forgotten. Query filters are
applied by reflection over every such type, so a newly added entity is isolated the moment it exists.
`SaveChanges` stamps the tenant on insert and refuses a modification that would cross a boundary.

## Workflow

```
Imported → Extracted → Validated ─────────────┐
                    ↘ NeedsReview → HumanVerified → ReadyForApproval
                                                  ↓
                                              Approved → QueuedForBexio → Syncing → Synced
                                                                             ↓         ↓
                                                            SyncFailed / BexioRejected  ReconciliationRequired
```

`Synced` leads only to `ReconciliationRequired` — reconciliation must be able to flag an invoice whose
Bexio counterpart has changed, but nothing reachable from `Synced` is sync-eligible, so a second
posting remains impossible. A test walks the graph and asserts exactly that.
