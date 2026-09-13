# Known limitations

What this POC does not do. Stated so nobody discovers it during an evaluation.

## Not verified against anything live

- **No Bexio call has ever been made.** Not once. The API, auth and documentation hosts are blocked by
  the build environment's egress policy and no sandbox credentials were available. The OAuth flow and
  API client are implemented and contract-tested against WireMock; they are **not** live-verified.
- **No Shopify store has been contacted.** `shopify.dev` is blocked.
- **No Amazon SP-API call has been made.** `developer-docs.amazon.com` is blocked.
- **No Azure OpenAI call has been made.** The gateway is tested against a recording fake.
- **No Azure resource has been used.** Blob Storage, Key Vault, Entra ID, Application Insights and
  Service Bus have local equivalents or are documented-only.

## Security

- **Entra ID authentication is not wired up.** The development identity is not authentication; the
  application refuses to start in that mode outside Development, but it must be replaced.
- **Malware scanning is an architectural hook, not an implementation.**
- No rate limiting on the application's own API.
- The audit log is append-only by application convention, not by a database grant.
- No penetration testing, dependency signing or SBOM.

## Functional

- **PDF and image extraction requires AI.** There is no deterministic PDF text extraction, so with AI
  disabled a PDF is stored, hashed and classified but not extracted. CSV and JSON are extracted
  deterministically.
- **No source-region highlighting.** §15 asks for it "where practical"; the UI shows the source payload
  beside the extracted data with per-field provenance, but does not highlight the originating region of
  a PDF.
- **No webhook endpoints.** Incremental sync is poll-based. The Shopify webhook secret is configured
  for HMAC verification but nothing consumes it yet.
- **No Amazon Reports API.** Settlement-level financial detail is out of scope; the Orders API alone
  cannot provide it.
- **Credit notes are modelled but not posted as Bexio credit notes.** A refund normalises to a canonical
  credit note with negative amounts; creating the corresponding Bexio document type is not implemented.
- **Partial payments are recorded but not reconciled against Bexio.**
- **No bulk approval.** Each invoice is approved individually, which is deliberate for a POC handling
  money but would not scale to thousands of invoices.
- **No email ingestion.** §11 lists email attachments; only direct upload (UI and `POST /api/documents`)
  is implemented.

## Tax

It is not a tax engine, by design (§39). No EU OSS/IOSS, no reverse-charge determination, no VAT
registry validation (only the Swiss UID check-digit **format**), no partial exemption, no margin
schemes, no withholding tax, no multi-jurisdiction apportionment.

## Operational

- Migrations run at startup — unsuitable for multi-instance deployment.
- No blue/green or canary deployment.
- No backup or restore procedure.
- No load testing. Performance characteristics under volume are unknown.
- The outbox dispatcher polls; it does not use `LISTEN`/`NOTIFY` or a broker.
- No dead-letter alerting. A dead-lettered message is visible in the UI but nothing pages anyone.

## Testing

- **No Playwright UI tests.** §2 lists Playwright for critical UI flows. The UI was verified by running
  it and driving the same workflow through the HTTP API the UI uses, which covers the logic but not the
  rendering. This is the clearest testing gap.
- No load or soak testing.
- No mutation testing.
- Multi-tenancy is tested for isolation, but the system has only ever run with one tenant.

## Scale

Every design choice assumes a POC's volume. A single outbox table polled by one worker, no
partitioning, no archival, and synchronous per-invoice processing in the UI are all appropriate here
and would need revisiting well before production volume.
