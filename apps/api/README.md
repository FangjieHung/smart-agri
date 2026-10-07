# apps/api

.NET 10 backend (see `docs/adr/2026-09-25-backend-stack.md` and
`docs/plans/2026-09-25-backend-milestone-1-skeleton.md`). Solution: `SmartAgri.slnx`.

```
src/SmartAgri.Domain/               entities (POCOs), enums; no third-party dependencies
src/SmartAgri.Application/          business rules (e.g. knowledge base visibility, sharing, upload checks), processing and embedding orchestration, retrieval (KnowledgeRetriever), job handler contract; Domain + abstraction packages only
src/SmartAgri.Infrastructure/       AppDbContext, EF mapping, Identity accounts, migrations, health checks, job claiming, text extraction, embedding clients and the model-call audit middleware, the pgvector VectorStoreCollection
src/SmartAgri.Api/                  Minimal API, sign-in (Identity + OpenIddict), background job runner/worker, Dockerfile, migrate + setup + reindex + rechunk + set-token-limit + retention-cleanup subcommands
tests/SmartAgri.Domain.Tests/       unit tests, no Docker needed
tests/SmartAgri.Application.Tests/  unit tests and the Application dependency rule, no Docker needed
tests/SmartAgri.Api.Tests/          integration tests; some need Docker (see below)
```

