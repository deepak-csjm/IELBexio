#!/usr/bin/env bash
#
# Verifies the Bexio adapter's unverified assumptions against a real Bexio account.
#
# This is the one command to run the moment a Bexio licence and sandbox are available. It probes every
# endpoint, field name and scope the adapter assumes, and writes a report saying which hold, which are
# wrong and exactly which file to change.
#
# Read-only by default. Pass --allow-write to additionally create ONE invoice to verify the write path
# — only ever against a sandbox, and the invoice is labelled so it is obvious it can be deleted.
#
# Usage:
#   scripts/verify-bexio.sh                     # read-only, against the configured account
#   scripts/verify-bexio.sh --allow-write       # also verify invoice creation (SANDBOX ONLY)
#   BASE_URL=http://host:port scripts/verify-bexio.sh
#
set -euo pipefail

BASE_URL="${BASE_URL:-http://127.0.0.1:5188}"
OUTPUT_DIR="${OUTPUT_DIR:-./artifacts}"
ALLOW_WRITE=false

for arg in "$@"; do
  case "$arg" in
    --allow-write) ALLOW_WRITE=true ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "Unknown option: $arg" >&2; exit 2 ;;
  esac
done

say() { printf '\n\033[1;34m==> %s\033[0m\n' "$1"; }

if [ "$ALLOW_WRITE" = "true" ]; then
  printf '\033[0;33m'
  cat <<'WARN'
WARNING: --allow-write creates a real invoice in the connected Bexio account.

Only use this against a SANDBOX. The invoice is titled CONFORMANCE-CHECK-DELETE-ME
and must be deleted afterwards.
WARN
  printf '\033[0m'
  read -r -p "Type 'sandbox' to continue: " confirmation
  [ "$confirmation" = "sandbox" ] || { echo "Aborted."; exit 1; }
fi

mkdir -p "$OUTPUT_DIR"
timestamp=$(date -u +%Y%m%dT%H%M%SZ)

say "Running the Bexio conformance check against ${BASE_URL}"

markdown="${OUTPUT_DIR}/bexio-conformance-${timestamp}.md"
json="${OUTPUT_DIR}/bexio-conformance-${timestamp}.json"

curl -sS --noproxy '*' -X POST \
  "${BASE_URL}/api/connections/bexio/conformance?allowWrite=${ALLOW_WRITE}&format=markdown" \
  -o "$markdown"

curl -sS --noproxy '*' -X POST \
  "${BASE_URL}/api/connections/bexio/conformance?allowWrite=${ALLOW_WRITE}" \
  -o "$json"

cat "$markdown"

refuted=$(python3 -c "import json,sys; print(json.load(open('$json'))['refuted'])" 2>/dev/null || echo "?")

printf '\n'
printf '    Markdown: %s\n' "$markdown"
printf '    JSON:     %s\n\n' "$json"

if [ "$refuted" = "0" ]; then
  printf '\033[0;32m==> No assumption was refuted. Promote the confirmed markers in BexioEndpoints.\033[0m\n\n'
  exit 0
fi

printf '\033[0;31m==> %s assumption(s) refuted. The work list is at the top of the report.\033[0m\n\n' "$refuted"
exit 1
