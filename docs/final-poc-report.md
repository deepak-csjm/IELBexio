# Final POC report

Date: 2026-09-13 · Branch: `claude/bexio-invoice-ingestion-poc-6eot5u`

Every claim below was verified by running the thing, not by reading the code. Where something was not
verified, this report says so rather than implying otherwise.

---

## WHAT WORKS

Verified by execution against a real PostgreSQL 16 database, on .NET 10.0.401.

### The complete §31 scenario, end to end

All twenty steps, executed by `scripts/run-demo.sh` through the application's own HTTP API — the same
surface the UI uses:

import → raw payload retained → canonical normalisation → deterministic validation → tax determination
→ human mapping → pre-flight preview → verification → **explicit approval** → transactional outbox →
background worker → pre-flight re-check → invoice created in Bexio → `Synced` → audit trail → **repeat
import creating no duplicate**.

The §31 reference invoice normalises to exactly the specified figures: net CHF 1,000.00, VAT 8.1% =
CHF 81.00, total CHF 1,081.00.

### The two guarantees that matter

**Nothing reaches Bexio without explicit human approval.** There is no API endpoint and no UI action
that posts to Bexio. `Invoice.WorkflowState` has a private setter and only `Approved` leads to a
sync-eligible state. A test calls the synchronisation service *directly* on an unapproved invoice and
confirms it is refused and that nothing was created.

**The same invoice can never be posted twice.** Three independent layers: a deterministic idempotency
key derived from the source document (not from our own row ids, so it survives re-imports), reserved in
the database *before* the Bexio call; a unique index; and a pre-sync check. A test bypasses the
application layer entirely and confirms the database still refuses.

### Verified by execution

| | Result |
|---|---|
| `dotnet build -c Release` | **0 warnings, 0 errors** (warnings are errors solution-wide) |
| `dotnet test` | **227 passed, 0 failed** (192 unit + 35 integration) |
| Migrations against an empty database | **25 tables created** |
| Container build | **succeeds**; runs as non-root (uid 1654) |
| Full demo against the **containerised** app | **passes all 12 stages** |
| UI | **14/14 pages return 200** and render real data |
| OpenAPI | **36 documented paths**, OpenAPI 3.1 |
| Secret scan | repository and **full git history clean**; scan tested against a planted token to confirm it catches one |
| Vulnerable dependencies | **none**, including transitive |

### Functionally complete

Canonical source-independent domain model · deterministic validation · deterministic tax determination
with versioned rules · dynamic Bexio tax mapping derived from discovered configuration · customer,
product, tax and account mapping · document ingestion with signature-based type validation ·
deterministic CSV and JSON extraction · the controlled AI gateway with every §13 control · the full
workflow state machine · role-gated explicit approval · transactional outbox with `FOR UPDATE SKIP
LOCKED`, leases and jittered backoff · error classification driving retry vs dead-letter ·
reconciliation · append-only audit · field-level provenance · multi-tenancy with reflection-applied
query filters · REST API · Blazor review UI with the source document beside the extracted data.

---

## WHAT IS MOCKED

| Component | Default | Real implementation |
|---|---|---|
| Bexio | `MockBexioClient` | `BexioApiClient` — full OAuth, contract-tested against WireMock |
| Shopify | Fixtures | `ShopifyGraphQlConnector` — **shares the normaliser with the fixture path** |
| Amazon | Fixtures | `AmazonSpApiConnector` — same shared-normaliser arrangement |
| AI | `DisabledAiService` | `AiGateway` + Azure OpenAI, tested against a recording fake |
| Blob storage | Local filesystem | `AzureBlobStore` |
| Identity | Development identity | Entra claim reading implemented, **not wired up** |

The fixture and live connectors share one normaliser, so the demo and the tests exercise the *real*
mapping logic — only the transport differs. A fixture connector with its own mapping would prove
nothing.

