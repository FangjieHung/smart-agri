# 後端 Milestone 2｜知識庫：實作計畫

**日期：** 2026-09-26
**狀態：** 已確認（2026-09-26）。第 7 節的決定事項已照建議定案，可以拆票。
**依據：** [業務流程審查](../reviews/2026-09-26-project-review-and-backlog.md)（`8d426fc`、`9c64ab2`）、[M1 計畫](2026-09-25-backend-milestone-1-skeleton.md)、`docs/adr/2026-09-25-*.md`（特別是 milestone-order、assistant-access-to-knowledge-and-databases、grounded-answers、background-jobs-on-postgresql、postgresql-as-single-store、llm-providers-and-data-residency、backend-stack、testing-and-banned-dependencies、frontend-backend-integration）、`docs/handoff/mock-to-api-mapping.md` §2.2／§3.2／§3.3／§4.2、`docs/handoff/tasks-6-10-backend-handoff.md` §3。
**拆票方式：** 與 M1 相同。每個 Slice 都是可單獨合併的垂直切片，各自附測試與驗收條件。Slice 編號是建議順序，「依賴」欄只列硬依賴。

---

## 1. 目標與非目標

**目標（M2 交付）：** 新組織不必依靠種子資料，就能在 API 模式完成審查文件建議的第一個業務閉環：

> 建立知識庫 → 批次上傳一組有文字層的文件 → 看到逐檔結果與抽取預覽 → 人工確認有效版本 → 用真實檢索試查，核對引用的文件、版本與頁碼／章節。

「開放助理使用」與模型生成答案屬於 M3（平台內對話）。M2 驗證的是「文件 → 可讀內容 → 段落與向量 → 檢索」這一段是否正確、可追溯、權限正確。

具體完成標準：

1. 知識庫、文件、版本、段落與向量都存在 PostgreSQL，全部帶 `OrganizationId`，沿用 M1 的具名查詢篩選與寫入保護。跨組織、跨知識庫的隔離有測試。
2. 上傳改為真實檔案：每檔獨立上傳、獨立重試，有格式、大小與重複內容檢查。原檔、可讀內容、來源位置、版本與權限之間的關聯完整保存。
3. 文件處理由 PostgreSQL 佇列加 `BackgroundService` 執行。五種處理狀態（等待、處理中、可使用、部分可讀、失敗）由後端決定，前端不再用計時器假造進度。
4. 解析支援 PDF（文字層）、DOCX、XLSX、TXT／Markdown，另外支援手動輸入的 FAQ。掃描頁、加密檔與非 UTF-8 文字檔都明確標示原因，不會被標成「可使用」。
5. 每個版本都要由擁有者確認生效後才能被檢索。新版本確認之前，舊的有效版本照常使用；「緊急停用」會立即停止檢索。版本與操作人都可追溯。
6. 檢索經過 `IEmbeddingGenerator` 與 `Microsoft.Extensions.VectorData` 抽象。試查結果列出文件、版本、頁碼／章節、原文摘錄與分數，並標示結果是否低於「查無結果」門檻。
7. 每次嵌入模型呼叫都寫入可稽核紀錄（組織、帳號、模型、時間、token 用量，不存內容）。
8. 先修正審查指出的兩個高優先問題：API 模式的模擬資料依組織與帳號隔離，團隊畫面改用真實帳號 ID。完成後，API 模式才能放進真實業務資料。

**非目標：** 見第 8 節。

---

## 2. 現況事實（寫計畫時查證，2026-09-26，基準 `9c64ab2`）

| 事實 | 出處 |
| --- | --- |
| 後端有 3 個正式專案、2 個測試專案；還沒有 Application 層 | `apps/api/SmartAgri.slnx:1-11` |
| `AppDbContext` 只有 `Organizations`、`Accounts`、`AccountPermissions`，加上 OpenIddict 四張表；沒有任何知識、文件或工作相關的實體 | `AppDbContext.cs:61-66`、`:106` |
| 所有 `IOrganizationScoped` 實體會自動套用具名篩選 `"Organization"`；沒有組織時查詢結果為空 | `AppDbContext.cs:41`、`:154-194` |
| 寫入保護：`OrganizationSaveChangesInterceptor` 自動填入組織；跨組織或沒有組織時的寫入一律丟例外 | `Tenancy/OrganizationSaveChangesInterceptor.cs:22-112` |
| 非 HTTP 程式碼用 `FixedOrganizationContext`，註解已寫明是給「代表單一組織執行的背景工作」使用 | `Infrastructure/Tenancy/FixedOrganizationContext.cs:5-12` |
| 防漏測試：`OrganizationModelTests` 要求未列白名單的實體都必須受組織範圍限制；`IgnoreQueryFiltersSourceTests` 只允許 `AccountLookup.cs` 呼叫 `IgnoreQueryFilters` | `tests/.../Tenancy/OrganizationModelTests.cs:32-45`、`IgnoreQueryFiltersSourceTests.cs:15-31` |
| pgvector 擴充已建立，但沒有任何向量欄位，也沒有引用 `Pgvector.EntityFrameworkCore`、`Microsoft.Extensions.VectorData` | `AppDbContext.cs:97-98`、`Migrations/20260925051943_InitialCreate.cs:14`、`Directory.Packages.props:7-30` |
| 沒有任何背景工作程式碼 | `grep -rn "BackgroundService\|SKIP LOCKED" apps/api/src` 無結果 |
| Endpoint 採用各功能一個 `Map…Endpoints` 的 Minimal API 寫法；權限用 `.RequirePermission(perm, ForbiddenReason)` 檢查 | `Program.cs:74-77`、`Team/TeamEndpoints.cs:63-80`、`Authorization/PermissionPolicies.cs:40-47` |
| `ForbiddenReason` 只有 `Team`、`PasswordChangeRequired`、`Unspecified`，前端已預留 `'knowledge-base'` | `Errors/ForbiddenReason.cs:20-42`；`demo-repository.ts:98` |
| `manage-data-sources` 目前只用在建立資料庫；權限說明寫的是「從模板建立資料庫、取得資料庫模板」 | `mock-demo-repository.ts:1590`、`:2652-2654`；`team.model.ts:48-53` |
| 後端的 CLI 子命令有 `migrate`、`setup`；程式啟動時不會自動 migrate | `Program.cs:40-53` |
| 前端知識庫 id 是 4 個字面值組成的 union，文件 id 是 `` `document-${string}` ``；五種狀態定義在 `KNOWLEDGE_DOCUMENT_STATUSES` | `knowledge-base.model.ts:4-8`、`:29`、`:87-93` |
| 分享範圍是 `'private' \| 'specific-accounts' \| 'public'`，另有 `allowOriginalDownload` | `knowledge-base.model.ts:41-49` |
| 知識庫相關的契約方法全部是同步的，並帶 `viewerAccountId`；只有 `getTeam` 已改成 Observable | `demo-repository.ts:321`、`:406-464`、`:272` |
| `HybridDemoRepository` 只覆寫 `getTeam`、`updateMemberPermissions`，而且 403 的 reason 除了改密碼以外一律折成 `team` | `hybrid-demo-repository.ts:105-148`、`:170-183` |
| 詳情頁的「加入示範文件」不會讀檔；進度靠 900ms 的遞迴 `setTimeout` 呼叫 `advanceKnowledgeDocument` 推進 | `knowledge-detail-page.component.html:36-40`；`knowledge-detail-page.component.ts:28`、`:156-171` |
| 知識庫清單的空狀態只有文字，沒有「建立知識庫」入口 | `knowledge-list-page.component.html:40-43` |
| 精靈的試問從 `DEMO_SEED.trialQuestions` 挑固定答案 | `mock-demo-repository.ts:1232-1290`；`demo-seed.ts:393-417` |
| 知識庫的「連接助理」由 mock 助理的 `knowledgeBaseIds` 推得；精靈與助理設定頁透過 `listConnectableSources` 取得可連接的來源 | `assistant.model.ts:67`；`mock-demo-repository.ts:2740-2746`；`assistant-draft.store.ts:108`、`assistant-settings.store.ts:95` |
| 引用有兩種形狀：精靈用的 `TrialCitationView { sourceId, sourceName, excerpt }`，對話用的 `ChatCitationView { id, knowledgeBaseName, documentName, excerpt, updatedLabel }` | `assistant-draft.model.ts:111-144`；`conversation.model.ts:99-151` |
| CI 的 `build` job 會跑前後端的 lint／test／build（含 Testcontainers）與 OpenAPI 差異檢查；`e2e` job 只跑 mock 建置的 Cypress | `.github/workflows/ci.yml:9-28`、`:30-62` |
| API 的本機開發 port 是 **5153**（M1 計畫寫的 5280 有誤） | `apps/api/src/SmartAgri.Api/Properties/launchSettings.json` |

