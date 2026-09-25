#!/bin/sh
# Customer-deploy container entrypoint (see deploy/docker-compose.yml and
# on-prem-packaging ADR): the Api never migrates on its own normal startup, so this
# script explicitly runs the `migrate` subcommand first. Either step failing aborts the
# container (set -e).
#
#   (no arguments)                       migrate, then start the web server
#   migrate                              migrate only
#   setup [...]                          migrate, then the one-shot `setup` subcommand
#                                        (docker compose run --rm api setup), which
#                                        exits when done and never starts the web server
set -eu

dotnet SmartAgri.Api.dll migrate

if [ "${1:-}" = "migrate" ]; then
  exit 0
fi

exec dotnet SmartAgri.Api.dll "$@"
