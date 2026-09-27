# 後端 Milestone 3｜平台內對話：實作計畫

**日期：** 2026-09-27
**狀態：** 已確認（2026-09-27）。第 7 節的決定事項已照建議定案，可以拆票。
**依據：** [M3 交接](2026-09-27-m3-handoff.md)、[M2 計畫](2026-09-26-backend-milestone-2-knowledge-base.md)、[業務流程審查](../reviews/2026-09-26-project-review-and-backlog.md)、`docs/adr/`（milestone-order、grounded-answers、frontend-backend-integration、backend-stack、llm-providers-and-data-residency、assistant-workspace-model、assistant-access-to-knowledge-and-databases、testing-and-banned-dependencies、withdrawal-and-retention、observability）、`docs/handoff/mock-to-api-mapping.md` §2.1／§2.4／§2.5／§2.6、`docs/handoff/tasks-6-10-backend-handoff.md` §3／§5、`docs/handoff/route-screen-matrix.md` §3.3／§5。
**拆票方式：** 與 M1、M2 相同。每個 Slice 都是可單獨合併的垂直切片，各自附測試與驗收條件。Slice 編號是建議順序，「依賴」欄只列硬依賴。

---

## 1. 目標與非目標

**目標（M3 交付）：** 組織內的成員可以在 API 模式完成審查文件的第二個業務閉環：

> 從精靈建立助理並連接已確認生效的知識庫 → 在精靈裡用真實模型試問 → 分享給組織內指定成員 → 成員在 `/app/chat/:assistantId` 以串流方式對話，回答附上可點開的引用；資料裡找不到時明確回「查無資料」，不讓模型自行編造。

具體完成標準：

1. 助理、精靈草稿、對話串、訊息與引用都存在 PostgreSQL，全部帶 `OrganizationId`，沿用 M1 的具名查詢篩選與寫入保護。跨組織、跨帳號的隔離有測試；**助理擁有者也讀不到其他成員的對話內容**。
2. 回答流程由伺服器強制「檢索 → 門檻 → 生成 → 引用驗證」（grounded-answers ADR）：
   - 檢索分數全部低於門檻時**不呼叫模型**，直接回「查無資料」；
   - 模型的引用只能指向本次檢索交給它的段落，對不上或沒有引用的回答**不採用**；
   - 組織資料與一般知識分成不同回覆類型標示，一般知識只在助理明確允許時出現。
3. 對話以 AG-UI 協定（SSE）串流；前端只用 `@ag-ui/client`，沿用既有對話畫面與五種回覆類型。
4. 每次對話模型呼叫都寫入可稽核紀錄（組織、帳號、助理、模型、時間、輸入與輸出 token），不存內容；OpenTelemetry 有耗時與 token 指標。
5. 精靈的試問改成真實生成，與正式對話走同一條回答流程。
6. 首頁「開始對話」改導向 `/app/chat/:assistantId`（2026-09-27 決定）。
7. 需要真實模型的驗收（本機手動驗收、回答評測）在負責人提供 OpenAI 金鑰前一律標「待補做」，自動化測試全部使用假的 `IChatClient`。

**非目標：** 見第 8 節。

---

## 2. 現況事實（寫計畫時查證，2026-09-27，基準 `e92abd4`）

| 事實 | 出處 |
| --- | --- |
| 後端是四層：Domain、Application、Infrastructure、Api；Application 只能引用 Domain 與兩個抽象套件，由 `ApplicationDependencyTests` 檢查 | `apps/api/src/SmartAgri.Application/SmartAgri.Application.csproj:1-24` |
| 後端沒有任何助理、草稿、對話或訊息的實體與 migration；也沒有引用 `IChatClient`、`Microsoft.Agents.*` 或 AG-UI 套件 | `apps/api/src/SmartAgri.Infrastructure/AppDbContext.cs:69-101`；grep 無結果 |
| 權限列舉已有 `ManageAssistants`、`UseSharedAssistants` | `apps/api/src/SmartAgri.Domain/Accounts/AccountPermission.cs:15`、`:27` |
| `KnowledgeRetrievalQuery` 已預留 `AssistantId` 與 `MinScore`（M3 依助理微調門檻的掛勾） | `apps/api/src/SmartAgri.Application/Knowledge/Retrieval/KnowledgeRetriever.cs:24-31` |
| 檢索結果 `RetrievedKnowledgePassage` 帶 ChunkId、知識庫、文件、版本、`LocationLabel`、全文與分數；`BelowThreshold` 表示全部低於門檻 | `KnowledgeRetriever.cs:41-51`、`:64` |
| 部署預設門檻 `Retrieval:MinScore = 0.3`、`Top = 5`（M2 決定照現狀接受） | `KnowledgeRetrievalSettings.cs:22-29`、`appsettings.json:9-12` |
| 嵌入的稽核模式：`ModelInvocationRecordingEmbeddingGenerator` 是 `DelegatingEmbeddingGenerator`，缺少 `ModelInvocationAttribution` 就丟例外，成功或失敗都寫一筆 `ModelInvocation` | `apps/api/src/SmartAgri.Infrastructure/Ai/ModelInvocationRecordingEmbeddingGenerator.cs:33-186` |
| `ModelInvocation` 已有 `AssistantId`（M2 恆為 null），只有 `InputTokens`，沒有輸出 token；`Purpose` 只有 `EmbedDocument`／`EmbedQuery` | `apps/api/src/SmartAgri.Domain/Ai/ModelInvocation.cs:11`、`:36`；`ModelInvocationPurpose.cs:8` |
| 模型錯誤對應 503 的模式：`KnowledgeEmbeddingException` → `503 embedding-not-configured／embedding-unavailable` | `apps/api/src/SmartAgri.Api/Knowledge/KnowledgeRetrievalEndpoints.cs:134-143` |
| 知識庫「分享」只決定能不能被助理連接，不會讓知識庫出現在別人的清單（PR #57，已接受） | `apps/api/src/SmartAgri.Application/Knowledge/KnowledgeBaseAccess.cs:14-47` |
| 測試用假嵌入以設定切換（`Ai:Embedding:Provider=Fake`），不是替換程式碼 | `apps/api/tests/SmartAgri.Api.Tests/Authentication/AuthTestHost.cs:39`、`:140-141` |
| `/app/chat`、`/app/chat/:assistantId`、`/app/chat/:assistantId/:conversationId` 共用 `WorkspaceChatPageComponent`；`/use/:assistantId` 是 `ChatShellPageComponent`，掛 `embeddedChatGuard` | `apps/admin/src/app/app.routes.ts:127-159` |
| 首頁「開始對話」目前導向 `/use/:assistantId` | `apps/admin/src/app/features/home/home-page.component.html:18`、`:39` |
| 對話只有一份 UI 實作；五種回覆類型與標籤 | `features/assistant-use/conversation/chat-conversation.component.ts`；`core/domain/conversation.model.ts:123-149`；`message/chat-message.component.ts:22-28` |
| 對話契約 9 個方法全部同步、mock-only，一次回傳整份對話，沒有串流；回覆來自 5 組關鍵字 fixture | `core/repositories/demo-repository.ts:527-606`；`core/repositories/demo-seed-chat.ts` |
| 精靈試問 `previewTrialAnswer` 用 `TrialAnswerView`，「沒有答案」叫 `no-answer`；對話用 `no-result`，兩套列舉 | `core/domain/assistant-draft.model.ts:122-138`；`tasks-6-10-backend-handoff.md:762` |
| 助理回答規則已有 `knowledgeScope`（`company-data-only`／`allow-general-knowledge`）、`refusalMessage`、`showCitations`、`keepOwnConversations` | `core/domain/assistant-draft.model.ts:28-48` |
| 前端 API 請求靠 `bearer-token.interceptor.ts` 加 `Authorization`；`@ag-ui/client` 走 `fetch`，不經過 Angular 攔截器 | `core/session/api-mode/bearer-token.interceptor.ts:6-11` |
| 前端尚未引用 `@ag-ui/*`，也沒有 SSE 程式碼；Angular `^22.0.0`、rxjs `~7.8.0` | `package.json` |