The Bexio mock is a genuine test double: it enforces referential integrity (unknown contact, unknown
tax, inactive tax, unconfigured currency and empty positions are all rejected), honours the idempotency
key, and can be driven into every failure mode of §30. Its identifiers are deliberately non-numeric
(`mock-tax-std-81`) so a mock value leaking into a mapping table is unmistakable.

---

## WHAT REQUIRES CREDENTIALS

None to run, test or demonstrate the POC. All of these are needed only for live mode:

| Credential | Enables |
|---|---|
| Bexio client id + secret | Live Bexio OAuth and synchronisation |
| Shopify shop domain + Admin API token | Live Shopify import |
| Amazon LWA client id/secret + refresh token | Live Amazon import |
| Amazon approved PII data-access role | Buyer identity on Amazon orders |
| Azure OpenAI endpoint + deployment | AI features |
| Azure Storage connection | Azure blob storage |
| Entra ID tenant + client id | **Real authentication** |

---

## WHAT WAS VERIFIED AGAINST LIVE BEXIO SANDBOX

**Nothing. No call has ever been made to Bexio from this codebase.**

This is the most important limitation in the report, so it is stated without qualification.

The build environment's egress policy blocks `docs.bexio.com`, `developer.bexio.com`, `api.bexio.com`
and `auth.bexio.com` — all return no connection. No sandbox credentials were available either.

Consequently, **acceptance criterion 1 ("Bexio sandbox can be connected using the current OAuth flow")
cannot be closed.** The flow is implemented and tested against a WireMock.Net simulation covering code
exchange, transparent refresh, token rotation, an issuer omitting a refresh token, revocation, an
undecryptable token, an unreachable issuer and disconnect. That proves *our* side implements OAuth
correctly. It does not prove Bexio behaves as simulated.

The same applies to `shopify.dev`, `developer-docs.amazon.com`, and Azure OpenAI.

---

## WHAT IS UNCERTAIN

Each is isolated so that correcting it is a contained change, and each is marked in code.

| # | Uncertainty | Containment |
|---|---|---|
| U1 | Bexio endpoint paths and version prefixes | One `BexioEndpoints` catalog, per-entry `VerificationStatus` |
| U2 | Bexio request/response field names | Tolerant DTOs; a shape mismatch is `Permanent`, surfaced not retried; WireMock tests pin the assumption |
| U3 | Bexio OAuth scope strings — especially `accounting` | Configuration; a rejection is a classified `Authorization` error |
| U4 | Bexio rate-limit headers and 429 semantics | Honours `Retry-After` when present, else jittered backoff |
| U5 | Whether Bexio honours an idempotency header | Sent, but the database guard is the real defence and does not depend on it |
| U6 | Shopify `Order` field names | One query constant; API version is a single config value |
| U7 | **Amazon `ItemPrice` is tax-inclusive** | Asserted by an explicit test; computed total cross-checked against Amazon's `OrderTotal` |
| U8 | Swiss VAT rates in the demo table | Versioned data, not code; an unmatched rate routes to a human |

**U7 deserves emphasis.** If `ItemPrice` were actually net rather than tax-inclusive, every Amazon
invoice would understate tax silently and systematically. It is the single most consequential
unverified assumption in the codebase, which is why it is asserted explicitly rather than left implicit.

---

## KNOWN LIMITATIONS

Full list in [`known-limitations.md`](known-limitations.md). The ones that would matter most in an
evaluation:

- **No Playwright UI tests.** §2 lists them. The UI was verified by running it and driving the identical
  workflow through the API it calls — which covers the logic but not the rendering. **The clearest
  testing gap.**
- **PDF and image extraction requires AI.** With AI disabled a PDF is stored, hashed and classified but
  not extracted. CSV and JSON are extracted deterministically.
- **No webhook endpoints.** Incremental sync is poll-based.
- **No source-region highlighting** on documents (§15 asks "where practical").
- **No bulk approval** — deliberate for money handling, but would not scale.
- **Credit notes** are modelled canonically but not posted as Bexio credit-note documents.
- **No email ingestion.**
- Not a tax engine, by design (§39): no OSS/IOSS, reverse charge, VAT registry validation, partial
  exemption or margin schemes.

