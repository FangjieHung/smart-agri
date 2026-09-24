# 後端 Milestone 1｜骨架：實作計畫

**日期：** 2026-09-25
**依據：** `docs/adr/2026-09-25-*.md`（特別是 milestone-order、backend-stack、deployment-and-tenancy、authentication、frontend-backend-integration、testing-and-banned-dependencies、observability、on-prem-packaging、organization-naming-in-code）、`docs/glossary.md`、`docs/handoff/mock-to-api-mapping.md`、`docs/handoff/tasks-6-10-backend-handoff.md`。
**拆票方式：** 下面每個 Slice 都是可以單獨合併的垂直切片，各自有測試與驗收條件；Slice 編號即建議順序，「依賴」欄寫的是硬依賴。

---

## 1. 目標與非目標

**目標（M1 交付）：** 一個組織內的帳號，用真實帳密登入後，可以開啟管理後台殼層；畫面上的「目前帳號、角色、七個權限、團隊與權限設定」都來自 API，而且跨組織的資料在資料庫層就查不到。其餘功能區（助理、知識庫、數據庫、對話、發布）仍然走 mock。

具體完成標準：

1. `apps/api` 可用 `nx` 建置、測試，CI 在每個 PR 上跑前後端的 lint／test／build。
2. PostgreSQL（含 pgvector 映像）以 docker compose 啟動，EF Core migration 可重複套用。
3. `Organization`、`Account` 有 `OrganizationId`，EF Core 全域查詢篩選強制套用，並有跨組織隔離測試。
4. ASP.NET Core Identity + OpenIddict 核發 token；帳號的角色與七個權限對應 `account.model.ts`。
5. OpenAPI 文件由 .NET 10 內建功能產生 → 前端 TypeScript 型別；CI 發現產生結果與提交不一致時失敗。
6. 前端以「API 模式」建置時，登入／工作階段／團隊權限走 HTTP；GitHub Pages 的 Demo 建置維持純 mock、行為不變。
7. OpenTelemetry 輸出，開發時在 Aspire 儀表板看得到請求與 SQL 追蹤。

**非目標：** 見第 5 節。

---

## 2. 現況事實（寫計畫時查證，2026-09-25）

| 事實 | 出處 |
| --- | --- |
| Nx 23.1.0，唯一的 plugin 是 `@nx/cypress/plugin`；`targetDefaults` 已有 `build`／`test`／`lint` 快取設定 | `nx.json:3-13`、`:14-25`；`package.json:34-55` |
| Node 版本鎖 24（本機預設 shell 是 Node 22.14，需先切換） | `.nvmrc:1` |
| 目前只有一條 workflow：push `master` 時建置並部署 Pages，**沒有 PR 的 CI** | `.github/workflows/deploy-pages.yml:3-6`、`:26-27` |
| admin 的 build 只有 `production`／`development` 兩組設定，沒有 `environments/`、沒有 `proxyConfig` | `apps/admin/project.json:31-52`、`:54-65` |
| `app.config.ts` 沒有 `provideHttpClient` | `apps/admin/src/app/app.config.ts:11-17` |
| 整個前端只有一個 repository 注入點 `DEMO_REPOSITORY` | `apps/admin/src/app/core/repositories/tokens.ts:7-26` |
| 契約全部**同步**回傳 `RepositoryView<T>`；union 已含 `loading` | `demo-repository.ts:106-132`、`:259-277` |
| 與身分／團隊相關的契約方法只有三個：`listAccounts()`、`getTeam(viewer)`、`updateMemberPermissions(viewer, member, permissions)` | `demo-repository.ts:260`、`:266`、`:273-277` |
| 三者的呼叫點：`listAccounts` 只被助理清單拿來判斷「能不能建立助理」；`getTeam`／`updateMemberPermissions` 只在團隊面板 | `assistant-list-page.component.ts:28-35`；`team-panel.component.ts:33-37`、`:86` |
| mock 內部所有權限判斷都經過 `accounts()`（seed 疊上團隊設定覆寫） | `mock-demo-repository.ts:2975-2987` |
| 「登入」其實是 `DemoSessionService.switchAccount()` 把 accountId 寫進 sessionStorage；守衛只看有沒有值與閒置 30 分鐘 | `demo-session.service.ts:26-29`、`:100-105`；`demo-session.guard.ts:9-12` |
| 登入頁寫死三個身分與密碼 `1234` | `demo-login-page.component.ts:17-21`、`:48`；`demo-login-page.component.html:23` |
| `AuthService` 是舊的假登入（`sa.auth.session`），只剩側欄登出在用 | `core/auth/auth.service.ts:4-17`；`side-nav.component.ts:34`、`:71-74` |
| 角色 3 種、權限 7 種；三個 Demo 帳號的初始權限 | `account.model.ts:20-30`；`demo-seed.ts:101-127` |
| 權限說明（label、是否真的被檢查）是前端常數 | `team.model.ts:40-94` |
| 錯誤對應：未登入／逾時 `401`；無權限與不存在都是 `403` + `{ reason, message }` 且內容完全相同；驗證失敗 `422` | `mock-to-api-mapping.md:67-71`；`tasks-6-10-backend-handoff.md:98-106`、`:127` |
| 建議 endpoint：`GET /api/v1/team`、`PUT /api/v1/team/members/{id}/permissions`；`listAccounts` 應改成 `share-targets`，不要做成帳號目錄 | `mock-to-api-mapping.md` §2.6（`listAccounts`／`getTeam`／`updateMemberPermissions` 三列） |
| 同步→非同步是替換主體；`computed()` 不能直接放 Observable，要改成 `toSignal`／resource，初始值 `loading` | `mock-to-api-mapping.md` §4.3 |
| 本機只有 .NET SDK 8.0.407／8.0.423；Docker CLI 29.6.1 在，但 colima 未啟動 | `dotnet --list-sdks`、`colima status` |

