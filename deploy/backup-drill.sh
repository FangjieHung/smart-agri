#!/usr/bin/env bash
# Backup and restore drill (#342): proves deploy/backup.sh and deploy/restore.sh bring a deployment
# back, on a throwaway stack built from this checkout (the same way as deploy/smoke-test.sh). Run it
# on the deployment host after changing either script or before relying on a new version
# (deploy/README.md, section 14.6). Takes a few minutes after the image is built.
#
#   deploy/backup-drill.sh
#
#  1. starts deploy/docker-compose.yml (Production), runs `setup`, signs in with its one-time password
#     and sets a new one, creates a knowledge base with an FAQ entry, an assistant, a conversation and
#     a LINE channel with dummy credentials (stored encrypted with the Data Protection key ring);
#  2. backs up three times with BACKUP_KEEP=2 (two files must be left, chmod 600 in a 700 directory);
#  3. `down -v` (all data gone), `up` again (a fresh, empty database and a new key ring), checks the
#     account cannot sign in, restores, and checks the account signs in with its password, the data is
#     there and the LINE channel secret decrypts (the LINE webhook accepts a signature made with it);
#  4. restoring again without --force is refused (the database has data);
#  5. a new machine: `down -v` and remove the env file and the certificates, restore (it puts them back
#     and creates the containers), check everything again;
#  6. a database volume created with another password and a different env file: restore without
#     --force is refused, with --force it replaces the env file, recreates the containers, resets the
#     database user's password, and everything checks out again;
#  7. backup.sh with postgres stopped exits non-zero.
#
# What it never touches: deploy/.env, deploy/certs/, deploy/backups/ and the `smartagri` compose project.
# Its own project is smartagri-drill-<time>-<pid> on 127.0.0.1:DRILL_PORT; everything it generates
# lives in deploy/.backup-drill.<random>/ (git-ignored, deleted on exit, with the containers, volumes
# and network). Needs docker (with compose), curl, openssl and python3.
#
# Settings (environment variables, all optional): DRILL_PORT (default 18342), DRILL_IMAGE (default
# smartagri-drill-api:local), DRILL_SKIP_BUILD=1 (use DRILL_IMAGE as it is), DRILL_KEEP_IMAGE=1,
# DRILL_TIMEOUT_SECONDS (default 180). Prints `backup-drill: PASS ...` and exits 0, or
# `backup-drill: FAIL: <why>` and exits non-zero. Never prints a password or a key.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
deploy="$repo_root/deploy"
compose_file="$deploy/docker-compose.yml"
port="${DRILL_PORT:-18342}"
image="${DRILL_IMAGE:-smartagri-drill-api:local}"
skip_build="${DRILL_SKIP_BUILD:-}"
keep_image="${DRILL_KEEP_IMAGE:-}"
timeout_seconds="${DRILL_TIMEOUT_SECONDS:-180}"
project="smartagri-drill-$(date +%Y%m%d%H%M%S)-$$"
api_url="http://127.0.0.1:${port}"
started=$(date +%s)

step() { printf 'backup-drill: [%3ss] %s\n' "$(($(date +%s) - started))" "$*"; }
fail() {
  printf 'backup-drill: FAIL: %s\n' "$*" >&2
  exit 1
}

for tool in docker curl openssl python3 cmp; do
  command -v "$tool" > /dev/null 2>&1 || fail "'$tool' is not installed"
done
docker compose version > /dev/null 2>&1 || fail "'docker compose' (Compose v2 or later) is not available"
case "$port" in
  '' | *[!0-9]*) fail "DRILL_PORT must be a port number, not '$port'" ;;
esac

# As in smoke-test.sh: the caller's shell must not leak settings into the throwaway stack or the scripts.
while IFS= read -r name; do
  unset "$name"
done < <(grep -oE '\$\{[A-Z][A-Z0-9_]*' "$compose_file" | sed 's/^\${//' | sort -u)
unset COMPOSE_FILE COMPOSE_PROJECT_NAME COMPOSE_ENV_FILES COMPOSE_PROFILES \
  BACKUP_DIR BACKUP_KEEP RESTORE_HEALTH_URL RESTORE_TIMEOUT_SECONDS

# Under deploy/ (inside the home directory): colima and Docker Desktop share it with their VM.
work="$(mktemp -d "$deploy/.backup-drill.XXXXXX")"
env_file="$work/drill.env"
certs="$work/certs"
backups="$work/backups"
built_image=''
compose=(docker compose -p "$project" -f "$compose_file" -f "$work/compose.drill.yml" --env-file "$env_file")

