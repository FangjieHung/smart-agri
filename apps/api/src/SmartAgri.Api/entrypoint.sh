#!/bin/sh
# Customer-deploy container entrypoint (see deploy/docker-compose.yml and
# on-prem-packaging ADR): the Api never migrates on its own normal startup, so this
# script explicitly runs the `migrate` subcommand first and only then starts the web
# server. Either step failing aborts the container (set -e).
set -eu

dotnet SmartAgri.Api.dll migrate
exec dotnet SmartAgri.Api.dll