`SmartAgri.Application` may reference only `SmartAgri.Domain` and the abstraction packages
`Microsoft.Extensions.AI.Abstractions` / `Microsoft.Extensions.VectorData.Abstractions`
(M2 plan §3): `ApplicationDependencyTests` reads its project file and fails on anything
else, so EF Core, Npgsql, ASP.NET Core and vendor SDKs stay in Infrastructure and Api.

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
- **Turning the filter off** is allowed in three places only, all before any organization exists
  for the request: `AccountLookup` (sign-in lookup by organization code + login name),
  `PublicAssistantLookup` (an anonymous website visitor's session finding the assistant's
  organization id, nothing else; M5a #196) and `PublicWebsiteChannelLookup` (the page embedded in a
  customer's website finds an assistant's website channel by its id; it returns nothing but the
  channel's state and allowed domains; M5a #201). A source-scanning test enforces this.
- **Raw SQL** (which neither the filter nor the write guard sees) is allowed in one place
  only, `JobClaimer` (claiming background jobs across organizations, see below); the
  same source-scanning test class enforces this.
- **Background jobs** run in a scope whose organization is the job's
  (`JobOrganizationScope`, entered only by `JobRunner`), so the filter and the write
  guard apply to job handlers exactly as to requests.

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
`http://localhost:4200`. Blank entries are ignored; with none left and the admin served by the Api
(`Admin:RootPath`, see "Serving the admin"), the origin of `PublicChannels:PublicBaseUrl` is used. If you apply migrations with `dotnet ef database update`
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
and the optional `ADMIN_SPA_ORIGIN` from `deploy/.env`. Outside Development OpenIddict also requires
HTTPS on `/connect/*`. The api container runs as a non-root user (uid 1654 in the .NET
images), so every `.pfx` in `deploy/certs/` must be readable by it — `openssl` writes them
`600` for the user who ran it, so `chmod 644 deploy/certs/*.pfx` (the passwords stay in
`deploy/.env`); otherwise the api refuses to start because it cannot read the certificate.

## Data Protection key ring

ASP.NET Core Data Protection encrypts three things in the Api: the Identity sign-in cookie,
OpenIddict's tokens and (from M5a) stored secret settings such as a LINE channel secret. They
all use one **key ring** of key files, which must live outside the database and survive a
container rebuild — otherwise every rebuild signs everyone out and makes every stored secret
unreadable (secrets-storage ADR).

| Setting | Meaning |
| --- | --- |
| `DataProtection:KeysPath` | directory the key files are written to (compose: `/app/keys`, the named volume `smartagri-dataprotection-keys`) |
| `DataProtection:CertificatePath` / `...Password` | PKCS#12 file whose key encrypts the key files (compose: `/app/certs/dataprotection.pfx`, i.e. `deploy/certs/dataprotection.pfx`, password in `DATA_PROTECTION_CERTIFICATE_PASSWORD`) |

In **any environment except `Development` and `Testing` the Api refuses to start** without both, or
when the directory cannot be created and written to. `Development` needs nothing: ASP.NET Core's
default (a per-user folder outside the repository, keys not encrypted) is used unless you set the
options. The application name is fixed to `SmartAgri`. Create the certificate like the token ones
(self-signed is fine):

```sh
openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes -subj "/CN=smartagri-dataprotection" \
  -addext "keyUsage=critical,keyEncipherment" -keyout dataprotection.key -out dataprotection.crt
openssl pkcs12 -export -inkey dataprotection.key -in dataprotection.crt -out deploy/certs/dataprotection.pfx
```

The image creates `/app/keys` owned by the non-root `app` user, so a fresh named volume is
writable. A bind mount (a host directory instead of the named volume) must be writable by that
user's uid (`$APP_UID`, 1654 in the .NET images); the Api names the path and refuses to start if it
is not.

**Back up the key volume and the certificate together with the database**, from the same point in
time as far as you can: the database holds ciphertext that only these keys can open. For example:

```sh
docker run --rm -v smartagri_smartagri-dataprotection-keys:/keys:ro -v "$PWD":/backup alpine \
  tar czf /backup/dataprotection-keys.tgz -C /keys .
```

(The compose file's project name is `smartagri` and the volume is declared as
`smartagri-dataprotection-keys`, so Docker's volume name is `smartagri_smartagri-dataprotection-keys`
— `docker compose -f deploy/docker-compose.yml --env-file deploy/.env config --volumes` shows the
declared name, `docker volume ls` the real one. Use the real one: `-v smartagri-dataprotection-keys:...`
would silently create and back up a new, empty volume.) Keys are rotated automatically every 90 days;
the old ones stay in the directory and keep decrypting old data, so never delete files from it.

**If the keys or the certificate are lost** the data is not recoverable: every stored secret
setting (LINE credentials, an organization's own model API key) must be entered again, every
sign-in cookie and visitor token stops working (people sign in again, visitors reopen the chat
window), and nothing else is affected. A new certificate cannot open an old key ring either: replace
the certificate only together with the key files, or accept the same loss.

Secret settings are **write-only**: the Api stores them with `ISecretProtector` (one purpose string
per field) and only ever returns `SecretStatusView { configured, lastFour, updatedAt }`; no
endpoint returns the value, and `lastFour` is null for a value shorter than four characters.

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

## Background jobs

Background work (document processing from M2 on, later periodic reports) runs on a
PostgreSQL table used as a queue, inside the Api process
(`docs/adr/2026-09-25-background-jobs-on-postgresql.md`, M2 plan Slice 4):

- **Enqueue** by adding a row in the same save as the business rows it is about, so both
  commit or neither does:
  `dbContext.BackgroundJobs.Add(BackgroundJob.Create(organizationId, kind, payload, clock.GetUtcNow()))`.
  Payloads are small JSON (ids, never document content).
- **Handle** a kind by implementing `IJobHandler` (Application) and registering it with
  `builder.Services.AddJobHandler<THandler>("kind")`. Each run gets a fresh scope acting
  for the job's organization. Delivery is at least once, so handlers must be idempotent.
- **Claiming** (`JobClaimer`) is one `UPDATE … WHERE id = (SELECT … FOR UPDATE SKIP LOCKED
  LIMIT 1) RETURNING …`: concurrent runners never get the same job, and a job still
  `running` after its lease (`Jobs:LeaseDuration`) expired — its process stopped — is
  claimed again. Only kinds with a registered handler are claimed.
- **Failures:** throwing `PermanentJobFailure` (or a `CrossOrganizationWriteException`)
  fails the job at once; any other exception retries it with exponential backoff until
  its `MaxAttempts`. On failing for good, the handler's `OnFinalFailureAsync` runs in the
  same transaction as marking the job `failed`. `LastError` keeps the latest error.
- **Telemetry:** one `smartagri.job` span per run (tags `smartagri.job.kind`,
  `smartagri.job.attempt`, `smartagri.job.outcome`, `smartagri.organization_id`) and the
  gauge `smartagri.jobs.queued` (per kind, refreshed every 15 s by the worker).

Configuration (section `Jobs`, e.g. `Jobs__Concurrency=2` as an environment variable):

| Key | Default | |
| --- | --- | --- |
| `WorkerEnabled` | `true` | Run `JobWorker` in this process. |
| `PollInterval` | `00:00:02` | Wait after finding nothing to claim. |
| `Concurrency` | `1` | Jobs run at the same time by this process. |
| `LeaseDuration` | `00:10:00` | Must exceed any handler's run time. |
| `RetryBaseDelay` / `RetryMaxDelay` | `00:00:30` / `00:30:00` | Backoff: base, doubling, capped. |

Integration tests turn the worker off (`AuthHostFixture`, and for every other test host
`TestHostDefaults` sets `Jobs__WorkerEnabled=false`, so no test ever processes jobs in the
database `appsettings.Development.json` names) and call `JobRunner.RunUntilIdleAsync()`
themselves, moving the test clock for backoff and leases.

## Knowledge documents

Owner-only endpoints under `/api/v1/knowledge-bases/{id}/documents` (M2 plan, Slice 5; new
versions, approval and disabling in "Versions, approval and emergency disable" below):

| Endpoint | Result |
| --- | --- |
| `POST .../documents` (multipart: `file`, optional `batchId`) | `201` `KnowledgeDocumentView` (`queued`) |
| `POST .../documents/{docId}/versions/{versionId}/retry` | `200` `KnowledgeDocumentView`; `409` unless the version is `failed` |
| `GET .../documents/{docId}/versions/{versionId}/file` | the original bytes, stored content type, `Content-Disposition: attachment` with `filename*` |
| `DELETE .../documents/{docId}` | `204`; the document, its versions, their files, units and chunks go in one transaction |

Uploads accept one file per request: `.pdf`, `.docx`, `.xlsx`, `.txt`, `.md`, at most
`Knowledge:MaxFileBytes`. Refusals are ProblemDetails with a `reason`: `413`
`file-too-large`; `415` `unsupported-file-type` or `file-content-mismatch` (a PDF must start
with `%PDF-`, a DOCX/XLSX must be a ZIP with `word/document.xml`/`xl/workbook.xml`);
`422` `duplicate-content` (same SHA-256 already in the knowledge base, with
`existingDocumentName`), `duplicate-name` (upload a new version instead), and
`file-missing`/`too-many-files`/`invalid-file-name`/`invalid-batch-id`. The rules are
`KnowledgeUploadRules` (Application); unique indexes enforce both duplicate rules in the
database too. An accepted upload writes the document, version 1, the original file
(`KnowledgeFileContents`, `bytea`, a table only downloads and processing read — see
`docs/adr/2026-09-26-original-files-in-postgresql.md`; the database and its backups grow
with the files), an activity row and a `knowledge.process-version` job in one save.

| Key | Default | |
| --- | --- | --- |
| `Knowledge:MaxFileBytes` | `20971520` (20 MB) | Largest accepted file, at most 256 MB. |

The upload endpoint's request body limit is `MaxFileBytes` plus 64 KB for the multipart
envelope, set per endpoint (Kestrel refuses a larger body with `413` before buffering it;
other endpoints keep Kestrel's default). **A reverse proxy in front of the Api must allow at
least as much**, or it answers with its own `413` page before the Api sees the request —
for nginx, e.g. `client_max_body_size 21m;` for the default. Raise both together.

### Text extraction, readability and chunks

The `knowledge.process-version` job (`ProcessKnowledgeVersionHandler`, M2 plan Slice 6) reads
each uploaded version and writes what it read, in one transaction with the version's status
(and, since Slice 7, every chunk's vector — see "Embeddings and vector search"):

- **Units** (`KnowledgeExtractedUnits`): a PDF page (PdfPig), a DOCX section at Heading 1–3
  (Open XML SDK; label = heading path, e.g. 「2 退換貨 › 2.1 退貨條件」; table rows as
  `cell | cell`), an XLSX worksheet (cells as Excel displays them; hidden sheets skipped), a
  Markdown section at `#`–`###`, or a whole TXT file. TXT/MD must be UTF-8 (a BOM is fine).
- **Readability** (`KnowledgeReadability`): a PDF page with fewer than 10 characters, or any
  unit more than 30% U+FFFD/private-use/control characters, is unreadable and gets no chunks.
  All readable → `ready`; some → `partially-readable` (the issue lists the pages); none →
  `failed` (「找不到可讀文字，可能是掃描檔；目前不支援 OCR」). No OCR.
- **Chunks** (`KnowledgeChunks`, `KnowledgeChunker`): never across a unit; about 600, at most
  1000 characters (Unicode scalars, so one Chinese character is one), 100 overlapping. Each
  worksheet chunk is whole rows headed by the sheet's header row, labelled
  「工作表『配送時間』第 2–30 列」.
- **Tables** in a Markdown or DOCX section (#301): every data row is a chunk of its own, one
  `欄名：值` line per non-empty cell (「項目：電話\n內容：(03) 012-3456」), labelled with the
  section's heading path like the rest of the section, whose text outside its tables is chunked
  exactly as before. A Markdown table is a GitHub-style pipe table with its `| --- |` delimiter
  row (without one, or inside fenced code, it stays text; a table with only a header row gives
  nothing); a DOCX table needs two non-empty rows, the first naming the columns (a one-row
  table stays text). The unit's text, which the preview shows, keeps the table as written. A
  short question (「電話幾號？」) matches one row far better than a whole table (#292).
- **Chunk format** (`KnowledgeDocumentVersions.ChunkFormat`, `KnowledgeChunkFormat`): which
  chunking rules cut a version's chunks — `1` before #301 (tables inside their section's text;
  what the migration gives every existing version), `2` with table rows, `3` (#324) with each
  row chunk's table recorded (`KnowledgeChunks.TableIndex`: which table of its unit, `null` for
  every other chunk), so an answer can send a retrieved row's whole table (see "Retrieval").
  Processing writes the current one; `rechunk` ("Changing the chunking rules: `rechunk`" below)
  brings older ones up to date.
- A password-protected PDF, non-UTF-8 text (e.g. Big5) or a damaged file fails the job at once
  (`PermanentJobFailure`, one attempt) with an issue telling the owner what to do.

The job is idempotent: a version already processed is left alone, and a reprocessed one (after
a retry) has its units and chunks replaced, never added to. Units and chunks are deleted with
their version, document and knowledge base (database cascades).

| Endpoint | Result |
| --- | --- |
| `GET .../documents/{docId}/versions/{versionId}/preview` | `200` `KnowledgeVersionPreviewView`: status, issue, units in order (location, readable, issue code, text) with their chunks (text, excluded) |
| `PUT .../documents/{docId}/versions/{versionId}/chunks/{chunkId}/exclusion` `{ excluded }` | `200` `KnowledgeChunkView`; a changed value writes a `chunk-excluded`/`chunk-included` activity row (chunk id only); `422` without `excluded` |

Both are owner only, with the same `403 knowledge-base` for everything else. Test fixtures and
how they were made: `tests/fixtures/knowledge/README.md`.

| Key | Default | |
| --- | --- | --- |
| `Knowledge:MaxExtractedUnits` | `2000` | Pages, sections or worksheets read per file; more makes the version `partially-readable`. |
| `Knowledge:MaxSheetRows` | `5000` | Data rows read per worksheet; more makes the version `partially-readable`. |

### Versions, approval and emergency disable

Processed `ready` only means the text could be read. **Nothing is retrievable until a person
approves it** — every version, version 1 included (M2 plan Slice 8, §7 decision 4). The rule,
in one place (`RetrievableChunks`, Application):

> a chunk is retrievable when it is not excluded **and** its version is its document's
> *current effective version* — approved, processed `ready`/`partially-readable`, and the one
> with the latest `EffectiveFrom ≤ now` among those (the higher version number on a tie) —
> **and** the document is not disabled **and** its `EmbeddingModel` is the configured model.

`EffectiveFrom ≤ @now` is part of the SQL, so a version scheduled for tomorrow takes over
tomorrow with nothing to run. "Scheduled", "effective" and "archived" are derived, never
stored; the database stores only `ReviewState` (`pending-review`/`approved`, a concurrency
token), `EffectiveFrom`, `ApprovedByAccountId`, `ApprovedAt` on versions and `DisabledAt`,
`DisabledByAccountId`, `DisabledReason` on documents (check constraints keep each group
consistent; a partial index on approved versions by document and `EffectiveFrom` serves the
rule).

| Endpoint | Result |
| --- | --- |
| `POST .../documents/{docId}/versions` (multipart: `file`, optional `batchId`) | `201` `KnowledgeDocumentView`; the upload checks and processing of a new document, `pending-review`; the document keeps its name, the version its file name; `422 duplicate-content` when any version in the knowledge base has the same bytes; `409 concurrent-version-upload` if another version took the number |
| `POST .../versions/approve` `{ versionIds, effectiveFrom? }` | `200` the approved versions; all or nothing: `422 versions-not-approvable` with `errors["versionIds[i]"]` for each entry not in this knowledge base, not processed readable, or already approved |
| `POST .../documents/{docId}/disable` `{ reason }` | `200` `KnowledgeDocumentView`; retrieval stops with this save; `422` without a reason (max 500), `409 document-already-disabled` |
| `POST .../documents/{docId}/enable` | `200`; back to exactly the state before (approvals made meanwhile included); `409 document-not-disabled` |
| `GET .../documents/{docId}` | `200` `KnowledgeDocumentDetailView`: the list row, disable details, versions newest first (processing status, `state` = `pending-review`/`scheduled`/`effective`/`archived`, uploader, approver, times) and the document's activity log |

`effectiveFrom` is ISO 8601 **with a time zone** (without one it would silently be read in the
server's zone, so it is refused), stored in UTC, and defaults to now. It may not be earlier than
now, with **one minute of tolerance for clock skew**: a time up to a minute in the past is
taken as now. Every approval, upload, disable and enable writes an activity row naming the
caller; the disable row also keeps the owner's reason, which the document clears once enabled
again, so the log can still say why it was stopped. All owner
only, with the same `403 knowledge-base` as everything else.

Lists and details show both questions: `statusCounts` stays the latest version's processing
status, and `inEffectCount` (a version in effect and not disabled — the document level of the
same rule), `awaitingApprovalCount` (latest version processed and pending) and
`disabledCount` sit next to it; each document row has `latestVersionState`,
`effectiveVersionNumber`, `disabled` and `inEffect`.

### FAQ entries

Hand-written Q&A (M2 plan Slice 10), under the same approval and retrieval rules as documents.
An FAQ entry is a `KnowledgeDocuments` row of `kind` `faq`; every write of its question and
answer is a new version, **pending review like every version**, so an edited answer only
replaces the one in effect once the owner approves it (`includePending` previews it first).

| Endpoint | Result |
| --- | --- |
| `POST .../faqs` `{ question, answer }` | `201` `KnowledgeFaqView` (the list row, `latest` and `effective` question/answer); version 1, `pending-review` |
| `GET .../faqs/{docId}` | `200` `KnowledgeFaqView`; `effective` is `null` while no version is in effect |
| `PUT .../faqs/{docId}` `{ question, answer }` | `200` `KnowledgeFaqView`; a new version, `pending-review`, while `effective` keeps answering; `409 concurrent-version-upload` if a concurrent change won |
| `DELETE .../faqs/{docId}` | `204`; exactly like deleting a document, recorded as `faq-deleted` |

- **Storage.** The content is stored like a file: canonical UTF-8 JSON
  `{"question":…,"answer":…}` in `KnowledgeFileContents`, content type
  `application/vnd.smartagri.faq+json` (`KnowledgeFaqEntry`). So an FAQ version has a size and a
  SHA-256 like any other, and the database's per-knowledge-base SHA-256 and name indexes apply
  unchanged.
- **Rules** (`KnowledgeFaqRules`): the question becomes one line and the answer keeps its lines,
  both normalized like extracted text (NFC, `\n`, trimmed); `422` for a missing or blank field,
  a question over 500 or an answer over 4000 characters. Then, content first, `422
  duplicate-content` (with `existingDocumentName`; an unchanged edit or one back to an older
  version included) and `422 duplicate-name`. The entry is listed under its latest question,
  cut to 255 characters with 「…」, and names are unique per knowledge base across documents and
  FAQ entries, so the same question twice is `duplicate-name`.
- **Processing.** Nothing to parse, but the `knowledge.process-version` job still runs, so
  embedding, retries and failures are a document's: one unit and one chunk located 「FAQ」
  (`KnowledgeFaqProcessing`), text 「問：…\n答：…」 so a question can match either, always
  `ready`.
- Everything else is the documents' endpoints, which take an FAQ entry as the document it is:
  history, approval, disable/enable, extraction preview, chunk exclusion, retry, the list and
  `faqCount`. Only `POST .../documents/{docId}/versions` refuses one (a file never becomes an
  FAQ version) with the same `403 knowledge-base` as an FAQ path given a document's id.
  Activity rows are content-free: `faq-created`, `faq-updated`, `faq-deleted`.

## Embeddings and vector search

Processing embeds every chunk (M2 plan Slice 7; llm-providers and postgresql-as-single-store
ADRs). Code only ever sees `IEmbeddingGenerator<string, Embedding<float>>`
(`Microsoft.Extensions.AI`) and `VectorStoreCollection<Guid, KnowledgeChunk>`
(`Microsoft.Extensions.VectorData`); the provider is configuration.

- **Where vectors live:** `KnowledgeChunks.Embedding`, a pgvector `vector` column **without a
  fixed dimension**, next to `EmbeddingModel` (the configured model that produced it). A chunk
  and its vector are written — and deleted — in one transaction. Search is exact cosine
  distance over the current organization's chunks of the configured model; there is no HNSW
  index in M2.
- **Pipeline:** after chunking, chunks are embedded in batches of `Ai:Embedding:BatchSize`
  (one model call per batch) before the version completes, so a version is never `ready`
  without vectors. A section's heading path or a worksheet's name and rows is embedded as the
  first line of its chunk (a page number is not). Every chunk is embedded, excluded ones too,
  so including a chunk again needs no model call. A failing call is retried by the job queue
  (backoff as in "Background jobs"); after the last attempt the version is `failed` with
  「嵌入模型暫時無法使用，請稍後重試」.
- **Audit:** every model call goes through `ModelInvocationRecordingEmbeddingGenerator`, which
  writes one `ModelInvocations` row — organization, account (the uploader during processing,
  the asker for a question, none for `reindex` and `rechunk`), assistant (M3), purpose (`embed-document`/`embed-query`), provider,
  model, input tokens when the provider reports them, duration, success, time — and **never
  any content**. A call without an organization or an attribution is refused before it reaches
  the provider. It also emits one client span `embeddings {model}` with `gen_ai.*` attributes
  (OpenTelemetry GenAI conventions) plus `smartagri.organization_id`, and the
  `gen_ai.client.operation.duration` / `gen_ai.client.token.usage` histograms.
- **`VectorStoreCollection<Guid, KnowledgeChunk>`** (`KnowledgeChunkVectorCollection`, scoped):
  `SearchAsync` (a vector — `ReadOnlyMemory<float>`, `float[]` or `Embedding<float>` — not
  text; `Filter` is an EF `Where`, and the organization filter always applies; `Score` is
  cosine similarity; `Top`, `Skip`, `ScoreThreshold`), `GetAsync` (by key or keys),
  `UpsertAsync`, `DeleteAsync`. Everything else throws `NotSupportedException`.
- **Retrieval always passes the eligibility filter** ("Versions, approval and emergency
  disable"): `SearchAsync(vector, top, new() { Filter = RetrievableChunks.InKnowledgeBase(knowledgeBaseId, clock.GetUtcNow(), settings.Model) })`
  — or, simply, search through `KnowledgeRetriever` ("Retrieval preview" below), which does it.
  EF follows `KnowledgeChunk.Version` and `.Document` (query-only navigations, never loaded
  by search) into inner joins and a `NOT EXISTS` over the document's later approved versions,
  inside the same exact-search query. Without that filter a search also returns unapproved,
  archived, scheduled and disabled content.

Configuration (section `Ai:Embedding`; as environment variables `Ai__Embedding__Provider`, …):

| Key | Default | |
| --- | --- | --- |
| `Provider` | (none) | `OpenAI`, `AzureOpenAI`, `OpenAICompatible` or `Fake`. |
| `Model` | | Required with a provider. For Azure OpenAI, the deployment name. |
| `Endpoint` | | Required for `AzureOpenAI` (the resource's v1 endpoint, `https://{resource}.openai.azure.com/openai/v1/`) and `OpenAICompatible` (e.g. `http://vllm:8000/v1`); optional for `OpenAI`. |
| `ApiKey` | | Required for `OpenAI` and `AzureOpenAI`; optional for `OpenAICompatible`. Environment only, never a checked-in file. |
| `DocumentPrefix` / `QueryPrefix` | empty | Put before every chunk / question, for models that need it (the e5 family: `passage: ` / `query: `). |
| `BatchSize` | `64` | Chunks per model call, 1–2048. |

All three real providers use the official `OpenAI` client (`Microsoft.Extensions.AI.OpenAI`);
Azure OpenAI's v1 endpoint accepts it with an API key, so `Azure.AI.OpenAI` is not needed.
**Anthropic has no embeddings API**: a deployment that answers with Claude (M3) still needs one
of these providers for embeddings.

- **`Fake`** gives deterministic hash vectors (characters and character pairs, seeded with the
  model name; one "token" per character) and calls nothing. It is **allowed only when
  `ASPNETCORE_ENVIRONMENT` is `Development` or `Testing`**: with any other environment the Api
  refuses to start (and `reindex` refuses to run), saying why. `appsettings.Development.json`
  uses it (`fake-dev`), so local development needs no key; set `Ai__Embedding__Provider`,
  `Ai__Embedding__Model` and `Ai__Embedding__ApiKey` in your shell to try a real model.
- **No provider** is allowed: the Api starts (and logs a warning), sign-in and uploads work,
  and each processing job fails its attempts until the version is `failed` with
  「系統尚未設定嵌入模型，請聯絡系統管理員設定後重試」. Configure a provider, restart, then retry the
  versions. Anything configured but unusable (unknown provider, missing model, key or
  endpoint) refuses to start.

### Changing the embedding model: `reindex`

Vectors of different models are never compared: after `Ai:Embedding:Model` changes, search
finds none of the old vectors until they are re-embedded. Deploy the new configuration, then:

```sh
dotnet run --project apps/api/src/SmartAgri.Api -- reindex                      # every organization
dotnet run --project apps/api/src/SmartAgri.Api -- reindex --organization anxin --batch-size 32
docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm api reindex
```

It re-embeds every chunk whose `EmbeddingModel` is not the configured one (including chunks
with no vector yet, e.g. processed before this existed), organization by organization, each
through a context acting for that organization, and prints progress per batch. Each batch is
saved as it completes and only the vector columns are written, so the Api can keep running and
an interrupted run can simply be started again. Exit codes: `0` done, `1` failed (e.g. the
model is unreachable; what was saved stays), `2` bad arguments or unusable configuration.
Until it has finished, the retrieval preview (and M3's answers) find nothing of the chunks not
yet re-embedded. Similarity scores are model-specific too: set `Retrieval:MinScore` for the new
model ("Retrieval preview" below).

### Changing the chunking rules: `rechunk`

When the chunker changes what it cuts from a file already stored (`KnowledgeChunkFormat.Current`
raised — #301's table rows), versions processed before keep their old chunks until they are cut
again from their original files. After deploying, preview, then run:

```sh
dotnet run --project apps/api/src/SmartAgri.Api -- rechunk --dry-run          # what it would do; writes nothing
dotnet run --project apps/api/src/SmartAgri.Api -- rechunk                    # every organization
dotnet run --project apps/api/src/SmartAgri.Api -- rechunk --organization anxin
docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm api rechunk --dry-run
```

It takes every `ready` or `partially-readable` version whose `ChunkFormat` is older than the
current one, organization by organization (each through a context acting for that organization,
like `reindex`), reads its stored file with today's extractors and chunker, and compares unit by
unit (`KnowledgeRechunking`):

- A unit whose chunks come out the same keeps them — ids (which citations point back to),
  vectors and exclusions — so a PDF, a worksheet or a section without tables costs no model call;
  such a version only has its format updated. A format-`2` version's table rows are cut the same
  way, so they only get their table index filled in, in the same transaction (「補上 N 個段落的表格序號」),
  also without a model call.
- A changed unit's chunks are embedded with the configured model (in `Ai:Embedding:BatchSize`
  batches, recorded as the organization's `embed-document` calls with no account), **before**
  any transaction, so the old chunks keep serving meanwhile. Then one repeatable-read transaction
  deletes that unit's old chunks, inserts the new ones and sets the format; it first checks the
  version is still in the old format and the chunks are still the ones planned with, so an
  owner's exclusion made at that moment fails the swap (reported, run it again) instead of being
  lost.
- **Exclusions carry over** where they can be mapped: a new chunk with the same text as an
  excluded one, or whose every line (for a table row, every value) is inside an excluded old
  chunk of the same unit — so excluding a whole table's section keeps all its rows excluded. An
  excluded chunk nothing maps to is listed with 「！」 (location, ordinal and id, never its text);
  the new chunks are not excluded, so check them in the extraction preview.
- A version whose file now reads differently from what is stored (other units or another status,
  e.g. after `Knowledge:MaxExtractedUnits` changed) is skipped, listed with 「！」, and stays in the
  old format.

`--dry-run` prints the same per-version lines (chunks before → after, how many would be
embedded, exclusions carried over or not) and calls no model. Running it again after it finished
does nothing. Exit codes: `0` done, `1` stopped (e.g. the model is unreachable; versions finished
stay finished) or a version was left in the old format, `2` bad arguments or unusable
configuration. Run `reindex` first if the embedding model changed too: `rechunk` leaves unchanged
chunks' vectors alone.

## Chat model

Conversations answer through `IChatClient` (`Microsoft.Extensions.AI.Abstractions`; M3 plan,
Slice 4), the same shape as "Embeddings and vector search" above — configuration section
`Ai:Chat`, its own `ChatClientProvider`/`ChatModelOptions`, and its own recording middleware.
There is no public conversation endpoint yet (that is Slice 7); this section documents the
plumbing so later slices only ever call `IChatClient`.

- **Audit:** every model call goes through `ModelInvocationRecordingChatClient`, which writes one
  `ModelInvocations` row — organization, account, assistant, purpose (`generate-answer`),
  provider, model, input **and output** tokens when the provider reports them, duration, success,
  time — and **never any content**. It wraps both `GetResponseAsync` and
  `GetStreamingResponseAsync`; for a streaming call, tokens come only from a `UsageContent` update
  the provider actually sends (never estimated from the text seen so far), and a call cancelled
  mid-stream is still recorded (`Succeeded = false`). A call without an organization or an
  attribution is refused before it reaches the provider. It also emits one client span
  `chat {model}` with `gen_ai.*` attributes plus `smartagri.organization_id`, and the
  `gen_ai.client.operation.duration` / `gen_ai.client.token.usage` histograms (shared with
  embeddings).
- **Errors:** an unconfigured deployment throws `ChatGenerationException` (`ProviderNotConfigured`
  = true) on every call, before reaching any provider; a configured provider's own failure is
  wrapped the same way (`ProviderNotConfigured` = false). `SmartAgri.Api.Ai.ChatErrors.ToApiResult`
  maps either to `503` with reason `chat-not-configured` / `chat-unavailable` — the same shape as
  the embedding pipeline's `embedding-not-configured` / `embedding-unavailable`.

Configuration (section `Ai:Chat`; as environment variables `Ai__Chat__Provider`, …):

| Key | Default | |
| --- | --- | --- |
| `Provider` | (none) | `OpenAI`, `AzureOpenAI`, `OpenAICompatible` or `Fake`. |
| `Model` | | Required with a provider. For Azure OpenAI, the deployment name. |
| `Endpoint` | | Required for `AzureOpenAI` and `OpenAICompatible`; optional for `OpenAI`. |
| `ApiKey` | | Required for `OpenAI` and `AzureOpenAI`; optional for `OpenAICompatible`. Environment only, never a checked-in file. |
| `MaxOutputTokens` | (provider default) | Applied to every call that does not set its own, through `ChatClientBuilder.ConfigureOptions` (the full `Microsoft.Extensions.AI` package — the only thing Infrastructure needs it for). |
| `TimeoutSeconds` | (client default) | Applied to the OpenAI client's `NetworkTimeout`. |
| `ReasoningEffort` | (provider default) | `None`, `Low`, `Medium`, `High` or `ExtraHigh`; applied to every call that does not set its own (`ChatOptions.Reasoning`). Some reasoning models refuse function tools on Chat Completions unless it is `None` (M4 #164: `gpt-6-luna` answers HTTP 400), which would break the #149 query tools and the #164 form tool. |
| `Id` | `Model` | The model's id in the list below (what an organization's choice is stored as, M6). Letters, digits, `-`, `_`, `.`, at most 64; a single `Ai:Chat` keeps any model name as its id. |
| `DisplayName` | `Model` | The name people see in the settings. |
| `Models` | (none) | More chat models the deployment offers (`Ai:Chat:Models:0:Provider`, … / `Ai__Chat__Models__0__Provider`, …), each with all the keys above. |

**More than one model (M6-1, #238).** `Ai:Chat` itself is the **deployment default**;
`Ai:Chat:Models` lists the others, each with its own provider, key, `MaxOutputTokens`,
`ReasoningEffort` and `TimeoutSeconds`. An entry with a blank `Provider` is a placeholder and is
skipped. With any other entry, startup refuses a blank `Ai:Chat:Provider`, two entries with the
same id (ids default to the model name, so two entries of one model need their own `Id`), and
every rule above per entry (`Fake` only in Development/Testing, keys, endpoints). The startup log
lists each model's id, provider and model (never a key). `ChatModelCatalog` holds them;
`IOrganizationChatModelResolver` (scoped) says which one the scope's organization uses, and the
scoped `IChatClient` sends every call there and records that model in `ModelInvocations`. The
resolver reads the organization's `ChatModelId` once per scope (M6-2), so a manager's change
applies to the next answer, test run or report; an id the deployment no longer offers falls back
to the default (`source: removed`) until the manager chooses again. The evaluation commands
(`eval-answers`, `eval-form-requests`) always use the deployment default.

**Choosing the organization's model (M6-2, #239).**

- **`GET /api/v1/organization/chat-model`** (any signed-in account of the organization) returns
  `{ options: [{ id, displayName, model }], selectedId, effective, source, canChange, lastChange,
  revision }`. `options` never carries a key, endpoint or provider settings; `effective` is `null`
  only when the deployment has no chat model; `source` is `selected`, `deployment-default` or
  `removed`; `canChange` is whether the caller is the manager; `lastChange` is
  `{ actorName, at }` (「已停用的帳號」 when the account can no longer be found).
- **`PUT /api/v1/organization/chat-model`** `{ modelId, revision }` — manager only: anyone else gets
  `403 organization-settings`. `modelId: null` goes back to the deployment default; an id not in
  `options` is `422` (`errors.modelId`); a stale `revision` is `409 organization-settings-conflict`;
  the same value again is `200` without writing anything. A change bumps
  `Organizations.SettingsRevision` and writes one `OrganizationActivities` row
  (`chat-model-changed`, detail `{ from: { id, displayName }, to: { id, displayName } }`).
- **The manager check** (`RequireOrganizationAdmin(ForbiddenReason)`, `Authorization/OrganizationAdmin.cs`)
  is the role `smb-admin`, read from `Accounts` on every request — never from the token's `role`
  claim — so changing a role in the database takes effect with the same access token. It is a role,
  not a permission: `manage-assistants` holders who are not managers cannot change organization
  settings. Code that only needs to know asks the scoped `RequestAccountRole`.

- **`Fake`** (`FakeChatClient`) is a scripted, reproducible answer generator, no model and no
  network. It reads the highest `[n]` passage number anywhere in the messages it is given and
  answers citing `[1]`, unless the last user message contains one of these test instructions
  (`SmartAgri.Application.Ai.FakeChatDirectives`), used by Slice 5's integration tests to exercise
  every validation branch:

  | Directive | Answers with |
  | --- | --- |
  | *(none)* | A generic sentence citing `[1]` (or nothing, if no passage was supplied). |
  | `#invalid-citation` | A citation past the last supplied passage (`[k+1]`). |
  | `#no-marker` | Prose with no `[n]` citation at all. |
  | `#cannot-answer` | Exactly `SmartAgri.Application.Ai.ChatAnswerMarkers.CannotAnswer`. |
  | `#fail-midway` | One streamed chunk, then a thrown exception (fails outright when not streaming). |

  Streaming always splits the answer into at least two chunks, and — whenever it contains a `[n]`
  marker — splits the marker itself across two chunks (right after its `[`), so the citation
  scanner is exercised against a marker that arrives in pieces. It is **allowed only when
  `ASPNETCORE_ENVIRONMENT` is `Development` or `Testing`**, exactly like the fake embedding
  provider: any other environment refuses to start. `appsettings.Development.json` uses it
  (`fake-chat-dev`).
- **No provider** is allowed: the Api starts (and logs a warning); every call throws
  `ChatGenerationException(ProviderNotConfigured: true)` until one is configured.
- **Real providers not yet verified end to end**: `ChatClientProvider`'s OpenAI-style construction
  mirrors `EmbeddingProvider`'s (same official `OpenAI` client, same v1-endpoint approach for Azure
  OpenAI), but confirming a real OpenAI streaming response actually carries usage — and that
  `GetStreamingResponseAsync`'s `UsageContent` update surfaces it — needs a real API key. Until
  that is verified, treat streaming usage from a real provider as **unconfirmed**; the middleware
  already handles "no usage reported" correctly (records `null`, never estimates) either way.

**Customer-deploy compose.** `deploy/docker-compose.yml` passes the chat model and related settings
through from `deploy/.env` (see `deploy/.env.example`): `CHAT_PROVIDER`, `CHAT_ENDPOINT`, `CHAT_MODEL`,
`CHAT_API_KEY`, `CHAT_MAX_OUTPUT_TOKENS`, `CHAT_TIMEOUT_SECONDS`, `CHAT_REASONING_EFFORT` →
`Ai__Chat__*`; `CHAT_FORM_REQUEST_TRIGGER` → `Chat__FormRequests__Trigger` (compose default `Model`,
while the code default stays `Keyword`); `STATISTICS_TIME_ZONE` → `Statistics__TimeZone` (default
`Asia/Taipei`). Blank values are fine: an empty provider means "no chat model yet", and empty
`MaxOutputTokens`/`TimeoutSeconds`/`ReasoningEffort` bind as unset. A reasoning model such as
`gpt-6-luna` needs `CHAT_REASONING_EFFORT=None`. `CHAT_ID` / `CHAT_DISPLAY_NAME` → `Ai__Chat__Id` / `Ai__Chat__DisplayName`
(optional), and one reserved second model, `CHAT_MODELS_0_PROVIDER`, `_ENDPOINT`, `_MODEL`, `_API_KEY`,
`_ID`, `_DISPLAY_NAME`, `_MAX_OUTPUT_TOKENS`, `_TIMEOUT_SECONDS`, `_REASONING_EFFORT` →
`Ai__Chat__Models__0__*`: all blank (or absent from an older `.env`) binds a blank provider and the entry
is skipped (#245; `ChatModelCatalogStartupTests` parses the compose file for both cases). `gpt-5.6-terra`
needs `None` too. Operator guide (Traditional Chinese): `deploy/README.md`, sections 11 and 12; the
two-real-model switch acceptance: `docs/evals/2026-10-07-245-model-switch-acceptance.md`. In `Model` mode without a configured chat model, the
selection call throws, is caught, and the keyword gate decides (`ChatFormRequestTool.SelectAsync`), so
the form still appears when a member asks for it in so many words; the answer itself still
returns `503 chat-not-configured`.

## Conversation runs (AG-UI)

`POST /api/v1/assistants/{id}/chat/runs` (M3 plan Slice 7; `SmartAgri.Api.Chat.ChatRunEndpoints`)
answers one question as an [AG-UI](https://docs.ag-ui.com) event stream, written with the
official .NET SDK (`AGUI.Abstractions`/`AGUI.Formatting`, Api layer only), and saves the exchange
in the same request. The body is AG-UI's `RunAgentInput` (what `@ag-ui/client`'s `HttpAgent`
sends); the question is the last `user` message; `threadId` empty/omitted means "the most recent
thread, or a new one". Refusals before the stream are ordinary JSON (`403 assistant-use`,
`403 chat-thread`, `422`, `503 chat-not-configured`/`embedding-not-configured`,
`409 chat-run-in-progress`); after that everything is an event:

`RUN_STARTED` → `TEXT_MESSAGE_START` → `TEXT_MESSAGE_CONTENT`… → `TEXT_MESSAGE_END` →
`CUSTOM smartagri.reply` (the final `ChatMessageView`, as `GET chat` returns it) →
`CUSTOM smartagri.thread` (`{ threadId, title }`, saved conversations only) → `RUN_FINISHED`;
or, when the model or embedding fails mid-way, `RUN_ERROR` with `code` = the `503` reason and
nothing saved but the question. The class remarks spell out every rule.

`409 chat-run-in-progress` uses an in-memory lock (`ChatRunLocks`): correct for one Api process
per deployment, not for several behind a load balancer.

**Form requests: `Chat:FormRequests:Trigger`** (M4 #164). Whether a question gets the assistant's
form (`request_database_form`, #148) is decided by:

- `Keyword` (default, also when unset): the server's keyword gate (`AssistantFormRequestRules.AsksForForm`),
  no model call — #148's behavior, which the `Fake`-model E2E relies on.
- `Model`: whenever the assistant has a form target it may use right now, one model call (purpose
  `form-request` in `ModelInvocations`) is offered `request_database_form` with that one form id as an
  `enum`; the server re-authorizes the id the model names exactly as #148 does (any other id or tool is
  simply no form). When the call fails, the keyword gate decides. This adds one model call per
  question to assistants with a form target; #149's record queries keep their own keyword gate and
  still come first.

Anything else fails startup. `eval-form-requests` (below) compares the two on a labelled set.

The same trigger decides case proposals (M7-9 #254, `propose_case`): in `Model` mode an assistant with
case types it may propose (and no form target) makes one `case-proposal` call. **When a request may be
offered both the form and at least one case type, there is one call, not two** (#286): purpose
`proposal-selection`, offered `request_database_form` and `propose_case` together, and the model calls
one of them or neither (`ProposalSelectionRules`, `ChatProposalSelectionTool`). Precedence stays query →
form → case → answer, at most one proposal per reply; the server accepts only an offered form id (then
re-authorized) or type id; a failed call falls back to the form gate, then the case keyword rule. All
three purposes count toward the monthly token limit. Both the case-only call and the combined call also
offer `no_matching_type` (#297, no parameters): the model's explicit "none of these fits" — no proposal,
the answer pipeline answers, same call, purpose, usage and `form-check` (`CaseProposalRules.Declarations`,
`ProposalSelectionRules.Declarations`; not a failure, so no keyword fallback).

In `Model` mode, a run that is about to make that selection call first sends `CUSTOM smartagri.form-check`
(empty value; #171) so the client can show a "checking" state; keyword mode and assistants without a form
target never send it. The combined call (#286) offers the form too, so it sends the same event at the
same place; a case-only call sends none. `GET /api/v1/assistants/{id}/chat/forms` lists the form(s) the caller may open from
the conversation's 「回報資料」 entry (the same `ChatFormRequestView` a form request carries; empty when
none), and `POST …/chat/forms/{databaseId}/dismissals` records that the member closed an offered form
(`ChatFormDismissals`: assistant, form, time — no account, conversation or content). Contracts: M4 design
doc §15.

**Protocol check with the JavaScript client.** `tools/agui-contract/fixtures/*.sse` are streams
recorded from the real endpoint (ids, timestamps and dates normalized).
`ChatRunEndpointsTests.The_recorded_streams_match_the_fixtures_the_ag_ui_client_check_parses`
fails when the endpoint's output drifts from them, and CI runs
`node tools/agui-contract/check-agui-stream.mjs`, which parses each one with `@ag-ui/client`'s
`HttpAgent` (pinned in the root `package.json`). After an intended change to the stream:

```bash
UPDATE_AGUI_FIXTURES=1 dotnet test apps/api/tests/SmartAgri.Api.Tests --filter-method '*The_recorded_streams_match*'
node tools/agui-contract/check-agui-stream.mjs
# or against a running Api:
node tools/agui-contract/check-agui-stream.mjs --url http://localhost:5153/api/v1/assistants/<id>/chat/runs --token <access token> --question '退貨運費由誰負擔？'
```

## Website channel: `PublicChannels:PublicBaseUrl`

The operator's walk-through for putting an assistant on a customer's website (HTTPS and the reverse
proxy, `deploy/.env`, key-ring backup, `set-token-limit`, the owner's publishing steps and why the
browser-side limits are not protection against scripts) is [`deploy/README.md`](../../deploy/README.md)
(Traditional Chinese); this section and the ones below are the reference for each setting.

The website channel (M5a, `SmartAgri.Api.Assistants.AssistantWebsiteChannelEndpoints`) is set up
and published under `/api/v1/assistants/{id}/publishing/website` (owner + `manage-publishing`).
Publishing needs the acceptance status `passed`, at least one allowed domain, the assistant not
paused, and only knowledge bases the assistant's owner owns; after that, whether it actually answers
visitors (`servingState`) is derived on every read and follows reruns without publishing again.

`PublicChannels:PublicBaseUrl` is the address visitors' browsers reach this API at (e.g.
`https://assistant.example.org`). The embed code customers paste is
`<script src="{PublicBaseUrl}/embed.js" data-assistant="{id}" async></script>`. It is optional at
startup (a malformed value refuses to start): without it `embedCode` is `null` and publishing a
website channel answers `422` with `errors["public-base-url"]`. Development uses
`http://localhost:5153` (`appsettings.Development.json`); the customer compose file reads
`PUBLIC_BASE_URL` from `deploy/.env`.

## Website visitors: `/api/v1/public/*`

Anonymous visitors of a customer's website chat with a published assistant through the chat window
(`apps/widget`) the API itself serves (M5a plan §3 B and D, #196; `SmartAgri.Api.PublicChannels`).
Visitors only ask knowledge-base questions: nothing they say is stored, and form requests, database
queries and handoffs are not on this path at all (an architecture test checks the handlers' types).

- **`POST /api/v1/public/assistants/{id}/visitor-sessions`** (anonymous; any credential is ignored),
  body `{ "host": "<embedding page origin>" | null }`. While the assistant's website channel is
  `serving` — re-derived on every request (`AssistantWebsiteChannelEndpoints.ServingStateAsync`; only
  the monthly usage is cached, 30 s) — it answers `201 { token, expiresAt, assistant: { displayName,
  welcomeMessage, brandColor, showCitations } }` with `Cache-Control: no-store`. Every other case (no
  such assistant or not a GUID, not published, no allowed domain, paused, suspended for acceptance,
  knowledge ownership or quota) gets the same `403 { reason: "public-assistant" }`, byte for byte.
  When `host` is an `https://` origin on the default port whose host is an allowed domain, that
  domain's `lastSeenAt` becomes now (passive installation detection; information only).
- **The token** (`VisitorTokens`) is Data Protection–protected (`ITimeLimitedDataProtector`, purpose
  `SmartAgri.PublicChannels.VisitorToken.v1`, the deployment key ring of `DataProtection:KeysPath`)
  `{ visitorId, assistantId, organizationId, issuedAt, expiresAt }`, valid **12 hours**. `visitorId`
  is a fresh random GUID per session, never written anywhere. Losing or replacing the key ring
  invalidates every visitor token (the widget simply starts a new session).
- **`Authorization: Visitor <token>`** is its own authentication scheme (`VisitorAuthentication`):
  the principal has `visitor_id`, `org_id` and `assistant_id` claims and no `sub`, so
  `ClaimsOrganizationContext` acts for the assistant's organization (filters, write guard and
  model-call recording unchanged) while no account endpoint ever accepts it. `/api/v1/public/*`
  authenticates **only** this scheme: a member's bearer token there is `401`, and a visitor token on
  any member endpoint is `401` too.
- **`POST /api/v1/public/assistants/{id}/chat/runs`** (`VisitorChatRunEndpoints`): the member
  endpoint's `RunAgentInput` → AG-UI SSE, answered by `GroundedAnswerService` alone. Earlier turns
  come from `messages` (at most 20, `user`/`assistant` text only); no thread or message is ever
  written, whatever `keepConversations` says. Events are `RUN_STARTED` → `TEXT_MESSAGE_*` →
  `CUSTOM smartagri.reply` → `RUN_FINISHED` (or `RUN_ERROR`); never `smartagri.thread` or
  `smartagri.form-check`. When the assistant hides citations, the reply carries none. The model call
  is recorded as purpose `public-answer` with `AccountId = null` (it counts toward the monthly
  limit), the outcome as channel `website`. Before the stream: `401` (no, invalid or expired token),
  `403 public-assistant` (token for another assistant, or not serving now), `422` (question blank or
  over 2,000 characters), `503 chat-not-configured`/`embedding-not-configured`, `409
  chat-run-in-progress` (this visitor already has a reply running; in-memory, per process).
- **Same origin only, no CORS.** `PublicOriginGuard` answers `403 { reason: "public-origin" }`
  before authentication to any `/api/v1/public/*` request whose `Origin` is not the API's own (the
  request's scheme, host and port, or `PublicChannels:PublicBaseUrl`'s — set it when a reverse proxy
  terminates TLS). Requests without `Origin` pass: this is a browser boundary, not abuse protection
  (the rate limits below are). No CORS policy is registered, so cross-origin browser calls also
  fail their preflight.
- **Rate limits** (`PublicRateLimiting`, M5a #197): ASP.NET Core's built-in rate limiter, on
  `/api/v1/public/*` **only** (member endpoints, the widget and health checks are never limited). They
  are the real defence against abuse: `frame-ancestors` and the same-origin check only bind browsers,
  and a script that sends no `Origin` is limited exactly like a browser. A request must pass every
  limit that applies; the first to refuse answers `429 { "reason": "rate-limited", … }` (problem JSON,
  `Cache-Control: no-store`) with `Retry-After` in whole seconds, before the answer stream starts.
  `Retry-After` is exact for session creation, the whole window for a sliding-window refusal (the
  built-in limiter cannot say when the next permit frees up, so it is never too early; an hour-limit
  refusal says `3600`) and `5` for the concurrency limit. The widget shows 「問題太頻繁了，請稍後再試」
  with a countdown from it. Values are counts, in `PublicChannels:RateLimits` (env:
  `PublicChannels__RateLimits__<Name>`), each at least 1, validated at startup:

  | Setting | Partition | Algorithm | Default |
  | --- | --- | --- | --- |
  | `SessionsPerIpPerMinute` | client IP, `visitor-sessions` | fixed window, 1 minute | 10 |
  | `RunsPerVisitorPerMinute` | `visitor_id` claim, `chat/runs` | sliding window, 1 minute | 6 |
  | `RunsPerVisitorPerHour` | `visitor_id` claim, `chat/runs` | sliding window, 1 hour | 60 |
  | `RunsPerIpPerMinute` | client IP, `chat/runs` | sliding window, 1 minute | 20 |
  | `RunsPerAssistantPerMinute` | `assistant_id` claim, `chat/runs` | sliding window, 1 minute | 120 |
  | `MaxConcurrentRunsPerAssistant` | `assistant_id` claim, `chat/runs` | replies being generated at once | 10 |
  | `LineWebhooksPerAssistantPerMinute` | assistant id in the URL, LINE webhook (M5b #231) | sliding window, 1 minute | 1,000 |
  | `LineMaxConcurrentWebhooksPerAssistant` | assistant, LINE webhook processor (not a `429`: the rest wait in the queue) | deliveries handled at once | 10 |
  | `LineQuestionsPerUserPerMinute` | assistant + LINE user, one-to-one LINE questions (M5b #232) | sliding window, 1 minute | 6 |
  | `LineQuestionsPerUserPerHour` | assistant + LINE user, one-to-one LINE questions | sliding window, 1 hour | 60 |
  | `LineQuestionsPerGroupPerMinute` | assistant + group or room, LINE questions | sliding window, 1 minute | 10 |
  | `LineQuestionsPerAssistantPerMinute` | assistant, all LINE questions | sliding window, 1 minute | 120 |
  | `LineMaxConcurrentQuestionsPerAssistant` | assistant, LINE answers being generated | answers at once | 10 |

  The `LineQuestions*` limits are not HTTP limits (LINE's servers send every request, so there is no
  client to partition by): the background processor applies them per question and answers a refused
  one with 「問題太頻繁了，請稍後再試」, see "LINE answers" below.

  The limiter runs after authorization (a missing or invalid visitor token is already `401` and costs
  nothing) and after the origin check (a foreign `Origin` is `403` and uses no permit). Narrowest
  partition first (visitor, IP, assistant); permits taken before a later limit refuses are not given
  back. An IPv6 client counts per /64; a connection without an address is one partition (`unknown`).
  **Counters live in this process's memory**: the deployment has one API container, and several
  instances would each count on their own (the limits then multiply by the instance count). A restart
  resets them.
- **Behind a reverse proxy: `PublicChannels:TrustedProxies`** (env `PublicChannels__TrustedProxies__0`;
  compose: `TRUSTED_PROXIES` in `deploy/.env`). A list of IP addresses and CIDR networks
  (`10.0.0.5`, `172.16.0.0/12`, `::1`; one entry may also hold several separated by commas, so a single
  `.env` value works); an entry that is neither refuses to start. **Not set (default): `X-Forwarded-*`
  is ignored** and the client is the connection's source address — so behind a reverse proxy every
  visitor looks like the proxy's one IP, shares one partition and is `429`'d almost at once. **Set it
  for any deployment behind a proxy** (nginx, a load balancer, a CDN). Once set, requests from those
  addresses — and only those — have `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host`
  applied (for every endpoint, not only the visitor API), so the rate limits partition by the real
  client and the origin check and embed code see `https://assistant.example.org` although the proxy
  speaks plain `http` to the API. The chain is read from the right and stops at the first address that
  is not a listed proxy, so a client cannot choose its own address by sending a header. The framework's
  default of trusting loopback does not apply: list `127.0.0.1` and `::1` for a proxy on the same
  machine, and the proxy's network (e.g. the compose network's subnet) for one in another container.
  A Production host that receives `X-Forwarded-For` without any `TrustedProxies` logs a warning once
  (`TrustedProxies`: "…is not configured…"). Also set `PublicChannels:PublicBaseUrl` to the public
  address.
- **Protocol check**: `tools/agui-contract/fixtures/visitor-*.sse` are recorded by
  `VisitorEndpointsTests.The_recorded_visitor_streams_match_the_fixtures_the_ag_ui_client_check_parses`
  and parsed by `check-agui-stream.mjs` like the member fixtures (re-record with
  `UPDATE_AGUI_FIXTURES=1 dotnet test apps/api/tests/SmartAgri.Api.Tests --filter-method '*The_recorded_visitor_streams_match*'`;
  `--visitor` sends the token with the `Visitor` scheme against a running Api).

```bash
# start a session, then ask (same machine, so no Origin header is involved)
curl -s -X POST http://localhost:5153/api/v1/public/assistants/<id>/visitor-sessions \
  -H 'Content-Type: application/json' -d '{"host":"https://shop.example.com"}'
node tools/agui-contract/check-agui-stream.mjs --visitor \
  --url http://localhost:5153/api/v1/public/assistants/<id>/chat/runs --token <token> --question '退貨運費由誰負擔？'
```

### Serving the chat window and `embed.js` (M5a #201)

The API itself serves what a visitor's browser loads, all anonymous and none under `/api`
(so not in the OpenAPI document): the chat window `GET /use/{assistantId}`, its files
`/widget/*`, and the loader `/embed.js`. The widget is `apps/widget`; the Dockerfile builds it in a
Node 24 stage (`npx nx build widget --configuration=production`) and copies the result to
`/app/wwwroot/widget` and `apps/embed-loader/src/embed.js` to `/app/wwwroot/embed.js`; the image still
runs as the non-root `app` user. Widget and visitor API are the same origin, so the visitor API needs no CORS.

| Setting | Meaning |
| --- | --- |
| `Widget:RootPath` | The widget build (`index.html`, hashed `main-*.js`, `chunk-*.js`, `styles-*.css`). Default `wwwroot/widget` (relative to the content root). |
| `Widget:EmbedScriptPath` | The loader. Default `wwwroot/embed.js`. |
| `PublicChannels:AllowLocalhostAncestors` | Development and Testing only: also allow `http://localhost:*` as an embedding page. `true` in any other environment refuses to start. |

| Path | Answer |
| --- | --- |
| `/widget/*` | A file of the build; `Cache-Control: public, max-age=31536000, immutable` for hashed names (`main-V76QUCWD.js`), `no-cache` for others; known content types only; `X-Content-Type-Options: nosniff`. **`index.html` is never served here**: as a plain file it would be a copy of the page without `frame-ancestors`. |
| `/embed.js` | The loader, `text/javascript; charset=utf-8`, `Cache-Control: public, max-age=300`. |
| `/use/{assistantId}` | See below; never cached (`Cache-Control: no-store`). |

Without a widget build (a Development checkout that never ran the build, or a broken image) `/use/{id}`
answers `503` with a plain text explanation naming `Widget:RootPath`, for every id; a missing loader
file is a `503` naming `Widget:EmbedScriptPath`. A rebuilt `index.html` is picked up without a restart.

**`/use/{assistantId}` and `frame-ancestors`.** The page is `index.html` with a fresh random nonce on
`<app-root ngCspNonce="…">` (Angular puts it on the `<style>` elements it inserts at run time), and:

```
Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'nonce-<n>'; connect-src 'self'; img-src 'self' data:; base-uri 'self'; form-action 'none'; frame-ancestors https://a.example https://b.example
Referrer-Policy: strict-origin
X-Content-Type-Options: nosniff
Cache-Control: no-store
```

- `frame-ancestors` is `https://<domain>` for each allowed domain (ordinal order), derived from the database
  **on every request** — nothing is cached, so a domain removed in the settings is gone with the next
  request — when the channel is `published` **or** `paused` and has at least one domain. The suspended
  states (acceptance, knowledge, quota) serve the page too: the widget asks the visitor API and shows
  「目前暫停服務」. `www.example.com` and `example.com` are separate entries; with
  `AllowLocalhostAncestors`, `http://localhost:*` is appended (not to the unavailable page below).
- Anything else — no such assistant (or an id that is not a GUID), another organization's assistant that
  was never published, a draft, an empty domain list — is one fixed page 「這個對話視窗目前無法使用」
  (`404`, `frame-ancestors 'none'`, no nonce): status, headers and body are byte-identical, so the response
  does not say whether the assistant exists. The lookup (`PublicWebsiteChannelLookup`) has no organization to
  work from, so it is the deliberate, narrow exception to organization isolation described above.
- **Opened directly in a tab** (`Sec-Fetch-Dest: document`; M5a plan decision D): 「請從官網開啟這個對話視窗」
  (`200`, `frame-ancestors 'none'`), decided before any lookup, so it is the same for every id. A missing
  header is served normally. This is a courtesy, not a security boundary — a visitor controls that header;
  the restriction on who can embed is `frame-ancestors`, and the real limits on abuse are the rate limits
  and the monthly token limit.
- The CSP deliberately allows no `unsafe-inline`: the page has no inline script or handler (the widget is
  built with `inlineCritical: false`), and brand colours are set through the CSSOM.

### Serving the admin (pre-launch #306)

With `Admin:RootPath` set, the Api also serves the admin SPA from its own origin, so one address
(`PublicChannels:PublicBaseUrl`) serves the admin, the API, LINE and the website embed. The admin is built in
API mode (`npx nx build admin --configuration=production-api`) in the Dockerfile's Node stage and copied to
`/app/wwwroot/admin`; the image sets `Admin__RootPath=wwwroot/admin`. Unset (the default for `dotnet run`),
nothing changes: no admin is served, and a path without an endpoint is still `401` from the fallback policy.

| Setting | Meaning |
| --- | --- |
| `Admin:RootPath` | The admin build (`index.html`, hashed `main-*.js`, `chunk-*.js`, `styles-*.css`, `favicon.ico`), relative to the content root or absolute. Set but not a folder with an `index.html`: the Api refuses to start. Blank: no admin. |

`AdminSpaHosting` is a middleware before authorization. It answers only a `GET`/`HEAD` that **no endpoint
matched** (a known path with another method keeps its `405`) and whose first segment is not one of the Api's:
`api`, `connect`, `.well-known`, `health`, `use`, `widget`, `embed.js`, `openapi`, plus the first literal
segment of every mapped route (read from the endpoint data sources, so a new endpoint group is excluded
automatically). Every Api answer, including `401` for an unknown `/api/...` path, is unchanged. Then:

| Request | Answer |
| --- | --- |
| A file of the build | The file; `Cache-Control: public, max-age=31536000, immutable` for hashed names (`main-V76QUCWD.js`), `no-cache` for others (`favicon.ico`); known content types only; `X-Content-Type-Options: nosniff`. |
| A missing path whose last segment has an extension (`/chunk-OLD12345.js`) | `404`, never the page, so a stale chunk fails loudly instead of being parsed as HTML. |
| Anything else (`/`, `/login`, `/auth/callback`, `/app/...`, `/chat/{id}`) | `index.html`, `Cache-Control: no-cache` (a new deployment is picked up at the next navigation). |

**Default admin origin.** When the admin is served this way and `Authentication:AdminSpa:Origins` has no
non-blank entry, `migrate` registers the origin of `PublicChannels:PublicBaseUrl` (`{origin}/auth/callback`,
`{origin}/login`); a `PublicBaseUrl` with a path gives no default (the admin is served at the root). Explicit
origins always win. `AdminSpaClientRegistrar.EffectiveOrigins` is the rule.

## LINE channel: settings, connection test and enabling (M5b)

An assistant's LINE channel (M5b, `SmartAgri.Api.Assistants.AssistantLineChannelEndpoints`, issues
#229 and #230) is set up under `/api/v1/assistants/{id}/publishing/line`, with the same access rule as
the website channel (owner + `manage-publishing`; a missing id, another organization's or someone
else's assistant all get the same `403 publishing`): `GET`, `PUT` (settings; `revision` conflict
`409`, per-field `422`), `POST …/line:test`, `POST …/line:publish`, `PUT …/line/paused` and
`POST …/line:unpublish`. `GET …/publishing`'s `line` is the same view.

- **Fields.** The official account id (`@` and 3–20 letters, digits, `.`, `_`, `-`), the channel id
  (10 digits), the channel secret (32 hexadecimal digits), the channel access token (at least 40
  characters, no white space), the welcome message sent on follow/join (at most 120 characters;
  default 「您好！有任何問題都可以直接問我。在群組裡請 @ 我再提問。」) and `nonTextReply`, sent when a
  one-to-one chat gets a message that is not text (#291; at most 500 UTF-16 code units, so an emoji
  outside the BMP counts 2; trimmed, line breaks and emoji inside kept; default
  「目前只能回答文字問題。」, which the `AddLineNonTextReply` migration gave every existing channel).
  Every `PUT` sends all of them (a full replace: an omitted `nonTextReply` is blank, `422`). The format
  rules and messages are the admin mock's: `apps/admin/src/app/core/domain/line-field-cases.json` is
  run by both `LineChannelRulesTests` and the frontend spec next to `publishing-channels.ts`
  (`ValidateField` for the four connection fields, `ValidateNonTextReply` for `nonTextReply`).
- **Write-only credentials.** `channelSecret` and `accessToken` are stored as `ProtectedSecret`s
  (Data Protection purposes `line.channel-secret` and `line.access-token`; the key ring section above
  applies — losing it means re-entering both). In a `PUT` they are optional: `null` or empty keeps
  the stored value (both are required on the first save), a value replaces it. No response ever
  contains the plaintext or the ciphertext: only `channelSecret`/`accessToken` as
  `{ configured, lastFour, updatedAt }`.
- **A connection change needs a new test.** Changing the official account id, the channel id or
  either credential clears the connection-check results and the bot user id, and an enabled or
  paused channel goes back to `draft` (`AssistantLineChannel.TryApplySettings`); changing only the
  welcome message or `nonTextReply` keeps everything.
- **Serving state.** `servingState` is derived like the website channel's (`ChannelServing`, shared
  by both channels), with "every connection check passed" in place of "has an allowed domain".
- **Webhook URL.** `webhookUrl` is `{PublicChannels:PublicBaseUrl}/api/v1/line/webhook/{assistantId}`,
  or `null` without a public base URL.
- **Testing the connection (`:test`).** Three checks, in order, with the stored access token
  (`SmartAgri.Application.Line.LineConnectionTester`): (1) `access-token` — `GET /v2/bot/info`
  accepts the token and the account's basic id (or premium id) is the official account id entered
  (case-insensitive, `@` optional); the bot's user id is stored; (2) `webhook-endpoint` —
  `PUT /v2/bot/channel/webhook/endpoint` sets LINE's webhook URL to `webhookUrl` (the owner does not
  paste it into the LINE Developers Console); (3) `webhook-test` — `POST /v2/bot/channel/webhook/test`:
  LINE sends a signed test event to that URL (it passes once the webhook endpoint, see the next
  section, is reachable over public HTTPS). A failed check marks the ones after it `skipped`. LINE
  failing is **never an HTTP error** here: the answer is `200` with the channel view, whose `checks`
  carry each result and a message in Traditional Chinese (a rejected token, another account's token,
  LINE's `429`, a timeout, a webhook URL LINE refuses, a wrong channel secret…). LINE allows 60
  webhook tests per channel per hour; beyond that the third check fails saying so. `422
  line-test-refused` only for this server's own preconditions (`settings`: nothing saved yet;
  `public-base-url`), and `409 line-revision-conflict` if the settings were saved again while LINE
  was being called (nothing stored). A failed test of an enabled channel keeps it enabled but stops it
  serving until a test passes again.
- **Enabling (`:publish`).** The website channel's publishing gate with "every connection check
  passed" in place of "has an allowed domain": on failure `422 line-publish-refused`, each reason under
  its own `errors` key — `connection`, `acceptance` (must be `passed` now), `assistant-paused`,
  `knowledge-ownership` (one message per knowledge base that is not the owner's), `public-base-url`.
  Enabling an enabled channel changes nothing; a paused one resumes.
- **LINE API client.** `SmartAgri.Infrastructure.Line.LineMessagingClient` is a hand-written typed
  `HttpClient` (no LINE SDK; `AddLineMessaging`): bot info, webhook endpoint, webhook test, reply,
  push and the loading animation, each call with the channel's token, a 10-second timeout, and every
  expected failure (`401`/`403`, `429`, other HTTP errors, timeout, network) returned as a result,
  never thrown. It logs only method, path, status and LINE's `x-line-request-id` — never a token, a
  body, a reply token or a user id — and `IHttpClientFactory`'s own request loggers are removed.
- **`Line:ApiBaseUrl`** (optional; `Line__ApiBaseUrl` as an environment variable): the Messaging
  API's base address, default `https://api.line.me`. Tests point it (or the client's handler) at a fake
  LINE server (`tests/SmartAgri.Api.Tests/Infrastructure/FakeLineServer.cs`); the API-mode E2E (CI's
  `e2e-api` job, `apps/admin-e2e/README.md`) points it at the Node fake `tools/fake-line-server/server.mjs`
  (`http://127.0.0.1:5180`). **Production refuses to start with any other value**, since the channel
  access tokens are sent there; Development and every other environment accept any absolute http(s) URL.

## LINE webhook: `POST /api/v1/line/webhook/{assistantId}` (M5b)

Where LINE delivers an assistant's LINE channel events (M5b #231;
`SmartAgri.Api.Line.LineWebhookEndpoints`). The connection test sets it as the channel's webhook URL
(`webhookUrl` above). Anonymous — any credential is ignored — and outside the visitor API, so no
same-origin check (LINE sends no `Origin`). Not in the OpenAPI document: it is LINE's contract, and
the admin never calls it.

- **Order of checks.** (1) Rate limit per assistant id in the URL
  (`PublicChannels:RateLimits:LineWebhooksPerAssistantPerMinute`, default 1,000, sliding minute;
  beyond it `429`), before the body is read, so a flood of forged requests costs little; (2) the raw
  body is read, at most 1 MB (else `413`), before any JSON parsing; (3) the assistant's organization is
  found (`PublicAssistantLookup`, no new place that turns the organization filter off) and its LINE
  channel read; (4) the channel secret is decrypted and `x-line-signature` checked:
  base64(HMAC-SHA256(channel secret, raw body bytes)), compared in constant time.
- **One refusal.** No such assistant (or not a GUID), no LINE channel, a secret that cannot be
  decrypted (key ring changed: re-enter it; logged as a warning), a missing, malformed or wrong
  signature: always the same bodiless `401`, byte for byte. The HMAC is computed on every path (with
  a random key when there is no secret).
- **Then `200`, bodiless, at once.** Also for no events (Console's "Verify" and LINE's webhook test
  send `{"destination":"U…","events":[]}`), for a draft channel (its connection test must pass before
  it can be enabled), and when `destination` is not the bot user id stored by the connection test (or
  none is stored yet): those events are **ignored** — a valid signature means LINE sent them, and a
  non-2xx answer would only make LINE redeliver. A signed body that is not a JSON object is `400`.
- **At least once → deduplicated.** LINE may deliver an event more than once (redelivery, with
  `deliveryContext.isRedelivery: true`, or network retries) with the same `webhookEventId`; ids
  accepted in the last 10 minutes are dropped (`LineWebhookDeduplicator`, in memory, at most 100,000).
- **In-process queue.** Accepted events go to a bounded `System.Threading.Channels` queue (1,000
  deliveries; when full, a delivery is dropped and logged) drained by `LineWebhookProcessor`, a
  `BackgroundService`: each delivery in its own scope acting for the assistant's organization, at most
  `PublicChannels:RateLimits:LineMaxConcurrentWebhooksPerAssistant` (default 10) per assistant and 64
  in all at once, its events in LINE's order. Not the PostgreSQL job queue: its polling and retries do
  not fit a reply token that lasts about a minute. **Single instance**: the queue, the deduplication
  and the conversation history live in this process's memory (like the visitor rate limits); a restart
  loses queued events, whose reply tokens would have expired anyway, and several API instances would
  each keep their own.
- **What each event does** (`LineWebhookEventHandler`; plan §3 D): `follow`/`join` — the channel's
  welcome message as a reply, only while the channel is `serving` (a draft, paused or suspended channel
  greets nobody); a non-text `message` — the channel's `nonTextReply` (default
  「目前只能回答文字問題。」) in a one-to-one chat while serving, with no model call and no token usage,
  nothing in a group or room; a text `message` of a published or paused channel —
  `ILineQuestionHandler` (`LineQuestionHandler`, see "LINE answers" below); `unfollow`/`leave` — the chat's remembered conversation is forgotten; `unsend` — that message
  (and the answer to it) is forgotten; anything else (`postback`, `memberJoined`, `messageEdited`,
  unknown types) is ignored without an error; an event in `standby` mode never gets a reply. A reply
  token is used at most once; a LINE failure is logged (event type, outcome, status,
  `x-line-request-id`) and dropped, never retried.
- **Conversation history** (`ILineConversationHistory`, `InMemoryLineConversationHistory`): per
  assistant and chat (user, group or room id), the newest 20 messages, forgotten after 30 minutes
  without activity, at most 10,000 conversations (least recently used first). Memory only.
- **Nothing from LINE is stored or logged**: no message, user/group id or token goes to the database
  or to a log line (the channel row is only written for the push counter, see below).

## LINE answers (M5b #232)

A text message to a published or paused LINE channel is answered by `SmartAgri.Api.Line.LineQuestionHandler`
(plan §3 D–G), in the webhook processor's scope for the assistant's organization — so the
organization's chat model, knowledge and monthly token limit apply. Per question, in order:

1. **Who is asked.** One-to-one: every text. Group or room: only a message whose
   `message.mention.mentionees[]` has `isSelf: true`; the bot's mention (its `index`/`length`) is
   removed from the question. Anything else, or a mention with no question, gets nothing.
2. **Serving state, again.** Not `serving` (paused, acceptance not passed, knowledge not owned, monthly
   tokens used up…) → the reply 「目前暫停服務」, no model call.
3. **Rate limits** (`PublicChannels:RateLimits:LineQuestions*`, table above; `LineQuestionRateLimiter`,
   in memory). Refused → 「問題太頻繁了，請稍後再試」 once per partition and window (for an
   assistant-wide limit: to the first chat refused), then silence; no model call. A question over 2,000
   characters gets 「問題請在 2000 個字以內。」.
4. **「輸入中」** (`POST /v2/bot/chat/loading/start`, one-to-one only — LINE refuses it in groups), for
   the reply deadline rounded up to 5 seconds (50 by default); the answer's arrival ends it.
5. **The answer**: `GroundedAnswerService.AnswerAsync` with the chat's remembered turns (newest 20,
   forgotten after 30 idle minutes, `unsend` removes one), recorded as model call purpose
   `line-answer` (counted toward the monthly limit) with no account and outcome channel `line` — no
   LINE id in either. A model or embedding failure replies 「目前無法回答，請稍後再試。」.
6. **The messages** (`SmartAgri.Application.Line.LineAnswerMessages`), plain text only (LINE renders
   no Markdown): the answer with each citation run written 「（來源 1、2）」 (removed when the assistant
   hides its sources), cut at 5,000 UTF-16 code units with 「…」; a `general-knowledge` answer ends
   with its notice; a `no-result` is the refusal message and the visitor's next step (never "contact
   the manager"). When the assistant shows citations, a second message: a Flex carousel, one bubble
   per cited document (at most 5) with its citation numbers, knowledge base, document and an excerpt of
   at most 200 characters; `altText` 「參考來源：文件 A、文件 B」 (at most 1,500). Over LINE's 30 KB per
   bubble or 50 KB per carousel, the excerpts shrink first, then bubbles go from the end.
7. **Delivery.** Measured from receiving the webhook request: within
   `PublicChannels:Line:ReplyDeadlineSeconds` (default 50, 1 to 60) a reply (free; the token is used
   once). Past it — or when LINE refuses the reply token (`400`) — a one-to-one answer is **pushed**
   (counted against the LINE account's monthly message quota) and counted on the channel row
   (`PushFallbackMonth`/`PushFallbackCount`, 「本月補送次數」 in `GET …/publishing/line`, one atomic
   `UPDATE`); a group's or room's is **dropped** (decision A: a push costs one message per member). Any
   other LINE failure, `429` included, is logged (outcome, status, `x-line-request-id`) and dropped,
   never retried. Only a delivered answer is remembered for the next question.

Nothing is written to `ChatThreads`/`ChatMessages`. Metrics (meter `SmartAgri`, no content):
`smartagri.line.answer.duration` (seconds from the event, tagged `delivery` = `reply`/`push`/`dropped`
and `chat` = `one-to-one`/`group`), `smartagri.line.push_fallbacks` and `smartagri.line.rate_limited`
(tagged `limit`).

## Monthly token limit: `set-token-limit`

Every organization has a monthly budget of chat-model tokens (M5a #195;
`SmartAgri.Application.Organizations.OrganizationTokenUsage`). When it is used up, every website
channel of the organization is `suspended-quota` (replies stop; internal use is never blocked) until
next month or until the limit is raised. There is no settings screen: operators set it.

- **The limit**: `Organization.MonthlyTokenLimit`, or, when that is unset, the deployment default
  `PublicChannels:DefaultMonthlyTokenLimit` (2,000,000; `DEFAULT_MONTHLY_TOKEN_LIMIT` in
  `deploy/.env`, blank = the built-in value; a negative value refuses to start). `0` means no
  replies at all.
- **What counts**: the sum of `InputTokens + OutputTokens` of the organization's `ModelInvocations`
  in the current calendar month **of `Statistics:TimeZone`** (midnight on the 1st to midnight on the
  1st), over the chat-model purposes only: conversations, wizard trial answers, acceptance reruns,
  form-request decisions, database-query tool selection, report summaries and website visitors'
  answers (`public-answer`) — also the internal
  ones, because the cost is the organization's as a whole. Embedding calls (`embed-document`,
  `embed-query`) never count; a call whose provider reported no usage (`null`) counts as 0, never
  estimated. A reply that finishes after the check can push usage slightly past the limit.
- **State**: `normal` under 80% of the limit, `near` from 80%, `exceeded` from 100% (which suspends
  the website channel). The sum uses the `(OrganizationId, At)` index and is cached per organization
  for 30 seconds, so a changed limit (or new usage) is seen by a running Api within 30 seconds.
- **`GET /api/v1/organization/usage`** (signed in, `manage-publishing`; otherwise `403`) returns
  `{ month: "YYYY-MM", usedTokens, limitTokens, state }`.
- **`set-token-limit`** is a one-shot subcommand like `migrate` and `reindex`, and works in any
  environment (also inside the Production container):

  ```sh
  dotnet SmartAgri.Api.dll set-token-limit --organization <code> --tokens 5000000
  dotnet SmartAgri.Api.dll set-token-limit --organization <code> --tokens default   # back to the deployment default
  # customer deploy: docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm api set-token-limit --organization <code> --tokens 5000000
  ```

  `<code>` is the organization code used at login. Exit code `0` done, `1` unknown organization (or
  the write failed — nothing is written), `2` bad arguments (`--tokens` must be `0`, a positive
  integer, or `default`).

## Conversation retention and the daily cleanup (M6-4, #241)

Each organization chooses how long conversations are kept: 30, 90, 180 or 365 days, or forever
(the default) — `Organization.RetentionDays` (`null` = forever).

- **`GET /api/v1/organization/retention`** (any signed-in account of the organization) returns
  `{ days, pending: { days, effectiveAt } | null, options: [30, 90, 180, 365], canChange, lastChange,
  revision }`. `lastChange` covers the manager's changes and the system switching to a pending value
  (「系統」); `revision` is `Organizations.SettingsRevision`, shared with the chat model.
- **`GET /api/v1/organization/retention/preview?days=N`** (manager only, else `403
  organization-settings`) returns `{ days, threadCount, cutoff }`: the threads a cleanup **now**
  would delete with N days — the same cutoff and query the cleanup uses. After the buffer the real
  number is larger. `N` outside the options is `422` (`errors.days`).
- **`PUT /api/v1/organization/retention`** `{ days, revision }` (manager only): `days` outside the
  options is `422` (`errors.days`), a stale revision `409 organization-settings-conflict`.
  - **Shorter** (forever → a number included) is stored as `pending` with `effectiveAt` = now + 7
    days; nothing is deleted under it before then.
  - **Longer** (a number → forever included) applies at once and drops `pending`.
  - **The current value** while something is pending drops it (「改回」).
  - A change writes `retention-changed` or `retention-change-cancelled` to `OrganizationActivities`
    in the same transaction; a value that changes nothing writes nothing.
- **The daily cleanup** is a `retention-cleanup` background job per organization, due at 03:00 of
  `Statistics:TimeZone`. The first save that gives the organization a retention in days (current or
  pending) starts the chain; each run moves `Organizations.RetentionCleanupNextRunAt` with a
  compare-and-set and only then queues the next run, so a job delivered twice deletes once and
  queues one next job; a run that finds the retention forever with nothing pending ends the chain.
  A run:
  1. makes a pending value whose `effectiveAt` has passed current (`retention-took-effect`);
  2. with a retention of N days, the cutoff is 00:00 of the local day N days before today;
  3. deletes threads whose `LastActivityAt` is before it — whole, the database cascades to their
     messages and citations — and every `AnswerOutcome` whose own `At` is before it, on every
     channel (website included);
  4. deletes in batches of 1,000, each its own transaction (index
     `ChatThreads (OrganizationId, LastActivityAt)`);
  5. writes one `retention-cleanup` activity (`{ days, cutoff, threadCount, answerOutcomeCount }`)
     when it deleted something, none otherwise; the counters
     `smartagri.retention.cleanup.runs` and `smartagri.retention.cleanup.deleted` (tag `record`)
     count every run.

  Model invocations, handoff copies in issues, periodic reports, database records and test runs are
  never touched. The first cleanup after shortening a long-used retention may delete a lot: run the
  first change off-peak.
- **Safety net**: where the job worker runs (`Jobs:WorkerEnabled`), startup re-queues the chain of
  every organization with a retention in days but no queued or running cleanup job (a job that
  failed for good breaks its chain); the same compare-and-set keeps it from forking.
- **`retention-cleanup`** is a one-shot subcommand that runs one organization's cleanup now (a due
  pending value first), without touching the chain:

  ```sh
  dotnet SmartAgri.Api.dll retention-cleanup --organization <code>
  dotnet SmartAgri.Api.dll retention-cleanup --organization <code> --as-of 2026-11-13T03:00:00+08:00   # Development/Testing only
  ```

  `--as-of` computes the cutoff (and whether a pending value is due) as of that time, for the API-mode
  E2E; any other environment refuses it. Exit code `0` done, `1` unknown organization or the cleanup
  failed, `2` bad arguments.

## Case groups (M7-1, #246)

承辦組 are groups of the organization's own accounts that take on a kind of case (M7 plan §3 A). They
are never deleted, only archived (decision G). All endpoints need an internal account
(`smb-admin`, `internal-employee`); an external customer gets `403 case`.

- **`GET /api/v1/case-groups`** (any internal account) returns `{ groups: [{ id, name, archived,
  archivedAt, members: [{ id, displayName }], createdAt, updatedAt }], canManage, candidates }`.
  Archived groups are left out — they are not offered where a group is chosen — unless the
  manager asks `?includeArchived=true` (ignored for anyone else). `candidates` (manager only) are
  the internal accounts, each `{ id, displayName, role }`.
- **`POST /api/v1/case-groups`** `{ name }`, **`PUT /api/v1/case-groups/{id}`** `{ name }`,
  **`POST /api/v1/case-groups/{id}:archive`**, **`:unarchive`** — manager only (`403
  organization-settings`, the same bytes for an id that does not exist or belongs to another
  organization). The name is trimmed, 1–40 characters (`422`, `errors.name`) and unique in the
  organization (`422 case-group-name-taken`, also from the unique index under concurrency). Each
  change writes `case-group-created`／`-renamed`／`-archived`／`-unarchived` (detail `{ id, name }`,
  plus `previousName` for a rename) to `OrganizationActivities` in the same save; a no-op writes
  nothing.
- **`PUT /api/v1/case-groups/{id}/members`** `{ accountIds }` (manager only) replaces the whole list.
  Only internal accounts of the organization: an external customer, an unknown id or another
  organization's account is `422 member-not-eligible` (`errors.accountIds`) and nothing is saved.
  Each addition and removal writes one `CaseGroupMemberChanges` row (account ids without foreign
  keys, so the history outlives the accounts); two concurrent saves of the same account are `409
  case-group-members-conflict`. A member's account cannot be deleted (`Restrict`, decision C).
- **`GET /api/v1/case-groups/{id}/member-changes`** (manager only): `[{ id, account, added,
  changedBy, changedAt }]`, newest first.
- **Archiving a group that is still in use** is `422 case-group-in-use`, each reason under its own
  field: the active case types that default to it (`errors.caseTypes`, M7-2) and its open cases
  (`errors.cases`, M7-3); `message` joins them. Move or deactivate the types and close or transfer
  the cases first.

Account names on these and the other history screens (data managers, submission records, the
settings' 「上次變更」, issues) come from one lookup, `AccountNames`: an account that can no longer be
found shows as 「已停用的帳號」.

## Case types (M7-2, #247)

案件類型 are defined by the manager (M7 plan §3 B). Each has a description (what the assistant reads
when it proposes a case, M7-9), a default case group and a default handling time; a new case starts
from a type, which fills in the group and the due time (M7-3). Types are never deleted, only
deactivated (decision O). Same access rules as the case groups.

- **`GET /api/v1/case-types`** (any internal account) returns `{ types: [{ id, name, description,
  defaultGroup: { id, name, archived }, defaultDueHours, isActive, createdAt, updatedAt }],
  canManage }`, in creation order. Inactive types are left out unless the manager asks
  `?includeInactive=true` (ignored for anyone else).
- **`POST /api/v1/case-types`**, **`PUT /api/v1/case-types/{id}`** `{ name, description,
  defaultGroupId, defaultDueHours, isActive }` — manager only (`403 organization-settings`, the
  same bytes for an unknown or foreign id). Every field is sent each time; an omitted `isActive` is
  active on create and unchanged on update. Field rules, all reported at once (`422`, `errors.<field>`):
  name trimmed, 1–40 characters; description trimmed, 0–500 characters; a default group;
  `defaultDueHours` 1–2,160 (90 days, calendar time — decision H; also a check constraint). The name
  is unique in the organization, active or not (`422 case-type-name-taken`). The default group must
  be one of the organization's groups (`422 case-group-not-found`) and not archived (`422
  case-group-archived`); an inactive type may keep a group that was archived later, but cannot be
  reactivated with it. The foreign key to the group is a same-organization composite with
  `Restrict`. Creating writes `case-type-created` (`{ id, name }`), a real change writes
  `case-type-updated` (`{ id, name, changed: [field names], isActive }` — never the description's
  text); a no-op writes nothing.

## Cases (M7-3, #248)

案件 are business work 「接下來誰要做」 (case ADR; M7 plan §3 C). Internal accounts only: an external
customer, and a case that does not exist, belongs to another organization or is not visible, all get
the very same `403 case` (byte for byte). Who sees a case is one rule, `CaseVisibility` (Application):
its creator, the members of its **current** case group, anyone who ever accepted it (from the
`accepted` events, M7-4), and the manager (`smb-admin`).

- **`POST /api/v1/cases`** `{ typeId, groupId, dueAt, title, description, databaseId?, submissionId?,
  assistantId?, threadId?, previousCaseId? }` creates a `manual` case in `pending` with one `created`
  event and answers `201` with the detail. Checks, in order: the fields, all at once (`422`,
  `errors.<field>`: title trimmed 1–120, description 0–4,000, a link pair both set or both absent);
  `dueAt` earlier than now is `422 due-in-past` (`errors.dueAt`, decision H); an inactive, unknown or
  foreign type is `422 case-type-inactive`; the group `422 case-group-not-found` /
  `case-group-archived`; a record the caller cannot read now (designated data manager holding
  `read-consented-submissions`), a withdrawn one, a thread that is not the caller's own, or a previous
  case that is not visible or still open is `422 link-not-available` (`errors.submissionId`,
  `errors.threadId`, `errors.previousCaseId`). The type, group, creator, owner and previous case are
  same-organization composite foreign keys (`Restrict`); the thread, record and issue are plain ids
  (decision S), checked again on every read.
- **`GET /api/v1/cases`** `?scope=all|created|owned|my-groups&status=open|closed|all|<status>&typeId=&groupId=`:
  the visible cases, newest first, not paged (decision Q); `status` defaults to `open` (`pending`,
  `in-progress`, `awaiting-info`). Each row: `{ id, title, status, origin, type, group, createdBy,
  owner, dueAt, createdAt, updatedAt }` (no description). An unknown `scope` or `status` is `422`.
- **`GET /api/v1/cases/{id}`**: `{ case, events, links }`. `links.record` is `{ databaseId,
  submissionId, state: available|withdrawn|unavailable, canRead }` (seeing the case never widens the
  record's access); `links.thread` `{ assistantId, threadId, canOpen }`, `links.assistantIssue`
  `{ issueId, canOpen }` and `links.previousCase` `{ caseId, canOpen }` say only whether the caller can
  open them. **No response ever carries conversation text** — not the thread's title either.
  `allowedActions` lists what the caller may do now (below) and `cancelReasonRequired` whether their
  `:cancel` needs a reason.

### Case actions (M7-4, #249)

One action table, `CaseActionRules` (Application; M7 plan §3 D, decisions I and J). Each action is
`POST /api/v1/cases/{id}:<action>` — or `POST /api/v1/cases/{id}/comments` for a comment — with the
`eventCount` the screen showed, answers `200` with the detail and writes exactly one event:

| action | body | who | statuses | result |
| --- | --- | --- | --- | --- |
| `accept` | — | a member of the current group | pending | in-progress; the caller becomes the case owner |
| `request-info` | `note` (required) | the case owner | in-progress | awaiting-info |
| `resume` | `note?` | the case owner | awaiting-info | in-progress |
| `complete` | `resolution` (required) | the case owner | in-progress, awaiting-info | completed |
| `cancel` | `reason` (required, except from the creator before acceptance) | pending: creator or manager; afterwards: case owner or manager | open | cancelled |
| `transfer` | `groupId`, `note?` | the case owner or the manager (only the manager while pending) | open | pending in the new group, no owner |
| `set-due` | `dueAt`, `note?` | the case owner | open | new due time |
| comment | `note` (required) | the creator or the case owner | open | a note; the creator's note on an awaiting-info case also moves it back to in-progress |

Checks, in order, and none writes anything: not visible (or an external customer) is the one `403
case`; a missing `eventCount`, a text over 2,000 characters, a missing `groupId`/`dueAt` is `422`
(no reason); another `eventCount` than the case's is **`409 case-changed`**; a status where no one may
do the action — every closed case — is `409 case-changed`; a status where the caller may not is
**`403 case-action`**; then `422 note-required` / `resolution-required` / `reason-required`, a
transfer to an unknown (`case-group-not-found`), archived (`case-group-archived`) or the same group
(`case-group-unchanged`), and a due time earlier than now (`due-in-past`). The case owner stays the
owner after leaving the group. `Case.EventCount` is a concurrency token and `(CaseId, Ordinal)` is
unique, so two people accepting the same version at once get one `200` and one `409`. A closed case
is never reopened: 「另開新案」 is `POST /api/v1/cases` with `previousCaseId`.

## Retrieval preview and `KnowledgeRetriever`

`KnowledgeRetriever` (Application, scoped; M2 plan Slice 9) is **the** way to search knowledge:
the retrieval preview uses it now and M3's conversations will call it too. It never calls a
generation model.

```csharp
var result = await retriever.RetrieveAsync(
    new KnowledgeRetrievalQuery(question, knowledgeBaseIds, accountId, assistantId /* M3 */,
        IncludePending: false, Top: null /* Retrieval:Top */, MinScore: null /* Retrieval:MinScore */),
    cancellationToken);
// result.Passages: closest first, each with document id/name, version id/number/state,
// location label, full chunk text and score; result.Threshold; result.BelowThreshold;
// result.Relevant (the passages at or above the threshold).
```

1. It embeds the question (`KnowledgeChunkEmbedder.EmbedQueryAsync`, with `QueryPrefix`): one
   `ModelInvocations` row, purpose `embed-query`, with the given account and assistant.
2. It searches `RetrievableChunks.InKnowledgeBases(ids, now, model, includePending)` — the
   eligibility rule of "Versions, approval and emergency disable" within those knowledge bases —
   by exact cosine similarity, `Top` results.
3. It reads the document names, version numbers and states of the results by id
   (`IKnowledgeVersionSources`, Infrastructure's `EfKnowledgeVersionSources`), since search does
   not load navigations.

Everything runs in the scope's organization, so ids of another organization's knowledge bases
match nothing; whether the caller may search the ids it passes (the owner here, an assistant's
connections in M3) is the caller's check. It throws `KnowledgeEmbeddingException` when the
question cannot be embedded; no knowledge base ids means no model call and no passages.

`BelowThreshold` is true when no passage reaches the threshold (or nothing was found): an
assistant restricted to the organization's data then answers 「查無結果」 without calling a model
(grounded-answers ADR) — unless some passage reaches `Retrieval:CandidateMinScore` (below). The
passages are returned anyway, so a person can see how close the nearest ones came.

**Small passages to retrieve, the whole table to answer from** (#324). Retrieval scores each
table row on its own (#301), but a question like 「週二有營業嗎？」 needs the opening hours *and*
the closed day. Once `GroundedAnswerService` has decided which passages to send (above
`MinScore`, or candidates — unchanged, so the expansion never changes which questions reach the
model), every table row among them brings the other not-excluded rows of the same version, unit
and table (`KnowledgeTableExpansion`, one `IKnowledgeTableRows` query per answer, none when no
passage is a table row): the table becomes one passage, its rows in their original order, at the
place of its closest selected row, so a table is sent once. The passage keeps that row's chunk,
document and score, so its citation points at the row retrieval found (the citation's text is
what the model read). Added rows share a budget of `GroundedAnswerPrompt.TableRowsMaxCharacters`
(2,000) characters per answer, taken nearest a selected row first; when some are left out, the
span's `smartagri.answer.table_rows_truncated` is `true` (`smartagri.answer.tables_expanded` and
`smartagri.answer.table_rows_added` count what was added), and `eval-answers` shows each
question's added rows (`同表補列`). Every caller of `GroundedAnswerService` behaves the same.

**`includePending`** (the preview only — never for answering) adds, per document, its **newest
approvable version**: the highest-numbered version still `pending-review` and processed
`ready`/`partially-readable` (a newer upload that is queued, processing or failed has no chunks
and does not hide it; an older pending version than the one in effect counts, since approving it
now would put it in effect). Its passages come back with `versionState` `pending-review`. Chunks
still must not be excluded and must be of the configured model, and **a disabled document shows
nothing even with `includePending`**: an emergency disable is absolute in every mode, and
approving a pending version of a disabled document would not make it citable until the document
is enabled either (check a corrected version of a disabled document in its extraction preview).
Archived and scheduled versions never appear.

| Endpoint | Result |
| --- | --- |
| `POST /api/v1/knowledge-bases/{id}/retrieval-preview` `{ question, includePending?, top? }` | `200` `{ passages: [{ documentId, documentName, versionNumber, versionState, locationLabel, excerpt, score, versionId, chunkId }], threshold, belowThreshold }` |

- Owner only, with the same `403 knowledge-base` as everything else — checked before the body,
  so a stranger learns nothing from validation either. Nothing is written except the question's
  `ModelInvocations` row.
- `422` (field errors) for a blank question, one longer than **500 characters** after trimming,
  or `top` outside **1–20**. `includePending` defaults to false, `top` to `Retrieval:Top`.
- `excerpt` is the chunk text, cut after **300 Unicode scalars** (about half a 600-character
  chunk: its 100-character overlap and a good part of what is new) and then ending in `…`;
  `chunkId`/`versionId` let the frontend open the passage in the extraction preview. `score` is
  the cosine similarity (higher is closer, at most 1), `threshold` the one it was judged by.
- `503` ProblemDetails when the question cannot be embedded, with `reason`
  `embedding-unavailable` (the provider failed or answered unusably; message
  「嵌入模型暫時無法使用，請稍後重試」 — try again later) or `embedding-not-configured` (no provider in
  this deployment; 「系統尚未設定嵌入模型，請聯絡系統管理員設定後重試」). The failed call is still
  recorded in `ModelInvocations`, and the cause is logged as a warning.

Configuration (section `Retrieval`; written out in `appsettings.json`, override with e.g.
`Retrieval__MinScore`):

| Key | Default | |
| --- | --- | --- |
| `MinScore` | `0.406` | The relevance threshold, a cosine similarity of 0–1. **Calibrated for OpenAI `text-embedding-3-small`** by the retrieval evaluation (`docs/evals/2026-10-06-retrieval-text-embedding-3-small.md`, #192). It depends on the model (the e5 family scores almost everything above 0.7), so set it again when `Ai:Embedding:Model` changes. `appsettings.Development.json` sets `0.3`, because the `Fake` model scores the fixture's matching page at about 0.32 and unrelated text below 0.1 — so with a real model in Development, also set `Retrieval__MinScore`. An assistant can tune its own. |
| `Top` | `5` | Passages per search when the caller does not say, 1–20. |
| `CandidateMinScore` | unset in code; `0.30` in `appsettings.json` (**provisional**, #302 — the pre-launch P5 evaluation decides the final value) | The candidate threshold (pre-launch plan §3 B). When **no** passage reaches `MinScore` but some reach this, a `company-data-only` assistant still asks the model, with those candidates (at most `Top`, closest first), and the prompt's refusal marker decides: a refusal is `no-result` with reason `cannot-answer`, an answer is `company-data` with citations as usual. Below both, `below-threshold` without a model call, as before. Passages at or above `MinScore` are always sent alone, as before. `allow-general-knowledge` assistants ignore it (they keep answering from general knowledge). Every caller of `GroundedAnswerService` — staff chat, website visitors, LINE, test runs, trial answers, `eval-answers` — behaves the same. The startup check requires `0 ≤ CandidateMinScore ≤ MinScore`: if you lower `MinScore` below it, lower it too, or turn it off with an empty value (`Retrieval__CandidateMinScore=`); unset, behaviour is exactly as before #302. An assistant whose own threshold is at or below it has no candidate band. Each `AnswerOutcome` records `UsedCandidates`, each test run the `CandidateMinScore` it ran with (`null` when it had no band). Model-specific, like `MinScore`. `appsettings.Development.json` leaves `MinScore` at `0.3`, so in Development the band is empty and the `Fake`-model tests answer as before. |

## Evaluating retrieval: `eval-retrieval`

The testing ADR asks for a question bank so the relevance threshold and the embedding model are
judged by data, not by feel, and the grounded-answers ADR for questions that should find nothing
(M2 plan Slice 16, #50). The bank and its demo documents are `apps/api/eval/retrieval/` (its README
describes the documents, the JSON format and how a run is judged); `eval-retrieval` runs it with
the **configured** embedding model and writes a Markdown report:

```sh
dotnet run --project apps/api/src/SmartAgri.Api -- migrate           # the database must be migrated
dotnet run --project apps/api/src/SmartAgri.Api -- eval-retrieval    # writes docs/evals/<date>-retrieval-<model>.md
dotnet run --project apps/api/src/SmartAgri.Api -- eval-retrieval --report /tmp/eval.md --set <dir> --timeout 600
```

- **Development and Testing only**: it refuses any other environment, because it writes an
  organization into the database. `dotnet run` uses Development (launch settings).
- It works in an organization of its own, **`retrieval-eval`** (「檢索評測（安心商行示範資料）」, with an
  account `eval` that has no password and cannot sign in), created on first use: your demo data is
  never touched. Every run first deletes that organization's knowledge bases, then imports the set
  through the normal pipeline — upload (the upload rules, activity rows, a processing job), the job
  queue (the command runs `JobRunner` itself, waiting up to `--timeout` seconds, default 600), and
  approval in order, so 退換貨辦法's version 2 is in effect and version 1 archived — then sends every
  question through `KnowledgeRetriever` (the set's knowledge bases, top 5, no pending versions, no
  account). Question embeddings are recorded in `ModelInvocations` as the evaluation
  organization's.
- The report has the run's settings, hit@5 and hit@1 overall and per category, the highest score
  among questions that should find nothing, the lowest score of a correct hit, a suggested
  threshold with how many questions it and the current `Retrieval:MinScore` judge correctly,
  when a candidate band is in effect how many should-find-nothing questions fall in it (they
  reach the model) and how many answerable ones only hit inside it (#302), every question's result, and the passages of each miss and of each should-find-nothing
  question. It goes to `docs/evals/<date>-retrieval-<model>.md` under the repository the current
  directory is in (overwritten by a second run the same day), or to `--report`. Exit codes: `0`
  done, `1` the model or processing failed (e.g. a wrong key: the queue retries until
  `--timeout`), `2` bad arguments, environment, set or configuration.
- **CI never runs it against a model**: it needs a real model and key. The integration tests run it
  with `Fake` to prove the pipeline end to end (`RetrievalEvaluationTests`); `Fake` scores are
  hashes of the text, and its report says so.

**With `Fake`** (`appsettings.Development.json`, no key): the command above. Only for checking the
pipeline; never calibrate with it.

**With OpenAI**: keep the key in a local file outside the repository (owner action items, item 3),
e.g. `~/.config/smart-agri/embedding.env` with `chmod 600`, holding `Ai__Embedding__Provider=OpenAI`,
`Ai__Embedding__Model=text-embedding-3-small` and `Ai__Embedding__ApiKey=…`:

```sh
set -a; . ~/.config/smart-agri/embedding.env; set +a
dotnet run --project apps/api/src/SmartAgri.Api -- eval-retrieval
```

**With a local OpenAI-compatible server** (M2 plan §7 decision 3: a permissively licensed
multilingual model from a non-Chinese team — Microsoft's `intfloat/multilingual-e5-large` (MIT) or
Snowflake's `Snowflake/snowflake-arctic-embed-l-v2.0` (Apache-2.0); not BAAI's bge). For example
Hugging Face's text-embeddings-inference (Apache-2.0), which serves `/v1/embeddings`; check the
image's current version and licence when adopting it, and check swap first — it needs about 3–4 GB:

```sh
docker run --rm -p 8081:80 -v "$HOME/.cache/smart-agri-tei:/data" \
  ghcr.io/huggingface/text-embeddings-inference:cpu-<version> --model-id intfloat/multilingual-e5-large

Ai__Embedding__Provider=OpenAICompatible \
Ai__Embedding__Endpoint=http://localhost:8081/v1 \
Ai__Embedding__Model=intfloat/multilingual-e5-large \
Ai__Embedding__QueryPrefix='query: ' \
Ai__Embedding__DocumentPrefix='passage: ' \
dotnet run --project apps/api/src/SmartAgri.Api -- eval-retrieval
```

The e5 family needs those prefixes (snowflake-arctic-embed-l-v2.0 takes `query: ` for questions and
none for passages; follow the model card) and scores almost everything above 0.7, so its threshold
lands near 0.8, far from OpenAI's.

**Calibrating** (in its own PR): with the chosen model's report meeting hit@5 ≥ 90%, set the
suggested threshold (or a value justified from the report) as `Retrieval:MinScore` in
`appsettings.json` **and** `KnowledgeRetrievalSettings.DefaultMinScore` (`RetrievalOptionsTests`
fails when they differ), and commit the report under `docs/evals/`.

> **Calibrated** (#191/#192, 2026-10-06): the OpenAI run is committed as
> `docs/evals/2026-10-06-retrieval-text-embedding-3-small.md` (hit@5 27/27), and `Retrieval:MinScore`
> is its suggested 0.406. The local-model run and its comparison with OpenAI wait for a fully
> on-premises customer (M5 handoff §3.4).

## Evaluating answers: `eval-answers`

The grounded-answers ADR asks for a question bank that also covers questions that should find
nothing, this time for the whole answer pipeline rather than retrieval alone (M3 plan Slice 13,
#83). The bank and its demo documents are `apps/api/eval/answers/` (its README describes the
format and how a run is judged); `eval-answers` runs it with the **configured** embedding and chat
models and writes a Markdown report:

```sh
dotnet run --project apps/api/src/SmartAgri.Api -- migrate         # the database must be migrated
dotnet run --project apps/api/src/SmartAgri.Api -- eval-answers    # writes docs/evals/<date>-answers-<chat model>.md
dotnet run --project apps/api/src/SmartAgri.Api -- eval-answers --report /tmp/eval.md --set <dir> --timeout 600
```

- **Development and Testing only**, for the same reason as `eval-retrieval`. It works in its own
  organization, **`answers-eval`** (「回答評測（安心商行示範資料）」), reset and reimported through the
  normal pipeline on every run exactly as `eval-retrieval` does, then answers every question
  through `GroundedAnswerService.AnswerAsync` with a `company-data-only` profile of its own (no
  assistant) and the deployment's `Retrieval:MinScore` and `Retrieval:CandidateMinScore`. A question with `followUpOf` is answered
  with the referenced question and the reply it actually got as one-turn conversation history (M3
  plan §7 decision D), so the retrieval query for it joins both questions.
- The report has the run's settings, reply-kind accuracy, citation hit rate (of the `company-data`
  questions, those citing at least one expected document), the rejection reason distribution
  (`GroundedRejectionReason`, so the prompt and threshold can be tuned by data — grounded-answers
  ADR), average input/output tokens from this run's `generate-answer` `ModelInvocations` (`—` when
  none reported a number), and every question's expected and actual reply side by side with the
  closest passage's score (`最高分`, whatever the threshold) and the reply text (`回覆內容`, `—` for
  `no-result`) — #303, so negative conclusions are judged without a temporary hack; the settings
  also show the prompt version (`回答提示版本`). With a candidate band (#302) the settings show
  `Retrieval:CandidateMinScore`, the summary how many questions were answered or refused from
  candidate passages, and each question whether it was (`候選段落`); the summary and each question
  also show the table rows sent besides the retrieved ones (`同表補列`, #324). It goes to
  `docs/evals/<date>-answers-<chat model>.md` (overwritten by a second run the same day), or to
  `--report`. Exit codes: `0` done, `1` a model or processing failed, `2` bad arguments,
  environment, set or configuration.
- **CI never runs it against a model**: it needs a real chat (and embedding) model and key. The
  integration tests run it with `Fake` to prove the pipeline end to end
  (`EvalAnswersIntegrationTests`) and check the report is byte-for-byte reproducible; `Fake` scores,
  citations and rejection reasons are not meaningful (`FakeChatClient` always cites the first
  passage it is given, whatever it says), and the report says so.
- The bank includes a **prompt-injection sample** (`notice-01`, M3 plan §7 risk 1): a document
  whose content asks whoever reads it to ignore its rules and leak a fake coupon code without
  citing the passage. `Fake` cannot act on it (it does not read content semantically); a real model
  run is when a person should check the pipeline actually resists it.

**With `Fake`** (`appsettings.Development.json`, no key): the command above. Only for checking the
pipeline; never calibrate with it.

**With OpenAI**: the same environment variables as `eval-retrieval` above cover both models
(`Ai__Embedding__*` and `Ai__Chat__*`); see `apps/api/README.md`'s "Evaluating retrieval" for the
local OpenAI-compatible embedding option (the chat model still needs OpenAI, Azure OpenAI or an
OpenAI-compatible endpoint of its own).

> **Deferred, like `eval-retrieval`'s calibration** (needs the owner's OpenAI key and consent): the
> real-model run with a committed report, checking the prompt-injection question by hand, and any
> resulting change to the prompt or to `Retrieval:MinScore`. Until then only the `Fake` run is
> verified.

## Evaluating form-request triggers: `eval-form-requests`

M4 #164: how often the keyword gate and the model choosing `request_database_form` miss a form
request (should have shown the form) or false-trigger (should not have), on the labelled set in
`apps/api/eval/form-requests/` (its README describes the format). No database is needed.

```sh
dotnet run --project apps/api/src/SmartAgri.Api -- eval-form-requests --trigger keyword   # no model needed
dotnet run --project apps/api/src/SmartAgri.Api -- eval-form-requests                     # keyword and model (Ai:Chat)
dotnet run --project apps/api/src/SmartAgri.Api -- eval-form-requests --report /tmp/form.md --set <dir> --trigger model
```

- **Development and Testing only.** The model trigger uses the production declaration and prompt
  (`AssistantFormRequestRules.Declaration`/`SelectionPrompt`) with the set's sample form, calling
  the configured chat model directly: no organization, so these calls are not in
  `ModelInvocations` (the report has the average tokens).
- The report (`docs/evals/<date>-form-requests-<model, or keyword>.md`, or `--report`) has missed and
  false triggers per trigger, agreement on the ambiguous questions, failed calls (not judged), and
  every question side by side. Exit codes: `0` done, `1` no chat model for `model`/`both` or a model
  call failed, `2` bad arguments, environment or set.
- With `Fake`, the model column only proves the pipeline (the fake follows the keyword gate unless a
  directive says otherwise), and the report says so. Results: `docs/evals/2026-10-05-164-form-request-trigger.md`
  and, after #286, `docs/evals/2026-10-07-286-combined-proposal-call.md`.
- `--case-types <dir>` (#286, model trigger only) also makes, per question, the combined selection call
  production makes when the assistant has case types too — the form tool next to `propose_case` with
  `<dir>`'s `caseTypes` (normally `apps/api/eval/case-proposals`) — and reports whether the form is still
  given or a case is proposed instead.

## Evaluating case-proposal triggers: `eval-case-proposals`

M7-12 #257: how often the keyword rule (`CaseProposalRules.KeywordProposal`, decision T) and the
model choosing `propose_case` miss a case proposal, false-trigger (on a question that should get no
proposal, the form, or the database query) or pick the wrong case type, on the labelled set in
`apps/api/eval/case-proposals/` (its README describes the format: a sample form, three case types,
50 questions). No database is needed.

```sh
dotnet run --project apps/api/src/SmartAgri.Api -- eval-case-proposals --trigger keyword   # no model needed
dotnet run --project apps/api/src/SmartAgri.Api -- eval-case-proposals                     # keyword and model (Ai:Chat)
dotnet run --project apps/api/src/SmartAgri.Api -- eval-case-proposals --report /tmp/case.md --set <dir> --trigger model
dotnet run --project apps/api/src/SmartAgri.Api -- eval-case-proposals --types types-with-exclusions.json --report /tmp/case.md
```

- **Development and Testing only**, same arguments and exit codes as `eval-form-requests`. A relative
  `--report` is resolved against the process's working directory (`dotnet run --project` runs in the
  project directory), so pass an absolute path or omit it.
- Each question is judged twice per trigger: the **case layer alone**, and the **whole proposal stage**
  (decision L: database query → form → case). The query layer is the `DatabaseQueryTools.AsksForStatistics`
  gate (the query model is not called); then `AssistantFormRequestRules.AsksForForm` and the case keyword
  rule (keyword), or — since #286, as production does for an assistant with both a form and case types —
  one combined `request_database_form` + `propose_case` call (model). In model mode every question gets
  that combined call and one case-only call (the case layer) with the production declarations and
  prompts, calling the configured chat model directly (not in `ModelInvocations`; the report has every
  question's tokens).
- `--types <file>` (#293) offers the same questions another wording of the case types: a JSON file
  with a `caseTypes` array whose keys and order match the set's (names and descriptions may differ;
  at most 500 characters, production's limit). A relative path is looked up in the working
  directory, then next to `questions.json`; `types-with-exclusions.json` there writes what each type
  does not cover. Without it the set's own descriptions are used, as before. The report names the file
  and its fingerprint.
- A reasoning model such as `gpt-6-luna` needs `Ai__Chat__ReasoningEffort=None` (see "Chat model").
- The default report name has no ticket number, so a second run on the same day overwrites the first;
  pass `--report` to keep both.
- Results and recommendations: `docs/evals/2026-10-07-257-case-proposal-trigger.md`; after the combined
  call, `docs/evals/2026-10-07-286-combined-proposal-call.md`; type descriptions that say what they
  exclude, `docs/evals/2026-10-07-293-case-type-exclusions.md`.

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

When it is set, `DevelopmentSeeder` first checks it against the exact same Identity
password rules a real account's password must satisfy (`AddIdentityCore<Account>` in
`AuthenticationServiceCollectionExtensions.AddSmartAgriAuthentication` —
`IdentityOptions.Password`, currently at least `MinimumPasswordLength` (12) characters
with an upper-case letter, a lower-case letter, a digit and a symbol; see that class for
the current values, since the rule is "same as real accounts", not a fixed list copied
here). It resolves the same registered `IPasswordValidator<Account>`s Identity itself
uses — it never duplicates the rule values — so a password that would be rejected when a
real administrator sets one is rejected here too, with the same error descriptions.
Validation runs before `migrate` creates a single organization or account: a password that
fails makes `migrate` exit non-zero with a message listing the failing rules (never the
password itself), and the database is left exactly as it was — no organizations, no
accounts. Only a password that passes gets hashed with `PasswordHasher<Account>` and
seeded as before. A checked-in or forgotten-default demo password still can never reach a
database, because the value has no default here and must never be committed — see
`deploy/.env.example` for where to set it and `tools/check-no-demo-secrets.sh` (run in CI)
for the checks that keep a real value out of every checked-in file.

### Demo knowledge: `SEED_DEMO_KNOWLEDGE`

With `SEED_DEMO_KNOWLEDGE=true` as well (Development only, like the rest), `migrate` also puts the
retrieval evaluation's demo documents ("Evaluating retrieval" above) into 安心商行, owned by its
`admin`: 商品使用指南, 退換貨政策 (退換貨辦法 version 1 archived, version 2 in effect) and 配送常見問題
(the delivery timetable and the FAQ), so API mode has real knowledge to search and preview.

```sh
export SEED_DEMO_PASSWORD='choose-a-strong-password-1!' SEED_DEMO_KNOWLEDGE=true
dotnet run --project apps/api/src/SmartAgri.Api -- migrate
```

It goes through the normal pipeline (`DemoKnowledgeSeeder`, `KnowledgeSetImporter`): each file is
uploaded as `POST .../documents` stores one (a new version as `POST .../versions` does), and as
`migrate` has no job worker, it runs the job queue itself once and approves what is processed, in
order, as the owner. With the embedding model of your configuration: `Fake` by default, whatever
`Ai__Embedding__*` says otherwise. Anything not processed yet (e.g. the model was unreachable and the
queue will retry) stays pending review; run `migrate` again later and it is approved.

**Idempotent like the accounts**: it only fills in what is missing — a knowledge base by owner and
name, a document by name, a version by content (SHA-256) — and never touches anything that is there,
including what you changed by hand; a document of the same name that is not the set's is left alone
with a warning. Without the setting (or with anything but `true`) it does nothing and opens no
connection; without 安心商行 and its `admin` (no `SEED_DEMO_PASSWORD`) it logs a warning and does
nothing.

## Running the customer-deploy compose file end to end

Operators: [`deploy/README.md`](../../deploy/README.md) is the step-by-step guide (certificates,
`deploy/.env`, reverse proxy, backups, publishing); the commands below are the shortest path.

```sh
cp deploy/.env.example deploy/.env   # then set a real POSTGRES_PASSWORD
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up --build
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:8080/health/ready
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:8080/embed.js   # 200: the loader customers' pages include
```

## First install: `setup`

A fresh deployment has no organization and no account. The first organization and its
administrator are created once with the `setup` subcommand (on-prem-packaging ADR) —
never by seed data, a web wizard or a password in configuration:

1. Prepare `deploy/.env` (real `POSTGRES_PASSWORD`, `PUBLIC_BASE_URL` — the admin is served
   there, see "Serving the admin" — certificate passwords) and put `signing.pfx` / `encryption.pfx` / `dataprotection.pfx` in
   `deploy/certs/` (see "Sign-in and tokens" and "Data Protection key ring"). `setup`
   builds the same host as the web server, so outside Development it also refuses to run
   without the certificates.
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
5. Start the stack (`docker compose ... up -d`), sign in to the admin SPA (`PUBLIC_BASE_URL`) as the
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

Domain and Application tests never need Docker:

```sh
dotnet test apps/api/tests/SmartAgri.Domain.Tests
dotnet test apps/api/tests/SmartAgri.Application.Tests
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