### 2.1 版本查證（NuGet／npm，2026-09-27）

| 套件 | 最新正式版 | 授權 | 備註 |
| --- | --- | --- | --- |
| `Microsoft.Extensions.AI`／`.Abstractions`／`.OpenAI` | 10.10.0／10.10.1／10.10.1 | MIT | repo 已用 `.Abstractions`、`.OpenAI` 10.10.1；`ChatClientBuilder`、`UseOpenTelemetry` 在 `Microsoft.Extensions.AI`，只放 Infrastructure |
| `OpenAI` | 2.14.0 | MIT | repo 已用 |
| `Microsoft.Agents.AI`／`.Abstractions`／`.OpenAI` | 1.22.0 | MIT | Agent Framework 核心已正式版 |
| `Microsoft.Agents.AI.Hosting`、`Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` | **無正式版**（最新 `1.22.0-preview.260918.1`） | MIT | AG-UI 的 ASP.NET Core hosting **仍是 preview**；API 名稱已從 `MapAGUI` 改成 `MapAGUIServer` 一次 |
| `Microsoft.Agents.AI.AGUI` | 無正式版（停在 `1.13.0-preview.260703.1`） | — | 已停止發版，功能拆到下列 `AGUI.*` |
| `AGUI.Abstractions`／`AGUI.Formatting`／`AGUI.Server` | **1.0.0** | MIT | AG-UI 協定官方組織（`ag-ui-protocol/ag-ui`）發布的 .NET SDK。`AGUI.Server` 是與框架無關的轉接器：`IAsyncEnumerable<ChatResponseUpdate>.AsAGUIEventStreamAsync(...)` 把 `IChatClient` 的串流轉成 AG-UI 事件；`AGUI.Formatting` 提供 SSE 輸出。相依只有 `Microsoft.Extensions.AI.Abstractions` ≥ 10.6.0、`System.Net.ServerSentEvents`、`System.Text.Json` |
| `@ag-ui/client`／`@ag-ui/core`／`@ag-ui/encoder`（npm） | 1.0.0（2026-09-17 轉正式） | MIT | `@ag-ui/client` 相依 `rxjs` 精確 7.8.1，落在 repo 的 `~7.8.0` 範圍內，不會有兩份 rxjs |

查證方式：`https://api.nuget.org/v3-flatcontainer/<id>/index.json`、registration 的 `licenseExpression`、`.nuspec` 相依；npm 用 `npm view <pkg> dist-tags license time dependencies --json`。版本一律寫在 `Directory.Packages.props`／`package.json`，實作時若出了新的 patch 版就一次更新並跑完整測試。

---

## 3. 關鍵選擇

**AG-UI 串流用正式版的 `AGUI.*` SDK，自己寫 Minimal API 端點；M3 不引入 Agent Framework。** frontend-backend-integration ADR 寫的是「後端使用 Agent Framework 的 AG-UI hosting」，但那個 hosting 套件到今天仍是 preview，違反 backend-stack ADR「核心只用正式版」。AG-UI 協定組織在 2026-09 發布了正式版 .NET SDK，`AGUI.Server` 直接吃 `IChatClient` 的串流，所以：

- 端點用 `AGUI.Abstractions` 的 `RunAgentInput` 當 body，以 `AGUI.Formatting` 輸出 SSE，事件格式與官方 hosting 相同；日後 hosting 轉正式版，只要換掉 Api 層的端點，Application 不受影響。
- M3 的回答流程是固定管線（檢索 → 門檻 → 生成 → 驗證），沒有工具呼叫，也沒有多步驟工作流程，用不到 Agent Framework 的編排能力。等 M4 的「固定查詢工具」與表單流程出現時再引入，屆時仍只放在編排層。
- 這項選擇已寫進兩份 ADR（第 7 節決定 A）。