### 2.1 版本查證（NuGet，2026-09-26）

| 套件 | 最新正式版 | 授權 | 備註 |
| --- | --- | --- | --- |
| `Microsoft.Extensions.AI`／`.Abstractions` | 10.10.0／10.10.1 | MIT | backend-stack ADR 指定的穩定邊界 |
| `Microsoft.Extensions.AI.OpenAI` | 10.10.1 | MIT | 已是正式版，依賴 `OpenAI` 2.14.0 |
| `OpenAI` | 2.14.0 | MIT | 官方用戶端，用於 OpenAI 與 OpenAI 相容端點（vLLM 等） |
| `Azure.AI.OpenAI` | 2.1.0（最新為 2.9.0-beta.1） | MIT | 正式版較舊，先試用 `OpenAI` 用戶端連 Azure 的 v1 相容端點，行不通才引入 |
| `Microsoft.Extensions.VectorData.Abstractions` | 10.10.0 | MIT | |
| `Pgvector`／`Pgvector.EntityFrameworkCore` | 0.3.2／0.3.0 | MIT | **0.3.0 宣告的相依是 `Npgsql.EntityFrameworkCore.PostgreSQL` 9.0.1**（2025-12 發布），與本 repo 的 EF Core 10 相容性要先驗證，見第 7 節風險 1 |
| `PdfPig` | 0.1.16 | Apache-2.0 | 尚未到 1.0，照 ADR 採用 |
| `DocumentFormat.OpenXml` | 3.5.1 | MIT | |

照 ADR 的「套件版本鎖定」規則，版本一律寫在 `Directory.Packages.props`。實作時若出了新的 patch 版，就一次更新並跑完整測試。

---

## 3. 關鍵選擇

**新增 `SmartAgri.Application` 層。** M1 計畫已預告「M2 出現背景處理與檢索編排時再加」。Application 放處理管線、檢索、版本規則與背景工作的處理器，只能引用 Domain 與抽象套件（`Microsoft.Extensions.AI.Abstractions`、`Microsoft.Extensions.VectorData.Abstractions`）。PdfPig、OpenXml、Npgsql、OpenAI 用戶端都放在 Infrastructure。另加一個架構測試讀 `SmartAgri.Application.csproj`，出現其他套件就失敗，把 ADR「業務程式碼不直接依賴供應商 SDK」變成可以自動檢查的規則。

**段落與向量放在同一列、同一張表。** `knowledge_chunks.embedding` 用**不指定維度**的 `vector` 欄位，另外記錄 `embedding_model`。檢索時一律加上 `embedding_model = 目前設定` 的條件，並以精確搜尋排序：先依組織與知識庫篩選，再用餘弦距離排序。**M2 不建 HNSW 索引。** 理由：

- 中小企業單一知識庫的段落數在數萬以內，精確搜尋是毫秒級，而且召回率 100%。
- 不固定維度，換嵌入模型時就不必改 migration，只要跑 `reindex`（ADR：換嵌入模型需重建向量索引）。
- 段落與向量同一列，符合「同一個交易內寫入與刪除」。

量大時再用 pgvector 的「型別轉換＋部分索引」依模型建立 HNSW 索引，只影響 Infrastructure。

**VectorData 抽象的實作範圍。** 在 Infrastructure 實作 `VectorStoreCollection<Guid, KnowledgeChunkRecord>`，底層走 EF Core 加 `Pgvector.EntityFrameworkCore`。`KnowledgeChunkRecord` 就是 EF 對應的實體，所以 `VectorSearchOptions.Filter` 可以直接交給 EF 的 `Where` 轉譯，也自動套用組織篩選。只實作 M2／M3 會用到的方法（`SearchAsync`、`UpsertAsync`、`DeleteAsync`、`GetAsync`），其餘方法丟 `NotSupportedException` 並以測試鎖住。官方 connector 轉為正式版後，可以在不改 Application 的前提下替換。

**原檔存在 PostgreSQL。** 用獨立的 `knowledge_file_contents` 表（`bytea`），列表查詢不會載入內容。單檔上限預設 20 MB，可設定。理由：postgresql-as-single-store ADR 要求客戶只需維運一個資料庫；原檔和中繼資料放在同一個交易、同一份備份裡，刪除時不會留下孤兒檔。詳見 [原檔存放 ADR](../adr/2026-09-26-original-files-in-postgresql.md)。