### 2.1 版本查證（NuGet／npm／映像，2026-09-25）

| 項目 | 版本 | 授權／來源 |
| --- | --- | --- |
| .NET SDK | 10.0.401（10.0 為 active LTS，2026-09-08 發布） | Microsoft |
| `Microsoft.AspNetCore.OpenApi`、`Microsoft.Extensions.ApiDescription.Server`、`Microsoft.AspNetCore.Identity.EntityFrameworkCore`、`Microsoft.EntityFrameworkCore.Design`、`Microsoft.AspNetCore.Mvc.Testing` | 10.0.12 | MIT |
| `Npgsql.EntityFrameworkCore.PostgreSQL`、`Npgsql.OpenTelemetry` | 10.0.3 | PostgreSQL License |
| `OpenIddict.AspNetCore`、`OpenIddict.EntityFrameworkCore` | 7.7.1 | Apache-2.0 |
| `OpenTelemetry.Extensions.Hosting`、`.Exporter.OpenTelemetryProtocol` | 1.19.1 | Apache-2.0 |
| `OpenTelemetry.Instrumentation.AspNetCore`、`.Http` | 1.19.0 | Apache-2.0 |
| `xunit.v3` 4.0.1、`xunit.runner.visualstudio` 4.0.0、`Microsoft.NET.Test.Sdk` 18.10.1 | — | Apache-2.0／MIT |
| `Shouldly` 4.3.0、`Testcontainers.PostgreSql` 4.15.0 | — | BSD-3／MIT |
| `@nx/dotnet` | 23.1.0（與 `nx` 23.1.0 對齊；latest 23.2.1） | MIT，nrwl 官方 |
| `openapi-typescript` | 7.13.0 | MIT，維護者 Drew Powers 等（非中國團隊） |
| `oidc-client-ts` | 3.5.0 | Apache-2.0，authts（維護者在瑞士／德國），無 peer dependency |
| 映像 | `pgvector/pgvector:0.8.6-pg18`、`mcr.microsoft.com/dotnet/aspire-dashboard:13.5.2` | PostgreSQL License／MIT |

實作當下若有新的 patch 版，照 ADR「套件版本鎖定」以 `Directory.Packages.props` 一次更新並跑完整測試。

---

## 3. 關鍵選擇

**Nx 整合 .NET：採用官方 `@nx/dotnet@23.1.0`。** 證據：`npm view @nx/dotnet` 顯示 MIT、repo `nrwl/nx`、維護者為 nrwl；`npm pack` 解開後 `dist/plugins/create-nodes.js` 以 MSBuild 分析 `.csproj` 推論 `build`／`test`／`restore`／`publish`／`pack`／`watch`／`run`／`clean` 目標，`create-dependencies.js` 依 `ProjectReference` 建相依圖。版本必須與 `nx` 完全一致（`package.json:55` 為 23.1.0，所以不用 latest 23.2.1）。不採 `@nx-dotnet/core`（社群、單一維護者）。若 plugin 推論的目標在 CI 出問題，退路是在 `apps/api/project.json` 手寫 `nx:run-commands`，不影響其他 Slice。

**OpenAPI → TS：採用 `openapi-typescript@7.13.0`，只產生型別**（`paths`／`components`），呼叫仍用 Angular `HttpClient`，保留攔截器。已知風險：它的 peer dependency 是 `typescript ^5.x`，而本 repo 是 `typescript ~6.0.2`（`package.json:59`）。Slice 7 的第一步就是驗證安裝；若 `npm install` 報 ERESOLVE，在根 `package.json` 加 `overrides`（`"openapi-typescript": { "typescript": "$typescript" }`）並確認產出正確；兩者都不行才換 `@hey-api/openapi-ts`（MIT，peer 已含 TS 6，但仍是 0.x）。

