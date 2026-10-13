#!/usr/bin/env bash
# Upgrade and rollback drill (#343): rehearses deploy/README.md section 15 on a throwaway stack, from an
# older version of this repository to this checkout and back. Run it before upgrading a deployment
# (section 15.4) or after changing the upgrade steps. Takes about as long as building both images.
#
#   deploy/upgrade-drill.sh <old version>        e.g. deploy/upgrade-drill.sh 12a1b2c
#
#  1. checks the old version out into a temporary `git worktree`, builds its image with
#     `docker compose build`, starts its deploy/docker-compose.yml (Production), runs `setup` and creates
#     data as deploy/backup-drill.sh does (an account with a new password, a knowledge base with an FAQ
#     entry, an assistant, a conversation and a LINE channel whose secret is stored encrypted);
#  2. upgrades to this checkout as section 15.2 says: tags the running image with the old version, backs
#     up with deploy/backup.sh, builds this checkout's image, stops the api, runs `migrate`, `up -d`, waits
#     for Docker to report the api healthy, and checks /health/ready and all of the data;
#  3. adds a marker table to the database: data written after the upgrade;
#  4. rolls back as section 15.3 says: stops the api, tags the old image back, removes the api container,
#     restores the pre-upgrade backup with deploy/restore.sh --force onto the old compose file, and checks
#     the api runs the old image again, all of the data is there and the marker table is gone.
#
# This checkout's backup.sh and restore.sh are used throughout (an old version may predate them). What it
# never touches: deploy/.env, deploy/certs/, deploy/backups/ and the `smartagri` compose project. Its own
# project is smartagri-upgrade-<time>-<pid> on 127.0.0.1:UPGRADE_DRILL_PORT; its images are that
# project's `<project>-api` (compose's default name, as `smartagri-api` in the README). Everything it
# generates lives in deploy/.upgrade-drill.<random>/ (git-ignored); on exit it removes the containers,
# volumes, network, both images (the build cache stays) and the old version's worktree.
# Needs git, docker (with compose), curl, openssl and python3.
#
# Settings (environment variables, all optional): UPGRADE_DRILL_PORT (default 18344),
# UPGRADE_DRILL_TIMEOUT_SECONDS (default 180). Prints `upgrade-drill: PASS ...` and exits 0, or
# `upgrade-drill: FAIL: <why>` and exits non-zero. Never prints a password or a key.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
deploy="$repo_root/deploy"
compose_file="$deploy/docker-compose.yml"
port="${UPGRADE_DRILL_PORT:-18344}"
timeout_seconds="${UPGRADE_DRILL_TIMEOUT_SECONDS:-180}"
project="smartagri-upgrade-$(date +%Y%m%d%H%M%S)-$$"
image="${project}-api"
api_url="http://127.0.0.1:${port}"
started=$(date +%s)

step() { printf 'upgrade-drill: [%3ss] %s\n' "$(($(date +%s) - started))" "$*"; }
fail() {
  printf 'upgrade-drill: FAIL: %s\n' "$*" >&2
  exit 1
}

case "${1:-}" in
  '' | -h | --help)
    sed -n '2,/^set -euo/p' "$0" | sed -e '$d' -e 's/^# \{0,1\}//'
    [ -n "${1:-}" ] && exit 0
    fail "which old version? deploy/upgrade-drill.sh <commit, tag or branch>"
    ;;
esac
[ $# -eq 1 ] || fail "give exactly one old version (see --help)"
for tool in git docker curl openssl python3 cmp; do
  command -v "$tool" > /dev/null 2>&1 || fail "'$tool' is not installed"
done
docker compose version > /dev/null 2>&1 || fail "'docker compose' (Compose v2 or later) is not available"
case "$port" in
  '' | *[!0-9]*) fail "UPGRADE_DRILL_PORT must be a port number, not '$port'" ;;
esac
old_sha="$(git -C "$repo_root" rev-parse --verify --quiet --short "$1^{commit}")" \
  || fail "'$1' is not a commit, tag or branch of this repository"
new_sha="$(git -C "$repo_root" rev-parse --short HEAD)"
git -C "$repo_root" cat-file -e "$old_sha:deploy/docker-compose.yml" 2> /dev/null \
  || fail "$old_sha has no deploy/docker-compose.yml"

# As in smoke-test.sh: the caller's shell must not leak settings into the throwaway stack or the scripts.
# Both versions' compose files count: the old one may read a variable this one no longer does.
while IFS= read -r name; do
  unset "$name"
