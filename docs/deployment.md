# Deployment

**Nothing here has been deployed.** No Azure resource was used in building this POC. This describes
the intended path and, more usefully, what must be done first.

## Before any deployment

| # | Requirement | Why |
|---|---|---|
| 1 | **Wire up Entra ID authentication** | The development identity is not authentication. The application refuses to start in that mode outside Development, so this is a hard gate, not advice. |
| 2 | **Verify the Bexio endpoint catalog** | Every entry is unverified. See `bexio-integration.md`. |
| 3 | **Move migrations out of startup** | Concurrent instances racing to migrate is a real failure mode. |
| 4 | **Wire up malware scanning** | Currently an architectural hook. |
| 5 | **Configure backup and restore** | Including consistency between PostgreSQL and blob storage. |
| 6 | **Add dead-letter alerting** | A dead letter is currently visible but silent. |

## Target shape

| Concern | Service |
|---|---|
| Application | Azure Container Apps or App Service (Linux container) |
| Database | Azure Database for PostgreSQL Flexible Server |
| Documents | Azure Blob Storage, private containers |
| Secrets | Azure Key Vault via managed identity |
| Identity | Microsoft Entra ID |
| Telemetry | Application Insights |
| AI | Azure OpenAI, managed identity preferred over a key |

## Configuration

All via environment variables prefixed `IELBEXIO_`, nested keys with a double underscore. Secrets come
from Key Vault through managed identity — never from application settings, and never from source.

```
IELBEXIO_ConnectionStrings__Postgres   # prefer Entra authentication over a password
IELBEXIO_Bexio__Mode=Api
IELBEXIO_Bexio__ClientId               # Key Vault reference
IELBEXIO_Bexio__ClientSecret           # Key Vault reference
IELBEXIO_Authentication__Mode=EntraId
IELBEXIO_BlobStorage__Provider=Azure
```

## Data Protection

Tokens are encrypted with ASP.NET Core Data Protection. In a multi-instance or restartable deployment
the key ring **must** be persisted to Blob Storage and encrypted with Key Vault — otherwise every
restart invalidates every stored Bexio token and every tenant must reconnect.

This is the single most commonly missed piece of an ASP.NET Core deployment, and the consequence here
is a silent loss of all integration credentials.

## Probes

```yaml
livenessProbe:  { httpGet: { path: /health/live,  port: 8080 }, periodSeconds: 30 }
readinessProbe: { httpGet: { path: /health/ready, port: 8080 }, periodSeconds: 10 }
```

The image has no `HEALTHCHECK` instruction: the aspnet runtime image ships neither curl nor wget, so
one would be a check that silently always fails. The platform probes over HTTP instead, which is how
Container Apps, App Service and Kubernetes work anyway.

## Scaling

The web tier is stateless and scales horizontally. The outbox worker runs in-process and is safe to run
on every instance — `FOR UPDATE SKIP LOCKED` plus leases mean instances do not collide. Splitting the
worker into its own deployment is a registration change (`AddIelBexioWorker`), not a redesign.

## Network

Restrict PostgreSQL to a private endpoint. Blob containers must stay private; the application streams
documents through an authorised endpoint or issues a short-lived SAS. Bexio, Shopify, Amazon and Azure
OpenAI are the only outbound destinations.

## CI/CD

CI builds, tests against a real PostgreSQL, runs security checks and builds and smoke-tests the
container. **There is deliberately no deploy job** (§35). Deployment should be a separate, reviewed,
manually approved workflow.
