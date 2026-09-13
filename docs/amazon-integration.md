# Amazon integration

Selling Partner API. **Verification status:** `developer-docs.amazon.com` is blocked by the build
environment's egress policy, and no credentials were available. No live call has been made.

Specification §10 is explicit that Amazon must not be assumed to expose what Shopify does. This
connector is built around the differences rather than around the similarities.

## The three differences that shape the code

### 1. Prices are tax-inclusive

SP-API reports `ItemPrice` as the amount the buyer paid **including** `ItemTax`, whereas Shopify
reports a net line total with tax alongside. The canonical model stores net, so net is derived as
`ItemPrice − ItemTax − PromotionDiscount`.

**Getting this backwards would overstate revenue on every Amazon order, silently and systematically.**
It is therefore asserted by an explicit test rather than left implicit, and it is the single most
consequential unverified assumption in this connector.

As a cross-check, the computed total is compared against Amazon's own `OrderTotal`, and a mismatch
becomes a visible note on the invoice rather than being swallowed.

### 2. Buyer identity is frequently absent

Buyer name, email and full address are **restricted data**. Accessing them requires an approved PII
data-access role for the application *and* a Restricted Data Token from the Tokens API.

Without both, the connector produces a customer with `IsAnonymised = true`, identified only by
`amazon-order:{AmazonOrderId}` — the one stable buyer-side reference available. The validator raises a
`CUSTOMER_AMBIGUOUS` warning and the UI shows "identity not supplied by source".

**No buyer identity is ever invented.** The connector also does not request PII unless
`HasApprovedPiiDataAccess` is configured: requesting data you are not approved for produces denials
and, worse, encourages working around them.

The capability declaration reflects this — `BuyerPersonalData` is only claimed when actually approved.

### 3. There is no per-line tax rate

Amazon reports tax **amounts**, not rates. The rate is back-computed as `tax ÷ net` and the assessment
records that it was derived rather than stated.

The fixture deliberately produces 23.46%, which is not a real VAT rate anywhere. That is the point: the
system reports the derived number honestly and tells a human to verify it, rather than rounding to a
plausible-looking 19% and asserting it.

## Other consequences

- **Amazon issues no invoice number.** The `AmazonOrderId` is used as the document reference rather
  than a number being invented.
- **Only a shipping address is supplied.** It is used for billing too, and the invoice carries a note
  stating that assumption.
- Shipping is charged per item and becomes its own line only when non-zero.

## Authentication

The seller's long-lived refresh token is exchanged at the LWA token endpoint for a short-lived access
token, cached until just before expiry and sent in `x-amz-access-token`. A failure response is never
echoed into a log, since it can contain credential material.

## Capabilities, compared with Shopify

| | Shopify | Amazon |
|---|---|---|
| Invoices / orders | yes | yes |
| Customers | yes | yes, often anonymised |
| Buyer PII | yes | only with an approved role and an RDT |
| Per-line tax rate | yes | **no** — derived |
| Payments | yes | **no** — needs the Finances API |
| Refunds | yes | **no** — needs settlement reports |
| Incremental sync | yes | yes |

Declaring the gap is the point. The application degrades gracefully instead of catching
`NotSupportedException`.

## Documented restrictions

Exposed as data via `AmazonRestrictions.All`, so the UI and API can show them at the moment they
matter rather than burying them in a document nobody reads then. They cover PII access, the absence of
tax rates, tax-inclusive pricing, the missing invoice number, settlement detail requiring the Finances
or Reports API, the asynchronous nature of the Reports API, rate limiting, and the unverified shapes.

## Not implemented

The Reports API (asynchronous: request, poll, download — it would need its own worker), the Finances
API, FBA-specific flows, and multi-marketplace aggregation.
