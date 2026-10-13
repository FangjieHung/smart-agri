#!/usr/bin/env bash
# Smoke test of the customer-deploy stack (#341): builds the production image from this checkout,
# starts deploy/docker-compose.yml with throwaway settings, checks what an operator would check
# first, runs `setup` once and removes everything again. CI runs it on every pull request (the
# `docker-smoke` job in .github/workflows/ci.yml); an operator can run it on the deployment host
# before upgrading (deploy/README.md, section 9.1).
#
#   deploy/smoke-test.sh
#
# What it never touches: deploy/.env, deploy/certs/ and the `smartagri` compose project. It runs as
# its own compose project (smartagri-smoke-<time>-<pid>), publishes the api on 127.0.0.1 only, on
# SMOKE_PORT (default 18341), and generates everything it needs on the fly into
# deploy/.smoke-test.<random>/ (git-ignored, deleted on exit): a random database password, three
# self-signed .pfx certificates with random passwords, and an env file. No real model is
# configured: the chat and embedding providers are OpenAICompatible at an address nothing listens on
# (Fake is refused outside Development), which is enough to start the api but never called here.
#
# Settings (environment variables, all optional):
#   SMOKE_PORT              host port on 127.0.0.1 (default 18341)
#   SMOKE_IMAGE             image name for the api (default smartagri-smoke-api:local)
#   SMOKE_SKIP_BUILD=1      use SMOKE_IMAGE as it is (CI builds it with a layer cache first)
#   SMOKE_KEEP_IMAGE=1      keep the image this script built (removed on exit otherwise)
#   SMOKE_TIMEOUT_SECONDS   how long to wait for /health/ready (default 180)
#
# Exits 0 when every check passes; otherwise prints `smoke-test: FAIL: <why>` (plus the api's last
# log lines when it did not start) and exits non-zero. The containers, network and volumes are
# removed in every case (`docker compose down -v`).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compose_file="$repo_root/deploy/docker-compose.yml"
port="${SMOKE_PORT:-18341}"
image="${SMOKE_IMAGE:-smartagri-smoke-api:local}"
skip_build="${SMOKE_SKIP_BUILD:-}"
keep_image="${SMOKE_KEEP_IMAGE:-}"
timeout_seconds="${SMOKE_TIMEOUT_SECONDS:-180}"
project="smartagri-smoke-$(date +%Y%m%d%H%M%S)-$$"
base_url="http://127.0.0.1:${port}"
admin_marker='<title>AI 助理工作台</title>'

step() { printf 'smoke-test: %s\n' "$*"; }
fail() {
  printf 'smoke-test: FAIL: %s\n' "$*" >&2
  exit 1
}

for tool in docker curl openssl; do
  command -v "$tool" > /dev/null 2>&1 || fail "'$tool' is not installed"
done
docker compose version > /dev/null 2>&1 || fail "'docker compose' (Compose v2 or later) is not available"
case "$port" in
  '' | *[!0-9]*) fail "SMOKE_PORT must be a port number, not '$port'" ;;
esac

# Variables from the caller's shell win over --env-file in compose interpolation, so a CHAT_API_KEY or
# POSTGRES_PASSWORD exported in this shell (or a COMPOSE_FILE) would leak into the test stack: unset
# every variable the compose file reads, and compose's own.
while IFS= read -r name; do
  unset "$name"
done < <(grep -oE '\$\{[A-Z][A-Z0-9_]*' "$compose_file" | sed 's/^\${//' | sort -u)
unset COMPOSE_FILE COMPOSE_PROJECT_NAME COMPOSE_ENV_FILES COMPOSE_PROFILES

# Under deploy/ rather than the system temp directory: Docker Desktop and colima share the home
# directory with their VM by default, not macOS's $TMPDIR, and the certificates are bind-mounted.
work="$(mktemp -d "$repo_root/deploy/.smoke-test.XXXXXX")"
built_image=''
compose=(docker compose -p "$project" -f "$compose_file" -f "$work/compose.smoke.yml" --env-file "$work/smoke.env")