**背景工作：一張 `background_jobs` 表、在 API 行程內執行。** 取件只用一條原生 SQL：`UPDATE … WHERE id = (SELECT id … WHERE status='queued' AND run_after <= now() ORDER BY run_after FOR UPDATE SKIP LOCKED LIMIT 1) RETURNING …`，並設定 `locked_until` 租約。處理時一律改用 `FixedOrganizationContext(job.OrganizationId)` 建立 DbContext，所以 M1 的篩選與寫入保護照常有效。取件的類別 `JobClaimer` 是除了 `AccountLookup` 以外，唯一允許跨組織的程式碼，原始碼測試要一併鎖住這一點。Worker 由設定 `Jobs:WorkerEnabled` 開關；整合測試關掉 worker，改呼叫 `JobRunner.RunUntilIdleAsync()`，結果才可重現。

**每個版本都要人工確認生效。** 處理結果是「可使用」只代表讀得到文字，不代表內容正確、版本有效（審查文件：「不能只因處理狀態是 `ready` 就自動供對外助理使用」）。可檢索條件集中寫成一個查詢規格，並以測試覆蓋所有組合：

> 段落未排除 **且** 所屬版本是該文件目前的有效版本（已確認、`effective_from ≤ now` 之中最新的一版）**且** 版本處理狀態為可使用或部分可讀 **且** 文件未被緊急停用 **且** `embedding_model` 等於目前設定。

生效日期直接寫在查詢條件裡，不需要排程工作去切換。為避免批次導入時要逐檔確認，提供「批次確認生效」。

**前端依 M1 Slice 10 的模式逐一替換。** 知識庫的契約方法改成回傳 Observable，拿掉 `viewerAccountId`。`MockDemoRepository` 以 `of(...)` 實作，讓 Pages Demo 也有新流程。`HybridDemoRepository` 在 API 模式走 HTTP。處理進度改成「詳情頁輪詢」：有等待中或處理中的項目時每 3 秒重新載入一次；Mock 依上傳後經過的時間算狀態。這樣元件不必分辨是哪一種模式，`advanceKnowledgeDocument` 也可以從契約移除（mapping §3.3）。依 frontend-backend-integration ADR，暫不使用 SignalR。

**權限。** 建立知識庫需要 `manage-data-sources`，前端的權限說明要一併更新（`team.model.ts:49-53`）。知識庫本身及其下所有資源的讀寫都照 mapping 的 `S+OWN`：只有擁有者可以操作。不存在與無權限都回位元組相同的 `403 { reason: "knowledge-base" }`，沿用 `ApiErrors.NotFound(reason)`。API 回應直接帶 `viewerCanManage` 等能力旗標，前端不再拿 `ownerAccountId` 和目前帳號比對，避免 API 模式下真實 GUID 與 Demo 身分 id 對不上。

**嵌入模型的設定層級。** M2 只做「每個部署」一組設定：`Ai:Embedding:Provider`（`OpenAI`／`AzureOpenAI`／`OpenAICompatible`）、`Endpoint`、`Model`、`ApiKey`，由環境變數注入；機敏設定加密依 secrets-storage ADR 在 M5 處理。Anthropic 沒有嵌入 API，所以用 Claude 回答的部署仍需另設嵌入來源，這點要寫進部署說明。另有 `Fake` 提供者，以雜湊產生固定向量，**只允許在 Development／Testing 環境啟用**，Production 設定成 `Fake` 會拒絕啟動。依組織選擇模型與後台設定畫面屬於之後的工作。

---

## 4. 資料模型

所有實體都實作 `IOrganizationScoped`，`OrganizationModelTests` 會自動檢查。

| 實體 | 主要欄位 | 說明 |
| --- | --- | --- |
| `KnowledgeBase` | Id、OwnerAccountId、Name、Purpose、SharingScope、AllowOriginalDownload、CreatedAt、UpdatedAt | 同組織內名稱不必唯一 |
| `KnowledgeBaseShare` | KnowledgeBaseId、AccountId | `specific-accounts` 的分享對象 |
| `KnowledgeDocument` | Id、KnowledgeBaseId、Kind（`document`／`faq`）、Name、DisabledAt、DisabledByAccountId、DisabledReason | 邏輯上的一份文件；版本掛在它底下 |
| `KnowledgeDocumentVersion` | Id、DocumentId、VersionNumber、FileName、ContentType、SizeBytes、Sha256、ProcessingStatus（5 種）、Issue、ReviewState（`pending-review`／`approved`）、EffectiveFrom、ApprovedByAccountId、ApprovedAt、UploadedByAccountId、UploadedAt | 「已封存」由查詢推得（已確認但不是目前有效版），不另存狀態 |
| `KnowledgeFileContent` | VersionId、Bytes | 原檔，與版本一對一 |
| `KnowledgeExtractedUnit` | VersionId、Ordinal、LocationKind（`page`／`section`／`sheet`／`faq`）、LocationLabel、Text、Readable、IssueCode | 抽取預覽的單位：PDF 的頁、DOCX／MD 的章節、XLSX 的工作表 |
| `KnowledgeChunk` | Id、VersionId、DocumentId、KnowledgeBaseId、UnitOrdinal、Ordinal、LocationLabel、Text、Excluded、Embedding（`vector`）、EmbeddingModel | 可檢索段落；KnowledgeBaseId 為了篩選而重複存放 |
| `KnowledgeActivity` | Id、KnowledgeBaseId、DocumentId?、VersionId?、Action、ActorAccountId?、At、Detail（jsonb，不含文件內容） | 上傳、確認生效、停用、排除段落、改分享等操作紀錄 |
| `BackgroundJob` | Id、Kind、Payload（jsonb）、Status、Attempts、MaxAttempts、RunAfter、LockedUntil、LastError、CreatedAt、CompletedAt | 佇列表，見第 3 節 |
| `ModelInvocation` | Id、AccountId?、AssistantId?、Purpose（`embed-document`／`embed-query`）、Provider、Model、InputTokens、DurationMs、Succeeded、At | llm-providers ADR 要求的稽核紀錄；M2 一律不存內容 |

**切段規則：** 段落不跨越抽取單位（頁、章節、工作表）。目標約 600 字、上限 1000 字、重疊 100 字，以中文字元計。XLSX 每段重複表頭列，位置標為「工作表『配送時間』第 2–30 列」。DOCX 沒有固定頁碼，所以用標題路徑（例：「2.1 退貨條件」）；PDF 用「第 3 頁」；FAQ 一個條目就是一段。

**可讀性判定：**

- PDF 單頁取出的文字少於 10 字，或亂碼比例（U+FFFD、私用區字元、控制字元）超過 30%，就判為不可讀，常見原因是掃描頁，或字型缺少 ToUnicode 對應。
- 全部可讀是 `ready`；部分可讀是 `partially-readable`，issue 列出頁碼；全部不可讀是 `failed`，issue 為「找不到可讀文字，可能是掃描檔；目前不支援 OCR」。
- 加密 PDF 直接 `failed`，提示「請解除密碼後重新上傳」，不重試。
- TXT／MD 必須是 UTF-8（可帶 BOM），否則 `failed`，提示「請另存為 UTF-8（目前不支援 Big5）」。

