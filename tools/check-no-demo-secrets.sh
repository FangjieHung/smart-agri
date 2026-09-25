#!/usr/bin/env bash
# Fails if the Demo's old fixed password ("1234") shows up in the API's checked-in
# configuration, or if anything under deploy/ hard-codes a real value for
# SEED_DEMO_PASSWORD.
#
# Per docs/adr/2026-09-25-authentication.md and the M1 skeleton plan (Slice 6):
# the frontend Demo's three fixed accounts and their shared password "1234" must never
# reach the real backend, and SEED_DEMO_PASSWORD (DevelopmentSeeder's password for the
# seeded demo accounts) has no default and must never be committed with a real value —
# only ever set in a developer's shell or CI secret store.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
status=0

if grep -rn '1234' "$repo_root/apps/api/src" --include='appsettings*.json'; then
  echo "error: found '1234' in apps/api/src appsettings*.json (the Demo's fixed password must never appear in checked-in API configuration)" >&2
  status=1
fi

while IFS= read -r -d '' file; do
  while IFS= read -r line; do
    value="${line#*SEED_DEMO_PASSWORD=}"
    value="$(printf '%s' "$value" | tr -d '[:space:]')"
    if [ -n "$value" ]; then
      echo "error: ${file} sets a real value for SEED_DEMO_PASSWORD (it must stay empty in every checked-in file under deploy/)" >&2
      status=1
    fi
  done < <(grep -n '^[^#]*SEED_DEMO_PASSWORD=' "$file" 2>/dev/null || true)
done < <(find "$repo_root/deploy" -type f -print0)

if [ "$status" -eq 0 ]; then
  echo "check-no-demo-secrets: no banned demo secrets found."
fi

exit "$status"
