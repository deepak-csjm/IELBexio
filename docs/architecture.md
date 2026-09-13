# Architecture

## Shape

A modular monolith: one deployable ASP.NET Core host, with module boundaries enforced by project
references rather than by convention. Microservices were rejected (ADR-0001) because this system's
hard problems are transactional consistency and auditability, and a distributed architecture makes
both harder while solving a scaling problem the POC does not have.

```
                    ┌────────────────────────────────────────────┐
Sources             │ Connectors.Shopify     Connectors.Amazon    │  adapters, replaceable
                    │  ShopifyGraphQl…         AmazonSpApi…       │
                    │  FixtureShopify…         FixtureAmazon…     │
                    └──────────────────┬─────────────────────────┘
                                       │ IInvoiceSource, ICustomerSource, …
                    ┌──────────────────▼─────────────────────────┐
Ingestion           │ ImportService                               │
                    │ raw payload persisted verbatim, then mapped  │
                    └──────────────────┬─────────────────────────┘
                    ┌──────────────────▼─────────────────────────┐
Canonical model     │ Invoice / InvoiceLine / Customer / Payment   │
                    │ no vendor vocabulary past this line          │
                    └──────────────────┬─────────────────────────┘
                    ┌──────────────────▼─────────────────────────┐
Deterministic       │ InvoiceValidator    TaxDeterminationService  │
                    │ pure functions, no AI, no I/O                │
                    └──────────────────┬─────────────────────────┘
                    ┌──────────────────▼─────────────────────────┐
AI (optional)       │ IAiService gateway → ai_proposals only       │ ◄── can be switched off entirely
                    └──────────────────┬─────────────────────────┘
                    ┌──────────────────▼─────────────────────────┐
Human               │ Blazor review UI → corrections → verify      │
                    │ → explicit approval (Approver role)          │
                    └──────────────────┬─────────────────────────┘
                    ┌──────────────────▼─────────────────────────┐
Dispatch            │ ONE transaction: state=Approved             │
                    │                + Approval + OutboxMessage    │
                    └──────────────────┬─────────────────────────┘
                    ┌──────────────────▼─────────────────────────┐
Worker              │ OutboxProcessor — FOR UPDATE SKIP LOCKED,    │
                    │ leases, exponential backoff with jitter      │
                    └──────────────────┬─────────────────────────┘
                    ┌──────────────────▼─────────────────────────┐
Bexio               │ pre-flight (again) → IBexioClient            │
                    └──────────────────┬─────────────────────────┘
                    ┌──────────────────▼─────────────────────────┐
Audit               │ AuditEvent, FieldProvenance,                 │
                    │ SynchronizationAttempt, reconciliation       │
                    └────────────────────────────────────────────┘
```

## Dependency rules

| Project | May reference |
|---|---|
| `Domain` | nothing |
| `Application` | `Domain` |
| `Infrastructure` | `Domain`, `Application`, and the connector/AI assemblies (composition root only) |
| `Connectors.*`, `Ai` | `Domain`, `Application` |
| `Web` | everything |

Connectors and the AI layer never reference `Infrastructure` or each other. The AI assembly has no
reference to `IBexioClient` at all — the boundary in §40 of the specification is therefore not a
policy that code could violate, it is an absence of the reference that would let it.

## The decisions that carry weight

### PostgreSQL is the source of truth, not Bexio

Workflow state, approvals, provenance and synchronisation state live here. Bexio holds accounting
records; it does not hold "who approved this and when, and what did the source originally say". A
design that treated Bexio as the system of record would have no answer when an auditor asks why a
number is what it is.

### The workflow is a state machine, and it is the only mutator

`Invoice.WorkflowState` has a private setter. `InvoiceWorkflow` holds the transition table. Every
state change in the system goes through `TransitionTo`, which throws on an illegal transition. This
is what turns "unapproved data cannot reach Bexio" from a rule someone must remember into a property
of the type. `Synced` leads only to `ReconciliationRequired`, which is itself not sync-eligible.

### The outbox is transactional, not a queue call

The approval and the intent to act on it are written in one database transaction. Publishing to a
message broker after committing the approval would leave a window where an invoice is approved but
will never be sent — or worse, sent but not recorded. Azure Service Bus is a reasonable future
addition *behind* the outbox, not in place of it (ADR-0006).

### Idempotency is keyed on the source document, not on our rows

`tenant + source system + source document id + source document version`. Keying on a generated invoice
id would not help: a re-import that created a second row would produce a second key and a second
posting. The key is reserved *before* the Bexio call, so a crash mid-flight reconciles rather than
duplicates.

### Deterministic first, AI second, and separable

Validation and tax determination are pure functions with no AI involvement, so their results are
identical whether AI is on or off. AI output lands only in `ai_proposals`. Accepting a proposal goes
through the same validated correction path a human typing the value would use — there is no
privileged write path for AI-originated values, and the resulting provenance records a human decision.

### Money is a type, not a decimal

`Money` carries an ISO-4217 currency, and adding two currencies throws. Every monetary column is
PostgreSQL `numeric`. An LLM never performs authoritative arithmetic.

## Request and worker context

Each HTTP request resolves a tenant and an actor into `ITenantContext` and `ICurrentUser`; EF global
query filters are built by reflection over every `TenantEntity`, so a newly added entity is isolated
the moment it exists rather than when someone remembers to filter it.

The outbox worker deliberately spans tenants — one queue serves them all — but sets the tenant from
each message before dispatching. An earlier version did not, and the audit events recording that money
had been posted were written with an empty tenant and became invisible to the tenant that owned them.
That is now covered by a regression test asserting no audit event is ever orphaned.

## Observability

One correlation id follows an invoice from import to synchronisation, across the HTTP request and the
background worker, and is stored on the invoice, every audit event, every provenance record and every
synchronisation attempt. `/api/audit?correlationId=…` returns the whole lifecycle.
