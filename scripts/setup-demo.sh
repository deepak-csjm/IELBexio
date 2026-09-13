#!/usr/bin/env bash
#
# Prepares and starts everything the demonstration needs:
#   infrastructure -> database -> migrations -> seed -> application.
#
# Idempotent: safe to re-run. Pass --fresh to drop and recreate the demo database.
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${REPO_ROOT}"

DB_NAME="${IELBEXIO_DEMO_DB:-ielbexio_demo}"
DB_HOST="${IELBEXIO_DB_HOST:-127.0.0.1}"
DB_PORT="${IELBEXIO_DB_PORT:-5432}"
DB_USER="${IELBEXIO_DB_USER:-postgres}"
DB_PASSWORD="${IELBEXIO_DB_PASSWORD:-postgres}"
APP_URL="${BASE_URL:-http://127.0.0.1:5188}"
LOG_FILE="${IELBEXIO_LOG:-/tmp/ielbexio-demo.log}"

say() { printf '\n\033[1;34m==> %s\033[0m\n' "$1"; }
ok()  { printf '    \033[0;32m✓\033[0m %s\n' "$1"; }

FRESH=0
[ "${1:-}" = "--fresh" ] && FRESH=1

say "Starting local infrastructure"
"${REPO_ROOT}/scripts/dev-env.sh"

say "Preparing the database '${DB_NAME}'"
export PGPASSWORD="${DB_PASSWORD}"

if [ "${FRESH}" = "1" ]; then
  psql -h "${DB_HOST}" -p "${DB_PORT}" -U "${DB_USER}" -d postgres \
    -c "DROP DATABASE IF EXISTS \"${DB_NAME}\" WITH (FORCE);" >/dev/null
  ok "dropped the existing demo database"
fi

if ! psql -h "${DB_HOST}" -p "${DB_PORT}" -U "${DB_USER}" -d postgres -tAc \
     "SELECT 1 FROM pg_database WHERE datname='${DB_NAME}'" | grep -q 1; then
  psql -h "${DB_HOST}" -p "${DB_PORT}" -U "${DB_USER}" -d postgres -c "CREATE DATABASE \"${DB_NAME}\";" >/dev/null
  ok "created ${DB_NAME}"
else
  ok "${DB_NAME} already exists"
fi

CONNECTION="Host=${DB_HOST};Port=${DB_PORT};Database=${DB_NAME};Username=${DB_USER};Password=${DB_PASSWORD}"

say "Building"
dotnet build "${REPO_ROOT}" -v quiet --nologo
ok "build succeeded"

say "Starting the application"
# Migrations are applied by the host at startup, and the demo tenant is seeded then too.
pkill -f "dotnet.*IelBexio\.Web\.dll" 2>/dev/null || true
sleep 1

export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="${APP_URL}"
export IELBEXIO_ConnectionStrings__Postgres="${CONNECTION}"
export IELBEXIO_Shopify__FixtureDirectory="${REPO_ROOT}/fixtures/shopify"
export IELBEXIO_Amazon__FixtureDirectory="${REPO_ROOT}/fixtures/amazon"
export IELBEXIO_BlobStorage__LocalRootPath="${REPO_ROOT}/local-blobs"
export IELBEXIO_Logging__LogLevel__Default="${IELBEXIO_LOG_LEVEL:-Warning}"

(
  cd "${REPO_ROOT}/src/IelBexio.Web"
  setsid nohup dotnet run --no-launch-profile --no-build > "${LOG_FILE}" 2>&1 < /dev/null &
)

for _ in $(seq 1 90); do
  if [ "$(curl -sS --noproxy '*' -o /dev/null -w '%{http_code}' "${APP_URL}/health/ready" 2>/dev/null || echo 000)" = "200" ]; then
    ok "the application is ready at ${APP_URL}"
    printf '\n    UI:      %s\n    OpenAPI: %s/openapi/v1.json\n    Logs:    %s\n\n' "${APP_URL}" "${APP_URL}" "${LOG_FILE}"
    printf '    Next: \033[1mscripts/run-demo.sh\033[0m\n\n'
    exit 0
  fi
  sleep 1
done

printf '\n\033[0;31m✗ The application did not become ready. Last log lines:\033[0m\n'
tail -30 "${LOG_FILE}"
exit 1
