# Bexio integration

## Read this first

**No live call has ever been made to Bexio from this codebase.**

The build environment's egress policy blocks `docs.bexio.com`, `developer.bexio.com`,
`api.bexio.com` and `auth.bexio.com`. No Bexio sandbox credentials were available either. Every
endpoint path, request/response field name and OAuth scope below is therefore **unverified against
primary documentation**, and is drawn from the project specification (which is authoritative for this
POC where it states a value) or from independent secondary sources such as community SDKs.

The specification forbids inventing endpoints and presenting them as fact (§3) and requires that
unverifiable details be isolated behind a mock and documented (§4). That is exactly what this design
does, and the mechanism is:

- Every path lives in [`BexioEndpoints`](../src/IelBexio.Connectors.Bexio/Configuration/BexioEndpoints.cs),
  one file, each entry carrying an explicit `VerificationStatus`.
- Every scope lives in `BexioScopes`, each with the reason it is requested.
- The DTOs are tolerant by design: unknown response fields are ignored, and identifiers are read as
  either string or number.
- A response that does not match the expected shape is classified `Permanent`, not retried, and
  surfaced to an operator with a pointer to this document — because retrying an unparseable response
  forever helps nobody.

If the real API differs, the correction is that one file plus the contract tests that pin it.

## Verification status of each endpoint

| Endpoint | Path | Status |
|---|---|---|
| Taxes | `GET /3.0/taxes` | **Stated in the project specification** (§6) |
| Company profile | `GET /2.0/company_profile` | Corroborated by secondary sources |
| Accounts | `GET /2.0/accounts` | Corroborated by secondary sources |
| Contacts | `GET /2.0/contact`, `POST /2.0/contact/search` | Corroborated by secondary sources |
| Articles | `GET /2.0/article`, `POST /2.0/article/search` | Corroborated by secondary sources |
| Invoice create | `POST /2.0/kb_invoice` | Corroborated by secondary sources |
| Invoice read | `GET /2.0/kb_invoice/{id}` | Corroborated by secondary sources |
| Currencies | `GET /3.0/currencies` | **Our assumption** — lowest confidence |

Bexio exposes several endpoint generations and they do not agree resource by resource (§5), so each
entry carries its own version prefix rather than sharing one base.

The currency endpoint is the weakest entry, and the code treats it accordingly: if it 404s or is
forbidden, currency validation degrades to a warning rather than failing an entire synchronisation.

## OAuth

Host and endpoints are taken verbatim from the specification §4:

- Issuer: `https://auth.bexio.com/realms/bexio`
- Authorization: `…/protocol/openid-connect/auth`
- Token: `…/protocol/openid-connect/token`
- API: `https://api.bexio.com`

Authorization-code flow with PKCE (S256) and opaque high-entropy state. PKCE is used even though this
is a confidential client: it closes the authorization-code interception window and costs nothing.

Personal Access Tokens are **not** used as the production mechanism, per §5.

### Scopes requested, and why

| Scope | Reason |
|---|---|
| `openid` | Required by the authorization-code flow. |
| `profile` | Identifies the connecting user so the connection is attributable in the audit log. |
| `offline_access` | Required for a refresh token. Without it the connection dies at first expiry and the unattended worker cannot operate. |
| `company_profile` | Shows which Bexio organisation is targeted, so a misdirected connection is visible *before* anything is posted. |
| `contact_edit` | Reads contacts for mapping, and creates one only when an operator explicitly asks. Secondary sources indicate a write scope implies its read scope. |
| `article_show` | Reads articles for product mapping. Read-only: this system never creates Bexio articles. |
| `kb_invoice_edit` | Creates the approved invoice. **The only write capability exercised**, reachable only from the worker after human approval. |
| `accounting` | Believed necessary to read the chart of accounts and tax table. **Unverified** — if the sandbox rejects it, remove it and account/tax discovery degrades to a pre-flight error rather than a silent wrong booking. |

No delete scope is requested. No scope permitting modification of existing accounting records is
requested.

### Token handling

Tokens are encrypted at rest with ASP.NET Core Data Protection under a dedicated purpose string. They
are held in memory only for the duration of a call, never returned to a caller outside the token
service, never projected into an API response, and never logged — not even truncated. Refresh is
serialised behind a semaphore so a burst of worker calls performs one refresh rather than several
racing ones. A rotated refresh token replaces the old one; an issuer that omits one leaves the
existing token in place. Disconnect clears the secret material rather than only flipping a status.

An undecryptable token (almost always a rotated key ring) is treated as "reconnect required" rather
than as a healthy connection or a crash.

## Mock and live