done < <({ cat "$compose_file"; git -C "$repo_root" show "$old_sha:deploy/docker-compose.yml"; } \
  | grep -oE '\$\{[A-Z][A-Z0-9_]*' | sed 's/^\${//' | sort -u)
unset COMPOSE_FILE COMPOSE_PROJECT_NAME COMPOSE_ENV_FILES COMPOSE_PROFILES \
  BACKUP_DIR BACKUP_KEEP RESTORE_HEALTH_URL RESTORE_TIMEOUT_SECONDS

# Under deploy/ (inside the home directory): colima and Docker Desktop share it with their VM.
work="$(mktemp -d "$deploy/.upgrade-drill.XXXXXX")"
old_tree="$work/old"
old_compose_file="$old_tree/deploy/docker-compose.yml"
env_file="$work/drill.env"
certs="$work/certs"
backups="$work/backups"
override="$work/compose.drill.yml"
# The deployment before (old) and after (new) the upgrade: one compose project, so the same volumes.
compose_old=(docker compose -p "$project" -f "$old_compose_file" -f "$override" --env-file "$env_file")
compose_new=(docker compose -p "$project" -f "$compose_file" -f "$override" --env-file "$env_file")

cleanup() {
  local status=$? tag
  trap - EXIT INT TERM
  if [ -f "$override" ]; then
    step "removing compose project $project (down -v) and its images"
    "${compose_new[@]}" down -v --remove-orphans > "$work/down.log" 2>&1 || {
      printf 'upgrade-drill: warning: docker compose -p %s down -v failed:\n' "$project" >&2
      tail -n 20 "$work/down.log" >&2 || true
    }
    while IFS= read -r tag; do
      docker image rm "$tag" > /dev/null 2>&1 || true
    done < <(docker image ls --format '{{.Repository}}:{{.Tag}}' --filter "reference=$image" 2> /dev/null)
  fi
  if [ -d "$old_tree" ]; then
    git -C "$repo_root" worktree remove --force "$old_tree" > /dev/null 2>&1 || true
  fi
  rm -rf "$work"
  git -C "$repo_root" worktree prune > /dev/null 2>&1 || true
  exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# --- Throwaway settings (as backup-drill.sh) -----------------------------------------------------
random_secret() { openssl rand -hex 24; }
mkdir "$certs"
signing_password="$(random_secret)"
encryption_password="$(random_secret)"
dataprotection_password="$(random_secret)"
make_pfx() { # name password
  PFX_PASSWORD="$2" openssl req -x509 -newkey rsa:2048 -sha256 -days 2 -nodes \
    -subj "/CN=smartagri-upgrade-$1" -keyout "$work/$1.key" -out "$work/$1.crt" > /dev/null 2>&1 \
    || fail "openssl could not create the $1 certificate"
  PFX_PASSWORD="$2" openssl pkcs12 -export -inkey "$work/$1.key" -in "$work/$1.crt" \
    -out "$certs/$1.pfx" -passout env:PFX_PASSWORD > /dev/null 2>&1 \
    || fail "openssl could not export $1.pfx"
  rm -f "$work/$1.key" "$work/$1.crt"
}
make_pfx signing "$signing_password"
make_pfx encryption "$encryption_password"
make_pfx dataprotection "$dataprotection_password"
chmod 755 "$certs"
chmod 644 "$certs/"*.pfx

unreachable_model='http://127.0.0.1:9/v1'
cat > "$env_file" << EOF
POSTGRES_PASSWORD=$(random_secret)
ASPNETCORE_ENVIRONMENT=Production
API_PORT=127.0.0.1:${port}
AUTH_SIGNING_CERTIFICATE_PASSWORD=${signing_password}
AUTH_ENCRYPTION_CERTIFICATE_PASSWORD=${encryption_password}
DATA_PROTECTION_CERTIFICATE_PASSWORD=${dataprotection_password}
PUBLIC_BASE_URL=https://localhost:${port}
TRUSTED_PROXIES=0.0.0.0/0,::/0
EMBEDDING_PROVIDER=OpenAICompatible
EMBEDDING_ENDPOINT=${unreachable_model}
EMBEDDING_MODEL=drill-embedding
CHAT_PROVIDER=OpenAICompatible
CHAT_ENDPOINT=${unreachable_model}
CHAT_MODEL=drill-chat
EOF
chmod 600 "$env_file"

# Only the certificates: no `image:`, so both versions build compose's default image name, as for real.
cat > "$override" << EOF
services:
  api:
    volumes:
      - ${certs}:/app/certs:ro
EOF

# What backup.sh and restore.sh need to find this stack (DEPLOY_COMPOSE_FILES is set per call).
export DEPLOY_ENV_FILE="$env_file" DEPLOY_CERTS_DIR="$certs" DEPLOY_PROJECT="$project" \
  BACKUP_DIR="$backups" RESTORE_TIMEOUT_SECONDS="$timeout_seconds" \
  DRILL_API="$api_url" DRILL_ORIGIN="https://localhost:${port}"
api_client=(python3 -I "$deploy/backup-drill-api.py")

# --- Helpers --------------------------------------------------------------------------------------
health_of() { # container: starting, healthy, unhealthy, or none without a healthcheck
  docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$1" 2> /dev/null || echo missing
}

# wait_up COMPOSE...: /health/ready answers 200 and, when the compose file has a healthcheck, Docker
# reports the api healthy.
wait_up() {
  local deadline container state health
  deadline=$(($(date +%s) + timeout_seconds))
  container="$("$@" ps -q api)"
  [ -n "$container" ] || fail "there is no api container"
  until [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 "$api_url/health/ready" || true)" = 200 ]; do
    state="$(docker inspect -f '{{.State.Status}}' "$container" 2> /dev/null || echo missing)"
    if [ "$state" != running ] || [ "$(date +%s)" -ge "$deadline" ]; then
      "$@" logs --no-color --tail 60 api >&2 || true
      fail "/health/ready did not answer 200 (api state: $state)"
    fi
    sleep 2
  done
  until health="$(health_of "$container")" && [ "$health" = healthy ] || [ "$health" = none ]; do
    [ "$health" != unhealthy ] || fail "Docker reports the api as unhealthy although /health/ready answers 200"
    [ "$(date +%s)" -lt "$deadline" ] || fail "Docker did not report the api as healthy (health: $health)"
    sleep 2
  done
  step "    ok   /health/ready 200, Docker health: $health"
}

# run_script NAME LOG ARGS...: runs a deploy script, shows its last line, returns its exit code.
run_script() {
  local name="$1" log="$2" status=0
  shift 2
  "$deploy/$name" "$@" > "$log" 2>&1 || status=$?
  printf '    %s (exit %s): %s\n' "$name" "$status" "$(grep -E '^(backup|restore): (OK|FAIL)' "$log" | tail -n 1)"
  return "$status"
}

# psql_db COMPOSE... -- SQL: one statement in the deployment's database, unaligned output.
psql_db() {
  local sql="${*: -1}"
  # shellcheck disable=SC2016 # expanded by the container's shell
  "${@:1:$#-2}" exec -T postgres sh -c 'exec psql -X -q -v ON_ERROR_STOP=1 -tA -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "$0"' "$sql"
}

# The image a container was created from, and an image tag's id.
container_image() { docker inspect -f '{{.Image}}' "$("$@" ps -a -q api)"; }
image_id() { docker image inspect -f '{{.Id}}' "$1"; }

# --- 1. The old version, with data ----------------------------------------------------------------
step "1. the old version $old_sha: a git worktree, docker compose build, up"
git -C "$repo_root" worktree add --detach "$old_tree" "$old_sha" > "$work/worktree.log" 2>&1 || {
  tail -n 20 "$work/worktree.log" >&2
  fail "git worktree add $old_sha failed"
}
"${compose_old[@]}" config --quiet 2> "$work/config.log" || {
  cat "$work/config.log" >&2
  fail "the old version's deploy/docker-compose.yml is not valid with the drill settings"
}
"${compose_new[@]}" config --quiet 2> "$work/config.log" || {
  cat "$work/config.log" >&2
  fail "deploy/docker-compose.yml is not valid with the drill settings"
}
"${compose_old[@]}" build api > "$work/build-old.log" 2>&1 || {
  tail -n 40 "$work/build-old.log" >&2
  fail "the old version's image does not build"
}
"${compose_old[@]}" up -d > "$work/up.log" 2>&1 || {
  tail -n 40 "$work/up.log" >&2
  fail "docker compose up (old version) failed"
}
wait_up "${compose_old[@]}"
step "    running setup (its output, with the one-time password, goes to a chmod 600 file)"
( umask 077 && "${compose_old[@]}" run --rm -T --no-deps api setup --organization-name '升級演練' \
  --organization-code upgrade --admin-login drill-admin > "$work/setup.out" 2> "$work/setup.err" ) || {
  tail -n 20 "$work/setup.err" >&2
  fail "setup failed"
}
"${api_client[@]}" seed "$work/state.json" "$work/setup.out" || fail "creating the drill data failed"
rm -f "$work/setup.out"
old_image_id="$(image_id "$image")"

# --- 2. Upgrade (deploy/README.md, section 15.2) ---------------------------------------------------
step "2. upgrading to this checkout ($new_sha) as deploy/README.md section 15.2 says"
step "    15.2 step 2: docker tag $image $image:$old_sha"
docker tag "$image" "$image:$old_sha"
step "    15.2 step 3: deploy/backup.sh"
DEPLOY_COMPOSE_FILES="$old_compose_file $override" run_script backup.sh "$work/backup.log" \
  || fail "backup.sh failed"
backup="$(find "$backups" -maxdepth 1 -type f -name 'smartagri-backup-*.tar' | sort | tail -n 1)"
[ -n "$backup" ] || fail "backup.sh wrote no backup file"
step "    15.2 step 4: the new version is this checkout"
step "    15.2 step 5: docker compose build api"
"${compose_new[@]}" build api > "$work/build-new.log" 2>&1 || {
  tail -n 40 "$work/build-new.log" >&2
  fail "this checkout's image does not build"
}
new_image_id="$(image_id "$image")"
[ "$new_image_id" != "$old_image_id" ] || fail "the new build did not produce a new $image"
step "    15.2 step 6: stop api, run --rm api migrate"
"${compose_new[@]}" stop api > "$work/stop.log" 2>&1 || fail "could not stop the api"
"${compose_new[@]}" run --rm -T api migrate > "$work/migrate.log" 2>&1 || {
  tail -n 20 "$work/migrate.log" >&2
  fail "migrate (new version) failed"
}
step "    15.2 step 7: up -d"
"${compose_new[@]}" up -d > "$work/up.log" 2>&1 || {
  tail -n 40 "$work/up.log" >&2
  fail "docker compose up (new version) failed"
}
step "    15.2 step 8: verify"
wait_up "${compose_new[@]}"
[ "$(container_image "${compose_new[@]}")" = "$new_image_id" ] || fail "the api does not run the new image"
while IFS= read -r line; do
  step "    docker compose ps: $line"
done < <("${compose_new[@]}" ps --format '{{.Service}} {{.Status}}')
"${compose_new[@]}" ps --format '{{.Service}} {{.Status}}' | grep -q '^api .*(healthy)' \
  || fail "docker compose ps does not show the api as (healthy)"
"${api_client[@]}" verify "$work/state.json" || fail "the upgraded deployment does not have everything"

# --- 3. Data written after the upgrade -------------------------------------------------------------
step "3. writing a marker table (data from after the upgrade, which the rollback must undo)"
psql_db "${compose_new[@]}" -- 'create table upgrade_drill_marker (id int)' > /dev/null \
  || fail "could not create the marker table"

# --- 4. Rollback (deploy/README.md, section 15.3) --------------------------------------------------
step "4. rolling back to $old_sha as deploy/README.md section 15.3 says"
step "    15.3 step 1: stop api"
"${compose_new[@]}" stop api > "$work/stop.log" 2>&1 || fail "could not stop the api"
step "    15.3 step 2: the old version's checkout (its compose file from now on)"
step "    15.3 step 3: docker tag $image:$old_sha $image"
docker tag "$image:$old_sha" "$image"
step "    15.3 step 4: rm -f api"
"${compose_old[@]}" rm -f api > "$work/rm.log" 2>&1 || fail "could not remove the api container"
step "    15.3 step 5: deploy/restore.sh --force <the pre-upgrade backup>"
DEPLOY_COMPOSE_FILES="$old_compose_file $override" run_script restore.sh "$work/restore.log" --force "$backup" \
  || fail "restore.sh --force failed"
step "    15.3 step 6: verify"
wait_up "${compose_old[@]}"
[ "$(container_image "${compose_old[@]}")" = "$old_image_id" ] || fail "the api does not run the old image again"
step "    ok   the api runs the old image again ($image:$old_sha, no rebuild)"
[ "$(psql_db "${compose_old[@]}" -- "select to_regclass('public.upgrade_drill_marker') is null")" = t ] \
  || fail "the marker table is still there: the database was not restored from the pre-upgrade backup"
step "    ok   the marker table is gone: the database is the pre-upgrade backup"
"${api_client[@]}" verify "$work/state.json" || fail "the rolled-back deployment does not have everything"

step "PASS: $old_sha -> $new_sha upgrade (backup, build, migrate, up, healthy, data) and rollback (old image, restore --force, data)"
