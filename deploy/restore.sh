#!/usr/bin/env bash
# Restores a backup written by deploy/backup.sh (#342; deploy/README.md, section 14) into the
# deployment: the database, the Data Protection key ring, the certificates and the env file.
#
#   deploy/restore.sh [--force] <smartagri-backup-....tar>
#
# What it does, in this order (it stops at the first problem; nothing is overwritten before step 5):
#  1. checks the backup file's checksums;
#  2. puts the env file and the certificates in place where they are missing (a new machine). Where
#     they exist but differ from the backup's it refuses, unless --force;
#  3. creates the containers if there are none yet (`docker compose ... up --no-start`, which builds
#     the image the first time) and starts postgres;
#  4. refuses when the database already holds data (any organization), unless --force;
#  5. with --force, replaces the env file and certificates that differ (keeping the current ones as
#     <name>.before-restore-<time>) and recreates the containers so they use them; stops the api;
#     restores the dump into a new temporary database (the current one is untouched if that fails);
#     adds the backup's key files to the key volume (no key file is ever deleted: the key ring only
#     grows); replaces the database with the restored one; sets the database user's password to the
#     one in the env file (a fresh database volume may have been created with another one);
#  6. starts the api (its entrypoint runs `migrate`, so an older backup is brought up to date) and
#     waits for /health/ready to answer 200.
#
# Settings (environment variables, all optional): RESTORE_TIMEOUT_SECONDS (how long to wait for the
# database and for /health/ready, default 180), RESTORE_HEALTH_URL (default
# http://127.0.0.1:<the api's published port>/health/ready), and DEPLOY_ENV_FILE, DEPLOY_CERTS_DIR,
# DEPLOY_COMPOSE_FILES, DEPLOY_PROJECT (deploy/backup-lib.sh). Nothing secret is ever printed.
# The last line is `restore: OK ...`; any failure prints `restore: FAIL: <why>` and exits non-zero.
set -euo pipefail
umask 077

step() { printf 'restore: %s\n' "$*"; }
fail() {
  printf 'restore: FAIL: %s\n' "$*" >&2
  exit 1
}
usage() { sed -n '2,/^set -euo/p' "$0" | sed -e '$d' -e 's/^# \{0,1\}//'; }

force=''
backup=''
while [ $# -gt 0 ]; do
  case "$1" in
    --force) force=1 ;;
    -h | --help)
      usage
      exit 0
      ;;
    -*) fail "unknown option '$1' (see --help)" ;;
    *)
      [ -z "$backup" ] || fail "give exactly one backup file (see --help)"
      backup="$1"
      ;;
  esac
  shift
done
[ -n "$backup" ] || fail "which backup? deploy/restore.sh [--force] <smartagri-backup-....tar>"
[ -f "$backup" ] || fail "the backup file does not exist: $backup"

# shellcheck source=deploy/backup-lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/backup-lib.sh"
timeout_seconds="${RESTORE_TIMEOUT_SECONDS:-180}"
case "$timeout_seconds" in
  '' | *[!0-9]*) fail "RESTORE_TIMEOUT_SECONDS must be a number of seconds, not '$timeout_seconds'" ;;
esac
need_tools docker tar gzip awk curl cmp

work="$(mktemp -d "${TMPDIR:-/tmp}/smartagri-restore.XXXXXX")"
api_stopped=''
db_swapped=''
temp_db=''
cleanup() {
  local status=$?
  trap - EXIT
  if [ "$status" -ne 0 ] && [ -n "$temp_db" ] && [ -z "$db_swapped" ]; then
    psql_in postgres "drop database if exists \"$temp_db\" with (force)" > /dev/null 2>&1 || true
  fi
  if [ "$status" -ne 0 ] && [ -n "$api_stopped" ]; then
    if [ -z "$db_swapped" ]; then
      printf 'restore: the database was not replaced; starting the api again\n' >&2
    else
      printf 'restore: the database WAS replaced but a later step failed; starting the api anyway\n' >&2
    fi
    "${compose[@]}" start api > /dev/null 2>&1 || printf 'restore: warning: could not start the api\n' >&2
  fi
  rm -rf "$work"
  exit "$status"
}
trap cleanup EXIT

# --- 1. The backup file ---------------------------------------------------------------------------
step "checking $backup"
tar -C "$work" -xf "$backup" || fail "$backup is not a readable tar file"
for member in "$member_database" "$member_keys" "$member_certs" "$member_env" "$member_sums"; do
  [ -f "$work/$member" ] || fail "$backup has no $member: is it a deploy/backup.sh backup?"
done
while read -r sum member; do
  [ -f "$work/$member" ] || fail "$backup lists $member in $member_sums but does not contain it"
  [ "$(sha256_of "$work/$member")" = "$sum" ] || fail "$member in $backup is damaged (checksum mismatch)"