**登入流程：Authorization Code + PKCE。** OpenIddict 的 `/connect/authorize` 在未登入時導向 Angular 的 `/login?returnUrl=...`；登入頁 `POST /api/v1/auth/login` 取得 Identity cookie（HttpOnly、SameSite=Strict，開發時經 Angular proxy 同源），再回到 authorize 換 code → `/connect/token`。前端以 `oidc-client-ts` 處理 PKCE 與回呼。好處：保留現有登入頁設計；日後組織改接自己的 OIDC 時，只是 authorize 端點多一個外部 provider，前端不動。

**權限不放進 token。** Access token 只帶 `sub`、`org_id`、`role`；七個權限每個請求從資料庫讀（同一請求內快取）。這樣團隊面板改權限「立即生效」的現有行為得以保留（`demo-repository.ts:268-272` 的契約註解）。

**前端的橋接：M1 保留 `AccountId` 字面值 union。** 其他功能區仍是 mock，mock 資料以 `account-smb-admin` 等 id 為鍵（`demo-seed.ts:101-127`）。API 模式下，前端依 `/me` 回傳的 `role` 對應到同角色的 Demo 身分 id，再交給 `DemoSessionService.switchAccount()`，47 個 `activeAccountId()` 呼叫點都不用改。這個對應表只存在於 API 模式的 adapter，隨各功能區換成真 API 時逐步移除（放寬 union 屬於那時的工作，見 `mock-to-api-mapping.md` §4.2）。

---

## 4. 專案結構

```
global.json                          # 根目錄：{"sdk":{"version":"10.0.401","rollForward":"latestPatch"}}
.config/dotnet-tools.json            # dotnet-ef 10.0.12（local tool，版本鎖定）
apps/api/
  SmartAgri.slnx
  Directory.Build.props              # net10.0、Nullable、TreatWarningsAsErrors、RestorePackagesWithLockFile
  Directory.Packages.props           # ManagePackageVersionsCentrally=true，所有版本在此
  openapi/v1.json                    # 建置時產生並提交，前端型別的來源
  src/SmartAgri.Domain/              # 實體、權限列舉、IOrganizationScoped；不依賴任何套件
  src/SmartAgri.Infrastructure/      # DbContext、migrations、Identity、OpenIddict 資料表、種子
  src/SmartAgri.Api/                 # Minimal API endpoints、OpenIddict server、授權 policy、OTel、Dockerfile
  tests/SmartAgri.Domain.Tests/      # 純單元測試，不需要 Docker
  tests/SmartAgri.Api.Tests/         # WebApplicationFactory + Testcontainers 的整合測試
deploy/
  docker-compose.yml                 # 客戶部署：api + postgres(pgvector)
  docker-compose.dev.yml             # 開發：postgres + aspire-dashboard（api 用 dotnet 直接跑）
  .env.example
```

**為什麼是三個正式專案：** Domain 要能在不碰 EF／ASP.NET 的情況下做單元測試，並防止業務程式碼直接依賴基礎設施（backend-stack ADR 的「穩定邊界」）；Infrastructure 集中所有第三方套件，替換時範圍明確。M1 **不建** Application 層：目前只有登入與團隊權限，endpoint 直接呼叫小型 service 即可；M2 出現背景處理與檢索編排時再加。不引入 MediatR／AutoMapper（testing ADR 禁用），DTO 手寫對應。**不建** Aspire AppHost 專案：儀表板用獨立容器即可滿足 observability ADR，且 compose 同時是地端交付物，只維護一份。

---

## 5. Vertical slices

### Slice 1｜工具鏈與 .NET 骨架 + PR CI
- **目的：** 讓 `nx` 同時管理前後端，並在 PR 上有 CI。
- **內容：**
  - 本機前置：安裝 .NET 10 SDK（官方 macOS arm64 安裝檔，與 SDK 8 並存）；`nvm use`（Node 24）。
  - 根目錄 `global.json`、`.config/dotnet-tools.json`；`apps/api` 如第 4 節建立 5 個專案與 `SmartAgri.slnx`；`Directory.Packages.props` 先放第 2.1 節用得到的套件。
  - `npm i -D @nx/dotnet@23.1.0`，`nx.json` 的 `plugins` 加入 `@nx/dotnet`。
  - Api 只有 `GET /health/live`。
  - 新增 `.github/workflows/ci.yml`（`pull_request` + push `master`）：`actions/setup-node`（`.nvmrc`）、`actions/setup-dotnet`（`global-json-file: global.json`）、`npm ci`、`dotnet restore --locked-mode`、`npx nx run-many -t lint test build`。
  - 禁用套件檢查腳本 `tools/check-banned-packages.sh`：`Directory.Packages.props` 與所有 `packages.lock.json` 出現 `MediatR`、`AutoMapper`、`FluentAssertions`、`Hangfire` 即失敗；CI 執行。
  - `deploy-pages.yml` 不動。
