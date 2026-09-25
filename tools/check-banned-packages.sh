#!/usr/bin/env bash
# Fails if any banned NuGet package shows up in the .NET dependency graph.
#
# Banned per docs/adr/2026-09-25-testing-and-banned-dependencies.md: these
# packages moved to commercial/copyleft licenses and must never be
# reintroduced. Checked against Directory.Packages.props (the single source
# of truth for versions under central package management) and every
# packages.lock.json (the fully resolved dependency graph, so a banned
# package pulled in transitively is also caught).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

banned_packages=(
  "MediatR"
  "AutoMapper"
  "FluentAssertions"
  "Hangfire"
)

status=0

while IFS= read -r -d '' file; do
  for package in "${banned_packages[@]}"; do
    if grep -Fq "\"${package}\"" "$file"; then
      echo "error: banned package '${package}' found in ${file}" >&2
      status=1
    fi
  done
done < <(find "$repo_root/apps/api" \
  \( -name "Directory.Packages.props" -o -name "packages.lock.json" \) \
  -not -path "*/bin/*" -not -path "*/obj/*" \
  -print0)

if [ "$status" -eq 0 ]; then
  echo "check-banned-packages: no banned packages found."
fi

exit "$status"
