# Local development

## Requirements

- .NET 10 SDK
- PostgreSQL 16+ (or Docker)

Nothing else. No Bexio account, Shopify store, Amazon credentials, Azure subscription or AI key is
needed to run the complete workflow — the defaults are mock and fixture throughout.

## Fastest path

```bash
./scripts/setup-demo.sh --fresh   # infrastructure, database, migrations, seed, application
./scripts/run-demo.sh             # the complete §31 scenario end to end
```

Then open <http://localhost:5188>.

## With Docker

```bash
cp .env.example .env              # optional
docker compose up --build
```

`docker compose up -d db` alone gives you just PostgreSQL if you would rather run the app from the SDK.

## By hand

```bash
createdb ielbexio
export IELBEXIO_ConnectionStrings__Postgres="Host=localhost;Port=5432;Database=ielbexio;Username=postgres;Password=postgres"
dotnet run --project src/IelBexio.Web
```

Migrations are applied and the demo tenant seeded at startup.

## Configuration

Precedence: `appsettings.json` → `appsettings.{Environment}.json` → User Secrets → environment
variables prefixed `IELBEXIO_` → command line.

Nested keys use a double underscore: `IELBEXIO_Bexio__ClientId`.

**Secrets never go in `appsettings.json`.** Locally:

```bash
cd src/IelBexio.Web
dotnet user-secrets set "Bexio:ClientId" "…"
dotnet user-secrets set "Bexio:ClientSecret" "…"
```

## Switching modes

| Setting | Values | Default |
|---|---|---|
| `Bexio:Mode` | `Mock`, `Api` | `Mock` |
| `Shopify:Mode` | `Fixture`, `Live` | `Fixture` |
| `Amazon:Mode` | `Fixture`, `Live` | `Fixture` |
| `Ai:Enabled` | `true`, `false` | `false` |
| `BlobStorage:Provider` | `Local`, `Azure` | `Local` |

Switching changes no domain code. That is the point of the adapter boundary.

## Roles during development

The development identity reads a role from a header, so you can demonstrate that approval is genuinely
role-gated:

```bash
curl -H "X-Demo-Role: Reviewer" -X POST http://localhost:5188/api/invoices/{id}/approve …
# → 403 FORBIDDEN
```

`X-Demo-User` sets the acting user. **This is not authentication** and the application refuses to
start in this mode outside Development.

## Tests

```bash
dotnet test                            # everything
dotnet test tests/IelBexio.UnitTests   # no database needed
IELBEXIO_TEST_POSTGRES="Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=postgres" \
  dotnet test tests/IelBexio.IntegrationTests
```

## Migrations

```bash
dotnet tool install --global dotnet-ef --version 10.*
export IELBEXIO_DESIGN_CONNECTION="Host=localhost;Port=5432;Database=ielbexio;Username=postgres;Password=postgres"

dotnet ef migrations add <Name> \
  --project src/IelBexio.Infrastructure --startup-project src/IelBexio.Infrastructure \
  --output-dir Persistence/Migrations
```

`.editorconfig` excludes generated migrations from analyser enforcement — the solution treats warnings
as errors, and EF's scaffolded code does not satisfy every analyser.

## Fixtures

`fixtures/shopify` and `fixtures/amazon`. Each file carries a `_fixture` block describing what it
demonstrates and stating that the shape is unverified. The set covers the §29 matrix: the reference
invoice, mixed rates with discount and shipping, foreign currency, a deliberately invalid total,
missing VAT with an ambiguous customer, a refund, and Amazon orders with and without buyer PII.

All of it is synthetic. Swiss UID numbers are format-valid but belong to no real entity, and every
email domain is `.example`.

## Behind a TLS-inspecting proxy

If your organisation re-signs outbound TLS, `dotnet restore` inside the container build fails with
`NU1301 … UntrustedRoot`. Drop the root certificate into `build-ca/` and it is installed into the
build stage. The directory is empty by default.