- **驗收：**
  - `dotnet --version` 在 repo 根目錄回 `10.0.4xx`。
  - `npx nx show projects` 列出 admin 與五個 .NET 專案；`npx nx run-many -t build test` 全綠。
  - 在分支故意加入 `<PackageVersion Include="AutoMapper" …/>`，CI 的 banned-packages 步驟失敗；移除後通過。
  - PR 上看到 CI 綠燈；`deploy-pages.yml` 無 diff。
- **依賴：** 無。

### Slice 2｜PostgreSQL、EF Core migration、docker compose
- **目的：** 有真的資料庫與可重複的 schema 管理，測試用真 PostgreSQL。
- **內容：**
  - `AppDbContext`（Npgsql）；第一個 migration 只含 `CREATE EXTENSION IF NOT EXISTS vector`（為 M2 預留，不建任何向量表）。
  - `deploy/docker-compose.dev.yml`：`pgvector/pgvector:0.8.6-pg18`，資料卷、healthcheck；`deploy/docker-compose.yml`：api（`apps/api/src/SmartAgri.Api/Dockerfile`，非 root）+ postgres，連線字串由環境變數注入。
  - 啟動時**不**自動 migrate；提供 `SmartAgri.Api migrate` 子命令（容器啟動腳本先跑 migrate 再跑 api）。
  - `GET /health/ready` 檢查 DB。
  - 測試共用 `PostgresFixture`（Testcontainers，同一映像），每個測試類別一個資料庫。
  - README 片段：本機 `colima start --cpu 4 --memory 4`（啟動前先看 `sysctl -n vm.swapusage`），並設定 `DOCKER_HOST=unix://$HOME/.colima/default/docker.sock`、`TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock`。
- **驗收：**
  - `docker compose -f deploy/docker-compose.dev.yml up -d` 後，`dotnet ef database update` 成功；再跑一次仍成功（冪等）。
  - `curl -s -o /dev/null -w "%{http_code}" localhost:5280/health/ready` 為 `200`；停掉 postgres 後為 `503`。
  - `docker compose -f deploy/docker-compose.yml up --build` 從空資料庫起得來，`/health/ready` 為 `200`。
  - 整合測試在 CI（ubuntu-latest 內建 Docker）通過。
- **依賴：** 1。

### Slice 3｜OpenTelemetry 與 Aspire 儀表板
- **目的：** 從第一個 endpoint 起就有追蹤，M2 的模型呼叫耗時／token 可以直接接上。
- **內容：**
  - Api 註冊 OTel traces／metrics／logs（ASP.NET Core、HttpClient、Npgsql instrumentation），OTLP exporter，端點由 `OTEL_EXPORTER_OTLP_ENDPOINT` 決定；未設定時不輸出。
  - 自訂 `ActivitySource("SmartAgri")`，span 帶 `smartagri.organization_id`（不帶帳號名稱或 email）。
  - `docker-compose.dev.yml` 加 `mcr.microsoft.com/dotnet/aspire-dashboard:13.5.2`（UI 18888、OTLP 4317）。正式 `docker-compose.yml` **不**包含任何監控服務。
- **驗收：**
  - 開發環境打 `/health/ready` 後，`http://localhost:18888` 的 Traces 出現該請求與一段 Npgsql span。
  - 整合測試以 in-memory exporter 斷言：一次請求至少產生一個 `Microsoft.AspNetCore` span。
  - `grep -c aspire deploy/docker-compose.yml` 為 `0`。
- **依賴：** 2。