---

## 5. Vertical slices

### 軌道 A｜前置：審查指出的兩個高優先問題

#### Slice 1｜API 模式的模擬資料依組織與帳號隔離（前端）
- **目的：** M2 之後，API 模式的 mock 助理與草稿會引用真實知識庫的 GUID。先讓不同組織、不同帳號在同一個瀏覽器中不會讀到彼此的模擬資料（審查的第一項高優先發現）。
- **內容：**
  - API 模式下，`MockDemoRepository` 寫入 `localStorage`／`sessionStorage` 時，鍵值加上 `/me` 回傳的真實 `organization.id` 與帳號 `id` 作為前綴。依審查建議，用分隔鍵值的方式處理，**不採用**登出時清除，因為清除會刪掉草稿，也處理不了同時開啟的多個分頁。
  - 儲存介面改成在每次存取時讀取目前身分，因為 repository 在登入前就已由 DI 建立。
  - mock 模式（Pages）的鍵值不變，既有 Demo 資料不需遷移。
- **驗收：**
  - 單元測試：同一個 storage 替身中，組織 A 的 `admin` 建立的草稿，對組織 B 的 `admin`，以及同組織中另一位同角色的帳號都不可見；切回 A 的 `admin` 仍然看得到。
  - mock 模式下，現有的 Cypress 套件全綠，`localStorage` 鍵名與改版前相同（以測試比對鍵名清單）。
- **依賴：** 無。

#### Slice 2｜團隊畫面改用真實帳號 ID
- **目的：** 修正「同角色的多位成員會互相覆蓋、改權限改到別人」（審查的第二項高優先發現）；知識庫的分享對象也需要真實帳號 ID。
- **內容：**
  - 依 mapping §4.2，把 `AccountId` 放寬成 `string`。團隊與分享相關的 view 型別直接使用 API 回傳的 GUID。
  - `HybridDemoRepository` 的 `getTeam`／`updateMemberPermissions` 不再把成員 id 換成 Demo 身分 id；團隊面板以 GUID 當列表的 key、編輯目標與更新目標。
  - 「角色 → Demo 身分」的對應只保留在 `activeAccountId()`，給仍然走 mock 的功能區使用，並加註「隨 M3 助理與對話換成 API 時移除」。
  - API 整合測試補一個情境：同組織中兩位 `smb-internal`（直接寫入資料庫）。
- **驗收：**
  - API 測試：`GET /team` 回傳兩位同角色成員、id 不同；以 A 的 id 修改權限後，B 的權限不變。
  - 前端 HttpTestingController 測試：兩位同角色成員顯示為兩列，修改 A 送出的 URL 是 A 的 GUID。
  - mock 模式的團隊面板行為與改版前相同，Cypress 全綠。
- **依賴：** 無（可與 Slice 1 平行）。

### 軌道 B｜知識庫後端

#### Slice 3｜Application 層、知識庫資料模型與 CRUD／分享 API
- **目的：** 知識庫成為後端真實資源，後續 Slice 都掛在它底下。
- **內容：**
  - 新增 `SmartAgri.Application` 專案、`SmartAgri.Application.Tests`，以及第 3 節的架構測試。
  - 實體：`KnowledgeBase`、`KnowledgeBaseShare`、`KnowledgeActivity`，加上一個 migration。
  - `ForbiddenReason.KnowledgeBase`（`knowledge-base`）。
  - Endpoint：
    - `GET /api/v1/knowledge-bases` 回傳 `KnowledgeBaseSummaryView[]`。可見範圍照 mock 現行的 `listKnowledgeBaseSummaries` 規則，並以整合測試鎖住。
    - `POST /api/v1/knowledge-bases`，body 為 `{ name, purpose }`，需要 `manage-data-sources`，擁有者就是呼叫者。
    - `GET`／`PATCH`／`DELETE /api/v1/knowledge-bases/{id}`。
    - `PUT /api/v1/knowledge-bases/{id}/sharing`，驗證規則照 `mock-demo-repository.ts:1550-1585`：先濾掉自己、未知或不屬於本組織的帳號 id；`specific-accounts` 濾完後沒有任何對象就回 `422`（「請至少選擇一個帳號或團隊。」）；非公開時強制 `allowOriginalDownload=false`。
    - 詳情中的 `shareTargets` 是同組織內除了自己以外的帳號，只帶 `{ id, displayName }`，不是帳號目錄。
  - 回應帶 `viewerCanManage`；同步更新 `openapi/v1.json` 與前端型別。
- **驗收（整合測試）：**
  - `customer`（沒有 `manage-data-sources`）建立知識庫得到 `403`。
  - 組織 A 的知識庫 id 以組織 B 的 token 查詢，與查詢不存在的 id 相比，status 與 body 完全相同。
  - `specific-accounts` 的對象只有其他組織的帳號 id 時得到 `422`，資料庫沒有寫入；與本組織的有效 id 混在一起時，只保存有效的 id。
  - 刪除知識庫後，其下的所有資料都被刪除（這時還只有分享與活動紀錄，後續 Slice 各自補上自己的資料）。
- **依賴：** 無。

#### Slice 4｜PostgreSQL 背景工作佇列
- **目的：** 做出可重用的背景工作機制，供文件處理使用，之後的定期報表也會用到。
- **內容：**
  - `BackgroundJob` 實體與 migration；`JobClaimer`（見第 3 節的原生 SQL）；`JobRunner`（依 `Kind` 分派給 `IJobHandler`）；`JobWorker : BackgroundService`（輪詢間隔、並行數可設定）。
  - 重試：可重試的錯誤依指數退避更新 `run_after`；達到 `MaxAttempts` 後設為 `failed` 並呼叫處理器的 `OnFinalFailureAsync`。不可重試的錯誤（處理器丟 `PermanentJobFailure`）直接失敗。
  - `locked_until` 逾時的工作會被重新取件，處理 API 行程崩潰的情況。
  - OTel：每個工作一段 `smartagri.job` span，帶 `kind`、`attempt`、`smartagri.organization_id`；加上佇列深度的 metric。
  - 原始碼測試：跨組織的原生 SQL 只允許出現在 `JobClaimer`。
- **驗收（整合測試）：**
  - 兩個並行的 runner 各自取件 100 次，每個工作只被處理一次。
  - 處理器丟可重試錯誤兩次後成功：`Attempts=3`，`LastError` 保留最後一次的訊息。
  - 處理器在組織 A 的工作中試圖寫入組織 B 的資料，會丟 `CrossOrganizationWriteException`，工作失敗。
  - 模擬租約過期：工作被第二個 runner 重新取走。
