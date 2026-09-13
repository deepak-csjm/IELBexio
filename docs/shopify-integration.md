# Shopify integration

Admin **GraphQL** API. The specification forbids building a new integration on the deprecated REST
Admin API (§4). GraphQL also lets one round trip fetch the order, its line items, tax lines, addresses
and transactions together — which matters because Shopify bills by query cost, not request count.

**Verification status:** `shopify.dev` is blocked by the build environment's egress policy, so the
query below could not be validated against the current schema and no live call has been made.

## The API version is in one place

`ShopifyOptions.ApiVersion`, interpolated at a single call site. §4 requires that it not be hardcoded
throughout the code; a quarterly bump is a configuration change.

## One normaliser, two transports

`FixtureShopifyConnector` and `ShopifyGraphQlConnector` share `ShopifyOrderNormalizer`. Only the
transport differs, so the demo and the tests exercise the real mapping logic. A fixture connector with
its own mapping would prove nothing.

## Decisions worth stating

**Shop money, not presentment money.** `shopMoney` is the merchant's own currency, which is what the
books are kept in. Presentment (buyer) currency is ignored rather than mixed in.

**Money is parsed strictly.** Shopify returns amounts as JSON *strings* precisely so clients do not
parse them as doubles. They are parsed as `decimal` with invariant culture. An amount that is present
but unparseable, or in a currency other than the order's, **throws** — a silently zeroed amount is the
most dangerous failure mode in an accounting integration.

**Tax lines are aggregated, not truncated.** A line may carry several tax lines because a jurisdiction
levies several taxes. They are summed and an effective rate derived from the actual amounts, with a
note on the invoice when more than one applied. Keeping only the first would understate the tax.

**Shipping becomes an explicit line.** Shopify carries it as a separate object; it is materialised as a
`LineKind.Shipping` line so it can be booked to its own Bexio revenue account and taxed correctly. The
validator reconciles shipping lines against the header shipping amount separately from product lines,
so it can be counted neither twice nor not at all.

**The document version is the order's `updatedAt`.** An unchanged re-fetch yields the same version and
is skipped; an edited order yields a new version and becomes a new canonical record rather than
silently mutating one that may already be approved.

**The buyer's VAT number comes from localization extensions**, not from the customer, and any
`TAX_CREDENTIAL_*` key is accepted rather than hardcoding one market.

**A fully refunded order becomes a credit note**, not an invoice with odd signs, so the negative-total
validation stays honest. Refund transactions are recorded with a negative amount.

## Incremental sync

Expressed as `updated_at:>=…` in Shopify's search syntax, with a cursor for pagination. §9 prefers
incremental synchronisation over repeated full downloads.

## Throttling

Shopify's GraphQL API uses a leaky-bucket **cost** budget. The connector reads the returned
`throttleStatus` and waits for the budget to refill when it runs low — backing off before being
throttled is cheaper than being throttled and retrying. It also handles the `THROTTLED` error code and
honours `Retry-After`.

A GraphQL endpoint returns HTTP 200 with an `errors` array for query-level failures, so the connector
checks the body rather than trusting the status code.

## Documented restrictions

Surfaced in the UI and the API, not just here:

- `read_orders` required. Shopify restricts apps to the last 60 days of orders unless it has granted
  `read_all_orders` for the app.
- `read_customers` required for buyer name and email; without it the customer object is null.
- Buyer tax credentials are only populated for markets where Shopify collects them.
- Cost-budget throttling rather than request-count rate limiting.
- Endpoint shapes are unverified.

## Not implemented

Webhooks (the HMAC secret is configured but no endpoint is exposed), product/variant sync as a
standalone capability, and multi-location inventory — none of which affect invoice ingestion.
