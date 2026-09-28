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
#
# The one allowed exception (M2 plan Slice 17, issue #51): the `e2e-api` job in
# .github/workflows/ci.yml seeds the demo accounts into a throwaway pgvector service
# container that lives only for that job, so it sets SEED_DEMO_PASSWORD to the fixed,
# public CI test value below instead of a secret. The check allows exactly that value, and
# only in ci.yml: any other value in any workflow file, or this value anywhere else under
# .github/workflows/, fails. The value is not a secret and must never be used for a real
# database; the deploy/ and appsettings checks above and below are unchanged, so it cannot
# reach a checked-in deploy file either.
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

# Workflow files: `SEED_DEMO_PASSWORD: <value>` (YAML env mapping) or `SEED_DEMO_PASSWORD=<value>`.
ci_test_password='Ci-E2e-Only-Pass-2026!'
ci_workflow="$repo_root/.github/workflows/ci.yml"
while IFS= read -r -d '' file; do
  while IFS= read -r line; do
    value="$(printf '%s' "$line" | sed -E 's/^[^#]*SEED_DEMO_PASSWORD[:=][[:space:]]*//; s/[[:space:]]+#.*$//')"
    value="$(printf '%s' "$value" | tr -d "[:space:]\"'")"
    case "$value" in
      '' | '${{'*) continue ;; # empty, or taken from a GitHub secret/variable
    esac
    if [ "$file" != "$ci_workflow" ] || [ "$value" != "$ci_test_password" ]; then
      echo "error: ${file} sets SEED_DEMO_PASSWORD to a value other than the e2e-api job's CI test value (only .github/workflows/ci.yml may set it, and only to that value)" >&2
      status=1
    fi
  done < <(grep -nE '^[^#]*SEED_DEMO_PASSWORD[:=]' "$file" 2>/dev/null | cut -d: -f2- || true)
done < <(find "$repo_root/.github/workflows" -type f \( -name '*.yml' -o -name '*.yaml' \) -print0 2>/dev/null)

if [ "$status" -eq 0 ]; then
  echo "check-no-demo-secrets: no banned demo secrets found."
fi

exit "$status"
