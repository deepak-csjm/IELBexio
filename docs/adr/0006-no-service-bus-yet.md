# ADR-0006 — No Azure Service Bus in the POC

**Status:** accepted

## Decision

The outbox is a PostgreSQL table polled by an in-process worker. No message broker.

## Reasoning

The specification permits Service Bus "where asynchronous processing materially benefits reliability"
(§2). Here it would not, yet: the reliability property that matters is atomicity between the approval
and the intent to send, and only a database transaction provides that. A broker would sit *behind* the
outbox as a delivery mechanism, not replace it.

Adding one now would add an external dependency, a second failure mode, and local-development friction
in exchange for throughput the POC does not need.

`SELECT … FOR UPDATE SKIP LOCKED` already allows multiple workers, which covers the realistic scaling
need.

## Given up

Polling latency (seconds) and database load proportional to poll frequency. Both are acceptable at this
volume, and the dispatcher is behind `IOutboxStore`, so substituting a broker-backed implementation is
contained.
