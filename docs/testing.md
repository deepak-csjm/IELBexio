# Testing

247 tests: 203 unit, 35 integration and 9 browser. Every integration and browser test runs against
a real PostgreSQL; the browser tests drive the real application in real Chromium.

## What each layer proves

**Unit tests** — pure logic with no I/O: `Money` invariants, the workflow transition graph,
deterministic validation, tax determination, normalisation from real fixtures, the AI gateway's
controls, file inspection, and the deterministic extractors.

**Contract tests (WireMock.Net)** — that our Bexio OAuth client implements the flow correctly:
code exchange, transparent refresh, token rotation, an issuer that omits a refresh token, revocation,
an undecryptable token, an unreachable issuer, and disconnect. They prove our side is right. They do
**not** prove Bexio behaves as simulated; that host is unreachable here.

**Integration tests (real PostgreSQL)** — everything that only a real database can honestly show:
unique constraints, `numeric` arithmetic, transaction boundaries, and `SELECT … FOR UPDATE SKIP
LOCKED`. They use the production composition root rather than hand-wiring test doubles, because tests
that build their own object graph verify a configuration that never ships.

**The end-to-end test** — all twenty steps of §31 in order, finishing with the repeat import that must
create no duplicate.

## Why not an in-memory provider

Because the guarantees under test are database guarantees. An in-memory provider does not enforce a
unique index, does not have `numeric`, and has no row locking — so a test that passed against it would
prove nothing about the thing that actually prevents a duplicate posting.

## Getting a database

The fixture resolves one in order: `IELBEXIO_TEST_POSTGRES`, then Testcontainers, then a local server
on 5432. Testcontainers is preferred and is what CI uses via a service container. It was unusable in
the environment this POC was built in because container registry image blobs are blocked — so rather
than let that become "integration tests are skipped", the fixture falls back. It never falls back to
in-memory.

Each test class gets its own uniquely named database with migrations applied, so tests neither see
each other's rows nor need ordering.

## The negative suite

§32 in full, and written as attacks rather than happy paths. Unbalanced totals; an unapproved invoice
with the worker called directly; missing customer, tax and account mappings; an inactive Bexio tax; a
Bexio tax whose rate contradicts ours; repeated import; the database constraint with application code
bypassed; outbox replay; double-submitted approval; every Bexio failure mode mapped to retry or
dead-letter; retry-then-recover; retry exhaustion; role refusals; approve-then-edit-then-post; editing
an approved invoice; an unsupported correction path; tenant isolation; reconciliation of a vanished
invoice.

## Tests that earned their keep

Seven real defects were found by tests or by running the application, not by reading code:

1. A zero-rate tax match was given full confidence. Zero tax is structurally ambiguous.
2. `BexioConnection.HasUsableRefreshToken` read the wall clock, disagreeing with the injected clock.
3. Header/line reconciliation conflated product and shipping net — and the existing test data itself
   double-counted 20 CHF while balancing by luck.
4. Tax assessments were read back from the database before being saved, so the **first** validation
   pass raised no tax warnings at all.
5. The invoice API returned EF entities with a navigation cycle; serialisation failed mid-stream and
   clients saw a silently truncated body.
6. The outbox worker wrote audit events with an empty tenant id, making the records that say money was
   posted invisible to their owner.
7. Document ingestion lived in a Razor code-behind with no API endpoint, so the path that decides
   whether a file is safe was unreachable by a test. It is now a service shared by the UI and the API.

Each has a regression test.

## Running

```bash
dotnet test                                       # everything
dotnet test tests/IelBexio.UnitTests              # no database needed
IELBEXIO_TEST_POSTGRES="Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=postgres" \
  dotnet test tests/IelBexio.IntegrationTests
dotnet test tests/IelBexio.E2ETests                # real browser; needs Chromium and PostgreSQL
./scripts/setup-demo.sh --fresh && ./scripts/run-demo.sh   # the demo, as a live check
```

The browser tests need a Chromium that Playwright can launch. `pwsh tests/IelBexio.E2ETests/bin/Debug/net10.0/playwright.ps1 install chromium`
downloads the matching build; on a machine that already has one (`PLAYWRIGHT_BROWSERS_PATH` set, as in
many CI images) the fixture finds it, and `IELBEXIO_E2E_CHROMIUM` overrides the choice outright.

## Browser tests

`tests/IelBexio.E2ETests` drives the real application in headless Chromium through Playwright: nine
tests covering the dashboard, every navigation destination, the review queue, the document-beside-data
screen, the full click-through from review to an invoice created in Bexio, an approval blocked by a
failing pre-flight, a server-side role refusal, an audit trace by correlation id, and the tax mapping
screen.

They start the published host as a **separate process** against a **per-test database**, rather than
using `WebApplicationFactory`. An in-process test server would not exercise static assets, the Blazor
circuit over a real WebSocket, or the background worker on its own timer — which are exactly the things
a UI test is for. Per-test isolation costs a few seconds of start-up each and buys a suite whose
failures do not depend on execution order; these tests mutate workflow state, so sharing one database
would mean an approval in one test changing what another test finds.

On failure each test writes a screenshot and the rendered HTML to `artifacts/e2e/`, because a UI
failure reported as nothing but a selector timeout is close to useless.

They earned their place immediately. Running them for the first time found three defects that every
other form of testing here had missed:

| Defect | Why nothing else caught it |
| --- | --- |
| Interactive renders had no tenant and no roles, so every page blanked out once its Blazor circuit connected | Request middleware set them; a circuit has its own DI scope it never enters. Page-level HTTP checks saw only the correct pre-render. |
| Every monetary amount rendered with a literal `row.Currency` instead of its currency | Razor treats an unprefixed attribute value on a `string` parameter as literal text. It compiles, and the API — which the other tests drive — was always correct. |
| Buttons pre-rendered before the circuit connected looked enabled and silently swallowed clicks | Only a real browser clicking a real button at real timing can see it. |

The second and third are the kind that only a browser finds: a correct system displaying incorrectly.

## Remaining gaps

- No load or soak testing, and no mutation testing.
- Multi-tenancy is tested for isolation, but the system has only ever run with one tenant.
- Browser coverage stops at the review and approval flows; other screens are checked for rendering only.
