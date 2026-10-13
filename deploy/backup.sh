#!/usr/bin/env bash
# Backup of a customer deployment (#342; deploy/README.md, section 14): everything needed to bring
# it back on this or another machine, in one file.
#
#   deploy/backup.sh
#
# One file per run, BACKUP_DIR/smartagri-backup-<YYYYmmdd-HHMMSS>.tar (chmod 600, the directory 700),
# holding:
#   database.dump              pg_dump --format=custom of the database, taken in the running postgres
#                              container (one consistent snapshot; the api can keep running)
#   dataprotection-keys.tar.gz the Data Protection key ring (the volume mounted at /app/keys): the
#                              only keys that open the stored secrets (LINE channel secret and access
#                              token) and the sign-in cookies; taken AFTER the dump, so every key the
#                              dump's ciphertext needs is in it
#   certs.tar.gz               DEPLOY_CERTS_DIR (deploy/certs): dataprotection.pfx encrypts the key
#                              files, signing.pfx and encryption.pfx are the token certificates
#   env                        DEPLOY_ENV_FILE (deploy/.env): the certificates' passwords (without
#                              them the .pfx files cannot be opened), the database password and the
#                              rest of the deployment's settings
#   manifest.txt, SHA256SUMS   what was backed up when, and checksums restore.sh verifies
# So a backup file is as secret as deploy/.env itself: keep it where only you can read it.
#
# Settings (environment variables, all optional): BACKUP_DIR (default deploy/backups, git-ignored),
# BACKUP_KEEP (how many backups to keep, newest first; default 14; older smartagri-backup-*.tar in
# BACKUP_DIR are deleted after a successful backup), and DEPLOY_ENV_FILE, DEPLOY_CERTS_DIR,
# DEPLOY_COMPOSE_FILES, DEPLOY_PROJECT (deploy/backup-lib.sh). Works from cron: no terminal needed,
# every path is absolute.
#
# The last line is `backup: OK <file> (<size>; ...)`; any failure prints `backup: FAIL: <why>`, exits
# non-zero and leaves no partial file. Nothing secret is ever printed.
set -euo pipefail
umask 077

step() { printf 'backup: %s\n' "$*"; }
fail() {
  printf 'backup: FAIL: %s\n' "$*" >&2
  exit 1
}

case "${1:-}" in
  '') ;;
  -h | --help)
    sed -n '2,/^set -euo/p' "$0" | sed -e '$d' -e 's/^# \{0,1\}//'
    exit 0
    ;;
  *) fail "unexpected argument '$1' (deploy/backup.sh takes none; see --help)" ;;
esac

# shellcheck source=deploy/backup-lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/backup-lib.sh"

backup_dir="$(from_repo "${BACKUP_DIR:-deploy/backups}")"
keep="${BACKUP_KEEP:-14}"
case "$keep" in
  '' | *[!0-9]* | 0) fail "BACKUP_KEEP must be a whole number of at least 1, not '$keep'" ;;
esac

need_tools docker tar gzip awk
[ -f "$env_file" ] || fail "the env file does not exist: $env_file (DEPLOY_ENV_FILE)"
[ -d "$certs_dir" ] || fail "the certificate directory does not exist: $certs_dir (DEPLOY_CERTS_DIR)"
[ -f "$certs_dir/dataprotection.pfx" ] \
  || fail "$certs_dir/dataprotection.pfx is missing: without it the key ring cannot be opened"
build_compose

postgres="$(service_container postgres running)"
[ -n "$postgres" ] || fail "the postgres container is not running: start the deployment first (docker compose ... up -d)"
api="$(service_container api)"
[ -n "$api" ] || fail "the deployment has no api container: start it first (docker compose ... up -d)"
keys_volume="$(mounted_volume "$api" /app/keys)"
[ -n "$keys_volume" ] || fail "the api container mounts no volume at /app/keys (the Data Protection key ring)"
# The postgres image is already on this machine: no other image is pulled for the key volume.
helper_image="$(docker inspect -f '{{.Config.Image}}' "$postgres")"

mkdir -p "$backup_dir"
chmod 700 "$backup_dir"
stamp="$(date +%Y%m%d-%H%M%S)"
name="smartagri-backup-$stamp.tar"
target="$backup_dir/$name"
[ -e "$target" ] && fail "$target already exists (two backups in the same second?)"
work="$(mktemp -d "$backup_dir/.partial-$stamp.XXXXXX")"
cleanup() {
  local status=$?
  trap - EXIT
  rm -rf "$work"
  exit "$status"
}
trap cleanup EXIT