- **依賴：** 無（可與 Slice 3 平行）。

#### Slice 5｜文件上傳、原檔保存與重複檢查
- **目的：** 真實檔案進得了系統，每檔獨立成功或失敗。
- **內容：**
  - `POST /api/v1/knowledge-bases/{id}/documents`（multipart，一次一檔，可帶選填的 `batchId`）：
    - 成功回 `201 KnowledgeDocumentView`（`queued`），並建立文件、第 1 版、原檔，以及一筆 `knowledge.process-version` 工作，全部在同一個交易內。
    - 檔案超過 `Knowledge:MaxFileBytes`（預設 20 MB）回 `413`。
    - 副檔名不在允許清單（`.pdf .docx .xlsx .txt .md`），或檔頭 magic bytes 與副檔名不符，回 `415`。
    - 同一知識庫中已有相同 SHA-256 的版本，回 `422 { reason: "duplicate-content", existingDocumentName }`。
    - 檔名與現有文件相同，回 `422 { reason: "duplicate-name" }`，提示改用「上傳新版本」。
  - `POST .../documents/{docId}/versions/{versionId}/retry`：只有 `failed` 可以重試，已在處理中回 `409`。
  - `GET .../documents/{docId}/versions/{versionId}/file`：擁有者下載原檔。
  - `DELETE .../documents/{docId}`：在同一個交易內刪除文件、所有版本、原檔與段落，並寫一筆不含內容的活動紀錄。
  - 這個 Slice 的工作處理器先只把狀態推到 `processing` 再推到 `failed`（issue 為「解析功能尚未啟用」）；Slice 6 再換成真正的處理。
  - Kestrel 與 endpoint 的請求大小上限設成與 `MaxFileBytes` 一致，部署說明註明反向代理也要配合調整。
- **驗收（整合測試）：**
  - 上傳 PDF、DOCX、XLSX、MD 各一份都得到 `201`；把 `.exe` 改名成 `.pdf` 得到 `415`；21 MB 的檔案得到 `413`。
  - 同一份內容換個檔名再上傳，得到 `422 duplicate-content`。
  - 刪除文件後，四張相關表都查不到該文件的資料。
  - 下載的原檔與上傳的內容位元組相同（比對 SHA-256）。
- **依賴：** 3、4。

#### Slice 6｜文字擷取、可讀性判定、切段與抽取預覽
- **目的：** 知道系統實際讀到什麼，這是「可使用」的依據。
- **內容：**
  - Infrastructure 實作 `IDocumentTextExtractor`：PdfPig 處理 PDF、OpenXml 處理 DOCX（依標題樣式分章節，表格轉成以 ` | ` 分隔的列）與 XLSX（每個工作表一個單位，保留表頭與儲存格的數字格式），TXT／MD 用 UTF-8 驗證並依 `#` 標題分章節。
  - Application 實作切段與可讀性判定（第 4 節規則），寫入 `KnowledgeExtractedUnit`、`KnowledgeChunk`（這時還沒有向量）；處理狀態依第 4 節決定。
  - `GET .../documents/{docId}/versions/{versionId}/preview`：依單位列出位置、可讀與否、原因、所含段落（文字、是否排除）。
  - `PUT .../chunks/{chunkId}/exclusion`，body 為 `{ excluded }`：排除封面、附錄、舊條款等段落，並寫活動紀錄。
  - 測試用的文件檔放在 `apps/api/tests/fixtures/knowledge/`，產生方式與字型授權記在同目錄的 README。至少要有：有文字層的中文 PDF、含整頁圖片的 PDF、加密 PDF、有標題與表格的 DOCX、兩個工作表且含單位欄位的 XLSX、UTF-8 的 MD、Big5 的 TXT。
- **驗收：**
  - 中文 PDF 為 `ready`，預覽的第 2 頁文字與 fixture 原文相同；含圖片頁的 PDF 為 `partially-readable`，issue 列出該頁頁碼；加密 PDF 為 `failed`，且 `Attempts=1`（不重試）。
  - DOCX 的段落位置顯示標題路徑；XLSX 的每個段落都含表頭列，位置顯示工作表名稱與列範圍。
  - Big5 TXT 為 `failed`，issue 包含「UTF-8」。
  - 排除一個段落後，預覽顯示為已排除，活動紀錄多一筆。
- **依賴：** 5。

#### Slice 7｜嵌入向量、向量存取與模型呼叫紀錄
- **目的：** 段落有向量，而且走 ADR 指定的抽象；嵌入模型可以設定、可以更換。
- **內容：**
  - **第一步**是驗證 `Pgvector.EntityFrameworkCore` 0.3.0 能否搭配 EF Core 10／Npgsql 10.0.3：`UseVector()`、`vector` 欄位的 migration、`CosineDistance` 轉譯。不行的話，退路是 Npgsql 的 `UseVector()` 加上以原生 SQL 做距離排序，封裝在同一個 `VectorStoreCollection` 實作之內（第 7 節風險 1）。
  - `KnowledgeChunk.Embedding`／`EmbeddingModel` 的 migration。
  - Infrastructure 依 `Ai:Embedding:*` 註冊 `IEmbeddingGenerator<string, Embedding<float>>`：OpenAI 與 OpenAI 相容端點使用 `Microsoft.Extensions.AI.OpenAI`；`Fake` 的限制見第 3 節。
  - 設定可以另外指定 `QueryPrefix`／`DocumentPrefix`：部分本機多語模型（例如 e5 系列）要求在問題與段落前加上固定前綴，否則分數會失準。
  - 用 `Microsoft.Extensions.AI` 的中介層包裝，每次呼叫都寫一筆 `ModelInvocation` 並產生 OTel span（`gen_ai.*` 屬性）；背景處理時 `AccountId` 為上傳者。
  - 處理管線在切段之後批次嵌入，段落與向量在同一個交易內寫入。嵌入服務暫時失敗時交給佇列重試；最終失敗時，版本為 `failed`，issue 為「嵌入模型暫時無法使用，請稍後重試」。
  - 實作 `VectorStoreCollection<Guid, KnowledgeChunkRecord>`（第 3 節的範圍）。
  - CLI 子命令 `reindex`：對目前模型以外的段落重新嵌入，可依組織分批，並回報進度。
- **驗收：**
  - 以 `Fake` 處理完成後，每個未排除的段落都有向量，`embedding_model` 等於設定值，`ModelInvocation` 的筆數等於嵌入批次數，而且表中沒有任何文件文字。
  - 以 `ASPNETCORE_ENVIRONMENT=Production` 搭配 `Provider=Fake` 啟動會失敗，並寫明原因。
  - 把設定換成另一個 `Fake` 模型名稱：檢索（Slice 9 完成後補測）查不到舊向量；跑 `reindex` 之後恢復。
  - 以組織 A 的 context 透過 `VectorStoreCollection.SearchAsync` 查詢，不會出現組織 B 的段落，而且無論是否帶 filter 都一樣。
  - 本機手動：設定真實的 OpenAI 或 OpenAI 相容端點，上傳一份 PDF 後，在 Aspire 儀表板看得到嵌入呼叫的 span。
