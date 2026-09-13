# ADR-0001 — Modular monolith, not microservices

**Status:** accepted

## Decision

One deployable ASP.NET Core host with module boundaries enforced by project references.

## Reasoning

This system's hard problems are transactional consistency and auditability, not scale. The approval,
its record and the outbox message must be written atomically; splitting them across services would
replace a database transaction with a distributed one, or with an eventual-consistency window during
which an invoice is approved but may never be sent.

The specification also says so directly (§8: "do not introduce microservices merely for appearance").

Boundaries are still real: connectors and the AI layer depend only on `Application` ports, never on
`Infrastructure` or on each other. Extracting one into its own process later is a hosting change rather
than a rewrite.

## Given up

Independent scaling and deployment per module. Neither is needed at POC volume, and the outbox worker —
the component most likely to need separate scaling — is already isolated behind
`AddIelBexioWorker` and can be split out without touching domain code.