**回答流程集中在 Application 的 `GroundedAnswerService`，端點只負責協定。** 同一個服務供三個入口使用：對話串流、精靈試問（不串流）、回答評測工具。它輸出一串與協定無關的領域事件：`TextDelta`、`Completed(reply, citations)`、`Rejected(reason)`，Api 層再轉成 AG-UI 事件。流程：

1. **決定可用的知識庫。** 取助理連接的知識庫，**在回答當下**重新檢查每一個是否仍可被助理擁有者連接：擁有者自己的、`Public`、或 `SpecificAccounts` 且包含擁有者。分享被收回的知識庫直接排除，不必等人修改助理設定。
2. **檢索。** 呼叫 `KnowledgeRetriever.RetrieveAsync`，`MinScore = 助理自訂門檻 ?? 部署預設`、`AssistantId` 帶入；`IncludePending` 一律為 false。多輪對話時，檢索查詢使用「上一則使用者問題＋這一則問題」，不另外呼叫模型改寫問題（見第 7 節決定 D）。
3. **門檻。** `BelowThreshold` 時：
   - `company-data-only`：**不呼叫模型**，直接回 `no-result`，文字用助理的 `refusalMessage`，`nextSteps` 用固定的建議清單。
   - `allow-general-knowledge`：以「不附任何段落」的提示呼叫模型，回覆類型固定為 `general-knowledge`，並附上固定的提示文字（例如「以下是一般知識，並非貴公司資料」）。這一段回答裡的任何引用標記都會被移除。
4. **生成。** 提示詞包含：助理角色說明與語氣、編號的段落 `[1]`…`[k]`（附文件名稱與位置）、最近幾輪對話（字數有上限），以及規則：只能根據段落回答、每句事實後面標 `[n]`、段落不足以回答時只輸出約定的「無法回答」標記。模型**看不到**任何 ChunkId，只看到本次請求的編號，所以「引用來自本次檢索」由編號對應在伺服器端保證。
5. **驗證。** 串流過程中辨識 `[n]`（全形 `【n】`、`［n］` 一併接受）。符合以下任一條件就不採用，改回 `no-result`：
   - 出現超出 1…k 的編號；
   - 整段回答沒有任何有效引用；
   - 模型輸出「無法回答」標記。
   通過時，回覆類型為 `company-data`，引用只列出實際被標記到的段落，依第一次出現的順序編號。

**先串流、驗證失敗時整則替換。** 為了讓使用者不必等整段生成完畢，文字邊生成邊送出；引用標記在前端先顯示成「確認中」的樣式，收到最終的 `smartagri.reply` 事件後才變成可點開的引用。驗證失敗時，最終事件帶的是 `no-result`，前端把已顯示的文字整則換掉。另一種做法是「整段生成完、驗證後才一次送出」，最安全但每題要多等數秒到十幾秒（第 7 節決定 C）。

**AG-UI 事件對應。** 標準事件：`RUN_STARTED`、`TEXT_MESSAGE_START`／`CONTENT`／`END`、`RUN_FINISHED`、`RUN_ERROR`。自訂事件（`CUSTOM`）：

- `smartagri.reply`：本則訊息最終的 `ChatReplyView` 與訊息 id，前端以它為準；
- `smartagri.thread`：伺服器建立或沿用的對話串 id 與標題（保存對話時才送）。

錯誤分成兩段：串流開始前的錯誤照一般 API 回 `4xx／503`（`403 assistant-use`、`422`、`409 chat-run-in-progress`、`503 chat-not-configured／chat-unavailable`）；串流開始後的模型錯誤送 `RUN_ERROR`，`code` 用同一組 reason。

**對話保存。**

- `keepOwnConversations = true`：使用者問題在串流開始前寫入；助理回覆在最終事件送出前、同一個交易內與引用一起寫入。使用者中途按「停止」或斷線時，只保留使用者訊息，助理那一則不寫入。
- `keepOwnConversations = false`：**完全不寫入**對話串與訊息（withdrawal-and-retention ADR）。前端把本頁的歷史放在 `RunAgentInput.messages` 送上來，伺服器只把它當成前文（字數有上限），**引用永遠只從本次檢索產生**，不接受用戶端傳來的引用。模型呼叫紀錄照寫（不含內容）。
- 引用在寫入時保存當下的快照：知識庫名稱、文件名稱、版本號、位置、原文摘錄，以及可為 null 的 `ChunkId`。文件改版或刪除後，舊對話仍顯示當時引用的內容（第 7 節決定 F）。

**對話模型的設定與稽核照抄嵌入的模式。**

- 設定區段 `Ai:Chat`：`Provider`（`OpenAI`／`AzureOpenAI`／`OpenAICompatible`／`Fake`）、`Endpoint`、`Model`、`ApiKey`、`MaxOutputTokens`、`TimeoutSeconds`。金鑰由環境變數 `Ai__Chat__ApiKey` 注入；Production 設成 `Fake` 會拒絕啟動。
- `ModelInvocationRecordingChatClient : DelegatingChatClient`，串流與非串流都處理；缺 `ModelInvocationAttribution` 就丟例外；新增 `Purpose = generate-answer`，`ModelInvocation` 加 `OutputTokens` 欄位。
- `FakeChatClient` 是可重現的腳本式假模型：依提示中的段落與問題裡的測試指令（例如 `#invalid-citation`、`#no-marker`、`#cannot-answer`、`#fail-midway`）產生固定輸出，讓整合測試能覆蓋每一條驗證分支。

**助理與分享。**

- 助理的「平台內分享」沿用 mapping §2.5 的 `PUT /api/v1/assistants/{id}/publishing/platform`（`S+OWN+MP`），實作與 `KnowledgeBaseShare` 相同的逐帳號分享表。
- 能使用助理的條件（`tasks-6-10-backend-handoff.md` §5.4）：擁有者一律可以；其他帳號需要 `use-shared-assistants`、在分享清單內，且助理不是暫停狀態。
- 不存在與沒有權限都回位元組相同的 `403 { reason: "assistant-use" }`。取消分享只收回權限、不刪對話；重新分享後，原本的對話串再次出現。
- 網站嵌入與 LINE 在 API 模式顯示「對外發布將於後續版本開放」，不接 mock。

