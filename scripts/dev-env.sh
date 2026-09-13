#!/usr/bin/env bash
# Starts the local development dependencies this POC needs.
# Idempotent: safe to re-run. Used by scripts/setup-demo.sh and by CI's local fallback.
set -euo pipefail

echo "==> PostgreSQL"
if ! pg_isready -h 127.0.0.1 -p 5432 -q 2>/dev/null; then
  if command -v pg_ctlcluster >/dev/null 2>&1; then
    pg_ctlcluster 16 main start || true
  else
    service postgresql start || true
  fi
  for _ in $(seq 1 30); do
    pg_isready -h 127.0.0.1 -p 5432 -q 2>/dev/null && break
    sleep 1
  done
fi
pg_isready -h 127.0.0.1 -p 5432 && echo "    PostgreSQL is accepting connections."

echo "==> Docker (optional; only needed for container build)"
if ! docker info >/dev/null 2>&1; then
  (sudo dockerd >/tmp/dockerd.log 2>&1 &) || true
  sleep 4
fi
docker info >/dev/null 2>&1 && echo "    Docker daemon is up." || echo "    Docker daemon unavailable (container build will be skipped)."