done < "$work/$member_sums"
mkdir "$work/certs"
tar -C "$work/certs" -xzf "$work/$member_certs" || fail "the certificates in $backup cannot be unpacked"
key_files="$(tar -tzf "$work/$member_keys" | grep -c 'key-.*\.xml$' || true)"
[ "$key_files" -gt 0 ] || fail "$backup holds no Data Protection key files"
[ -f "$work/$member_manifest" ] && step "the backup was $(sed -n 2p "$work/$member_manifest")"

# --- 2. env file and certificates -----------------------------------------------------------------
stamp="$(date +%Y%m%d-%H%M%S)"
missing=() # "source|target|mode"
differ=()  # same, for files that exist and differ (replaced only with --force)
plan_file() { # source target mode
  if [ ! -e "$2" ]; then
    missing+=("$1|$2|$3")
  elif ! cmp -s "$1" "$2"; then
    differ+=("$1|$2|$3")
  fi
}
plan_file "$work/$member_env" "$env_file" 600
while IFS= read -r cert; do
  plan_file "$work/certs/$cert" "$certs_dir/$cert" 644
done < <(cd "$work/certs" && find . -type f | sed 's|^\./||' | sort)

if [ "${#differ[@]}" -gt 0 ]; then
  for entry in "${differ[@]}"; do
    entry="${entry#*|}"
    printf 'restore: differs from the backup: %s\n' "${entry%|*}" >&2
  done
  [ -n "$force" ] \
    || fail "the files above differ from the backup's; rerun with --force to replace them (the current ones are kept as <name>.before-restore-<time>)"
fi

install_file() { # source target mode
  mkdir -p "$(dirname "$2")"
  cp "$1" "$2.restoring"
  chmod "$3" "$2.restoring"
  mv "$2.restoring" "$2"
}
for entry in ${missing[@]+"${missing[@]}"}; do
  IFS='|' read -r source target mode <<< "$entry"
  install_file "$source" "$target" "$mode"
  step "put in place (it was missing): $target"
done
# The api runs as a non-root user and must be able to enter the certificate directory.
chmod 755 "$certs_dir"

build_compose

# Runs SQL in a database as the deployment's database user (the container's own POSTGRES_USER).
psql_in() { # database sql
  # shellcheck disable=SC2016 # expanded by the container's shell
  "${compose[@]}" exec -T postgres sh -c 'exec psql -X -q -v ON_ERROR_STOP=1 -tA -U "$POSTGRES_USER" -d "$0" -c "$1"' "$1" "$2"
}

wait_for_postgres() {
  local deadline container
  deadline=$(($(date +%s) + timeout_seconds))
  container="$(service_container postgres)"
  until [ "$(docker inspect -f '{{.State.Health.Status}}' "$container" 2> /dev/null)" = healthy ]; do
    [ "$(date +%s)" -lt "$deadline" ] || fail "postgres did not become healthy within ${timeout_seconds}s"
    sleep 2
  done
}

# --- 3. Containers and postgres -------------------------------------------------------------------
if [ -z "$(service_container postgres)" ] || [ -z "$(service_container api)" ]; then
  step "creating the containers (docker compose up --no-start; the first time this builds the image)"
  "${compose[@]}" up --no-start > "$work/up.log" 2>&1 || {
    tail -n 40 "$work/up.log" >&2
    fail "docker compose up --no-start failed"
  }
fi
"${compose[@]}" start postgres > "$work/start.log" 2>&1 || {
  tail -n 20 "$work/start.log" >&2
  fail "could not start postgres"
}
wait_for_postgres
# shellcheck disable=SC2016 # expanded by the container's shell
db_name="$("${compose[@]}" exec -T postgres sh -c 'printf %s "$POSTGRES_DB"')"
case "$db_name" in
  '' | *[!A-Za-z0-9_-]*) fail "unexpected POSTGRES_DB '$db_name' in the postgres container" ;;
esac

# --- 4. Refuse to overwrite data ------------------------------------------------------------------
organizations=0
if [ "$(psql_in postgres "select count(*) from pg_database where datname = '$db_name'")" = 1 ] \
  && [ "$(psql_in "$db_name" "select to_regclass('public.\"Organizations\"') is not null")" = t ]; then
  organizations="$(psql_in "$db_name" 'select count(*) from "Organizations"')" \
    || fail "could not count the organizations in $db_name"
fi
if [ "$organizations" != 0 ] && [ -z "$force" ]; then
  fail "the database $db_name already holds data ($organizations organizations); rerun with --force to replace ALL of it with the backup"
fi

# --- 5. Restore -----------------------------------------------------------------------------------
recreate=''
for entry in ${differ[@]+"${differ[@]}"}; do
  IFS='|' read -r source target mode <<< "$entry"
  cp -p "$target" "$target.before-restore-$stamp"
  install_file "$source" "$target" "$mode"
  step "replaced (the old one is kept as $(basename "$target").before-restore-$stamp): $target"
  recreate=1