**精靈草稿存成 jsonb。** 草稿形狀完全由精靈 UI 決定、會隨畫面調整，所以 `AssistantDraft` 只存 `Payload`（jsonb，附 `SchemaVersion`）、`OwnerAccountId`、`SavedAt`、`Revision`。`Revision` 用來偵測多分頁同時編輯，衝突時回 `409`。只有「由草稿建立助理」這一步才做逐欄驗證，並寫進正規化的 `Assistant` 表；建立成功後，在同一個交易內刪除草稿。

**前端沿用 M2 的替換模式。** 以 #45 建立的非同步契約與 `HybridDemoRepository` 為基礎：

- 對話串 CRUD 與讀取改成 Observable；
- 送出訊息改成新的 `ChatRunner` 介面：API 模式用 `@ag-ui/client` 的 `HttpAgent`，並手動帶入 bearer token，因為 `fetch` 不經過攔截器；Mock 模式把 fixture 回答切成片段、依序送出，讓 Pages Demo 也走同一套串流 UI。
- `/use/:assistantId` 在 API 模式下：已登入就轉到 `/app/chat/:assistantId`，未登入就到登入頁；Mock 模式維持原樣，讓 Demo 繼續展示對外情境。

---

## 4. 資料模型

所有實體都實作 `IOrganizationScoped`，`OrganizationModelTests` 會自動檢查。

| 實體 | 主要欄位 | 說明 |
| --- | --- | --- |
| `Assistant` | Id、OwnerAccountId、Name、Purpose、TemplateId?、Tone、RoleInstructions、KnowledgeScope、RefusalMessage、ShowCitations、KeepConversations、MinScore?（null 用部署預設）、Status（`ready`／`paused`）、CreatedAt、UpdatedAt | `published` 屬於 M5；`draft` 由 `AssistantDraft` 表示，不進這張表 |
| `AssistantKnowledgeBase` | AssistantId、KnowledgeBaseId、ConnectedAt | 複合外鍵確保同組織；知識庫刪除時連帶刪除連接 |
| `AssistantShare` | AssistantId、AccountId | 平台內分享的對象 |
| `AssistantDraft` | Id、OwnerAccountId、Payload（jsonb）、SchemaVersion、Revision、SavedAt | 每個帳號可以有多份具名草稿（assistant-workspace-model ADR） |
| `ChatThread` | Id、AssistantId、AccountId、Title、CreatedAt、LastActivityAt | 依 `(AccountId, AssistantId)` 隔離；只有 `KeepConversations` 的助理才會建立 |
| `ChatMessage` | Id、ThreadId、Author（`account`／`assistant`）、Text、ReplyKind?、Notice?、NextSteps（jsonb）?、CreatedAt | 助理訊息保存驗證後的最終結果，不保存被拒絕的原始模型輸出 |
| `ChatMessageCitation` | MessageId、Ordinal、ChunkId?、KnowledgeBaseId?、DocumentId?、VersionId?、KnowledgeBaseName、DocumentName、VersionNumber、LocationLabel、Excerpt | 引用快照；外鍵在來源刪除時設成 null |
| `ModelInvocation`（修改） | 加 `OutputTokens?`；`Purpose` 加 `generate-answer` | 仍然不存內容 |

**對應前端型別：**

- `ChatCitationView.updatedLabel` 用版本的 `EffectiveFrom` 日期；
- `ChatCitationView.excerpt` 取段落前 200 字；
- 引用抽屜需要的完整原文，由新的 `GET /api/v1/assistants/{id}/chat/citations/{messageId}/{ordinal}` 讀快照（仍檢查 `assistant-use` 與對話串歸屬）。

---

## 5. Vertical slices

### 軌道 A｜前置（M2 已開的票）

- **#45 知識庫契約改非同步、API 模式走 HTTP**：軌道 D 的所有前端票都依賴它（2026-09-27 決定先做 #45，其餘 M2 前端票與 M3 平行）。
- **#49 精靈與設定頁改用真實知識庫清單**：Slice 11 依賴它。
- **#51 CI 的 API 模式 E2E**：Slice 14 依賴它。

### 軌道 B｜助理後端

#### Slice 1｜助理資料模型、清單與設定 API
- **目的：** 助理成為後端真實資源，對話與分享都掛在它底下。
- **內容：**
  - 實體 `Assistant`、`AssistantKnowledgeBase`，加上一個 migration；新增 `ForbiddenReason.AssistantConfiguration`、`AssistantUse`。
  - 可連接規則寫成 Application 層的查詢規格 `AssistantKnowledgeAccess.ConnectableBy(ownerAccountId)`，建立、改設定與回答時共用。
  - Endpoint（照 mapping §2.6）：
    - `GET /api/v1/assistants`（`S+MA`，只列自己擁有的）；
    - `GET /api/v1/assistants?usable=true`（`S`，自己擁有的，加上分享給我的；沒有 `use-shared-assistants` 時只有自己的）；
    - `GET`／`PATCH /api/v1/assistants/{id}/settings`（`S+MA+OWN`，驗證失敗時完全不寫入）；
    - `PUT`／`DELETE /api/v1/assistants/{id}/sources/knowledge-base/{knowledgeBaseId}`（不能連接不可連接的知識庫，也不能解除最後一個來源，兩者都回 `422`）；
    - `DELETE /api/v1/assistants/{id}`（連帶刪除所有人的對話串，第 7 節決定 G）。
  - `sources/database/*` 回 `422`，訊息寫明「數據庫將於後續版本開放」。
  - 同步更新 `openapi/v1.json` 與前端型別。
- **驗收（整合測試）：**
  - 組織 A 的助理 id 以組織 B 的 token 查詢，與查詢不存在的 id 相比，status 與 body 完全相同。
  - 連接「別人的 `Private` 知識庫」得到 `422`，而且沒有寫入；連接「`SpecificAccounts` 且包含擁有者」的知識庫成功。
  - `usable=true` 不會列出「分享給我但我沒有 `use-shared-assistants`」的助理。
  - `PATCH` 同時帶一個合法欄位與一個不合法欄位時得到 `422`，資料庫完全不變。
