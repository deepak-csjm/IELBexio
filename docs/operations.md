# Operations

## Health

| Endpoint | Meaning |
|---|---|
| `/health/live` | The process is up. Use for liveness — restarting on this is safe. |
| `/health/ready` | The process can reach its database. Use for readiness and load-balancer membership. |

They are separate on purpose: a container that is up but cannot reach PostgreSQL should stop receiving
traffic without being killed and restarted in a loop.

## Tracing one invoice

Every invoice carries a correlation id from import to synchronisation, across the HTTP request and the
background worker.

```
GET /api/audit?correlationId={id}
```

returns the whole lifecycle — import, validation, corrections, verification, approval, queueing and the
worker-side sync events. The Audit Log screen has the same filter.

## The outbox

The Synchronization screen shows the queue, attempt counts, next attempt time, last error and its
category, plus every synchronisation attempt with its idempotency key.

**A dead-lettered message needs a human.** It means either the error class says retrying cannot help
(a validation rejection, an authorisation failure) or the attempt limit was reached. Retrying is
available to Admin and IntegrationManager from the UI or:

```
POST /api/synchronizations/outbox/{id}/retry
```

A manual retry re-queues an already-approved invoice; the approval, its fingerprint and the full
pre-flight are re-checked before anything is sent. It cannot be used to post something never approved.

**There is no dead-letter alerting.** A dead letter is visible but nothing pages anyone. Listed in
`known-limitations.md`.

## Retry behaviour

| Category | Retried | Notes |
|---|---|---|
| `RateLimit` | yes | honours `Retry-After` |
| `Transient`, `Network` | yes | exponential backoff with full jitter |
| `Authentication` | yes | a refresh may fix it; capped by attempt count |
| `Validation`, `Authorization`, `NotFound`, `Conflict`, `Permanent` | **no** | dead-lettered for a human |

Full jitter matters: when a rate limit or outage trips many messages at once, an unjittered backoff has
them all retry at the same instant and trip it again.

## Reconciliation

```
POST /api/synchronizations/reconcile
```

Compares what this system believes it posted against what Bexio holds. A missing or mismatched invoice
moves to `ReconciliationRequired` and raises an audit event.

**Nothing is repaired automatically.** Choosing which side is right is a judgement, not a rule. An
unreachable Bexio is reported as a failed *check*, not as a discrepancy — otherwise an outage would
flood the queue with false positives.

Worth running on a schedule; there is no built-in scheduler.

## AI budget

The AI Usage screen shows month-to-date spend, call counts, refusals and cache hits. When the budget is
exhausted the gateway refuses and routes records to human review — it does not degrade quality or
silently overspend. Costs are estimates from configured per-token rates; provider billing is
authoritative.

## When Bexio is down

Approvals continue. Work accumulates in the outbox and drains when Bexio returns. Nothing is lost, and
nothing is posted twice on recovery — the idempotency key is reserved before the call.

## When the worker restarts mid-flight

A claimed message stays leased; the lease expires and another worker reclaims it. If the crash happened
after Bexio committed but before we recorded it, the retry finds the reserved idempotency key,
reconciles the state and does **not** create a second invoice.

## Scaling the worker

`SELECT … FOR UPDATE SKIP LOCKED` means several instances can run against one outbox table without
coordination. Leases handle crashed workers. Nothing else needs changing to add an instance.

## Backup

Not implemented; this is a POC. PostgreSQL holds all workflow state, so any credible deployment needs
point-in-time recovery. Documents in blob storage need their own backup, and the two must be restored
to a consistent point — a document hash referenced by a row that no longer exists is a broken audit
trail.

## Logs

Structured, with the correlation id in scope. Tokens, secrets and passwords are never logged; Bexio
error bodies are truncated before logging because they can echo request content.
