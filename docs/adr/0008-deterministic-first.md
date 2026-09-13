# ADR-0008 — Deterministic extraction and validation before AI

**Status:** accepted

## Decision

CSV and JSON invoices are parsed by hand-written extractors. Validation and tax determination are pure
functions with no AI involvement.

## Reasoning

Principle 4: if ordinary software can reliably solve something, do not use AI. A CSV invoice is fully
machine-readable, so sending it to a language model adds cost, latency and a hallucination risk in
exchange for nothing.

More importantly, it makes acceptance criterion 9 verifiable: because validation is deterministic, its
results are provably identical with AI on or off, and "AI can be disabled without breaking deterministic
processing" is something tests can assert rather than something the architecture merely claims.

The extractors are deliberately strict — unknown structure is a clean failure that routes to review,
never a partial guess. A half-extracted invoice is more dangerous than an unextracted one because it
looks finished.

## Given up

PDF and image extraction require AI; with AI disabled those documents are stored, hashed and classified
but not extracted. Deterministic PDF text extraction was considered out of scope for a POC.