- **依賴：** 無。

#### Slice 2｜精靈草稿與「由草稿建立助理」
- **目的：** 精靈的保存與建立改由後端處理，每個帳號各自隔離。
- **內容：**
  - 實體 `AssistantDraft`。
  - Endpoint（照 mapping §2.1）：
    - `GET`／`PUT`／`DELETE /api/v1/assistant-drafts/{id}`；
    - `GET /api/v1/assistant-drafts`（具名草稿清單）；
    - `POST /api/v1/assistant-drafts`；
    - `POST /api/v1/assistants`，body 為 `{ draftId }`，`201` 回 `AssistantConfigurationView`。
  - `PUT` 帶 `revision`，不一致時回 `409`。
  - 建立時的逐欄驗證回 `AssistantDraftFieldError[]`：名稱、用途、至少一個可連接的知識庫、回答規則。
  - `GET /api/v1/connectable-sources` 只回可連接的知識庫。
  - 範本與試問題組留在前端常數，不做 endpoint。
- **驗收：**
  - 帳號 A 讀帳號 B 的草稿 id，得到與不存在時相同的 `403 assistant-draft`。
  - 兩個分頁用同一個 `revision` 先後保存，第二個得到 `409`。
  - 建立成功後草稿被刪除；建立失敗（`422`）時草稿仍在，而且沒有任何 `Assistant` 列。
  - 草稿裡含其他組織的知識庫 id 時，建立得到 `422`。
- **依賴：** Slice 1。

#### Slice 3｜平台內分享
- **目的：** 助理可以分享給組織內指定成員；這是 M3「組織內部使用」的授權邊界。
- **內容：**
  - 實體 `AssistantShare`。
  - `GET /api/v1/assistants/{id}/publishing`：只回平台內分享的真實資料，網站與 LINE 回 `not-available`。
  - `PUT /api/v1/assistants/{id}/publishing/platform`（`S+OWN+MP`）：先濾掉自己、未知或其他組織的帳號 id，照 M2 知識庫分享的驗證規則。
  - 使用權限集中寫成 `AssistantUseAccess.UsableBy(accountId, permissions)`，Slice 1 的 `usable=true` 與所有對話端點共用。
- **驗收：**
  - 取消分享後，被取消的帳號呼叫任何對話端點都得到 `403 assistant-use`，他的對話串仍留在資料庫；重新分享後，原本的對話串再次出現在清單裡。
  - 助理暫停後，非擁有者無法使用，擁有者可以。
- **依賴：** Slice 1。

### 軌道 C｜回答流程

#### Slice 4｜對話模型接入、稽核與假模型
- **目的：** 建立 `IChatClient` 的供應商切換、稽核與錯誤對應，後續 Slice 只依賴抽象。
- **內容：**
  - 設定 `Ai:Chat` 與 `ChatClientProvider`（`OpenAI`、`AzureOpenAI` 走 v1 相容端點、`OpenAICompatible`、`Fake`）。
  - `ModelInvocationRecordingChatClient`、`ChatGenerationException`，以及 `503 chat-not-configured／chat-unavailable`。
  - OpenTelemetry：`gen_ai.*` 屬性的 client span，加上 token 與耗時指標。
  - `ModelInvocation.OutputTokens` 與 migration。
  - `FakeChatClient` 與它的測試指令。
  - `Directory.Packages.props` 加 `Microsoft.Extensions.AI` 10.10.0，只給 Infrastructure 用。
- **驗收：**
  - 串流與非串流呼叫都寫入一筆 `ModelInvocation`，`Purpose = generate-answer`，並帶 `AssistantId`、輸入與輸出 token；呼叫失敗也寫入，`Succeeded = false`。
  - 缺少 attribution 的呼叫直接丟例外。
  - Production 環境設定 `Fake` 時拒絕啟動。
  - 確認 OpenAI 串流回應會帶用量：M.E.AI 的 OpenAI 用戶端要能取得串流的 usage，取不到時記錄為 null，不得猜測；以真實模型驗證的部分標「待補做」。
- **依賴：** 無。

#### Slice 5｜有依據的回答服務 `GroundedAnswerService`
- **目的：** 把 grounded-answers ADR 變成伺服器端可測試的規則，這是 M3 風險最高的一片。
- **內容：**
  - 第 3 節的五個步驟：可用知識庫、檢索、門檻、提示組裝、串流中的引用解析與驗證。
  - 輸出與協定無關的領域事件。
  - 提示詞放在 Application 的資源檔，附版本號；版本號記在 OpenTelemetry span 的屬性，方便日後比較評測結果。
- **驗收（Application 測試，使用假模型與假檢索結果）：**
  - `company-data-only` 且全部低於門檻時：模型呼叫次數為 0，回 `no-result`，文字等於助理的 `refusalMessage`。
  - `allow-general-knowledge` 且低於門檻時：模型收到的提示不含任何段落，回 `general-knowledge`，帶固定提示文字，回答裡的 `[n]` 被移除。
  - 模型引用 `[k+1]`、完全沒有引用、或輸出「無法回答」標記：三種情況都回 `no-result`，並記錄拒絕原因（指標，不含內容）。
  - 合法引用 `[2][1][2]` 產生兩筆引用，依第一次出現的順序編號，對應到正確的 ChunkId 與快照欄位。
  - 全形 `【1】` 可以辨識；跨越兩個串流片段的標記（`[` 與 `1]` 分兩次送達）可以辨識。
  - 某個知識庫在建立助理後被取消分享，檢索時就不再包含它的段落。
  - 助理自訂的 `MinScore` 優先於部署預設。
- **依賴：** Slice 1、Slice 4。

