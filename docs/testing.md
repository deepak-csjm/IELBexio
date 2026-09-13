# Testing

221 tests: 192 unit and 29 integration. Every integration test runs against a real PostgreSQL.

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

Six real defects were found by tests or by running the application, not by reading code:

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

Each has a regression test.

## Running

```bash
dotnet test                                       # everything
dotnet test tests/IelBexio.UnitTests              # no database needed
IELBEXIO_TEST_POSTGRES="Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=postgres" \
  dotnet test tests/IelBexio.IntegrationTests
./scripts/setup-demo.sh --fresh && ./scripts/run-demo.sh   # the demo, as a live check
```

## The gap

**No Playwright UI tests.** §2 lists Playwright for critical UI flows. The UI was verified by running
it — all 14 pages return 200 and render real data — and by driving the identical workflow through the
HTTP API the UI itself calls. That covers the logic thoroughly but not the rendering or the client-side
interaction. It is the clearest testing gap in this POC.
