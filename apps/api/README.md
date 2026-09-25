# apps/api

.NET 10 backend (see `docs/adr/2026-09-25-backend-stack.md` and
`docs/plans/2026-09-25-backend-milestone-1-skeleton.md`). Solution: `SmartAgri.slnx`.

```
src/SmartAgri.Domain/          entities, enums; no third-party dependencies
src/SmartAgri.Infrastructure/  AppDbContext, migrations, health checks
src/SmartAgri.Api/             Minimal API, Dockerfile, migrate subcommand
tests/SmartAgri.Domain.Tests/  unit tests, no Docker needed
tests/SmartAgri.Api.Tests/     integration tests; some need Docker (see below)
```

## Local database (colima + Docker)

Testcontainers and `deploy/docker-compose*.yml` both need a Docker daemon. On macOS
this repo uses [colima](https://github.com/abiosoft/colima) rather than Docker Desktop.

1. **Check memory headroom before starting colima** — a colima VM plus Postgres can be
   pushed into swap on a busy machine, and a starved VM makes Testcontainers time out
   in confusing ways:
   ```sh
   sysctl -n vm.swapusage
   ```
   If swap is already mostly used, free up memory (close other dev servers, browser
   tabs, etc.) before continuing.

2. **Start colima** with a modest allocation:
   ```sh
   colima start --cpu 4 --memory 4
   ```

3. **Point the Docker CLI and Testcontainers at colima's socket**:
   ```sh
   export DOCKER_HOST=unix://$HOME/.colima/default/docker.sock
   export TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock
   ```
   (`DOCKER_HOST` is what the `docker` CLI and `docker compose` use; Testcontainers
   talks to the same daemon but needs `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE` because it
   also mounts the socket into containers it starts, and colima's host-side socket path
   isn't the path the *container* sees it at.)

Both variables only need to be set in shells that run `dotnet test`, `docker compose`,
or anything else that talks to Docker.

## Running migrations locally

```sh
dotnet tool restore   # once, installs the pinned dotnet-ef from .config/dotnet-tools.json
docker compose -f deploy/docker-compose.dev.yml up -d
dotnet ef database update \
  --project apps/api/src/SmartAgri.Infrastructure \
  --startup-project apps/api/src/SmartAgri.Infrastructure
```

Running `database update` again against the same database is a no-op (migrations are
idempotent). To add a new migration:

```sh
dotnet ef migrations add <Name> \
  --project apps/api/src/SmartAgri.Infrastructure \
  --startup-project apps/api/src/SmartAgri.Infrastructure \
  --output-dir Migrations
```

`dotnet ef` never needs a running database for `migrations add` or
`migrations script` — `AppDbContextDesignTimeFactory` supplies a placeholder connection
string that is never opened at design time.

The Api itself never migrates automatically on startup. Apply migrations explicitly
with the `migrate` subcommand, then start the app normally:

```sh
dotnet run --project apps/api/src/SmartAgri.Api -- migrate
dotnet run --project apps/api/src/SmartAgri.Api
```

The customer-deploy container (`apps/api/src/SmartAgri.Api/Dockerfile`) does exactly
this in its entrypoint script before starting the web server.

## Running the customer-deploy compose file end to end

```sh
cp deploy/.env.example deploy/.env   # then set a real POSTGRES_PASSWORD
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up --build
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:8080/health/ready
```

## Running tests

Domain tests never need Docker:

```sh
dotnet test apps/api/tests/SmartAgri.Domain.Tests
```

Api tests are split by the xUnit trait `Category=Docker`. Tests carrying it use
`PostgresFixture` (Testcontainers) or otherwise need a real database; everything else
runs anywhere.

Run only the tests that don't need Docker:

```sh
dotnet test apps/api/tests/SmartAgri.Api.Tests --filter-not-trait "Category=Docker"
```

Run only the Docker-dependent tests (needs colima running, see above):

```sh
dotnet test apps/api/tests/SmartAgri.Api.Tests --filter-trait "Category=Docker"
```

Run everything (what CI does via `npx nx run-many -t test`, on ubuntu-latest runners
that ship Docker):

```sh
dotnet test apps/api/tests/SmartAgri.Api.Tests
```

(`--filter-trait`/`--filter-not-trait` are Microsoft.Testing.Platform's own flags, not
VSTest's `--filter`; this repo's `global.json` opts every test project into
Microsoft.Testing.Platform, so plain `dotnet test` already uses this runner. The
equivalent flags on the in-process xUnit v3 console runner —
`dotnet run --project ... -- -trait "Category=Docker"` / `-trait-` to exclude — also
work if you build and run the test assembly directly instead of through `dotnet test`.)
