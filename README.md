# Bexio Intelligent Invoice Integration — Proof of Concept

An invoice ingestion platform that takes structured and document data from commerce platforms,
normalises it into a canonical PostgreSQL-backed model, validates it deterministically, routes
anything needing judgement to a human, and only then synchronises **approved** records into Bexio.

It is deliberately **not** a Shopify→Bexio point-to-point integration, and **not** a Bexio clone.

```
Sources ─▶ Connectors ─▶ Raw payload retained ─▶ Canonical model ─▶ Deterministic validation
        ─▶ AI proposals (optional) ─▶ Human review ─▶ Explicit approval ─▶ Transactional outbox
        ─▶ Background worker ─▶ Bexio adapter ─▶ Reconciliation + audit
```

PostgreSQL is the internal source of truth. Bexio is a downstream destination.

---

## The two rules everything else serves

**1. Nothing reaches Bexio without an explicit human approval.**
There is no API endpoint that posts to Bexio, and no UI button that does either. Approval writes an
outbox message inside the same database transaction as the state change; a background worker performs
the synchronisation and re-runs every pre-flight check first. `Invoice.WorkflowState` has a private
setter, and the only path to a sync-eligible state is through `Approved`.

**2. The same invoice can never be posted twice.**
The idempotency key is derived from `tenant + source system + source document id + source document
version` — never from our own row ids, so it survives re-imports. It is reserved in the database
*before* Bexio is called, so a crash between Bexio committing and us recording it reconciles on retry
instead of creating a second invoice. A unique index enforces it even if application code is bypassed.

Both are covered by tests that attack them rather than assume them.

---

## Running it

Requirements: .NET 10 SDK and a PostgreSQL instance. Nothing else — no Bexio account, no Shopify
store, no Amazon credentials, no Azure subscription and no AI key are needed to run the full workflow.

```bash
./scripts/setup-demo.sh --fresh    # infrastructure, database, migrations, seed, application
./scripts/run-demo.sh              # the complete §31 scenario, end to end
```

Then open <http://localhost:5188>.

With Docker instead:

```bash
cp .env.example .env               # optional; defaults run entirely on mocks and fixtures
docker compose up --build
```

`run-demo.sh` drives the application's own HTTP API — the same surface the UI uses — and finishes by
re-running the import to prove no duplicate invoice is created.

---

## What is real, and what is not

This matters more than a feature list, so it is stated plainly here and in detail in
[`docs/final-poc-report.md`](docs/final-poc-report.md).

| Area | Status |
|---|---|
| Canonical model, validation, tax determination, workflow, approval, outbox, sync, audit, provenance | **Real.** Running against PostgreSQL, covered by tests. |
| Bexio | **Mock by default.** A real OAuth client exists and is contract-tested against WireMock, but **no live Bexio call has ever been made** — the API and documentation hosts are unreachable from the build environment. |
| Shopify / Amazon | **Fixtures by default.** Live connectors are implemented; the fixture and live paths share one normaliser, so the demo exercises the real mapping logic. |
| AI | **Disabled by default.** Fully implemented behind a controlled gateway. Everything works with it off. |
| Azure (Blob, Key Vault, Entra ID, App Insights) | **Local equivalents by default**, Azure implementations behind the same interfaces. |

**The endpoint paths, field names and OAuth scopes in the Bexio adapter could not be verified against
primary documentation.** They are marked with an explicit `VerificationStatus` in
[`BexioEndpoints`](src/IelBexio.Connectors.Bexio/Configuration/BexioEndpoints.cs) and isolated so that
correcting them is a one-file change. See [`docs/bexio-integration.md`](docs/bexio-integration.md).

---

## Layout

| Project | Responsibility |
|---|---|
| `IelBexio.Domain` | Canonical entities, `Money`, the workflow state machine. No I/O. |
| `IelBexio.Application` | Ports and use cases: validation, tax determination, pre-flight, idempotency, outbox. |
| `IelBexio.Infrastructure` | EF Core + PostgreSQL, services, outbox store, blob storage, composition root. |
| `IelBexio.Connectors.Bexio` | `IBexioClient` — mock and live OAuth implementations. |
| `IelBexio.Connectors.Shopify` | Admin GraphQL and fixture connectors, one shared normaliser. |
| `IelBexio.Connectors.Amazon` | SP-API and fixture connectors, one shared normaliser. |
| `IelBexio.Ai` | The single controlled AI gateway and its facades. |
| `IelBexio.Web` | REST API, Blazor UI, background worker. |

Connectors and the AI layer depend only on `Application` ports — never on `Infrastructure` or on each
other. Composition happens once, in `ServiceCollectionExtensions`.

---

## Tests

```bash
dotnet test                                    # everything
IELBEXIO_TEST_POSTGRES="Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=postgres" \
  dotnet test tests/IelBexio.IntegrationTests  # against a specific PostgreSQL
```

Integration tests use a real PostgreSQL, never an in-memory provider: they exist to verify unique
constraints, `numeric` arithmetic, transactions and `SELECT … FOR UPDATE SKIP LOCKED`. The fixture
resolves a database from `IELBEXIO_TEST_POSTGRES`, then Testcontainers, then a local server.

---

## Documentation

| | |
|---|---|
| [architecture.md](docs/architecture.md) | Structure and the reasoning behind it |
| [domain-model.md](docs/domain-model.md) | The canonical model and its constraints |
| [bexio-integration.md](docs/bexio-integration.md) | Bexio adapter, OAuth, and **what is unverified** |
| [shopify-integration.md](docs/shopify-integration.md) | Shopify connector and its restrictions |
| [amazon-integration.md](docs/amazon-integration.md) | Amazon connector and its restrictions |
| [tax-model.md](docs/tax-model.md) | Tax determination — and why this is not a tax engine |
| [ai-architecture.md](docs/ai-architecture.md) | The AI gateway, its controls and its hard boundary |
| [security.md](docs/security.md) | Controls, and honest statements of what they do not cover |
| [threat-model.md](docs/threat-model.md) | Threats, mitigations, residual risk |
| [data-provenance.md](docs/data-provenance.md) | How a value is traced to its source |
| [testing.md](docs/testing.md) | Test strategy and what each layer proves |
| [local-development.md](docs/local-development.md) | Getting set up |
| [deployment.md](docs/deployment.md) | Azure deployment and production gaps |
| [operations.md](docs/operations.md) | Running it: health, retries, reconciliation |
| [assumptions.md](docs/assumptions.md) | Every assumption made, and its risk |
| [known-limitations.md](docs/known-limitations.md) | What this POC does not do |
| [final-poc-report.md](docs/final-poc-report.md) | **What works, what is mocked, what is uncertain** |
| [adr/](docs/adr/) | Architecture decision records |

---

## Licence and data

All demo data is synthetic. Company names, VAT numbers, addresses and email addresses in fixtures are
invented; the Swiss UID numbers are format-valid but belong to no real entity, and every email domain
is `.example`. No real personal or company financial data appears anywhere in this repository.