---

## SECURITY GAPS

Full analysis in [`security.md`](security.md) and [`threat-model.md`](threat-model.md).

| Gap | Severity | Note |
|---|---|---|
| **No real authentication** | **High** | The development identity is not authentication. The app refuses to start in that mode outside Development, but it must be replaced before any use. |
| No malware scanning | Medium | Architectural hook only (`MalwareScanState`, `RequireMalwareScan`). |
| No API rate limiting | Medium | Not implemented. |
| Audit immutability not database-enforced | Medium | Append-only by application convention; production should `REVOKE UPDATE, DELETE`. |
| Compromised Approver account | Medium | Dual control is implemented behind a flag; enable it. |
| Data Protection keys beside the database locally | Low in Azure | In Azure the key ring lives in Blob Storage encrypted with Key Vault — separate trust boundaries. Locally it is on disk. |
| No SBOM or dependency signing | Low | CI does fail on known vulnerabilities. |
| No penetration testing | — | Not performed. |

Implemented and verified: role-gated approval enforced in services (not just hidden buttons), tokens
encrypted at rest and never logged/returned/rendered, approval-version fingerprinting defeating
approve-then-edit-then-post, tenant isolation, signature-based upload validation rejecting executables
and archives, path-traversal defence, no permanent public document URLs, parameterised SQL throughout,
and structural prompt-injection defences including the fence-escape case.

---

## PRODUCTION GAPS

| # | Gap | Why it matters |
|---|---|---|
| 1 | Entra ID not wired up | No real authentication. |
| 2 | Bexio catalog unverified | Live synchronisation is untested against the real API. |
| 3 | Migrations run at startup | Concurrent instances racing to migrate is a real failure mode. |
| 4 | Data Protection key ring not persisted | **Every restart would invalidate every stored Bexio token**, forcing all tenants to reconnect. The most commonly missed piece of an ASP.NET Core deployment. |
| 5 | No backup or restore procedure | PostgreSQL and blob storage must be restorable to a *consistent* point. |
| 6 | No dead-letter alerting | A dead letter is visible but nothing pages anyone. |
| 7 | No load testing | Performance under volume is unknown. |
| 8 | No blue/green or canary deployment | — |
| 9 | Polling outbox, no archival or partitioning | Appropriate for a POC; needs revisiting well before production volume. |

---

## ACCEPTANCE CRITERIA (§37)

| # | Criterion | Status |
|---|---|---|
| 1 | Bexio sandbox connectable via current OAuth | ⚠ **Implemented, not live-verified** — host unreachable, no credentials |
| 2 | Bexio configuration discoverable | ✅ Implemented; verified against the mock |
| 3 | Shopify data importable | ✅ Verified via fixtures through the shared normaliser |
| 4 | Amazon path demonstrable with fixtures | ✅ Verified |
| 5 | Structured **and** document ingestion | ✅ Both, verified end to end: an uploaded CSV reaches Bexio through the same approval path. PDF/image extraction needs AI |
| 6 | Source-independent canonical records | ✅ Verified |
| 7 | Deterministic validation works | ✅ Verified, extensively |
| 8 | AI isolated behind a controlled gateway | ✅ Verified |
| 9 | AI can be disabled without breaking processing | ✅ Verified — AI is **off by default** and the entire demo runs |
| 10 | Human review and correction work | ✅ Verified |
| 11 | Approval is explicit | ✅ Verified |
| 12 | Unapproved data cannot reach Bexio | ✅ Verified by direct attack |
| 13 | Bexio synchronisation is asynchronous | ✅ Verified |
| 14 | Duplicate posting prevented | ✅ Verified at three layers |
| 15 | All important changes auditable | ✅ Verified end to end under one correlation id |
| 16 | Provenance available | ✅ Verified — 18 provenance records on the reference invoice |
| 17 | Tax mapping dynamic, not hardcoded | ✅ Verified — derived from discovered Bexio taxes, matched on rate |
| 18 | Failures retryable where appropriate | ✅ Verified per error category |
| 19 | Negative scenarios tested | ✅ §32 covered |
| 20 | Complete happy path demonstrated | ✅ All 20 steps |
| 21 | No secrets committed | ✅ Verified across full history |
| 22 | Automated tests pass | ✅ 227/227 |
| 23 | Documentation accurately describes what is live, mocked, unavailable or uncertain | ✅ This report |