- **依賴：** 6。

#### Slice 8｜版本、確認生效與緊急停用
- **目的：** 業務負責人能在上線前確認「哪一版是目前有效的」。
- **內容：**
  - `POST .../documents/{docId}/versions`（multipart）：上傳新版本，走 Slice 5 的檢查與 Slice 6、7 的處理，`ReviewState=pending-review`。
  - `POST /api/v1/knowledge-bases/{id}/versions/approve`，body 為 `{ versionIds[], effectiveFrom? }`，可一次確認多個版本。只要有任一版本不是可使用或部分可讀，就整批回 `422`、完全不寫入。`effectiveFrom` 預設為現在，不可早於現在。
  - `POST .../documents/{docId}/disable`（`{ reason }`，必填）與 `.../enable`。
  - `GET .../documents/{docId}`：回傳版本歷程，每一版附狀態（處理狀態、待確認、已排定生效、目前有效、已封存）、上傳者、確認者與時間，另附這份文件的活動紀錄。
  - Application 的 `RetrievableChunks` 查詢規格（第 3 節）；知識庫清單與詳情的狀態計數也改用同一個規格計算。
- **驗收：**
  - 以表格驅動的整合測試覆蓋所有組合：未確認的第 1 版不可檢索；確認後可檢索；第 2 版未確認時仍用第 1 版；第 2 版確認生效後，第 1 版變成已封存、不可檢索；`effectiveFrom` 設在明天的第 2 版，今天仍用第 1 版（用 `TimeProvider` 快轉驗證）；停用後整份文件都不可檢索，恢復後回到停用前的狀態；被排除的段落在任何情況下都不可檢索。
  - 批次確認時混入一個 `failed` 版本會回 `422`，其他版本也都維持 `pending-review`。
  - 每個操作都有活動紀錄，操作人正確。
- **依賴：** 5（可與 6、7 平行，檢索相關的驗收在 7 完成後補齊）。

#### Slice 9｜檢索試查 API 與可重用的檢索服務
- **目的：** 在沒有模型生成的情況下，驗證「這個問題會引用哪一版、哪一頁」。M3 的對話直接重用同一個檢索服務。
- **內容：**
  - Application 的 `KnowledgeRetriever`：嵌入問題（寫 `ModelInvocation`，`Purpose=embed-query`），接著在指定的知識庫集合內搜尋可檢索段落，回傳前 k 筆與分數，並依 `Retrieval:MinScore`（預設值寫在設定中）標示 `belowThreshold`。
  - `POST /api/v1/knowledge-bases/{id}/retrieval-preview`，body 為 `{ question, includePending }`，回傳 `{ passages: [{ documentId, documentName, versionNumber, versionState, locationLabel, excerpt, score }], threshold, belowThreshold }`。`includePending=true` 時，待確認版本的段落也會列入並加上標示，讓負責人在確認生效前先看效果。
  - 問題字數上限 500，空白回 `422`。
- **驗收（整合測試，使用可控向量的假嵌入器）：**
  - 問題「收到商品幾天內可退貨？」的第一筆結果，指向有效版本第 2 版的正確頁碼；把 `includePending` 關掉時，不會出現待確認版本的段落。
  - 分數全部低於門檻時，`belowThreshold=true`，而且不會丟例外。
  - 組織 B 的 token 對組織 A 的知識庫試查，得到與「不存在」相同的 `403`。
- **依賴：** 7、8。

#### Slice 10｜FAQ 條目
- **目的：** 補齊 ADR 第一版要支援的 FAQ 來源。
- **內容：**
  - `POST .../faqs`（`{ question, answer }`），以及 `PUT`、`DELETE .../faqs/{docId}`。每個 FAQ 是 `Kind=faq` 的文件，一個版本一段，編輯時產生新版本、走同樣的確認生效。
  - 不經背景解析，但嵌入仍經過佇列，行為與文件一致。
- **驗收：** 新增的 FAQ 確認生效後可以被試查到，位置標示「FAQ」；編輯之後，未確認前仍回傳舊答案。
- **依賴：** 7、8。

### 軌道 C｜前端

#### Slice 11｜知識庫契約改成非同步，API 模式走 HTTP
- **目的：** 知識庫清單、詳情、建立、分享與刪除接上真實後端；Pages Demo 的行為維持一致。
- **內容：**
  - `DemoRepository` 的知識庫方法改成回傳 Observable，拿掉 `viewerAccountId`；新增 `createKnowledgeBase`、`deleteKnowledgeBase`、`deleteKnowledgeDocument`；移除 `addDemoKnowledgeDocument`、`advanceKnowledgeDocument`（上傳在 Slice 12）。`KnowledgeBaseId`、`KnowledgeDocumentId` 放寬成 `string`。
  - `MockDemoRepository` 依「上傳後經過的時間」計算狀態，拿掉元件的 `setTimeout` 推進；`HybridDemoRepository` 走 HTTP，403 的 reason 改為照實對應，不再折成 `team`。
  - 清單頁加上「建立知識庫」入口（有 `manage-data-sources` 才顯示），空狀態提供同一個入口。
  - 清單與詳情分別顯示載入中、權限不足、錯誤與真正空白四種狀態，不把失敗顯示成「沒有資料」（沿用審查對「我的助理」的建議）。
  - 詳情頁在有等待中或處理中的項目時，每 3 秒輪詢一次；分頁隱藏時暫停。
- **驗收：**
  - `mock-demo-repository` 的知識庫 spec 改寫成非同步後全數通過；新增 Hybrid 的 HttpTestingController 測試（`403 knowledge-base`、`422` 分享驗證、建立後重新載入）。
  - API 模式手動驗收：以 `admin` 建立知識庫 → 出現在清單中 → 改分享 → 用 `customer` 登入看不到。
  - mock 模式的 `knowledge.cy.ts` 依新流程更新後全綠。
- **依賴：** 1、2、3。

#### Slice 12｜批次上傳與逐檔結果
- **內容：**
  - 詳情頁的「上傳文件」可一次選取多檔。先在前端檢查副檔名與大小，並列出本次清單；以並行數 3 逐檔上傳（`HttpClient` 的 `reportProgress`），每檔顯示進度與結果（成功、格式不支援、過大、內容重複並附既有文件名、檔名重複並提供「改為上傳新版本」）。
  - 失敗的項目可以單獨重試，不必整批重傳；全部結束後顯示摘要：成功幾檔、部分可讀幾檔、失敗幾檔。
  - Mock 依檔名、大小與副檔名模擬相同的結果，不讀取檔案內容。