### Slice 4｜組織與帳號模型、全域查詢篩選、跨組織隔離測試
- **目的：** 落實「所有資料帶 `OrganizationId`、每次查詢由服務端過濾」，並用測試防止漏網。
- **內容：**
  - Domain：`Organization`（Id、Name、Code：組織代碼，全系統唯一、登入時使用）、`IOrganizationScoped { Guid OrganizationId }`、`AccountRole`（3 值）、`AccountPermission`（7 值），序列化名稱與 `account.model.ts:20-30` 的 kebab-case 字串完全相同（`JsonStringEnumMemberName`）。
  - Infrastructure：`Account : IdentityUser<Guid>, IOrganizationScoped`（LoginName、DisplayName、Role）；帳號名稱只需在同一組織內唯一：`(OrganizationId, NormalizedLoginName)` 唯一索引；Identity 內部的 `UserName` 存成「組織代碼/帳號名稱」組合值以滿足 Identity 的全域唯一索引，畫面與 API 一律只顯示 `LoginName`（見 authentication ADR）；`AccountPermissions` 資料表（AccountId、Permission、OrganizationId）。
  - `IOrganizationContext`：由已驗證的 `org_id` claim 取得；沒有時為「無組織」，此時所有受篩選的查詢回傳空集合（不是全部）。
  - `AppDbContext` 在 `OnModelCreating` 掃描所有 `IOrganizationScoped` 實體，套用具名查詢篩選 `"Organization"`（EF Core 10 named filters，日後可再加軟刪除篩選而不互相覆蓋）。
  - `SaveChanges` interceptor：新增時自動填入目前組織；修改或新增別的組織的資料一律丟例外。
  - 登入查帳號是唯一允許 `IgnoreQueryFilters()` 的地方，集中在 `AccountLookup`，並以測試鎖住呼叫點數量。
- **驗收（皆為 `SmartAgri.Api.Tests` 的 Testcontainers 測試）：**
  - 組織 A、B 各建一個同名帳號 `admin` 皆成功；同一組織內建第二個 `admin` 失敗。
  - 在組織 A、B 各建帳號；以 A 的 context 查 `Accounts` 只看得到 A；以「無組織」查為空。
  - 以 A 的 context 儲存一筆 `OrganizationId = B` 的資料 → 例外，資料庫無寫入。
  - 模型測試：`AppDbContext.Model` 中除了白名單（`Organization`、Identity 的 role 表、OpenIddict 四張表）之外，每個實體都實作 `IOrganizationScoped` 且帶 `"Organization"` 篩選；之後任何人新增實體忘了加，這個測試就會失敗。
  - 原始碼測試：`IgnoreQueryFilters` 在 `apps/api/src` 只出現在 `AccountLookup`。
- **依賴：** 2。

### Slice 5｜登入：Identity + OpenIddict、`/me`、401／403 規則
- **目的：** 真實帳密登入並核發 token；API 知道「誰、屬於哪個組織、有哪些權限」。
- **內容：**
  - OpenIddict server（EF Core stores）：authorization、token、end-session、userinfo 端點；只啟用 authorization code + PKCE（public client `admin-spa`，redirect `/auth/callback`）；開發用暫時簽章金鑰，正式環境金鑰路徑由設定提供（沒有就拒絕啟動）。
  - `GET /api/v1/auth/login-options` → `{ organizationCodeRequired }`：資料庫只有一個組織時為 `false`，登入頁可隱藏組織代碼欄位。
  - `POST /api/v1/auth/login`（組織代碼＋帳號＋密碼 → Identity cookie；只有一個組織時組織代碼可省略；`204`；錯誤一律 `401` 同一訊息，不區分組織代碼錯、帳號不存在或密碼錯）、`POST /api/v1/auth/logout`；Identity lockout 開啟。
  - Access token 內容：`sub`、`org_id`、`role`；存活 30 分鐘（對齊 `DEMO_SESSION_TIMEOUT_MS`，`demo-session.service.ts:29`）；M1 不發 refresh token。
  - 授權 policy：每個 `AccountPermission` 一條 `RequirePermission(...)`，從資料庫讀權限。
  - `GET /api/v1/me` → `{ id, displayName, role, permissions[], organization: { id, name } }`。
  - 錯誤格式：`401` 無 body；`403` 為 ProblemDetails 加 `reason`、`message`；`422` 加 `errors`。共用 helper 保證「不存在」與「無權限」產生位元組相同的 `403`。
- **驗收：**
  - 整合測試走完整 PKCE：login → authorize → token → `/me` 回傳正確的 role 與權限。
  - 錯密碼、不存在的帳號與不存在的組織代碼回應 body 相同；兩個組織各有 `admin` 時，以各自組織代碼登入拿到各自的 `/me`。
  - 錯密碼與不存在的帳號回應 body 相同；連續 5 次錯誤後帳號鎖定。
  - 沒帶 token 打 `/me` 為 `401`；token 過期為 `401`。
  - 以 A 組織 token 帶 B 組織資源 id 與不存在的 id 打同一個受保護端點（Slice 8 的 `PUT .../members/{id}/permissions` 完成後補上），兩者 status 與 body 完全相同。
  - token 解碼後沒有 `permissions` claim。
- **依賴：** 4。