`IBexioClient` has two implementations, selected by `Bexio:Mode`. No domain or workflow code knows
which is active. `RequiresAuthorizedConnection` lets pre-flight ask the adapter whether an OAuth
connection is needed, instead of the workflow having to know about modes.

`MockBexioClient` is a genuine test double, not a stub. It holds reference data, assigns ids, enforces
the referential integrity a real API would (unknown contact, unknown tax, inactive tax, unconfigured
currency and empty position list are all rejected), honours the idempotency key, and can be driven
into every failure mode of §30.

Its identifiers are deliberately non-numeric — `mock-tax-std-81`, not `28` — so a mock value that
leaks into a mapping table is unmistakable rather than plausible.

## Reference data and tax mapping

`BexioReferenceSyncService` reads taxes, accounts, contacts and articles from whatever the connected
account actually reports, and derives tax mappings from that. **No Bexio identifier is ever seeded or
hardcoded** (acceptance criterion 17).

Taxes are matched on **rate**, not on name. Names are localised and user-edited — "MWST 8.1%", "VAT
8.1", "Umsatzsteuer normal" — while the rate is what determines the money. Where several active taxes
share a rate, the service maps one and raises a warning naming the ambiguity rather than choosing
silently. Where no Bexio tax exists at a required rate, it says so; invoices needing that rate fail
pre-flight until a human resolves it.

Account mapping is auto-created only when there is exactly one candidate. Guessing which revenue
account a business uses is not this system's decision.

## Pre-flight

Runs twice: once to build the preview an approver sees, and again inside the worker immediately before
posting. The second run is not redundant — configuration can change, a Bexio tax can be deactivated,
or the connection can be revoked between approval and dispatch.

Checks: no prior synchronisation; connection valid; required scopes granted; customer mapping exists;
per-line tax determined, human-verified where confidence is low, mapped, active, and at a rate that
matches our own determination; account mapping exists; currency configured; totals balance.

The rate cross-check deserves note: if our determination says 8.1% and the mapped Bexio tax is 2.6%,
posting would book a different tax amount than the one that was validated and approved. That is an
error, not a warning.

## Errors

Mapped onto the §20 taxonomy, which decides retryability:

| HTTP | Category | Retried |
|---|---|---|
| 401 | `Authentication` | yes (a refresh may fix it; capped by attempts) |
| 403 | `Authorization` | no |
| 404 | `NotFound` | no |
| 409 | `Conflict` | no |
| 422, 400 | `Validation` | no |
| 429 | `RateLimit` | yes, honouring `Retry-After` |
| 5xx, 408 | `Transient` | yes |
| network, timeout | `Network` | yes |
| unparseable response | `Permanent` | no |

## Verifying it, once a licence exists

The markers above are only useful if turning them into facts is easy — otherwise the verification never
happens and they become decoration. So it is one command:

```bash
# Read-only. Probes every endpoint, field name and scope, and reports which assumptions hold.
BASE_URL=http://localhost:5188 scripts/verify-bexio.sh

# Additionally verifies invoice creation. SANDBOX ONLY — it creates one real invoice,
# titled CONFORMANCE-CHECK-DELETE-ME, and requires typed confirmation.
scripts/verify-bexio.sh --allow-write
```

Point the application at a sandbox first (`Bexio:Mode=Api`, client id and secret configured, OAuth
connection completed), then run it. It writes a Markdown report and a JSON report to `artifacts/`.

The report is ordered worst-first and, for each refuted assumption, names the exact file to change. It
distinguishes three outcomes deliberately:

| Outcome | Meaning |
|---|---|
| **Refuted** | The assumption is wrong. This is the work list. A 404 means the path is wrong; a 422 means the payload shape is wrong; an unparseable response means the DTO is wrong. |
| **Inconclusive** | Could not be determined — usually a 403 (the path may still be right, the scope is missing) or an empty account. Not a failure. |
| **Confirmed** | Observed working. Promote the marker to `VerifiedAgainstOfficialDocs`. |

One probe deserves particular mention: if every tax parses with a rate of zero, that is reported as
**refuted** rather than as "this account has only zero-rated taxes". A silently-zeroed tax rate is the
kind of failure nothing else would catch, and it would book every invoice with no VAT.

Running it against the mock exercises the harness but verifies nothing about the real API — and the
report says so in bold rather than letting a green run be mistaken for evidence.

### Still to confirm by hand

The harness cannot check everything. These remain manual:

1. Rate-limit headers and 429 semantics.
2. Whether Bexio honours an idempotency header. The database-level guard is the real defence and does
   not depend on it, but knowing would allow a second layer.
3. Whether a write scope really implies its read scope.
