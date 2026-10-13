# Shared by deploy/backup.sh and deploy/restore.sh (#342); sourced, not run. Finds the deployment the
# same way the operator runs it (deploy/README.md: `docker compose -f deploy/docker-compose.yml
# --env-file deploy/.env ...`), builds the `compose` command array and offers small helpers.
#
# Settings (environment variables, all optional; relative paths are relative to the repository root):
#   DEPLOY_ENV_FILE        the deployment's env file (default deploy/.env)
#   DEPLOY_CERTS_DIR       the directory mounted at /app/certs (default deploy/certs)
#   DEPLOY_COMPOSE_FILES   compose files, separated by spaces (default deploy/docker-compose.yml); add
#                          your own extra file here if you start the deployment with two `-f`
#   DEPLOY_PROJECT         compose project name (default: the compose file's `name:`, smartagri)
# shellcheck shell=bash
# shellcheck disable=SC2034 # the variables are used by the scripts that source this file

# cron runs with a minimal PATH; docker lives in one of these on Ubuntu and macOS.
PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/opt/homebrew/bin:$PATH"
export PATH

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Absolute path of $1, taken relative to the repository root when it is relative.
from_repo() {
  case "$1" in
    /*) printf '%s\n' "$1" ;;
    *) printf '%s\n' "$repo_root/$1" ;;
  esac
}

env_file="$(from_repo "${DEPLOY_ENV_FILE:-deploy/.env}")"
certs_dir="$(from_repo "${DEPLOY_CERTS_DIR:-deploy/certs}")"
project="${DEPLOY_PROJECT:-}"

# The files (backup member names) every backup holds.
member_database='database.dump'
member_keys='dataprotection-keys.tar.gz'
member_certs='certs.tar.gz'
member_env='env'
member_manifest='manifest.txt'
member_sums='SHA256SUMS'

need_tools() {
  local tool
  for tool in "$@"; do
    command -v "$tool" > /dev/null 2>&1 || fail "'$tool' is not installed"
  done
  docker compose version > /dev/null 2>&1 || fail "'docker compose' (Compose v2 or later) is not available"
  docker info > /dev/null 2>&1 \
    || fail "cannot talk to Docker (is it running, and is this user in the docker group or root?)"
}

# compose=(docker compose [-p ...] -f ... --env-file ...): call after the env file exists.
build_compose() {
  local file
  compose=(docker compose)
  [ -n "$project" ] && compose+=(-p "$project")
  for file in ${DEPLOY_COMPOSE_FILES:-deploy/docker-compose.yml}; do
    file="$(from_repo "$file")"
    [ -f "$file" ] || fail "compose file not found: $file (DEPLOY_COMPOSE_FILES)"
    compose+=(-f "$file")
  done
  compose+=(--env-file "$env_file")
}

# Container id of a compose service, empty when there is none. `running` limits it to a running one.
service_container() { # service [running]
  if [ "${2:-}" = running ]; then
    "${compose[@]}" ps -q --status running "$1" 2> /dev/null || true
  else
    "${compose[@]}" ps -a -q "$1" 2> /dev/null || true
  fi
}

# Docker's name of the volume a container mounts at a path (the key ring: api, /app/keys).
mounted_volume() { # container destination
  docker inspect -f "{{range .Mounts}}{{if eq .Destination \"$2\"}}{{.Name}}{{end}}{{end}}" "$1"
}

sha256_of() {
  if command -v sha256sum > /dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

size_of() { wc -c < "$1" | tr -d ' '; }

human_size() { # bytes
  awk -v b="$1" 'BEGIN {
    split("B KB MB GB TB", u, " "); i = 1
    while (b >= 1024 && i < 5) { b /= 1024; i++ }
    printf (i == 1 ? "%d %s" : "%.1f %s"), b, u[i]
  }'
}