### Slice 6｜開發種子資料
- **目的：** 本機與 E2E 有和 Demo 一致的三個帳號，且正式設定不含任何預設密碼。
- **內容：**
  - `DevelopmentSeeder` 只在 `Development` 環境註冊：組織「安心商行」（組織代碼 `anxin`）+ 三個帳號（`admin`／`internal`／`customer`，顯示名稱、角色、初始權限照 `demo-seed.ts:101-127`）；另一個組織「對照組織」（組織代碼 `control`）+ 一個同名的 `admin` 管理者，供手動檢查隔離。
  - 密碼來自環境變數 `SEED_DEMO_PASSWORD`（`.env.example` 說明），**沒有預設值**，未設定就讓 seeder 失敗並寫明原因；密碼需符合 Identity 預設強度，所以不可能是 `1234`（authentication ADR）。
  - 冪等：已存在就只補缺的權限，不覆寫手動改過的權限。
- **驗收：**
  - 以 `ASPNETCORE_ENVIRONMENT=Production` 啟動整合測試主機，資料庫中帳號數為 `0`。
  - CI 步驟：`grep -rn '1234' apps/api/src --include='appsettings*.json'` 無結果；`deploy/` 下無 `SEED_DEMO_PASSWORD` 的實際值。
  - 開發環境跑兩次 seeder，帳號仍為 4 個；`admin` 登入後 `/me` 權限等於 `demo-seed.ts:106-111`。
- **依賴：** 4（驗收中的登入部分需 5）。

### Slice 7｜OpenAPI 文件 → 前端型別 + CI 差異檢查
- **目的：** 前後端型別單一來源，任何一邊改了沒同步就擋下。
- **內容：**
  - Api 使用 `Microsoft.AspNetCore.OpenApi`；`Microsoft.Extensions.ApiDescription.Server` 在 build 時輸出 `apps/api/openapi/v1.json`（`OpenApiDocumentsDirectory`），並提交。
  - 先驗證 `npm i -D openapi-typescript@7.13.0` 在 TS 6 下的安裝（見第 3 節的退路）。
  - Nx 目標 `admin:api-types`：`dependsOn` Api 的 `build`，執行 `openapi-typescript apps/api/openapi/v1.json -o apps/admin/src/app/core/api/api-schema.ts`；inputs／outputs 設好以利快取。
  - 型別對齊測試 `core/api/api-schema.spec.ts`：以雙向可指派檢查確認產生的權限／角色 union 等於 `AccountPermission`、`AccountRole`（`account.model.ts:20-30`）。
  - CI 在 build 之後執行 `npx nx run admin:api-types && git diff --exit-code -- apps/api/openapi apps/admin/src/app/core/api`。
- **驗收：**
  - 本機執行 `admin:api-types` 後 `git status` 乾淨。
  - 在分支替 `/me` 回應加一個欄位但不重產型別 → CI 的 drift 步驟失敗；重產並提交後通過。
  - 把 C# 的某個權限序列化名稱改掉 → `api-schema.spec.ts` 型別檢查失敗。
  - `v1.json` 內無 `company` 字樣（organization-naming ADR）。
- **依賴：** 5。

### Slice 8｜團隊與權限 API
- **目的：** 取代 mock 的 `getTeam`／`updateMemberPermissions`，權限規則與 Demo 相同。
- **內容：**
  - `GET /api/v1/team` → `{ members: [{ id, displayName, role, permissions, lockedPermissions }], savedAt }`；需 `manage-assistants`。權限描述（label、`enforcedNote`）仍是前端常數（`team.model.ts:40-94`），API 不重複。
  - `PUT /api/v1/team/members/{id}/permissions`：需 `manage-assistants`；成員不存在、屬於其他組織、無權限 → 相同的 `403 { reason: "team" }`，訊息不含成員名稱；移除自己的 `manage-assistants` 或不認得的權限值 → `422` 且完全不寫入（`team.model.ts:137-153` 的規則）；權限依 `ACCOUNT_PERMISSIONS` 的順序正規化後儲存。
  - 同步更新 `openapi/v1.json` 與前端型別（Slice 7 的 CI 會檢查）。
- **驗收（整合測試）：**
  - admin 取得 3 位成員，不含「對照組織」的帳號。
  - internal（無 `manage-assistants`）取 team → `403 team`，body 與「帶不存在成員 id」的 PUT 相同。
  - admin 移除自己的 `manage-assistants` → `422`，資料庫不變。
  - admin 移除 internal 的 `read-consented-submissions` 後，internal 的 `/me` 立即反映（同一顆 token，不需重新登入）。
- **依賴：** 5、7。