**22 of 23 met. Criterion 1 is blocked by environment, not by design** — the implementation exists and
is contract-tested; only live verification is missing.

---

## DEFECTS FOUND BY TESTING AND RUNNING

Worth listing, because each was found by executing rather than by reading, and each is now covered by a
regression test.

1. **Zero-rate tax given full confidence.** Zero tax is structurally ambiguous — zero-rating, exemption,
   export and reverse charge all present identically. Now capped below the review threshold.
2. **`HasUsableRefreshToken` read the wall clock**, disagreeing with the service's injected clock.
3. **Header/line reconciliation conflated product and shipping net.** The fix also revealed that an
   existing test's own data double-counted 20 CHF of shipping while balancing the total by luck.
4. **Tax assessments were read back from the database before being saved**, so the *first* validation
   pass raised no tax warnings at all and an invoice with an unclassifiable tax was routed to
   `Validated`. Pre-flight still blocked it, but nobody was told to look.
5. **Pre-flight demanded an OAuth connection in Mock mode**, where none exists. `IBexioClient` now
   declares `RequiresAuthorizedConnection`.
6. **Sync checked state eligibility before checking whether the document was already synchronised**, so
   a replayed message for finished work looked like a failure and would have dead-lettered.
7. **The invoice API returned EF entities with a navigation cycle.** Serialisation failed *mid-stream*,
   so clients saw a silently truncated body rather than an error.
8. **The outbox worker wrote audit events with an empty tenant id**, making the two records that say
   money was posted invisible to the tenant that owned them.
9. **`.editorconfig` was missing from the Docker build context**, so the container build failed where
   the host build succeeded.

---

## RECOMMENDED NEXT STEPS

**Before anything else**

1. Wire up Entra ID authentication. Nothing else should be considered until this is done.
2. Obtain a Bexio sandbox, verify every entry in `BexioEndpoints`, and run the contract tests against
   it. Confirm the `accounting` scope.
3. Persist the Data Protection key ring to Blob Storage encrypted with Key Vault.
4. Move migrations out of application startup into a release step.

**Then**

5. Verify the Amazon tax-inclusive pricing assumption (U7) against a real order.
6. Wire up malware scanning behind the existing hook.
7. Add Playwright tests for the review and approval flows.
8. Add dead-letter alerting and a reconciliation schedule.
9. `REVOKE UPDATE, DELETE` on the audit tables.
10. Enable dual control (`prepared_by != approved_by`).

**Then, in priority order**

11. Deterministic PDF text extraction, so AI is needed only for genuinely unstructured documents.
12. Shopify webhooks for near-real-time ingestion.
13. Bexio credit-note posting for refunds.
14. Load testing, then revisit the polling outbox and the lack of archival.
15. API rate limiting.
16. An SBOM and dependency signing.

---

## Honest summary

This is a working, tested POC of a credible architecture, not a demo that looks like one. The workflow,
the financial guarantees and the audit trail are real and verified against a real database. The
deterministic core runs with AI switched off, which is the point.

What it is not is production-ready, and the two reasons are specific rather than general: **there is no
real authentication**, and **nothing has ever been verified against a live Bexio API**. Both are
tractable — the code for each exists and is tested against simulations — but neither should be
described as done, and this report does not describe them that way.
