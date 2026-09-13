# ADR-0003 — Transactional outbox, not direct dispatch

**Status:** accepted

## Decision

Approval writes the state change, the approval record and an outbox message in one database
transaction. A background worker performs the synchronisation.

## Reasoning

The alternative — approve, commit, then call Bexio — has a window in which the process can die between
the two. The invoice is then approved and will never be sent, with nothing recording the intent.
Calling Bexio *inside* the transaction is worse: a slow external API holds a database transaction open,
and a commit failure after a successful post creates a ledger entry the system does not know about.

Writing the intent atomically with the decision means the two can never disagree, and delivery becomes
a retry problem rather than a correctness problem.

## Given up

Latency: synchronisation is asynchronous, so the UI cannot report the Bexio result immediately. This is
also a requirement (§20), and the Synchronization screen gives full visibility.

Publishing to a broker instead of a table has the same atomicity problem, which is why Azure Service
Bus would sit *behind* the outbox, not in place of it.