done

if [ -n "$(service_container api running)" ]; then
  step "stopping the api"
  "${compose[@]}" stop api > /dev/null 2>&1 || fail "could not stop the api"
fi
api_stopped=1

if [ -n "$recreate" ]; then
  # A container keeps the environment it was created with: recreate postgres with the new env file.
  step "recreating postgres with the restored env file"
  "${compose[@]}" up -d --no-build postgres > "$work/up.log" 2>&1 || {
    tail -n 40 "$work/up.log" >&2
    fail "could not recreate postgres"
  }
  wait_for_postgres
fi

temp_db="${db_name}_restore_$$"
step "restoring the database into a temporary database"
if ! psql_in postgres "drop database if exists \"$temp_db\" with (force)" > /dev/null \
  || ! psql_in postgres "create database \"$temp_db\"" > /dev/null; then
  fail "could not create the temporary database $temp_db"
fi
# shellcheck disable=SC2016 # expanded by the container's shell
"${compose[@]}" exec -T postgres sh -c \
  'exec pg_restore -U "$POSTGRES_USER" -d "$0" --no-owner --no-privileges --exit-on-error --single-transaction' \
  "$temp_db" < "$work/$member_database" > "$work/pg_restore.log" 2>&1 || {
  tail -n 20 "$work/pg_restore.log" >&2
  fail "pg_restore failed; the current database was not changed"
}

api="$(service_container api)"
keys_volume="$(mounted_volume "$api" /app/keys)"
[ -n "$keys_volume" ] || fail "the api container mounts no volume at /app/keys (the Data Protection key ring)"
helper_image="$(docker inspect -f '{{.Config.Image}}' "$(service_container postgres)")"
step "adding the backup's $key_files key files to the key volume $keys_volume"
# As root, tar keeps the files' owner (the api's uid 1654) and modes; existing key files are kept.
docker run --rm -i --network none -v "$keys_volume:/keys" --entrypoint tar "$helper_image" \
  -C /keys --numeric-owner -xzf - < "$work/$member_keys" > "$work/keys.log" 2>&1 || {
  tail -n 20 "$work/keys.log" >&2
  fail "could not write the key files into $keys_volume; the current database was not changed"
}

step "replacing the database $db_name with the restored one"
psql_in postgres "drop database if exists \"$db_name\" with (force)" > /dev/null || fail "could not drop the old database"
db_swapped=1
psql_in postgres "alter database \"$temp_db\" rename to \"$db_name\"" > /dev/null \
  || fail "could not rename the restored database $temp_db to $db_name (do it by hand with psql)"
temp_db=''
restored_organizations="$(psql_in "$db_name" 'select count(*) from "Organizations"')" || restored_organizations='?'

# The role lives outside the dump: make its password the env file's (\getenv: never on a command line).
# shellcheck disable=SC2016 # expanded by the container's shell
"${compose[@]}" exec -T postgres sh -c \
  'printf "%s\n" "\\getenv pw POSTGRES_PASSWORD" "alter role \"$POSTGRES_USER\" password :'"'"'pw'"'"';" \
    | psql -X -q -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres' > /dev/null 2> "$work/role.log" || {
  tail -n 5 "$work/role.log" >&2
  fail "could not set the database user's password from the env file"
}

# --- 6. Start and check ---------------------------------------------------------------------------
if [ -n "$recreate" ]; then
  step "recreating and starting the api with the restored env file and certificates"
  "${compose[@]}" up -d --no-build api > "$work/up.log" 2>&1 || {
    tail -n 40 "$work/up.log" >&2
    fail "could not recreate the api"
  }
  api="$(service_container api)"
else
  step "starting the api"
  "${compose[@]}" start api > /dev/null 2>&1 || fail "could not start the api"
fi
api_stopped=''
health_url="${RESTORE_HEALTH_URL:-}"
if [ -z "$health_url" ]; then
  published="$("${compose[@]}" port api 8080 2> /dev/null | head -n 1 || true)"
  [ -n "$published" ] || fail "the api publishes no port 8080; set RESTORE_HEALTH_URL"
  health_url="http://127.0.0.1:${published##*:}/health/ready"
fi
deadline=$(($(date +%s) + timeout_seconds))
until [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 "$health_url" || true)" = 200 ]; do
  state="$(docker inspect -f '{{.State.Status}}' "$api" 2> /dev/null || echo missing)"
  if [ "$state" != running ] || [ "$(date +%s)" -ge "$deadline" ]; then
    "${compose[@]}" logs --no-color --tail 40 api >&2 || true
    fail "the api did not answer 200 on $health_url (state: $state); see the log lines above"
  fi
  sleep 2
done

step "OK database $db_name restored ($restored_organizations organizations), $key_files key files in $keys_volume, api $health_url 200"