#### Slice 6｜對話串與訊息的資料模型與讀寫 API
- **目的：** 私人對話串落地，清單與歷史改由後端提供。
- **內容：**
  - 實體 `ChatThread`、`ChatMessage`、`ChatMessageCitation`。
  - Endpoint（照 mapping §2.4）：
    - `GET`／`POST /api/v1/assistants/{id}/chat/conversations`；
    - `PATCH`／`DELETE .../conversations/{threadId}`（`DELETE` 回剩下的清單）；
    - `GET /api/v1/assistants/{id}/chat?conversation=`，省略時開啟最後活動的那一段；
    - 引用全文的 endpoint。
  - `threadId` 一律當成未驗證的輸入：不存在與屬於別人都回同一個 `403 chat-thread`。
  - 側欄需要的「跨助理最近 10 個對話串」：`GET /api/v1/chat/recent-conversations`（assistant-workspace-model ADR），只列目前仍可使用的助理。
- **驗收：**
  - 助理擁有者用任何 endpoint 都讀不到其他成員的對話串；以擁有者的 token 查成員的 threadId，與查不存在的 threadId 相比，回應位元組相同。
  - `KeepConversations = false` 的助理：`POST conversations` 回 `422`，`GET chat` 的 `threadId` 為 null。
  - 標題空白或超過 60 字回 `422`（只有 `message`）。
- **依賴：** Slice 3。

#### Slice 7｜AG-UI 串流端點
- **目的：** 對話回覆以 AG-UI SSE 串流，並在同一個請求內完成保存。
- **內容：**
  - `POST /api/v1/assistants/{id}/chat/runs`：body 為 `RunAgentInput`，`threadId` 可省略，語意同 `sendChatMessage`；回傳 `text/event-stream`。
  - 以 `AGUI.Formatting` 輸出；第 3 節的標準事件與自訂事件都要送。
  - 串流開始前的檢查：`assistant-use`、`chat-thread`、問題空白或超過 2,000 字時回 `422`、同一個對話串已有執行中的回覆時回 `409 chat-run-in-progress`、模型未設定時回 `503`。
  - 用戶端中斷時，以 `CancellationToken` 停止模型呼叫，並照第 3 節的規則保存。
  - 版本加入 `AGUI.Abstractions`、`AGUI.Formatting`、`AGUI.Server` 1.0.0（Api 層）。
- **驗收（整合測試，用 `HttpClient` 讀 SSE）：**
  - 事件順序為 `RUN_STARTED` → 至少一個 `TEXT_MESSAGE_CONTENT` → `smartagri.reply` → `RUN_FINISHED`。
  - 保存的訊息與 `smartagri.reply` 的內容一致；`#fail-midway` 時只收到 `RUN_ERROR`，資料庫只有使用者訊息。
  - `KeepConversations = false` 時，資料庫沒有任何對話列；用戶端在 `messages` 夾帶偽造的引用時，最終回覆不會出現該引用。
  - 以 `@ag-ui/client` 寫一個 Node 腳本解析本端點的輸出（放在 `tools/`，CI 執行一次），確認前後端協定相容。
- **依賴：** Slice 5、Slice 6。

#### Slice 8｜精靈試問改真實生成
- **目的：** 建立助理之前，就能用真實回答流程確認知識庫與規則是否正確。
- **內容：**
  - `POST /api/v1/assistant-drafts/{id}/trial-answers`（`S+MA`，不串流）：依草稿的知識庫與回答規則，呼叫 `GroundedAnswerService` 的非串流模式。回傳的形狀與 `ChatReplyView` 相同，加上檢索到的段落與分數，讓建立者判斷門檻是否合適。
  - `ModelInvocation.AssistantId` 在這裡為 null，另加 `DraftId`；或在 `Purpose` 加 `trial-answer`，實作時擇一並寫在 PR 裡。
- **驗收：**
  - 草稿的知識庫全部低於門檻時，模型呼叫次數為 0。
  - 別人的草稿 id 回 `403 assistant-draft`。
  - 回應裡的段落只包含可連接的知識庫。
- **依賴：** Slice 2、Slice 5。

### 軌道 D｜前端（全部依賴 #45）

#### Slice 9｜對話契約非同步化、對話串改用 API、首頁導向
- **目的：** 對話頁的讀取與對話串管理在 API 模式走 HTTP；「開始對話」進入工作區流程。
- **內容：**
  - `listChatThreads`、`createChatThread`、`renameChatThread`、`deleteChatThread`、`getAssistantChat` 改成 Observable，並拿掉 `viewerId` 參數（mapping §4.3 的六點照 #45 的做法處理）。
  - 首頁兩處 `routerLink` 改成 `['/app/chat', assistant.id]`。
  - API 模式下 `/use/:assistantId` 的轉址規則照第 3 節。
  - 側欄的「最近 10 個對話串」改接 Slice 6 的 endpoint。
- **驗收：**
  - Mock 模式的 `chat-history`、`private-conversations`、`error-states`、`accessibility`、`responsive`、`anonymous-visitor`、`consented-submission`、`publishing` 全部通過（這 8 支都用到 `/use/` 或「開始對話」）；需要時補上等待條件。
  - 從首頁點「開始對話」的案例改成斷言進入 `/app/chat/:assistantId`；直接 `cy.visit('/use/...')` 的案例在 Mock 模式維持不變。
  - 本機跑受影響的 Cypress spec。
- **依賴：** #45、Slice 6（API 模式的部分）。

#### Slice 10｜串流對話 UI
- **目的：** 送出訊息改成串流，支援停止、錯誤與「引用驗證後才可點開」。
- **內容：**
  - `ChatRunner` 介面與兩種實作；`npm i @ag-ui/client@1.0.0`（精確版本）。
  - 畫面：
    - 送出後立即顯示使用者訊息，同時鎖住送出按鈕，避免重複送出；
    - 串流中顯示文字與「停止」按鈕；
    - 引用標記在收到 `smartagri.reply` 前是不可點的樣式；
    - 驗證失敗時整則替換成 `no-result`；
    - `RUN_ERROR` 與 `503` 顯示可重試的錯誤。
  - 串流文字用 `aria-live="polite"`，但只在完成時朗讀，避免螢幕報讀器逐字朗讀。
  - Mock 模式把 fixture 切片送出，並提供一個 `?demoScenario=` 情境展示「驗證失敗後替換」。
