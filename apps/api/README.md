# apps/api

.NET 10 backend (see `docs/adr/2026-09-25-backend-stack.md` and
`docs/plans/2026-09-25-backend-milestone-1-skeleton.md`). Solution: `SmartAgri.slnx`.

```
src/SmartAgri.Domain/          entities, enums; no third-party dependencies
src/SmartAgri.Infrastructure/  AppDbContext, Identity accounts, migrations, health checks
src/SmartAgri.Api/             Minimal API, sign-in (Identity + OpenIddict), Dockerfile, migrate + setup subcommands
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
   tokens. `GET /api/v1/me` →
   `{ id, displayName, role, permissions[], organization, passwordChangeRequired }`.

`/connect/endsession` and `/connect/userinfo` are also available. The access token only
carries `sub`, `org_id` and `role`; **permissions are read from the database on every
request** (one policy per permission, `RequirePermission(...)`), so permission changes
apply immediately. API endpoints never accept the cookie, and every endpoint requires a
signed-in caller unless it opts out with `AllowAnonymous()`.

Errors: `401` has no body; `403` is ProblemDetails plus `reason` and `message`, and
"not found" is the very same `403` (`ApiErrors.NotFound` = `ApiErrors.Forbidden`);
`422` is ProblemDetails plus `message` and `errors`.

**Must change password.** An account created by `setup` (below) carries
`PasswordChangeRequired`. Like permissions, the flag is read from the database on every
request, never from the token. While it is set, every protected endpoint except
`GET /api/v1/me` and `POST /api/v1/auth/change-password` answers
`403` `reason: password-change-required` (and this takes precedence over any
permission `403`). Anonymous endpoints (sign-in, `/connect/*`, health) are not gated, so
the account can still sign in and get the token it needs to change its password. New
endpoints are gated automatically; to exempt one, add `.AllowWhilePasswordChangeRequired()`
(`Authorization/PasswordChangeGate.cs`, enforced in `ApiAuthorizationResultHandler`).

`POST /api/v1/auth/change-password` `{ currentPassword, newPassword }` (bearer token) →
`204`. It clears the flag and rotates the security stamp, so the old password and any
earlier sign-in cookie stop working; the access token in hand keeps working, now
ungated, until it expires. Errors:

| Status | When |
| --- | --- |
| `422` `errors.currentPassword` | blank or wrong current password (a wrong one also counts towards lockout) |
| `422` `errors.newPassword` | blank, same as the current one, or breaks a password rule (one message per rule) |
| `401` (no body) | no usable token, account gone, or account locked out (including by this attempt) |

A wrong current password is `422`, not `401`: the caller is already authenticated, and
`401` would make the SPA drop the session over a typo. Password rules for any password
set through Identity: at least 12 characters, with an upper-case letter, a lower-case
letter, a digit and a symbol. Identity's messages are in Traditional Chinese
(`LocalizedIdentityErrorDescriber`).

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

## OpenAPI document and frontend types

`Microsoft.AspNetCore.OpenApi` plus `Microsoft.Extensions.ApiDescription.Server` write
`apps/api/openapi/v1.json` on every build of `SmartAgri.Api` (`dotnet build`, or the Nx
target `SmartAgri.Api:build`); the file is committed and is the single source
`apps/admin/src/app/core/api/api-schema.ts` is generated from by `openapi-typescript`
(Nx target `admin:api-types`). Whenever an endpoint's request or response shape changes,
regenerate both and commit them together:

```sh
dotnet build apps/api/src/SmartAgri.Api
npx nx run admin:api-types
git status   # apps/api/openapi and apps/admin/src/app/core/api should both be clean
```

CI fails on drift: after `nx run-many -t build`, it reruns `nx run admin:api-types` and
`git diff --exit-code -- apps/api/openapi apps/admin/src/app/core/api`
(`.github/workflows/ci.yml`). `apps/admin/src/app/core/api/api-schema.spec.ts`
compile-time-checks (bidirectional assignability, not just `expect()`) that the generated
`AccountRole`/`AccountPermission` unions still equal `account.model.ts`'s; a renamed wire
value on either side fails `npx nx test admin`, not just the drift check. `api-schema.ts`
is generated (never hand-edited): it is excluded from ESLint and Prettier.

Document generation runs the app's own composition root (via the design-time host that
`Microsoft.Extensions.ApiDescription.Server` invokes) up to
`WebApplicationBuilder.Build()` — it never calls `.Run()`, opens an HTTP listener, or
queries the database. `SmartAgri.Api.csproj` forces that one build-time invocation into
the `Development` environment (see the comment on `SetOpenApiGenerationEnvironment`
there), so it needs neither real token certificates nor a running Postgres; this does not
relax `TokenCredentials.Resolve`'s production check itself, which still refuses to start
outside `Development` without certificates.

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

## Development seed data

`DevelopmentSeeder` (`SmartAgri.Infrastructure.Seeding`) gives local development and E2E
the same accounts as the frontend Demo (M1 skeleton plan, Slice 6). It is registered only
in `Development` (`DevelopmentSeedingServiceCollectionExtensions.AddDevelopmentSeeding`),
so it does not exist in any other environment's container — a Production host cannot run
it even by mistake. `migrate` runs it right after applying migrations and registering the
`admin-spa` client, still only in `Development`:

```sh
export SEED_DEMO_PASSWORD='choose-a-strong-password-1!'   # optional; leave unset to skip demo seeding (see below)
dotnet run --project apps/api/src/SmartAgri.Api -- migrate
```

It creates:

| Organization | Code | Account | Role | Permissions |
| --- | --- | --- | --- | --- |
| 安心商行 | `anxin` | `admin` | `smb-admin` | `manage-assistants`, `manage-data-sources`, `manage-publishing`, `read-consented-submissions` |
| 安心商行 | `anxin` | `internal` | `internal-employee` | `use-shared-assistants`, `read-consented-submissions` |
| 安心商行 | `anxin` | `customer` | `external-customer` | `submit-authorized-forms`, `read-own-tracking` |
| 對照組織 | `control` | `admin` | `smb-admin` | same as 安心商行's `admin` |

The 安心商行 accounts' display names, roles and permissions are copied from
`apps/admin/src/app/core/repositories/demo-seed.ts` (`DevelopmentSeedDataTests` parses
that file and fails the moment the two disagree). 對照組織 exists only so a developer can
sign in as two different organizations' `admin` and confirm neither can see the other's
data; it has no frontend counterpart.

**Idempotent at the organization/account level, not the permission level.** "只補缺的"
(only fill in what is missing) means: a missing organization is created, and a missing
account is created with its full permission list from the table above. An account that
already exists is left **completely untouched** — display name, role, password and
permissions are never added to, removed, or otherwise changed, no matter what they
currently are. In particular, if an operator revokes one of these permissions by hand
(e.g. in the team panel), running `migrate` again does **not** bring it back — that
manual change survives forever, exactly as required by "不覆寫手動改過的權限" (never
overwrite a manually changed permission). The only way to reset a seeded account back to
its table permissions is to delete it and run the seeder again, which recreates it fresh.

`SEED_DEMO_PASSWORD` has no default and is optional. If it is unset or blank,
`DevelopmentSeeder` logs a warning and skips seeding entirely, before opening any database
connection — `migrate` still applies migrations, registers `admin-spa` and exits 0, just
with no demo organizations or accounts. This is deliberate: it is what lets the local
first-install flow below ("First install: `setup`") run `migrate` then `setup` against a
fresh Development database, since `setup` requires the database to have no organization
yet. Set the password only when you want the demo accounts instead of running `setup`
locally.

When it is set, the value is used as-is: `DevelopmentSeeder` hashes it with
`PasswordHasher<Account>` directly rather than going through Identity's
`UserManager`/password validators, so it is never rejected for being "too weak" — whatever
you set becomes the seeded accounts' password, unvalidated. (Identity's real rules —
`AuthenticationServiceCollectionExtensions.MinimumPasswordLength` and the rest of the
default policy — still apply the first time one of those accounts changes its own
password through the app.) A checked-in or forgotten-default demo password still can
never reach a database, because the value has no default here and must never be
committed — see `deploy/.env.example` for where to set it and
`tools/check-no-demo-secrets.sh` (run in CI) for the checks that keep a real value out of
every checked-in file.

## Running the customer-deploy compose file end to end

```sh
cp deploy/.env.example deploy/.env   # then set a real POSTGRES_PASSWORD
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up --build
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:8080/health/ready
```

## First install: `setup`

A fresh deployment has no organization and no account. The first organization and its
administrator are created once with the `setup` subcommand (on-prem-packaging ADR) —
never by seed data, a web wizard or a password in configuration:

1. Prepare `deploy/.env` (real `POSTGRES_PASSWORD`, `ADMIN_SPA_ORIGIN`, certificate
   passwords) and put `signing.pfx` / `encryption.pfx` in `deploy/certs/` (see "Sign-in
   and tokens"). `setup` builds the same host as the web server, so outside Development
   it also refuses to run without the certificates.
2. Build the image and create the schema:
   ```sh
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env build
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm api migrate
   ```
   (Optional: the container entrypoint runs `migrate` before `setup` anyway, and before
   every normal start.)
3. Run `setup` in a terminal and answer the questions (organization name, organization
   code, administrator login name, display name):
   ```sh
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm api setup
   ```
   or non-interactively (all three flags are then required; the display name defaults to
   the login name):
   ```sh
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm -T api setup \
     --organization-name "安心農場" --organization-code anxin \
     --admin-login admin --admin-display-name "王小明"
   ```
   The organization code (`a-z`, `0-9`, `-`, at most 32) is typed at every login when a
   deployment has several organizations, and **cannot be changed later**. The login
   name is ASCII (letters, digits, `- . _ @ +`, at most 64). `setup --help` prints the
   details.
4. `setup` prints a **one-time password once** and exits (it never starts the web
   server). Copy it now: it is not written to any file, log or telemetry and cannot be
   shown again. It goes to the container's stdout, which `run --rm` discards with the
   container; if the Docker daemon ships container output to a remote logging driver,
   keep this in mind.
5. Start the stack (`docker compose ... up -d`), sign in to the admin SPA as the
   administrator with the one-time password, and set a new password when asked. Until
   then the API only allows `GET /api/v1/me` and `POST /api/v1/auth/change-password`.

`setup` refuses (exit code 1, nothing changed) when any organization already exists or
when migrations are pending; bad or missing arguments exit with 2. The administrator
gets role `smb-admin` and all seven permissions. Organization, account and permissions
are written in one serializable transaction, so two concurrent runs cannot both
succeed.

Locally, against `deploy/docker-compose.dev.yml`'s database, with `SEED_DEMO_PASSWORD`
left unset so `migrate` skips the demo seed data (see "Development seed data") and the
database stays organization-less for `setup`, which refuses once any organization exists:

```sh
dotnet run --project apps/api/src/SmartAgri.Api -- migrate
dotnet run --project apps/api/src/SmartAgri.Api -- setup
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