- **驗收：**
  - 元件測試：一次選 5 檔，其中 1 檔回 `415`、1 檔回 `422 duplicate-content`，其餘成功；只重試失敗的那 1 檔，另外 3 檔不會被再次送出。
  - API 模式手動：一次上傳 fixtures 目錄中的 7 份檔案，逐檔結果與後端狀態一致。
  - 390px 寬度下沒有水平溢出（照 `responsive.cy.ts` 的量法，注意 Linux CI 的傳統捲軸寬度）。
- **依賴：** 5、11。

#### Slice 13｜抽取預覽、排除段落與版本確認畫面
- **內容：**
  - 文件列開啟預覽：依頁、章節或工作表列出抽取文字，並標示不可讀的單位與原因；段落可以切換「不納入檢索」。
  - 版本區：上傳新版本、版本歷程與狀態、確認生效（可選生效日期）、緊急停用與恢復（需填原因）。
  - 詳情頁支援多選「批次確認生效」，以及「只看待確認」的篩選。
  - 顯示處理狀態的地方，同時顯示是否已生效，避免「可使用」被誤讀成「助理已在使用」。
- **驗收：**
  - 元件測試涵蓋批次確認時的 `422` 顯示，以及停用必填原因。
  - API 模式手動：上傳退貨政策第 2 版 → 預覽 → 排除封面段落 → 確認生效 → 版本歷程顯示第 1 版已封存，操作人正確。
- **依賴：** 6、8、11。

#### Slice 14｜檢索試查畫面
- **內容：**
  - 知識庫詳情新增「試查」分頁：輸入問題後，列出段落、文件、版本、位置、摘錄與分數；可切換「包含待確認版本」。
  - 低於門檻時顯示「助理設定為只用組織資料時，這題會回答查無結果」。
  - Mock 以關鍵字比對示範段落，模擬相同的結果形狀。
- **驗收：** 元件測試涵蓋三種結果：有結果、低於門檻、含待確認版本。API 模式手動驗收，用 fixtures 的退貨政策問題核對頁碼。
- **依賴：** 9、11。

#### Slice 15｜助理精靈與設定頁改用真實知識庫清單
- **目的：** API 模式下，mock 助理能連接真實知識庫。助理本身在 M3 才換成 API。
- **內容：**
  - `listConnectableSources` 改成回傳 Observable；Hybrid 回傳 API 的知識庫，加上 mock 的資料庫。
  - mock 助理引用了不存在的知識庫 id 時（例如 API 模式下的種子助理），直接略過不顯示。
  - 知識庫詳情的「連接助理」在 API 模式下由 mock 助理推得，並加註「助理設定仍為示範資料」。
  - 精靈的試問維持 mock，模型生成屬於 M3。
- **驗收：** API 模式手動：在精靈中選擇剛建立的真實知識庫，儲存助理後，該知識庫詳情的「連接助理」出現這個助理；換成另一個組織登入，看不到這個助理，也看不到該知識庫。
- **依賴：** 11。

### 軌道 D｜品質驗收

#### Slice 16｜檢索評測題庫與開發用示範知識
- **目的：** ADR 要求用題庫判斷門檻與品質，不憑感覺調整。
- **內容：**
  - `apps/api/eval/retrieval/`：以安心商行為情境的示範文件（商品指南、退換貨政策第 1、2 版、配送時間表、FAQ），加上約 30 題。每題標註預期的文件、版本與位置；至少 5 題是「應該查無結果」。
  - CLI 子命令 `eval-retrieval`：以目前設定的真實嵌入模型跑完題庫，輸出前 5 名命中率、「查無結果」題的最高分，以及建議門檻；結果寫入 `docs/evals/<日期>-retrieval.md`。**不在 CI 執行**（需要真實模型與金鑰）。
  - 依第 7 節決定 3，至少跑兩次：OpenAI 的嵌入模型一次、本機 OpenAI 相容端點上的非中國團隊多語模型一次，並在報告中比較兩者。
  - `DevelopmentSeeder` 選擇性地（`SEED_DEMO_KNOWLEDGE=true`）把同一批示範文件放進安心商行，走正常的上傳與處理管線，並確認生效。
- **驗收：**
  - 以選定的嵌入模型跑一次並提交報告：前 5 名命中率 ≥ 90%，並依報告把 `Retrieval:MinScore` 的預設值寫進設定。
  - 用 `Fake` 跑 `eval-retrieval` 能完整執行（驗證流程本身，不看分數）。
- **依賴：** 9。

#### Slice 17｜CI 的 API 模式 E2E
- **目的：** M2 的價值在前後端整合，只靠本機手動驗收，回歸問題會漏掉。M1 把這項列為「需要時另開票」，M2 開始需要。
- **內容：**
  - `ci.yml` 新增 `e2e-api` job：啟動 pgvector 服務容器，執行 `migrate`（Development 環境，示範密碼使用 CI 專用的測試值），再以 `Ai:Embedding:Provider=Fake` 啟動 API；admin 以 `api` 設定建置並透過 proxy 服務。
  - Cypress 新增 `knowledge-api.cy.ts`，流程為：登入 → 建立知識庫 → 批次上傳 3 份 fixture → 等待處理完成 → 確認生效 → 試查命中 → 換成另一個組織登入後看不到。
  - `tools/check-no-demo-secrets.sh` 要容許這個 CI 測試值，做法寫在腳本註解中。
- **驗收：** PR 上 `e2e-api` 為綠燈；故意讓試查 endpoint 回 `500`，這個 job 會失敗。
- **依賴：** 12、13、14。

### 軌道 E｜新增組織成員（2026-09-26 決定納入 M2）

#### Slice 18｜新增組織成員帳號（審查待補功能 1）
- **內容：**
  - `POST /api/v1/team/members`，body 為 `{ loginName, displayName, role, permissions }`，需要 `manage-assistants`（與團隊頁相同）。
  - 回應 `201 { member, oneTimePassword }`，一次性密碼只在這個回應中出現一次，不寫入日誌或 OTel。
  - 新成員標記「首次登入須改密碼」，沿用 M1 Slice 11 的機制。
  - 同組織中登入名稱重複回 `422`；不同組織可以重複。
  - 團隊頁提供「新增成員」對話框，一次性密碼只顯示一次，並附複製按鈕。
- **驗收：** 照審查文件第 1 項的預期驗收，包含 API 整合測試（授權、跨組織、同角色多人、重複名稱），以及 API 模式的 E2E：新增後登入、被要求改密碼、修改其權限。
- **依賴：** 1、2（E2E 需要 17）。
- **不含：** 停用、離職交接、職務異動、密碼重設（審查所說的「組織成員生命週期」其餘部分）。

