# Tax model

## What this is not

**This is not a tax engine.** Specification §39 rules that out explicitly, and the design takes it
seriously rather than treating it as a caveat. The system classifies rates it recognises, and routes
everything it does not to a human. It makes no determination about place of supply, export treatment,
reverse charge, registration thresholds, or which rate a given product legally attracts.

Anything labelled "confidence" here is a **workflow routing signal**, not a legal or tax opinion. The
UI says so where it is displayed. A high signal means "the data was unambiguous", never "this is
correct for tax purposes".

## Five separate things, deliberately kept apart

Specification §6 requires source tax data, internal representation, determination, Bexio mapping and
human verification to be distinguishable. `TaxAssessment` stores each independently:

| | |
|---|---|
| What the source said | `SourceTaxCode`, `SourceTaxName`, `SourceTaxRatePercent` |
| What we concluded | `InternalTaxCode`, `TaxType`, `RatePercent`, `DeterminationMethod`, `DeterminationVersion` |
| What we intend to send | `ProposedBexioTaxId` |
| What was actually sent | `AppliedBexioTaxId` |
| Who checked it | `HumanVerified`, `VerifiedBy`, `VerifiedAt` |

A reviewer can therefore see all of them at once, and the audit trail distinguishes "what we proposed"
from "what was booked".

## Determination

Deterministic, in this order of preference:

1. **The rate the source stated.**
2. **Back-computed from the amounts** when no rate was stated but tax and net are present.
3. **Undetermined** — a first-class outcome that routes to a human.

AI never appears in this chain. An AI tax suggestion is a separate `AiProposal` that a human must
accept, and accepting it records `HumanEntered`.

The rule table is data, not code, and is versioned (`DeterminationVersion`) so a conclusion reached
last year remains interpretable after the rates change. Country-specific rules take precedence over
wildcards, and every rule has a validity window, so a 2023 invoice is evaluated against the rates that
applied in 2023.

Default rules cover published Swiss rates: 8.1% standard, 2.6% reduced and 3.8% accommodation from
2024-01-01, and 7.7% standard for 2018-2023. **These are demo configuration values, not tax advice.**

## Why zero-rating is never high confidence

A zero rate looks identical in source data whether it is zero-rating, exemption, export, or reverse
charge — and those are not interchangeable for reporting. So even when the rule table matches a zero
rate exactly, the confidence signal is capped below the review threshold and the rationale says why.

This was originally a bug: the implementation gave a matched zero-rate rule full confidence, and a
test caught it. Refusing to be confident here is the point of the design, not a limitation of it.

## Place of supply

Assumed to be the billing country, and the rationale on every assessment **says so explicitly** so a
reviewer knows exactly which assumption they are checking. Determining place of supply properly
requires knowing the nature of the supply, both parties' registration status and the delivery
mechanism — which is a tax engine, and out of scope.

## Bexio mapping

Mappings are derived from the taxes the connected Bexio account actually reports, matched on **rate**
rather than name, because names are localised and user-edited while the rate determines the money. No
Bexio tax id is ever hardcoded or seeded.

Pre-flight refuses to post when: the tax is undetermined; a low-confidence conclusion has not been
human-verified; no mapping exists; the mapped tax is inactive in Bexio; or **the mapped tax's rate
disagrees with our determination** — because posting then would book a different amount than the one
validated and approved.

## Arithmetic

Every tax figure is recomputed and checked deterministically. `subtotal + tax + shipping − discount =
total`, and per line `net = quantity × unit price − discount`, `tax = net × rate ÷ 100`,
`gross = net + tax`. All in `decimal`, stored as PostgreSQL `numeric`, with a small configurable
tolerance for legitimate per-line rounding drift.

An LLM never performs authoritative monetary arithmetic (§21).

## Known gaps

Deliberate, and listed so nobody mistakes them for oversights: no EU OSS/IOSS handling, no reverse
charge determination, no validation of VAT registration against any registry (only the Swiss UID
check-digit **format**), no partial exemption, no margin schemes, no withholding tax, and no
multi-jurisdiction apportionment on a single line.
