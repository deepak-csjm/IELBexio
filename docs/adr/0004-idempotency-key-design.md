# ADR-0004 — Key idempotency on the source document, and reserve before calling

**Status:** accepted

## Decision

`tenant + source system + source document id + source document version + operation`, hashed. The key is
inserted into a uniquely indexed table **before** the Bexio call.

## Reasoning

**Why not our own invoice id.** A re-import that created a second canonical row would produce a second
key and a second posting. Keying on the source document's own identity makes the guarantee hold across
re-imports, which is precisely the case §31 step 20 requires.

**Why reserve first.** If the key were written after a successful call, a crash between Bexio
committing and us recording it would leave no trace — and the retry would create a second invoice.
Reserving first means the retry finds the key, reconciles, and does not duplicate. This is the whole
reason the ordering is what it is.

**Why a unique index rather than a check.** An application check has a race between reading and
writing. The index does not.

## Given up

A crash after reserving but before calling leaves a reserved key with no external id. The retry path
handles it: the attempt is not `Succeeded`, so it proceeds normally.