**相依摘要：**

- 前置：{1, 2} 互相獨立。
- 後端：{3, 4} 平行 → 5 → 6 → 7；5 → 8；{7, 8} → {9, 10}；9 → 16。
- 前端：{1, 2, 3} → 11；{5, 11} → 12；{6, 8, 11} → 13；{9, 11} → 14；11 → 15。
- 整合：{12, 13, 14} → 17；{1, 2} → 18。

後端軌道與 Slice 1、2 可以同時開工；前端的 11 在 3 合併後開始，然後與後端的 5–9 並行。

---

## 6. 審查發現的對應

| 審查項目 | 在 M2 的處理 |
| --- | --- |
| 高：API 模式的 mock 資料在同網域 `localStorage` 互相碰撞 | Slice 1 |
| 高：同角色成員被轉成同一個 Demo ID | Slice 2 |
| 中：「我的助理」把載入中、權限不足或部分失敗顯示成空白 | **不在 M2 相依鏈內**，建議開一張獨立的小票立即處理；知識庫頁面在 Slice 11 採用同樣的四種狀態 |
| 中：首頁對沒有 `manage-assistants` 的帳號仍顯示「建立新助理」 | 獨立小票，不阻擋 M2 |
| 待確認：首頁「開始對話」導向 `/use/:assistantId` | 平台內對話屬於 M3，在 M3 計畫中決定（第 7 節決定 6） |
| 待補 1：新增組織成員 | Slice 18（第 7 節決定 1，已納入） |
| 待補 2：建立知識庫與批次上傳 | Slice 3、5、11、12 |
| 待補 3：版本與生效管理 | Slice 8、13 做確認生效、生效日期、封存、緊急停用與操作紀錄；「批次標籤／類別／適用對象」與「新舊版自動比對」延後 |
| 待補 4：內容轉換、預覽與校對 | Slice 6、13 做預覽、不可讀標示與排除段落；「由文件自動整理 FAQ 草稿」延後到 M3 以後（需要模型生成） |
| 待補 5：真實試問與持續維護 | Slice 9、14、16 做檢索層的試查與題庫；每個助理的測試題組、答錯建立處理事項、改版後重跑都在 M3 |
| 待補 6：跨部門案件、交接與追蹤 | 排在 M4 之後，開工前先寫 ADR（第 7 節決定 5） |

---

## 7. 決定事項、風險與待辦

**已決定（2026-09-26，照草案的建議定案）：**

1. **Slice 18「新增組織成員」納入 M2**，作為可平行的軌道 E。它的前置條件（Slice 1、2）本來就在 M2 裡，而 M3 開放組織內多人使用對話前一定要有這個功能。
2. **原檔存放在 PostgreSQL**（第 3 節），見 [原檔存放 ADR](../adr/2026-09-26-original-files-in-postgresql.md)。
3. **嵌入模型：M2 開發使用 OpenAI 的嵌入模型（需要 API 金鑰）；評測時另以本機的 OpenAI 相容端點跑一個多語嵌入模型，比較兩者的繁體中文效果**（Slice 16）。本機模型只能從非中國團隊、寬鬆授權的模型中挑選（llm-providers ADR），例如 Microsoft 的 `multilingual-e5-large`（MIT）或 Snowflake 的 `snowflake-arctic-embed-l-v2.0`（Apache-2.0）。BAAI 的 bge 系列出自中國團隊，不採用。
4. **每個版本都必須人工確認生效，包括第 1 版**；以批次確認降低導入成本（Slice 8、13）。
5. **跨部門案件（審查待補 6）排在 M4（數據庫、表單紀錄）之後**，開工前先寫 ADR，定義「文件、表單、工作項目」三者的關係。已補在 [里程碑 ADR](../adr/2026-09-25-milestone-order.md)。
6. **首頁「開始對話」的導向**：在 M3 計畫中決定。

**待使用者提供**（依負責人指示留到 M2 最後補做；補做前各票的替代做法與步驟，見 [負責人待辦事項](2026-09-26-m2-owner-action-items.md)）：

1. **幾份去識別化的真實文件**（PDF、Excel 各 2–3 份），用來補強 Slice 6 的 fixtures 與 Slice 16 的評測題庫。台灣中小企業常見的 PDF 匯出方式與 Excel 排版，是解析品質最大的未知數。
2. **OpenAI API 金鑰**：Slice 7 的本機手動驗收與 Slice 16 需要；只放在本機環境變數，不進 repo。

**技術風險：**

1. **`Pgvector.EntityFrameworkCore` 0.3.0 宣告的相依是 EF Core 9**：Slice 7 的第一步就驗證，退路已寫在該 Slice。
2. **中文 PDF 的擷取品質**：缺少 ToUnicode 對應、直書、表格跨欄都可能產生亂碼或錯誤的閱讀順序。可讀性判定只能抓到亂碼，抓不到「順序錯了」，要靠 Slice 13 的人工預覽與 Slice 16 的題庫補強。
3. **大量上傳時的資源用量**：解析與嵌入都在 API 行程內執行；worker 的並行數預設 1，XLSX 的單位數要設上限。地端部署時，反向代理的上傳大小也要一併調整。
4. **API 模式的 mock 助理引用真實知識庫**：Slice 15 之後，mock 助理存的是真實 GUID，靠 Slice 1 的鍵值隔離才不會跨組織；M3 助理換成 API 時，這些 mock 資料不會遷移，要在 M3 的發布說明中註明。
5. **本機資源**：Testcontainers 的測試類別會增加；啟動 colima 前先看 swap 用量。

---

## 8. 不在 M2 範圍

- 模型生成答案、引用驗證、「只用組織資料」的回答門檻、AG-UI 串流、平台內對話（M3）。精靈的試問在 M2 仍是 mock。
- 助理、草稿與對話的後端實體，以及每個助理的門檻微調（M3）。
- 每個助理的測試題組、答錯後建立處理事項、改版後自動重跑、營運追蹤指標（M3 以後）。
- OCR、舊版 `.doc`／`.xls`、PPTX、圖片、CSV、Big5 編碼。
- 批次標籤、類別、適用對象；新舊版本的自動比對與衝突偵測；覆核人角色、更新週期提醒；知識庫擁有權轉移。
- 由文件自動整理 FAQ 草稿（需要模型生成）。
- HNSW 等近似搜尋索引、混合檢索（關鍵字加向量）、重新排序模型。
- 依組織選擇模型的設定畫面、機敏設定加密（M5）。
- SignalR 推播。
- 成員的停用、移除、離職交接與密碼重設（Slice 18 只做新增）。
- 數據庫、對外發布的任何真實 API（M4、M5）。