step "dumping the database (pg_dump in the postgres container)"
# POSTGRES_USER and POSTGRES_DB are the container's own environment, so this script never reads the
# database password. The local socket needs no password in the official image.
# shellcheck disable=SC2016 # expanded by the container's shell
"${compose[@]}" exec -T postgres sh -c 'exec pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --format=custom' \
  > "$work/$member_database" 2> "$work/pg_dump.err" || {
  tail -n 20 "$work/pg_dump.err" >&2
  fail "pg_dump failed"
}
[ -s "$work/$member_database" ] || fail "pg_dump wrote nothing"
"${compose[@]}" exec -T postgres pg_restore --list < "$work/$member_database" > "$work/toc.txt" 2> "$work/pg_restore.err" || {
  tail -n 20 "$work/pg_restore.err" >&2
  fail "the dump cannot be read back (pg_restore --list)"
}
tables="$(grep -c ' TABLE DATA ' "$work/toc.txt" || true)"
rm -f "$work/toc.txt" "$work/pg_dump.err" "$work/pg_restore.err"

step "copying the Data Protection key ring (volume $keys_volume)"
docker run --rm --network none -v "$keys_volume:/keys:ro" --entrypoint tar "$helper_image" \
  -C /keys -czf - . > "$work/$member_keys" 2> "$work/keys.err" || {
  tail -n 20 "$work/keys.err" >&2
  fail "could not read the key volume $keys_volume"
}
rm -f "$work/keys.err"
key_files="$(tar -tzf "$work/$member_keys" | grep -c 'key-.*\.xml$' || true)"
[ "$key_files" -gt 0 ] || fail "the key volume $keys_volume holds no key files (has the api ever started?)"

step "copying the certificates and the env file"
tar -C "$certs_dir" -czf "$work/$member_certs" . || fail "could not read $certs_dir"
cert_files="$(tar -tzf "$work/$member_certs" | grep -c '\.pfx$' || true)"
cp "$env_file" "$work/$member_env" || fail "could not read $env_file"

commit="$(git -C "$repo_root" rev-parse --short HEAD 2> /dev/null || echo unknown)"
{
  printf 'smartagri backup %s\n' "$stamp"
  printf 'created: %s\n' "$(date '+%Y-%m-%d %H:%M:%S %z')"
  printf 'host: %s\n' "$(hostname)"
  printf 'repository commit: %s\n' "$commit"
  printf 'compose: %s\n' "${compose[*]}"
  printf 'database: %s tables, dump %s bytes (pg_dump --format=custom)\n' \
    "$tables" "$(size_of "$work/$member_database")"
  printf 'data protection key files: %s (volume %s)\n' "$key_files" "$keys_volume"
  printf 'certificates: %s .pfx files (from %s)\n' "$cert_files" "$certs_dir"
  printf 'env file: %s\n' "$env_file"
} > "$work/$member_manifest"
(
  cd "$work"
  for member in "$member_database" "$member_keys" "$member_certs" "$member_env" "$member_manifest"; do
    printf '%s  %s\n' "$(sha256_of "$member")" "$member"
  done
) > "$work/$member_sums"

tar -C "$work" -cf "$work/$name" \
  "$member_manifest" "$member_sums" "$member_database" "$member_keys" "$member_certs" "$member_env" \
  || fail "could not write the backup file"
chmod 600 "$work/$name"
mv "$work/$name" "$target"

# Retention: only after a good backup, only files this script writes.
removed=0
while IFS= read -r old; do
  rm -f "$backup_dir/$old"
  removed=$((removed + 1))
done < <(cd "$backup_dir" && find . -maxdepth 1 -type f -name 'smartagri-backup-*.tar' | sed 's|^\./||' \
  | sort -r | tail -n +$((keep + 1)))
kept="$(find "$backup_dir" -maxdepth 1 -type f -name 'smartagri-backup-*.tar' | wc -l | tr -d ' ')"

step "OK $target ($(human_size "$(size_of "$target")"); database $(human_size "$(size_of "$work/$member_database")") with $tables tables, $key_files key files, $cert_files certificates, env file); $kept backups kept in $backup_dir (BACKUP_KEEP=$keep, $removed older removed)"