cleanup() {
  local status=$?
  trap - EXIT INT TERM
  # A failed step may have left the env file away; compose needs it to interpolate even for `down`.
  [ -f "$env_file" ] || cp "$work/original/drill.env" "$env_file" 2> /dev/null || true
  if [ -f "$work/compose.drill.yml" ]; then
    step "removing compose project $project (down -v)"
    "${compose[@]}" down -v --remove-orphans > "$work/down.log" 2>&1 || {
      printf 'backup-drill: warning: docker compose -p %s down -v failed:\n' "$project" >&2
      tail -n 20 "$work/down.log" >&2 || true
    }
  fi
  if [ -n "$built_image" ] && [ -z "$keep_image" ]; then
    docker image rm "$built_image" > /dev/null 2>&1 || true
  fi
  rm -rf "$work"
  exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# --- Throwaway settings (as smoke-test.sh) -------------------------------------------------------
random_secret() { openssl rand -hex 24; }
mkdir "$certs" "$work/original"
signing_password="$(random_secret)"
encryption_password="$(random_secret)"
dataprotection_password="$(random_secret)"
make_pfx() { # name password
  PFX_PASSWORD="$2" openssl req -x509 -newkey rsa:2048 -sha256 -days 2 -nodes \
    -subj "/CN=smartagri-drill-$1" -keyout "$work/$1.key" -out "$work/$1.crt" > /dev/null 2>&1 \
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

write_env() { # database password
  local unreachable_model='http://127.0.0.1:9/v1'
  cat > "$env_file" << EOF
POSTGRES_PASSWORD=$1
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
}
write_env "$(random_secret)"

cat > "$work/compose.drill.yml" << EOF
services:
  api:
    image: ${image}
    volumes:
      - ${certs}:/app/certs:ro
EOF

# What backup.sh and restore.sh need to find this stack instead of the real deployment.
export DEPLOY_ENV_FILE="$env_file" DEPLOY_CERTS_DIR="$certs" DEPLOY_PROJECT="$project" \
  DEPLOY_COMPOSE_FILES="$compose_file $work/compose.drill.yml" BACKUP_DIR="$backups" BACKUP_KEEP=2 \
  RESTORE_TIMEOUT_SECONDS="$timeout_seconds" DRILL_API="$api_url" DRILL_ORIGIN="https://localhost:${port}"
api_client=(python3 -I "$deploy/backup-drill-api.py")

"${compose[@]}" config --quiet 2> "$work/config.log" || {
  cat "$work/config.log" >&2
  fail "deploy/docker-compose.yml is not valid with the drill settings"
}

# --- Helpers --------------------------------------------------------------------------------------
wait_ready() {
  local deadline container state
  deadline=$(($(date +%s) + timeout_seconds))
  container="$("${compose[@]}" ps -q api)"
  until [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 "$api_url/health/ready" || true)" = 200 ]; do
    state="$(docker inspect -f '{{.State.Status}}' "$container" 2> /dev/null || echo missing)"
    if [ "$state" != running ] || [ "$(date +%s)" -ge "$deadline" ]; then
      "${compose[@]}" logs --no-color --tail 60 api >&2 || true
      fail "/health/ready did not answer 200 (api state: $state)"
    fi
    sleep 2
  done
}

up() {
  "${compose[@]}" up -d --no-build > "$work/up.log" 2>&1 || {
    tail -n 40 "$work/up.log" >&2
    fail "docker compose up failed"
  }
  wait_ready
}

project_volumes() { docker volume ls -q --filter "label=com.docker.compose.project=$project" | wc -l | tr -d ' '; }

# run_script NAME LOG ARGS...: runs a deploy script, shows its last line, returns its exit code.
run_script() {
  local name="$1" log="$2" status=0
  shift 2
  "$deploy/$name" "$@" > "$log" 2>&1 || status=$?
  printf '    %s (exit %s): %s\n' "$name" "$status" "$(grep -E '^(backup|restore): (OK|FAIL)' "$log" | tail -n 1)"
  return "$status"
}

expect_refusal() { # log pattern what
  grep -q -- "$2" "$1" || {
    tail -n 20 "$1" >&2
    fail "$3: the refusal does not say '$2'"
  }
}

newest_backup() { find "$backups" -maxdepth 1 -type f -name 'smartagri-backup-*.tar' | sort | tail -n 1; }

# --- Build ----------------------------------------------------------------------------------------
if [ -n "$skip_build" ]; then
  docker image inspect "$image" > /dev/null 2>&1 || fail "DRILL_SKIP_BUILD is set but the image $image does not exist"
  step "using the existing image $image (DRILL_SKIP_BUILD)"
else
  step "building $image"
  docker build -f "$repo_root/apps/api/src/SmartAgri.Api/Dockerfile" -t "$image" "$repo_root" > "$work/build.log" 2>&1 || {
    tail -n 40 "$work/build.log" >&2
    fail "the production image does not build"
  }
  built_image="$image"
fi

# --- 1. A deployment with data --------------------------------------------------------------------
step "1. starting compose project $project on $api_url"
up
step "    running setup (its output, with the one-time password, goes to a chmod 600 file)"
( umask 077 && "${compose[@]}" run --rm -T --no-deps api setup --organization-name '備份演練' \
  --organization-code drill --admin-login drill-admin > "$work/setup.out" 2> "$work/setup.err" ) || {
  tail -n 20 "$work/setup.err" >&2
  fail "setup failed"
}
"${api_client[@]}" seed "$work/state.json" "$work/setup.out" || fail "creating the drill data failed"
rm -f "$work/setup.out"

# --- 2. Backups -----------------------------------------------------------------------------------
step "2. backing up three times with BACKUP_KEEP=2"
for run in 1 2 3; do
  [ "$run" = 1 ] || sleep 1 # one file name per second
  run_script backup.sh "$work/backup-$run.log" || fail "backup.sh failed (run $run)"
done
count="$(find "$backups" -maxdepth 1 -type f -name 'smartagri-backup-*.tar' | wc -l | tr -d ' ')"
[ "$count" = 2 ] || fail "BACKUP_KEEP=2 left $count backups"
[ -n "$(find "$backups" -maxdepth 0 -perm 0700)" ] || fail "the backup directory is not chmod 700"
[ -z "$(find "$backups" -maxdepth 1 -type f ! -perm 0600)" ] || fail "a backup file is not chmod 600"
[ -z "$(find "$backups" -maxdepth 1 -name '.partial-*')" ] || fail "backup.sh left a partial file"
backup="$(newest_backup)"
step "    ok   2 backups kept, directory 700, files 600; using $(basename "$backup")"
cp "$backup" "$work/the-backup.tar"
backup="$work/the-backup.tar"

# --- 3. Everything gone, a fresh deployment, restore ----------------------------------------------
step "3. down -v: removing the containers and every volume"
"${compose[@]}" down -v > "$work/down.log" 2>&1 || fail "docker compose down -v failed"
[ "$(project_volumes)" = 0 ] || fail "volumes of $project are left after down -v"
step "    ok   no volumes left; starting a fresh, empty deployment"
up
"${api_client[@]}" expect-gone "$work/state.json" || fail "the fresh deployment still knows the account"
run_script restore.sh "$work/restore-1.log" "$backup" || fail "restore.sh failed on the fresh deployment"
"${api_client[@]}" verify "$work/state.json" || fail "the restored deployment does not have everything"

# --- 4. Refusal ------------------------------------------------------------------------------------
step "4. restoring again without --force must be refused"
if run_script restore.sh "$work/restore-2.log" "$backup"; then
  fail "restore.sh overwrote a database with data without --force"
fi
expect_refusal "$work/restore-2.log" 'already holds data' "restore.sh without --force"
step "    ok   refused: the database already holds data"

# --- 5. A new machine ------------------------------------------------------------------------------
step "5. a new machine: down -v, and the env file and certificates removed"
"${compose[@]}" down -v > "$work/down.log" 2>&1 || fail "docker compose down -v failed"
mv "$env_file" "$work/original/drill.env"
mkdir "$work/original/certs"
mv "$certs/"*.pfx "$work/original/certs/"
run_script restore.sh "$work/restore-3.log" "$backup" || fail "restore.sh failed on a new machine"
cmp -s "$env_file" "$work/original/drill.env" || fail "the restored env file differs from the original"
for pfx in signing encryption dataprotection; do
  cmp -s "$certs/$pfx.pfx" "$work/original/certs/$pfx.pfx" || fail "the restored $pfx.pfx differs from the original"
done
step "    ok   env file and the three certificates put back byte for byte"
"${api_client[@]}" verify "$work/state.json" || fail "the deployment restored on a new machine does not have everything"

# --- 6. Another database password and env file -----------------------------------------------------
step "6. a fresh deployment created with another database password and env file"
"${compose[@]}" down -v > "$work/down.log" 2>&1 || fail "docker compose down -v failed"
write_env "$(random_secret)"
up
if run_script restore.sh "$work/restore-4.log" "$backup"; then
  fail "restore.sh replaced a different env file without --force"
fi
expect_refusal "$work/restore-4.log" 'differ from the backup' "restore.sh with a different env file"
step "    ok   refused without --force: the env file differs from the backup's"
run_script restore.sh "$work/restore-5.log" --force "$backup" || fail "restore.sh --force failed"
cmp -s "$env_file" "$work/original/drill.env" || fail "restore.sh --force did not put the backup's env file back"
[ -n "$(find "$work" -maxdepth 1 -name 'drill.env.before-restore-*' -perm 0600)" ] \
  || fail "restore.sh --force did not keep the replaced env file (chmod 600)"
step "    ok   --force replaced the env file and kept the old one (chmod 600)"
"${api_client[@]}" verify "$work/state.json" || fail "the deployment restored with --force does not have everything"

# --- 7. backup.sh without a database ---------------------------------------------------------------
step "7. backup.sh with postgres stopped must fail"
"${compose[@]}" stop postgres > "$work/stop.log" 2>&1 || fail "could not stop postgres"
if run_script backup.sh "$work/backup-fail.log"; then
  fail "backup.sh succeeded without a running database"
fi
expect_refusal "$work/backup-fail.log" 'postgres container is not running' "backup.sh with postgres stopped"
[ "$(find "$backups" -maxdepth 1 -type f -name 'smartagri-backup-*.tar' | wc -l | tr -d ' ')" = 2 ] \
  || fail "the failed backup changed the backup directory"
step "    ok   exit non-zero, clear message, no file written"

step "PASS: backup, retention, restore onto a fresh deployment and onto a new machine, --force, and both refusals"
