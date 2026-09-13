# Assumptions

Every assumption this POC rests on, why it was made, and what happens if it turns out to be wrong.

## Load-bearing

### A1 — Bexio endpoint paths and field names
**Assumed:** the paths and shapes in `BexioEndpoints` and the adapter DTOs.
**Why:** `docs.bexio.com`, `developer.bexio.com`, `api.bexio.com` and `auth.bexio.com` are all blocked
by the build environment's egress policy, and no sandbox credentials were available.
**If wrong:** live synchronisation fails. Mock mode, the entire workflow, validation, approval and
duplicate prevention are unaffected.
**Contained by:** one catalog file with per-entry `VerificationStatus`; tolerant DTOs; a shape mismatch
classified `Permanent` and surfaced rather than retried; WireMock contract tests that pin the
assumption so a correction is a visible diff.

### A2 — Bexio OAuth scope strings
**Assumed:** `openid profile offline_access company_profile contact_edit article_show kb_invoice_edit
accounting`, and that a write scope implies its read scope.
**Why:** same as A1; corroborated by several independent SDKs.
**If wrong:** the authorization request is rejected, or a call returns 403 — classified
`Authorization` and not retried.
**Contained by:** the scope list is configuration. `accounting` is the least certain; removing it
degrades account and tax discovery to a pre-flight error rather than a silent wrong booking.

### A3 — Shopify Admin GraphQL field names
**Assumed:** the `Order` shape used by the query and the fixtures.
**Why:** `shopify.dev` is blocked.
**If wrong:** live import fails with a GraphQL error naming the field. Fixture mode is unaffected.
**Contained by:** one query constant; the API version is a single configuration value.

### A4 — Amazon SP-API shapes and semantics
**Assumed:** the operation paths, and specifically that `ItemPrice` is **tax-inclusive**.
**Why:** `developer-docs.amazon.com` is blocked.
**If wrong:** if `ItemPrice` were actually net, every Amazon invoice would understate tax and overstate
nothing — a silent, systematic error. This is the single most consequential unverified assumption in
the connector, which is why it is asserted by an explicit test rather than left implicit.
**Contained by:** the computed total is compared against Amazon's own `OrderTotal`, and a mismatch
becomes a visible note on the invoice.

### A5 — Swiss VAT rates in the demo rule table
**Assumed:** 8.1% standard, 2.6% reduced, 3.8% accommodation from 2024-01-01; 7.7% standard for
2018-2023.
**Why:** published headline rates, used so demo data is coherent.
**If wrong:** a demo invoice classifies as undetermined and routes to a human — which is the correct
behaviour anyway.
**Contained by:** rates are versioned data, not code. **These are configuration values, not tax
advice.**

## Structural

### A6 — Place of supply is the billing country
**Why:** determining it properly requires knowing the nature of the supply, both parties' registration
status and the delivery mechanism — a tax engine, explicitly out of scope (§39).
**Contained by:** every assessment's rationale states the assumption, so a reviewer knows what they
are checking.

### A7 — The header subtotal excludes shipping
**Why:** forced by the §21 identity `subtotal + tax + shipping − discount = total`; including shipping
in the subtotal double-counts it.
**Note:** a test originally encoded the opposite convention and balanced by luck while double-counting
20 CHF of net revenue. The separate product/shipping reconciliation now catches exactly that.

### A8 — A new source document version is a new canonical invoice
**Why:** silently mutating an already-approved invoice because the source edited the order would be
far worse than creating a second record with its own history.
**Consequence:** an edited Shopify order produces a second invoice. The UI flags it as a duplicate for
a human.

### A9 — One tenant operationally, multi-tenant in the model
**Why:** §23. Nothing in the domain assumes a single company; the tenant is resolved per request.

### A10 — Single approver
**Why:** §16 permits it for the POC. `prepared_by` and `approved_by` are separate columns and the
dual-control check is implemented behind a flag, so enabling it is a configuration change.

## Environmental

### A11 — Migrations at application startup
**Acceptable for:** a POC and single-instance deployment.
**Not acceptable for:** multi-instance production, where concurrent instances race. Flagged in
`deployment.md` as something to move into a release step.

### A12 — Integration tests need a real PostgreSQL
**Why:** they verify unique constraints, `numeric` arithmetic, transactions and
`FOR UPDATE SKIP LOCKED` — none of which an in-memory provider honestly exercises.
**Contained by:** the fixture resolves a database from `IELBEXIO_TEST_POSTGRES`, then Testcontainers,
then a local server. Testcontainers is preferred but was unusable here because container registry
image blobs are blocked.

### A13 — FluentAssertions is pinned to 7.2.2
**Why:** version 8 moved to the Xceed commercial licence, which would impose a per-developer cost on
anyone building this. 7.2.2 is the last Apache-2.0 release. See ADR-0010.

### A14 — Estimated AI cost is an estimate
Computed from configured per-token rates. Provider billing is authoritative. The budget control is
therefore approximate, and deliberately errs towards refusing rather than overspending.