cleanup() {
  local status=$?
  trap - EXIT INT TERM
  if [ -f "$work/compose.smoke.yml" ]; then
    step "removing compose project $project (down -v)"
    "${compose[@]}" down -v --remove-orphans > "$work/down.log" 2>&1 || {
      printf 'smoke-test: warning: docker compose -p %s down -v failed:\n' "$project" >&2
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

random_secret() { openssl rand -hex 24; }

# --- Throwaway settings ------------------------------------------------------------------------
step "generating throwaway certificates and settings in ${work#"$repo_root"/}"
mkdir "$work/certs"
signing_password="$(random_secret)"
encryption_password="$(random_secret)"
dataprotection_password="$(random_secret)"
make_pfx() { # name password; as deploy/README.md section 2 does, without the prompt
  PFX_PASSWORD="$2" openssl req -x509 -newkey rsa:2048 -sha256 -days 2 -nodes \
    -subj "/CN=smartagri-smoke-$1" -keyout "$work/$1.key" -out "$work/$1.crt" > /dev/null 2>&1 \
    || fail "openssl could not create the $1 certificate"
  PFX_PASSWORD="$2" openssl pkcs12 -export -inkey "$work/$1.key" -in "$work/$1.crt" \
    -out "$work/certs/$1.pfx" -passout env:PFX_PASSWORD > /dev/null 2>&1 \
    || fail "openssl could not export $1.pfx"
  rm -f "$work/$1.key" "$work/$1.crt"
}
make_pfx signing "$signing_password"
make_pfx encryption "$encryption_password"
make_pfx dataprotection "$dataprotection_password"
# The api runs as a non-root user (deploy/README.md section 2: chmod 644).
chmod 755 "$work/certs"
chmod 644 "$work/certs/"*.pfx

unreachable_model='http://127.0.0.1:9/v1'
cat > "$work/smoke.env" << EOF
POSTGRES_PASSWORD=$(random_secret)
ASPNETCORE_ENVIRONMENT=Production
API_PORT=127.0.0.1:${port}
AUTH_SIGNING_CERTIFICATE_PASSWORD=${signing_password}
AUTH_ENCRYPTION_CERTIFICATE_PASSWORD=${encryption_password}
DATA_PROTECTION_CERTIFICATE_PASSWORD=${dataprotection_password}
PUBLIC_BASE_URL=http://localhost:${port}
EMBEDDING_PROVIDER=OpenAICompatible
EMBEDDING_ENDPOINT=${unreachable_model}
EMBEDDING_MODEL=smoke-embedding
CHAT_PROVIDER=OpenAICompatible
CHAT_ENDPOINT=${unreachable_model}
CHAT_MODEL=smoke-chat
CHAT_MODELS_0_PROVIDER=OpenAICompatible
CHAT_MODELS_0_ENDPOINT=${unreachable_model}
CHAT_MODELS_0_MODEL=smoke-chat-second
CHAT_MODELS_0_ID=smoke-second
EOF
chmod 600 "$work/smoke.env"

# Same service, our image name and our certificates (compose replaces a volume entry by its target).
cat > "$work/compose.smoke.yml" << EOF
services:
  api:
    image: ${image}
    volumes:
      - ${work}/certs:/app/certs:ro
EOF

"${compose[@]}" config --quiet 2> "$work/config.log" || {
  cat "$work/config.log" >&2
  fail "deploy/docker-compose.yml is not valid (docker compose config)"
}

# --- Build ------------------------------------------------------------------------------------
# With `docker build` and the context and Dockerfile from the compose file's own `build:` section, so
# a wrong path there fails here too; plain `docker build` works with and without BuildKit/buildx.
resolved="$("${compose[@]}" config 2> /dev/null)"
build_context="$(printf '%s\n' "$resolved" | sed -nE 's/^ +context: +//p' | head -n 1)"
dockerfile="$(printf '%s\n' "$resolved" | sed -nE 's/^ +dockerfile: +//p' | head -n 1)"
[ -n "$build_context" ] && [ -n "$dockerfile" ] \
  || fail "deploy/docker-compose.yml has no build context and dockerfile for the api"
case "$dockerfile" in
  /*) dockerfile_path="$dockerfile" ;;
  *) dockerfile_path="$build_context/$dockerfile" ;;
esac
[ -f "$dockerfile_path" ] || fail "the api's Dockerfile does not exist: $dockerfile_path (deploy/docker-compose.yml build section)"

if [ -n "$skip_build" ]; then
  docker image inspect "$image" > /dev/null 2>&1 || fail "SMOKE_SKIP_BUILD is set but the image $image does not exist"
  step "using the existing image $image (SMOKE_SKIP_BUILD)"
else
  step "building $image from ${dockerfile_path#"$repo_root"/}"
  build_started=$(date +%s)
  docker build -f "$dockerfile_path" -t "$image" "$build_context" || fail "the production image does not build"
  built_image="$image"
  step "built $image in $(($(date +%s) - build_started))s"
fi

# --- Start ------------------------------------------------------------------------------------
step "starting compose project $project on $base_url"
"${compose[@]}" up -d --no-build > "$work/up.log" 2>&1 || {
  tail -n 40 "$work/up.log" >&2
  fail "docker compose up failed"
}

api_logs() {
  printf 'smoke-test: last api log lines:\n' >&2
  "${compose[@]}" logs --no-color --tail 60 api >&2 || true
}

api_container="$("${compose[@]}" ps -q api)"
[ -n "$api_container" ] || fail "the api container was not created"
deadline=$(($(date +%s) + timeout_seconds))
until [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 "$base_url/health/ready" || true)" = 200 ]; do
  state="$(docker inspect -f '{{.State.Status}} {{.RestartCount}}' "$api_container" 2> /dev/null || echo 'missing 0')"
  if [ "${state%% *}" != running ] || [ "${state##* }" != 0 ]; then
    api_logs
    fail "the api container stopped or restarted before /health/ready answered 200 (state: $state)"
  fi
  if [ "$(date +%s)" -ge "$deadline" ]; then
    api_logs
    fail "/health/ready did not answer 200 within ${timeout_seconds}s"
  fi
  sleep 2
done
step "ok   /health/ready 200"

# --- Checks -----------------------------------------------------------------------------------
# fetch PATH: the body goes to $work/body, prints "<status> <content-type>".
fetch() {
  curl -s --max-time 10 -o "$work/body" -w '%{http_code} %{content_type}' "$base_url$1" || echo '000 none'
}

check_admin() { # path
  local result
  result="$(fetch "$1")"
  [ "${result%% *}" = 200 ] || fail "GET $1 answered ${result%% *}, not 200 (the admin SPA)"
  case "${result#* }" in
    text/html*) ;;
    *) fail "GET $1 answered content-type '${result#* }', not text/html (the admin SPA)" ;;
  esac
  grep -qF "$admin_marker" "$work/body" \
    || fail "GET $1 is not the admin's index.html (no $admin_marker): is apps/admin copied into the image?"
  step "ok   $1 200, the admin's index.html"
}
check_admin /
# A deep link the browser reloads: every GET that is not the api's own path gets the admin.
check_admin /app/home

result="$(fetch /embed.js)"
[ "${result%% *}" = 200 ] || fail "GET /embed.js answered ${result%% *}, not 200 (the embed loader)"
case "${result#* }" in
  *javascript*) ;;
  *) fail "GET /embed.js answered content-type '${result#* }', not JavaScript" ;;
esac
step "ok   /embed.js 200, ${result#* }"

# --- setup ------------------------------------------------------------------------------------
# Its standard output holds the administrator's one-time password: discarded, never printed. Errors go
# to standard error (redacted by the command itself) and are shown only when it fails.
step "running setup once (its output holds a one-time password and is discarded)"
setup_status=0
"${compose[@]}" run --rm -T --no-deps api setup \
  --organization-name '冒煙測試' --organization-code smoke --admin-login smoke-admin \
  > /dev/null 2> "$work/setup.err" || setup_status=$?
if [ "$setup_status" -ne 0 ]; then
  tail -n 30 "$work/setup.err" >&2
  fail "setup exited with $setup_status"
fi
step "ok   setup exited 0"

step "PASS: image, compose stack, admin, embed loader and setup all work"