### Slice 9｜前端 API 模式：登入、工作階段、`/me`
- **目的：** 後台殼層使用真實登入；Pages Demo 不受影響。
- **內容：**
  - 新增 `apps/admin/src/environments/environment.ts`（`apiMode: false`）與 `environment.api.ts`（`apiMode: true`）；`project.json` 新增 `api` build 設定（`fileReplacements`）與 `serve` 的 `api` 設定（`proxyConfig: apps/admin/proxy.api.json`，把 `/api`、`/connect` 轉到 `http://localhost:5280`）。`production` 設定不變，Pages 仍是 mock。
  - `app.config.ts` 在 API 模式才 `provideHttpClient(withFetch(), withInterceptors([bearerToken, unauthorized]))`；`oidc-client-ts` 只在 API 模式以動態 import 載入。
  - `ApiSessionService`：登入頁提交 → `POST /api/v1/auth/login` → 回到 `returnUrl`（authorize）→ `/auth/callback` 完成 PKCE → `GET /me` → 依 `role` 對應 Demo 身分 id 後呼叫 `DemoSessionService.switchAccount()`；另外公開 `permissions` signal（mock 模式由 `listAccounts()` 推得）。token 存在該分頁的 sessionStorage（沿用 Demo「一個分頁一個身分」的語意）。
  - 任何 API 回 `401` → 清除工作階段並導到 `/login`，顯示既有逾時說明。
  - 登入頁：API 模式依 `login-options` 顯示或隱藏「組織代碼」欄位，上次輸入的組織代碼記在 localStorage；隱藏三個 Demo 身分與「密碼都是 1234」說明（`demo-login-page.component.html:23`），錯誤訊息改為不區分帳號或密碼。
  - 助理清單的 `canCreateAssistant` 改讀 `permissions` signal，不再呼叫 `listAccounts()`（`assistant-list-page.component.ts:28-35`）。
  - 側欄登出改走 `ApiSessionService.logout()`（API 模式呼叫 end-session）；刪除舊的 `core/auth/auth.service.ts` 與未掛路由的 `features/auth/pages/login-page.component.ts`。
- **驗收：**
  - `npx nx test admin` 全過，新增 `ApiSessionService`（HttpTestingController）與 `unauthorized` 攔截器的測試。
  - `npx nx build admin --configuration=production` 通過預算（`project.json:33-44`），且 `grep -l "connect/authorize" dist/smart-agri-admin/browser/*.js` 無結果。
  - 本機：compose dev + `dotnet run` + `npx nx serve admin --configuration=api`，以組織代碼 `anxin`、`admin` 帳號登入 → `/app/home`；側欄顯示「安心商行管理者」；登出後直接開 `/app/home` 被導回 `/login`。
  - 用 `customer` 登入時，助理清單沒有「建立助理」入口。
  - 現有 Cypress 套件（mock 模式）全綠。
- **依賴：** 5、6、7。

### Slice 10｜前端團隊面板改走 HTTP（非同步契約）
- **目的：** 建立 M2 之後逐區替換要沿用的「非同步契約 + 混合 repository」模式。
- **內容：**
  - `DemoRepository` 只改兩個方法：`getTeam(): Observable<RepositoryView<TeamView>>`、`updateMemberPermissions(memberId, permissions): Observable<UpdateMemberPermissionsResult>`，拿掉 `viewerAccountId`（依 `mock-to-api-mapping.md` §4.2）。`MockDemoRepository` 以 `of(...)` 實作，行為不變。
  - `HybridDemoRepository`（僅 API 模式）：這兩個方法走 HTTP 並把回應轉成 `TeamView`（成員 id 用 Slice 9 的對應表換回 Demo 身分 id）；其餘方法全部委派給 `MockDemoRepository`。`tokens.ts` 的 factory 依 `apiMode` 決定回傳哪一個。
  - `MockDemoRepositoryOptions` 新增 `accountsSource`：API 模式下 mock 的 `accounts()`（`mock-demo-repository.ts:2979`）改用 API 取得的帳號與權限，讓仍在 mock 的功能區（例如發布、收集紀錄）的權限檢查與真實權限一致。
  - 團隊面板改用 `rxResource`（初始 `loading`，模板已有該分支），儲存後重新載入；拿掉 `revision` 遞增（`team-panel.component.ts:30-37`）。
- **驗收：**
  - `mock-demo-repository-team.spec.ts` 的 7 個案例改寫為非同步後全過；新增 `HybridDemoRepository` 的 HttpTestingController 測試（403 team、422 自鎖、成功後重新載入）。
  - API 模式手動：admin 在團隊面板移除 internal 的 `read-consented-submissions` → 改用 internal 登入，數據庫收集紀錄顯示權限不足（mock 區域吃到了真實權限）。
  - mock 模式（Pages）團隊面板行為與改版前相同，Cypress 全綠。
- **依賴：** 8、9。

