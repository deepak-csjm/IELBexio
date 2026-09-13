# Data provenance

Every important canonical value can be traced to where it came from (§22). This is not a nice-to-have
in a financial system: when someone asks why a number is what it is, "the integration produced it" is
not an answer.

## What is recorded

`FieldProvenance` rows carry the entity and field path, the origin, the source system and document id,
**the exact source field path**, any transformation applied, the extraction method, the AI proposal id
and model where applicable, who modified it, when, the value at that point, and the correlation id.

## Origins

| Origin | Meaning |
|---|---|
| `SourceApi` | Copied or deterministically derived from a structured source payload |
| `Deterministic` | Computed by code (arithmetic, rule table, normalisation) |
| `DocumentParser` | Extracted by a deterministic parser |
| `AiProposalAccepted` | Originated as an AI proposal that **a human accepted** |
| `Human` | Entered or corrected by a person |
| `Seed` | Demo or default value |

`AiProposalAccepted` is deliberately distinct from a hypothetical "AI" origin. AI never writes a
canonical value; a human accepts a proposal, and the record says so, naming both the human and the
model that suggested it. Accountability stays with the person.

## Three layers

1. **The raw payload**, retained verbatim with a SHA-256, so normalisation can be re-run and diffed
   without re-contacting the source.
2. **Field provenance**, mapping each canonical field to its source field.
3. **The audit trail**, recording every change with actor, reason, correlation id and whether AI was
   involved.

## What it looks like

The reference invoice's total traces as:

```
Invoice.totalAmount = 1081.00 CHF
  ↳ origin       : SourceApi
  ↳ source       : Shopify, document gid://shopify/Order/10001
  ↳ source field : currentTotalPriceSet.shopMoney.amount
  ↳ transformation: Normalised by the Shopify connector
  ↳ correlation  : 01a099de6e8b7d8bb809b302e726bde5
```

The UI shows this beneath each field on the invoice screen, next to the verbatim source document.

## Correlation

One correlation id follows an invoice from import through validation, review, approval and the
background worker to synchronisation, and is stored on the invoice and on every audit event,
provenance record and synchronisation attempt. `/api/audit?correlationId=…` returns the whole
lifecycle in one query — including the worker-side events, which are written under the invoice's
tenant precisely so they are visible there.