- **驗收：**
  - 元件測試覆蓋串流中、完成、替換、錯誤、停止五種狀態。
  - `accessibility.cy.ts` 在串流完成後以 axe 掃描通過。
  - API 模式的串流由 Slice 14 的 E2E 驗收。
- **依賴：** Slice 9；API 模式另需 Slice 7。

#### Slice 11｜助理清單、設定、精靈與平台內分享改用 API
- **目的：** 助理的建立與管理在 API 模式走真實後端。
- **內容：**
  - mapping §2.1、§2.5（只有平台內分享）、§2.6 的助理相關方法，照 #45 的模式替換。
  - 精靈的資料庫來源在 API 模式隱藏。
  - 發布頁的網站與 LINE 在 API 模式顯示「後續開放」。
  - M2 期間 mock 助理的資料**不遷移**，發布說明要寫明（第 7 節已決定 4）。
- **驗收：**
  - `create-assistant`、`assistant-list`、`team-and-access`、`publishing` 的 Mock 模式 spec 通過。
  - API 模式由 Slice 14 覆蓋「由草稿建立 → 分享 → 另一個帳號可以使用」。
- **依賴：** #45、#49、Slice 2、Slice 3。

#### Slice 12｜精靈試問接真實生成、統一「沒有答案」的列舉
- **目的：** 試問與正式對話用同一套回覆類型與呈現方式。
- **內容：**
  - `TrialAnswerView` 改成沿用 `ChatReplyView` 的子集，`no-answer` 改名為 `no-result`，`test-step` 的標籤表一併更新。
  - API 模式下試問可以自由輸入問題（固定題組仍保留作為建議），並顯示檢索段落與分數。
- **驗收：**
  - `create-assistant.cy.ts` 在 Mock 模式通過。
  - 元件測試覆蓋三種回覆類型的顯示。
- **依賴：** Slice 8、Slice 11。

### 軌道 E｜品質驗收

#### Slice 13｜回答評測工具 `eval-answers`
- **目的：** 用題庫檢查整條回答流程，並據此調整提示詞與門檻（grounded-answers ADR：題庫必須涵蓋「應該查無結果」的題目）。
- **內容：**
  - 沿用 `eval-retrieval` 的題庫與示範知識，每題加上 `expectedKind`（`company-data`／`no-result`），`company-data` 題另列出應被引用的文件。
  - 指標：回覆類型正確率、引用命中率、拒絕原因分布、平均 token 用量。
  - 報告寫入 `docs/evals/`。
  - CI 用假模型跑一次冒煙測試。
- **驗收：**
  - 用假模型時，報告可以重現。
  - 真實模型的評測與 `MinScore` 調整標「待補做」，等負責人提供 OpenAI 金鑰。
- **依賴：** Slice 5。

#### Slice 14｜CI 的 API 模式 E2E 加上對話
- **目的：** 用真實後端（假模型）驗證第 1 節的業務閉環。
- **內容：**
  - 在 #51 的 API 模式 E2E 上加兩支 spec：
    - 建立知識庫 → 上傳 → 確認生效 → 由精靈建立助理 → 分享 → 以成員身分對話，看到串流回答與可點開的引用；
    - 問一題範圍外的問題，得到「查無資料」。
  - 後端以 `Ai:Chat:Provider=Fake`、`Ai:Embedding:Provider=Fake` 啟動。
- **驗收：** CI 綠燈。
- **依賴：** #51、Slice 7、Slice 10、Slice 11。

---

## 6. 審查發現的對應

| 審查項目 | 在 M3 的處理 |
| --- | --- |
| 待確認：首頁「開始對話」導向 `/use/:assistantId` | 改成 `/app/chat/:assistantId`（2026-09-27 決定），Slice 9 |
| 高：API 模式的 mock 草稿／對話共用 `localStorage` | 草稿與對話在 API 模式改由後端保存（Slice 2、6、9、11），徹底移除這個風險 |
| 中：「我的助理」把載入中、權限不足或部分失敗顯示成空白 | Slice 11 替換時採用與知識庫頁相同的四種狀態 |
| 中：首頁對沒有 `manage-assistants` 的帳號仍顯示「建立新助理」 | 已在 M2 期間處理（#54，已關閉）；Slice 11 在 API 模式以權限旗標維持同樣行為 |
| 待補 5：真實試問驗收與持續維護 | M3 做精靈的真實試問（Slice 8、12）與回答評測（Slice 13）。「每個助理可保存的測試題組、答錯建立處理事項、改版後重跑」排在 M3 之後、對外發布之前（第 7 節決定 E） |
| 問題未解決時轉人工 | 審查建議「平台內對話完成後、對外發布前」。M3 只做到 `no-result` 有明確下一步（`refusalMessage` 與 `nextSteps` 可以寫聯絡窗口）；真正的轉派與指派與上一列一起排在 M3 之後（第 7 節決定 E） |
| 營運追蹤 | M3 先留下不含內容的指標：查無資料率、引用被拒絕率、依助理的 token 用量（Slice 4、5）。工作清單與畫面在 M3 之後 |
| 待補 4：由文件自動整理 FAQ 草稿 | 不在 M3（第 8 節） |

---

## 7. 決定事項、風險與待辦

**已決定（2026-09-27，負責人回覆開場提問）：**

1. **M2 前端票的順序**：#45 先做；#46–#49、#52 與 M3 後端平行進行；M3 前端（軌道 D）排在 #45 之後。
2. **M2 各 PR 的待確認語意全部照現狀接受**：
   - 知識庫清單只有擁有者看得到；
   - 目前有效版本依 `EffectiveFrom` 判斷，沒有回滾；
   - `Retrieval:MinScore = 0.3`，停用的文件連待確認版本也隱藏；
   - 停用原因保留在活動紀錄。
   M3 以 0.3 當部署預設，助理可以自訂。
