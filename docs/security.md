# Security

Controls implemented, and — equally important — honest statements of what they do not cover.

## Identity and authorisation

Roles: `Admin`, `Reviewer`, `Approver`, `IntegrationManager`, `ReadOnly`. Checked in the services, not
only in the UI, so hiding a button is not the control. Tests attack this directly: a Reviewer without
the Approver role is refused approval, and a ReadOnly user is refused corrections.

**Authentication is the largest gap in this POC, and it is deliberate rather than overlooked.** The
specification requires Microsoft Entra ID. The application is written against `ICurrentUser` so that
swapping Entra in is a registration change, and the middleware already reads Entra claims when
`Authentication:Mode=EntraId`. But no Entra tenant was available here, so the POC ships a
**development identity** that reads a role from configuration or a request header.

That is not authentication. The middleware **refuses to start** in development-identity mode outside
the Development environment, so the unsafe path cannot be reached by accident in a deployed
environment — but it must be replaced before any real use.

## Token handling

- Encrypted at rest via ASP.NET Core Data Protection under a dedicated purpose string.
- Never returned by any API endpoint. The Bexio connection endpoint projects only non-secret fields.
- Never rendered in the UI. The Settings page shows "configured (value hidden)".
- Never logged, not even truncated. Token-endpoint failures log an error *category*, never the body,
  and never the form — which contains the client secret.
- Refresh is serialised, rotation is handled, disconnect clears the material rather than flipping a flag.

**What this does not protect against:** an attacker who has both the database and the Data Protection
key ring. In Azure the key ring is persisted to Blob Storage and encrypted with Key Vault, so those
are separate trust boundaries; locally the keys sit on disk. Stated plainly rather than implied.

## Approval integrity

- Only `ReadyForApproval` may be approved, enforced by the state machine.
- Approving requires the Approver or Admin role.
- Approval, its record and the outbox message are written in **one transaction**.
- `ApprovalVersion` fingerprints the financially significant content. The worker recomputes it before
  posting and refuses a stale approval, which defeats approve-then-edit-then-post. A test performs
  exactly that attack, tampering *consistently* so that arithmetic validation cannot catch it and only
  the fingerprint can.
- Corrections are refused once an invoice is approved.
- Dual control (`prepared_by != approved_by`) is implemented behind a flag.

## Duplicate financial transactions

Covered in detail in the README and `architecture.md`. Three independent layers: the deterministic
idempotency key, a unique database index, and a pre-sync check. A test bypasses the application layer
entirely and asserts the database still refuses.

## Prompt injection

See `ai-architecture.md`. Structural: constant system prompt, fenced untrusted content with delimiter
neutralisation, no tools, schema-validated output landing only in a proposals table.

## File upload

- **The client-declared content type is never trusted.** Type is determined from the file's own
  leading bytes, and a declared type contradicting the bytes is grounds for rejection.
- Executables (MZ, ELF), archives, OLE documents and shebang scripts are refused by signature.
- Content types are an allow-list, because the set of dangerous formats is open-ended while the set
  this system can process is small and known.
- Binary content cannot masquerade as CSV or JSON: those must be valid UTF-8 with no NUL bytes.
- Size is capped before the bytes are hashed or stored.
- Stored filenames are generated, never derived from the upload.
- The local blob store asserts every resolved path stays under its root, so `../../etc/passwd` cannot
  escape.
- Rejections are recorded with the file's hash, so repeated attempts are visible.

**Malware scanning is an architectural hook, not an implementation.** `MalwareScanState` exists and
`FileSecurityOptions.RequireMalwareScan` gates processing, but no scanner is wired in. Listed in
`known-limitations.md`.

## Cross-tenant access

Every business entity carries `TenantId`. Global query filters are applied by **reflection over every
`TenantEntity`**, so a newly added entity is isolated the moment it exists rather than when someone
remembers. Writes that would cross a tenant boundary throw `CrossTenantAccessException`. A test
confirms a second tenant sees nothing.

The outbox worker legitimately spans tenants but sets the tenant per message. An earlier version did
not, and audit events recording that money had been posted were written with an empty tenant and
became invisible to their owner. A regression test now asserts no audit event is ever orphaned.

## Document exposure

There is never a permanent public document URL. The local provider issues no URL at all; bytes are
streamed through an authorised endpoint. The Azure provider issues a short-lived SAS only on demand,
and containers are created with `PublicAccessType.None`.

## Injection and deserialisation

All database access is through EF Core with parameterised queries. The one hand-written SQL statement
(the outbox claim) uses `FromSqlInterpolated`, which parameterises rather than concatenates. JSON is
deserialised into known DTOs with `System.Text.Json`; no polymorphic or type-name-handling
deserialisation exists anywhere.

## SSRF

No user-supplied URL is ever fetched. Every outbound host comes from configuration.

## Secrets in source

`.env.example` contains placeholders only. CI fails on a committed `.env`, on token shapes this
project could leak, and on a `.env.example` value that is not obviously a placeholder. The scan was
tested against a planted token to confirm it actually catches one, and reports the repository and its
full history clean.

## Webhook spoofing

`ShopifyOptions.WebhookSecret` exists for HMAC verification. **No webhook endpoint is exposed in this
POC**, so there is nothing to spoof yet; the configuration is there for when one is added.

## Transport

HSTS and HTTPS redirection outside Development. Behind Azure Front Door or App Service, TLS
termination and certificate management are the platform's.

## Honest gaps

| Gap | Status |
|---|---|
| Entra ID authentication | Not wired up. Development identity only. **The largest gap.** |
| Malware scanning | Architectural hook only. |
| Rate limiting on the application's own API | Not implemented. |
| CSRF beyond Blazor's built-in antiforgery | Not separately hardened. |
| Audit log immutability at the database level | Append-only by application convention; no `REVOKE UPDATE/DELETE` grant. |
| Field-level encryption of PII at rest | Not implemented; relies on database-level encryption. |
| Penetration testing | Not performed. |
| Dependency signing / SBOM | Not produced. |