### Slice 11｜首次安裝初始化：`setup` 指令與首次登入改密碼
- **目的：** 客戶自行部署時，有安全且一致的方式建立第一個組織與管理者；不依賴開發用種子資料。
- **內容：**
  - API 專案支援指令模式：`docker compose run --rm api setup`（本機為 `dotnet run --project apps/api/src/SmartAgri.Api -- setup`），執行完即結束，不啟動 Web 伺服器。
  - 互動詢問組織名稱、組織代碼、管理者帳號名稱與顯示名稱；也可用參數提供（`--organization-name`、`--organization-code`、`--admin-login`），方便自動化部署。組織代碼建立後不可修改（authentication ADR）。
  - 自動產生符合密碼強度的一次性密碼，只在終端機顯示一次，不寫入任何檔案或日誌。
  - 管理者角色為 smb-admin，並給予全部 7 項權限；帳號標記「首次登入須改密碼」。
  - 資料庫已有任何組織時拒絕執行，結束代碼非 0，並說明原因。
  - 標記「須改密碼」的帳號登入後，API 只允許 `GET /api/v1/me` 與 `POST /api/v1/auth/change-password`，其餘端點回 `403`（`reason: password-change-required`）。
  - 前端（API 模式）：`/me` 帶 `passwordChangeRequired: true` 時導到「設定新密碼」頁，完成後才進入後台。
- **驗收：**
  - 整合測試：空資料庫執行 `setup` → 建立 1 個組織、1 個管理者；再執行一次 → 結束代碼非 0，組織數仍為 1。
  - 一次性密碼不出現在應用程式日誌與 OpenTelemetry 輸出中（以測試擷取日誌比對）。
  - 以一次性密碼登入後呼叫其他受保護端點得到 `403 password-change-required`；改密碼後 `/me` 的 `passwordChangeRequired` 為 `false`，端點恢復正常；舊的一次性密碼無法再登入。
  - 本機：`docker compose -f deploy/docker-compose.yml run --rm api setup` 在全新資料庫可完成初始化，並能用輸出的密碼從前端登入、被要求改密碼。
- **依賴：** 4、5（前端頁面需 9）。

**相依摘要：** 1 → 2 → {3, 4}；4 → 5 → {6, 7}；{5, 7} → 8；{5, 6, 7} → 9；{8, 9} → 10；{4, 5} → 11（前端部分需 9）。Slice 3 可與 4–8 平行。

---

## 6. 風險與待確認

1. **`openapi-typescript` 與 TypeScript 6 的 peer dependency 衝突**：Slice 7 第一步驗證；退路已寫在第 3 節。
2. ~~帳號名稱唯一的範圍~~ **已決定（2026-09-25）**：帳號名稱只需在同一組織內唯一，登入時以組織代碼區分；只有一個組織的部署可省略組織代碼（見 Slice 4、5、9）。
3. ~~前端 token 存放與更新~~ **已決定（2026-09-25）**：照 M1 暫定——分頁 sessionStorage、30 分鐘到期重新登入、不發 refresh token；閒置逾時與 refresh token 之後另議。
4. **角色 → Demo 身分的橋接**：只在「每個角色恰好一個帳號」時成立（M1 的種子資料如此）。在所有功能區換成真 API 之前，若有人在 API 模式的組織內新增第二個同角色帳號，mock 區域會把兩人當成同一位 Demo 身分。M1 不提供新增帳號功能，所以風險只存在於手動改資料庫。
5. ~~正式環境第一位管理者怎麼建立~~ **已決定（2026-09-25）**：以 `setup` 指令建立，產生一次性密碼、首次登入須改密碼，資料庫已有組織時拒絕執行（見 Slice 11、on-prem-packaging ADR）。
6. **本機資源**：Testcontainers 與 compose 都需要 colima；依過往經驗，記憶體吃緊時背景程序會被中止，啟動前先看 swap。

---

## 7. 不在 M1 範圍

- 知識庫、數據庫、對話、發布的任何真實 API（M2 以後，依 milestone-order ADR）；`listAccounts` 對應的 `share-targets` 端點屬於發布區。
- pgvector 資料表、背景工作佇列、`IChatClient`／Agent Framework、AG-UI 串流。
- 組織自己的 OIDC／AD 單一登入、匿名訪客與 LINE 使用者的身分流程（只保留：token 帶 `org_id`、OpenIddict 可再加外部 provider）。
- 帳號的邀請、停用、移除；組織的建立與管理畫面；密碼重設與 email 寄送。
- Refresh token、多因素驗證。
- 機敏設定加密（secrets-storage ADR，M5 發布管道時實作）。
- 前端 `AccountId` 等字面值 union 放寬成 `string`、`company-*` 識別名稱改名（隨各功能區換 API 時一併處理）。
- CI 內跑「API 模式」的 Cypress E2E（M1 只做本機手動驗收；需要時另開票）。
- Helm chart、正式環境監控系統的打包。