3. **開發用對話模型：OpenAI**（與嵌入共用同一把金鑰）。程式碼只依賴 `IChatClient`，Azure OpenAI 與 OpenAI 相容端點也一併支援。實際模型名稱在補做真實驗收時由負責人確認，寫在本機設定，不寫死在程式碼。
4. **首頁「開始對話」導向 `/app/chat/:assistantId`**；`/use/:assistantId` 保留給 M5 對外發布，API 模式下轉址。M2 期間的 mock 助理資料不遷移，在發布說明中註明。

**已決定（2026-09-27，負責人對草案的待確認項回覆「按建議」）：**

- **A. AG-UI 用正式版 `AGUI.*` SDK 加自訂端點，M3 不引入 Agent Framework。** 已在本計畫的 PR 中更新兩份 ADR：
  - [前後端整合 ADR](../adr/2026-09-25-frontend-backend-integration.md)：新增「修訂（2026-09-27）」，後端改以 AG-UI 官方 .NET SDK 輸出，Agent Framework 的 hosting 轉正式版後可以替換；
  - [後端技術棧 ADR](../adr/2026-09-25-backend-stack.md)：新增「補充（2026-09-27）」，Agent Framework 在需要工具呼叫的 M4 才引入。
  不採用的替代方案是直接使用 preview 的 `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` 並鎖定版本，開發較快，但違反「核心只用正式版」，而且那個套件已經改名過一次。
- **B. `allow-general-knowledge` 的語意**：
  - 檢索低於門檻時，才以不附段落的方式呼叫模型，回 `general-knowledge`；
  - 高於門檻時一律只回 `company-data`；
  - M3 不做「同一則回答混合兩種類型」，維持 union 互斥，前端契約不必改。
  代價是有段落時就不會補充一般知識。
- **C. 先串流、驗證失敗時整則替換。** 不採用「整段生成完、驗證後才送出」：它最保守，但每題要多等數秒到十幾秒，也失去串流的意義。
- **D. 多輪對話的檢索查詢**：M3 用「上一則使用者問題＋這一則問題」，不另外呼叫模型改寫問題（省一次呼叫，也不會在門檻判斷前就產生費用）。Slice 13 的題庫要加入追問題；結果不好再加入改寫。
- **E. M3 之後、對外發布之前，另排一個里程碑**：
  - 每個助理可保存的測試題組與「全部重跑」；
  - 答錯或查無資料時建立處理事項並指派負責人（包含轉人工）；
  - 文件改版後重跑受影響的題組；
  - 營運追蹤清單。
  這些都以 M3 的真實對話與指標為基礎，也是 M5 對外發布的閘門。已補進 [里程碑 ADR](../adr/2026-09-25-milestone-order.md) 的「補充（2026-09-27）」。
- **F. 引用保存快照**：對話保存當時的摘錄與版本；文件刪除後，舊對話仍顯示快照。不採用的替代方案是來源刪除時一併清除對話中的引用，但會讓舊回答失去依據。
- **G. 刪除助理時，連帶刪除所有成員的對話串**，刪除確認文字要寫明這一點。不採用的替代方案是只允許「暫停」，但資料就會一直留著。

**待使用者提供**（沿用 [負責人待辦事項](2026-09-26-m2-owner-action-items.md) 第 3 項）：OpenAI API 金鑰，用於 Slice 4 的本機手動驗收與 Slice 13 的真實評測。提供之前這兩項標「待補做」，不阻擋其他票。

**技術風險：**

1. **提示注入**：文件內容可能夾帶「忽略以上指示」之類的文字。段落以明確的分隔標記包起來，系統指示放在段落之前；引用驗證能擋下「沒有依據的回答」，但擋不住「引用了被注入的段落」。Slice 13 的題庫要加入一份含注入文字的示範文件。
2. **模型不照格式標引用**：小型模型常漏標或自創格式，引用被拒絕率可能偏高。Slice 5 的解析器要寬鬆接受常見變體；以 Slice 13 的拒絕原因分布調整提示詞。必要時改成「句末至少一個引用」的較寬規則，但仍不接受超出範圍的編號。
3. **AG-UI SDK 都很新**：JS 與 .NET SDK 都是 2026-09 才轉正式版。Slice 7 用 `@ag-ui/client` 實際解析後端輸出，並鎖定精確版本；升級前重跑該檢查。
4. **串流用量取不到**：部分 OpenAI 相容端點在串流時不回 usage，這時 `OutputTokens` 記為 null，不估算。
5. **成本與濫用**：M3 只開放組織內部，只設「單一對話串同時只能有一個執行中的回覆」與問題長度上限；依組織或帳號的額度與速率限制放在 M5（public-channel-protection ADR）。
6. **前端非同步化的連鎖修改**：對話頁有多處 `revision` 重讀模式（mapping §4.3），Slice 9 的改動範圍不小。照 #45 的經驗拆 commit，Cypress spec 逐一在本機跑。

---

## 8. 不在 M3 範圍

- 對外發布：`/use` 的匿名訪客、網站嵌入、LINE、CORS／CSP `frame-ancestors`、防濫用與額度（M5）。
- 表單請求、同意、提交回執與撤回的真實後端（`form-request`／`submission-receipt`，屬於 M4 數據庫）；對話頁的這兩種回覆在 API 模式不會出現。
- Agent Framework、工具呼叫、MCP（M4 起，依第 7 節決定 A）。
- 每個助理的測試題組、答錯建立處理事項、轉人工、改版後自動重跑、營運追蹤畫面（第 7 節決定 E）。
- 同一則回答混合組織資料與一般知識（第 7 節決定 B）。
- 以模型改寫追問、混合檢索、重新排序模型、HNSW 索引。
- 對話保存期限的組織設定與自動刪除工作：ADR 預設是永久保存，使用者可以自行刪除對話串；設定畫面與排程刪除放在組織設定一起做（M5）。
- 由文件自動整理 FAQ 草稿。
- 依組織選擇模型的設定畫面、機敏設定加密（M5）。
- 對話的搜尋、分頁、釘選、封存、編輯已送出的訊息、重新生成。
- 知識庫擁有權轉移、助理擁有權轉移。
