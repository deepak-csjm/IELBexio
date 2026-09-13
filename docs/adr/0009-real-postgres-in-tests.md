# ADR-0009 — Integration tests use a real PostgreSQL, never an in-memory provider

**Status:** accepted

## Decision

Every integration test runs against a real PostgreSQL. The fixture resolves one from
`IELBEXIO_TEST_POSTGRES`, then Testcontainers, then a local server — and never falls back to in-memory.

## Reasoning

The guarantees under test are database guarantees: unique constraints, `numeric` arithmetic,
transaction boundaries, and `SELECT … FOR UPDATE SKIP LOCKED`. An in-memory provider enforces none of
them, so a test that passed against it would prove nothing about the mechanism that actually prevents a
duplicate posting.

The three-way fallback exists because the environment this POC was built in blocks container registry
image blobs, making Testcontainers unusable. Rather than let that become "integration tests are
skipped", the fixture finds a database another way. Testcontainers remains preferred and is what CI
uses.

## Given up

Tests need a database, so they are slower than pure unit tests and need one more thing installed. Unit
tests remain database-free, and the split is clear.
