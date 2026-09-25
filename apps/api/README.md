# apps/api

.NET 10 backend (see `docs/adr/2026-09-25-backend-stack.md` and
`docs/plans/2026-09-25-backend-milestone-1-skeleton.md`). Solution: `SmartAgri.slnx`.

```
src/SmartAgri.Domain/          entities, enums; no third-party dependencies
src/SmartAgri.Infrastructure/  AppDbContext, Identity accounts, migrations, health checks
src/SmartAgri.Api/             Minimal API, sign-in (Identity + OpenIddict), Dockerfile, migrate subcommand
tests/SmartAgri.Domain.Tests/  unit tests, no Docker needed
tests/SmartAgri.Api.Tests/     integration tests; some need Docker (see below)
```

## Organization isolation

Every entity implementing `IOrganizationScoped` (Domain) is isolated automatically by
`AppDbContext` — nothing to opt into per entity:

- **Reads:** the named query filter `"Organization"` keeps rows to the current
  organization (`IOrganizationContext`, from the authenticated `org_id` claim). With no
  organization every filtered query returns nothing, never everything.
- **Writes:** `OrganizationSaveChangesInterceptor` fills `OrganizationId` on new rows and
  throws `CrossOrganizationWriteException` (before any SQL is sent) for any add, change
  or delete of another organization's row, or of any scoped row with no organization.
  `OrganizationId` is also a concurrency token, so updates/deletes by key match on it.
- **Turning the filter off** is allowed in one place only, `AccountLookup` (sign-in
  lookup by organization code + login name); a source-scanning test enforces this.

`OrganizationModelTests` fails if a new entity is neither organization scoped nor on its
short whitelist (`Organization`, Identity role tables, OpenIddict's tables). Accounts'
login names are unique per organization; Identity's internal `UserName` is stored as
`{organizationCode}/{loginName}` and is never shown.

## Sign-in and tokens

ASP.NET Core Identity holds accounts and passwords; OpenIddict (Apache-2.0) issues tokens
(authentication ADR). Only the **authorization code + PKCE** flow is enabled, for one
public client, `admin-spa`:

1. The SPA reads `GET /api/v1/auth/login-options` → `{ organizationCodeRequired }`
   (`false` only when the database has exactly one organization).
2. `POST /api/v1/auth/login` `{ organizationCode?, loginName, password }` → `204` plus
   the Identity cookie (`smartagri.signin`: HttpOnly, SameSite=Strict, 30 minutes, not
   sliding). **Every** failure is the same bodiless `401` (unknown organization code,
   unknown account, wrong password, locked account). Five consecutive wrong passwords
   lock the account for 15 minutes. `POST /api/v1/auth/logout` clears the cookie.
3. `GET /connect/authorize?...&code_challenge=...` with the cookie → redirect to
   `{origin}/auth/callback?code=...`. Without a valid cookie it redirects to
   `/login?returnUrl=<this authorize request>` (the SPA's login page; `prompt=none`
   gets `login_required` instead).
4. `POST /connect/token` (code + `code_verifier`) → a 30-minute access token (signed
   JWT) and an identity token. No refresh tokens.
5. API calls send `Authorization: Bearer <access token>`; the Api validates its own
   tokens. `GET /api/v1/me` → `{ id, displayName, role, permissions[], organization }`.

`/connect/endsession` and `/connect/userinfo` are also available. The access token only
carries `sub`, `org_id` and `role`; **permissions are read from the database on every
request** (one policy per permission, `RequirePermission(...)`), so permission changes
apply immediately. API endpoints never accept the cookie, and every endpoint requires a
signed-in caller unless it opts out with `AllowAnonymous()`.

Errors: `401` has no body; `403` is ProblemDetails plus `reason` and `message`, and
"not found" is the very same `403` (`ApiErrors.NotFound` = `ApiErrors.Forbidden`);
`422` is ProblemDetails plus `message` and `errors`.

**The `admin-spa` client** is written to the database by the `migrate` subcommand (never
on web startup), from `Authentication:AdminSpa:Origins` — each origin gets
`{origin}/auth/callback` and `{origin}/login` as redirect URIs. Development defaults to
`http://localhost:4200`. If you apply migrations with `dotnet ef database update`
instead, also run `dotnet run --project apps/api/src/SmartAgri.Api -- migrate` once so
the client exists.

**Token keys.** In `Development` the Api uses ephemeral signing/encryption keys
(regenerated on every start, so earlier tokens stop validating). In **any other
environment it refuses to start** unless both certificates are configured:

| Setting | Meaning |
| --- | --- |
| `Authentication:SigningCertificatePath` / `...Password` | PKCS#12 file whose key signs tokens |
| `Authentication:EncryptionCertificatePath` / `...Password` | PKCS#12 file whose key encrypts authorization codes |
| `Authentication:Issuer` (optional) | absolute issuer URI; defaults to the request origin |

(As environment variables: `Authentication__SigningCertificatePath`, etc.) For example,
self-signed certificates are fine for this purpose:

```sh
openssl req -x509 -newkey rsa:2048 -sha256 -days 730 -nodes -subj "/CN=smartagri-signing" \
  -addext "keyUsage=critical,digitalSignature" -keyout signing.key -out signing.crt
openssl pkcs12 -export -inkey signing.key -in signing.crt -out deploy/certs/signing.pfx
openssl req -x509 -newkey rsa:2048 -sha256 -days 730 -nodes -subj "/CN=smartagri-encryption" \
  -addext "keyUsage=critical,keyEncipherment" -keyout encryption.key -out encryption.crt
openssl pkcs12 -export -inkey encryption.key -in encryption.crt -out deploy/certs/encryption.pfx
```

`deploy/docker-compose.yml` mounts `deploy/certs/` (git-ignored) and reads the passwords
and `ADMIN_SPA_ORIGIN` from `deploy/.env`. Outside Development OpenIddict also requires
HTTPS on `/connect/*`.

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

## Traces, metrics and logs (OpenTelemetry)

The Api always registers OpenTelemetry instrumentation for ASP.NET Core, `HttpClient` and
Npgsql, plus a custom `ActivitySource("SmartAgri")`
(`SmartAgri.Domain.Observability.SmartAgriActivitySource`) for application-level spans —
see `docs/adr/2026-09-25-observability.md`. None of it is exported anywhere unless
`OTEL_EXPORTER_OTLP_ENDPOINT` is set: with it unset (the default), instrumentation still
runs (so in-process consumers like tests can see spans) but nothing leaves the process.
This keeps the customer-deploy image free of any bundled monitoring product; customers
point `OTEL_EXPORTER_OTLP_ENDPOINT` at their own collector.

To view traces locally with the [.NET Aspire dashboard](https://aspire.dev/dashboard/standalone/)
(`deploy/docker-compose.dev.yml`, dev-only — the customer-deploy compose file has no
monitoring service):

```sh
docker compose -f deploy/docker-compose.dev.yml up -d
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
dotnet run --project apps/api/src/SmartAgri.Api
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:5153/health/ready
```

Open <http://localhost:18888> → Traces. The `/health/ready` request should appear with a
child Npgsql span.

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
