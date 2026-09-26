# apps/api

.NET 10 backend (see `docs/adr/2026-09-25-backend-stack.md` and
`docs/plans/2026-09-25-backend-milestone-1-skeleton.md`). Solution: `SmartAgri.slnx`.

```
src/SmartAgri.Domain/               entities (POCOs), enums; no third-party dependencies
src/SmartAgri.Application/          business rules (e.g. knowledge base visibility, sharing, upload checks), processing and embedding orchestration, retrieval (KnowledgeRetriever), job handler contract; Domain + abstraction packages only
src/SmartAgri.Infrastructure/       AppDbContext, EF mapping, Identity accounts, migrations, health checks, job claiming, text extraction, embedding clients and the model-call audit middleware, the pgvector VectorStoreCollection
src/SmartAgri.Api/                  Minimal API, sign-in (Identity + OpenIddict), background job runner/worker, Dockerfile, migrate + setup + reindex subcommands
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
- **Turning the filter off** is allowed in one place only, `AccountLookup` (sign-in
  lookup by organization code + login name); a source-scanning test enforces this.
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
  the asker for a question, none for `reindex`), assistant (M3), purpose (`embed-document`/`embed-query`), provider,
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
(grounded-answers ADR). The passages are returned anyway, so a person can see how close the
nearest ones came.

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
| `MinScore` | `0.3` | The relevance threshold, a cosine similarity of 0–1. **A placeholder** until the retrieval evaluation (M2 Slice 16) calibrates it for the chosen model; it depends on the model (OpenAI's `text-embedding-3` models separate related text around here, the e5 family scores almost everything above 0.7), so set it again when `Ai:Embedding:Model` changes. The `Fake` model scores the fixture's matching page at about 0.32 and unrelated text below 0.1. M3 will let an assistant tune its own. |
| `Top` | `5` | Passages per search when the caller does not say, 1–20. |

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
  every question's result, and the passages of each miss and of each should-find-nothing
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

> **Deferred to the end of M2** (#50; docs/plans/2026-09-26-m2-owner-action-items.md, items 3
> and 4): the OpenAI run with a committed report, the local-model run and its comparison with
> OpenAI, the hit@5 ≥ 90% check, and the `Retrieval:MinScore` calibration all wait for the
> owner's API key and consent to run a local model. Until then `Retrieval:MinScore` 0.3 stays a
> placeholder, and only the `Fake` run is verified.

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
