# ADR-0002 — PostgreSQL is the source of truth; Bexio is a destination

**Status:** accepted

## Decision

Workflow state, canonical records, mappings, provenance, approvals and synchronisation state live in
PostgreSQL. Bexio holds accounting records only.

## Reasoning

Bexio can answer "what is booked". It cannot answer "who approved this, when, against which version,
what did the source originally say, and what did the system change along the way" — which is exactly
what a financial audit asks.

Treating the accounting system as the system of record would also make the workflow untestable without
it, and would tie every state transition to an external API's availability.

## Given up

Two systems now hold overlapping data and can diverge. That is why reconciliation exists, and why a
divergence flags for a human rather than being auto-corrected — choosing which side is right is a
judgement.
