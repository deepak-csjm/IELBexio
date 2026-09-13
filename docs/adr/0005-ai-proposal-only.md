# ADR-0005 — AI proposes; it never writes a canonical value

**Status:** accepted

## Decision

AI output is written only to `ai_proposals`. Accepting a proposal goes through the same validated
correction path a human typing the value would use, and records the origin as a human decision.

## Reasoning

A financial system must be able to say who is accountable for every value. "The model produced it" is
not an acceptable answer to an auditor. Routing acceptance through the human correction path means
there is no privileged write for AI-originated values, the field allow-list applies equally, and the
provenance names both the person and the model.

The enforcement is structural rather than procedural: the AI assembly holds no reference to
`IBexioClient`, so there is no method it could call to post an invoice. That is not a rule someone must
remember — it is a missing reference.

## Given up

Straight-through processing. Every AI suggestion costs a human decision, which is the intended
trade-off (§40) and the reason the system can be trusted with money at all.
