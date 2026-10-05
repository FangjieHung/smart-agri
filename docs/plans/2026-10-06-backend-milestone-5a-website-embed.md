# 後端 Milestone 5a｜網站嵌入：實作計畫

**日期：** 2026-10-06
**狀態：** 已確認（2026-10-06）。第 7 節開場的四項由負責人決定，A–F 由負責人回覆「照建議」定案，可以拆票。issue 標題前綴用「M5a｜」。
**依據：** [M4 結案與 M5 交接](2026-10-05-m4-closeout-m5-handoff.md) 第 3–6 節、[M3.5 計畫](2026-09-29-assistant-acceptance-milestone.md)（發布閘門）、[M3 計畫](2026-09-27-backend-milestone-3-in-platform-chat.md) 第 3 節（回答流程、AG-UI 事件、不保存對話的作法）、`docs/adr/`（milestone-order、public-channel-protection、secrets-storage、llm-providers-and-data-residency、authentication、grounded-answers、withdrawal-and-retention、assistant-access-to-knowledge-and-databases、testing-and-banned-dependencies、on-prem-packaging）、`docs/handoff/mock-to-api-mapping.md` §2.5／§3.4／§4.2、`docs/handoff/route-screen-matrix.md` §5.2、`docs/handoff/tasks-6-10-backend-handoff.md` §5.7／§6、`docs/handoff/ai-assistant-backend-integration-handoff.md` §3.2。
**拆票方式：** 與 M1–M4 相同。每個 Slice 都是可單獨合併的垂直切片，各自附測試與驗收條件。Slice 編號是建議順序，「依賴」欄只列硬依賴。會改 migration 或 `openapi/v1.json` 的 Slice 用接續分支依序做。

---

## 1. 目標與非目標

**目標（M5a 交付）：** 助理擁有者可以把通過驗收的助理放上自己的官網：

> 在助理的「發布」頁設定允許的網域與外觀 → 驗收狀態為「通過」時按下發布 → 把一行 `<script>` 貼到官網 → 未登入的訪客點開右下角的對話視窗，得到附引用的回答；資料裡找不到時回「查無資料」與聯絡方式。不在允許清單的網站載不出對話視窗；用量超過上限或驗收未通過時，對外回覆自動暫停，組織內部照常使用。

具體完成標準：

1. **發布閘門**：只有驗收狀態為 `passed`、允許網域至少一個、助理不是暫停狀態時才能發布。發布之後依第 3 節 C 的規則持續判斷：「過期」照常服務，「未通過」或「尚未驗收」自動暫停對外回覆，重跑通過後自動恢復。
2. **獨立的嵌入入口**：新的 Nx app `apps/widget`，由 API 在 `/use/{assistantId}` 提供，作為 iframe 載入；客戶網站只貼一支原生 JS 載入器 `/embed.js`。admin 的初始 bundle **不增加**（基準 589.34 kB）。widget 有自己的預算（第 3 節 A）。
3. **網域限制由伺服器強制**：`/use/{assistantId}` 的回應依該助理的允許網域送出 `Content-Security-Policy: frame-ancestors`；清單為空、未發布或助理不存在時一律 `frame-ancestors 'none'`，畫面內容相同。訪客 API 只接受 widget 自己的來源，不對任何外部來源開放 CORS。
4. **匿名訪客**：不需要帳號，以伺服器簽發的短期訪客憑證識別。訪客只能問知識庫問題：**不保存任何對話內容**，不開放表單請求、數據庫查詢與轉人工（2026-10-06 決定）。模型呼叫紀錄與回覆統計照寫，不含內容。
5. **防濫用**：以 ASP.NET Core Rate Limiter 依訪客、IP、助理限制頻率；組織每月 token 上限由部署設定與營運指令決定，用到 80% 時在 admin 顯示提示，超過時暫停對外回覆。
6. **機敏設定的基礎設施**：Data Protection 金鑰環存在資料庫以外並可備份，加上「只寫不讀」的機敏欄位型別與「已設定＋末四碼」的檢視；M5b 的 LINE 憑證是第一個使用者。
7. **第一批前置工作**：#41、#50 的真實模型補做與 `Retrieval:MinScore` 校準，以及 #188。

**非目標：** 見第 8 節。

---

## 2. 現況事實（寫計畫時查證，2026-10-06，基準 `bc3044e`）

| 事實 | 出處 |
| --- | --- |
| `AssistantStatus` 只有 `ready`／`paused`；註解保留 `published` 給 M5 | `apps/api/src/SmartAgri.Domain/Assistants/AssistantStatus.cs:5-22` |
| `Assistant` 沒有任何發布、頻道、網域欄位 | `apps/api/src/SmartAgri.Domain/Assistants/Assistant.cs:19-80` |
| 發布 API 已有 `GET {id}/publishing`、`PUT {id}/publishing/platform`、`PUT …/platform/paused`，都要求 `manage-publishing`；`Website`、`Line` 是 `NotAvailablePublishingChannelView` 佔位 | `apps/api/src/SmartAgri.Api/Assistants/AssistantEndpoints.cs:98-106`、`:289-313`、`:880-900` |
| 驗收狀態 `not-accepted`／`passed`／`failed`／`outdated` 是每次讀取時推導，不儲存；「過期」＝最近一次完成的執行之後，有自動觸發、尚未完成的執行；沒有依時間過期 | `apps/api/src/SmartAgri.Domain/Assistants/AssistantAcceptanceStatus.cs:11-29`、`apps/api/src/SmartAgri.Application/Assistants/AssistantAcceptanceRules.cs:37-65` |
| 驗收狀態目前只顯示、不擋任何使用；M3.5 明訂閘門在 M5 實作 | `AssistantEndpoints.cs:45-46`、`docs/plans/2026-09-29-assistant-acceptance-milestone.md:34`、`:316` |
| 全域 fallback 授權政策要求登入；目前只有 health、OpenAPI（Development）與 `/connect/*` 是匿名端點 | `apps/api/src/SmartAgri.Api/Authentication/AuthenticationServiceCollectionExtensions.cs:189-194`、`apps/api/src/SmartAgri.Api/Program.cs:140-178` |
| 對話串流 `POST /api/v1/assistants/{id}/chat/runs` 需要登入，`RunAsync` 依帳號找可用助理、對話串、表單請求與數據庫查詢 | `apps/api/src/SmartAgri.Api/Chat/ChatRunEndpoints.cs:152-166`、`:189-330` |
| 不保存對話的助理：前文取自 `RunAgentInput.messages`，上限 20 則 | `ChatRunEndpoints.cs:145`、`:261`、`:386-389` |
| 回答流程以「助理擁有者」判斷可檢索的知識庫，與提問者無關；但 `GroundedAnswerRequest.AccountId` 是非 null 的 `Guid` | `apps/api/src/SmartAgri.Application/Answers/GroundedAnswerService.cs:184`、`apps/api/src/SmartAgri.Application/Answers/GroundedAnswerModels.cs:91-97` |
| 沒有組織時，查詢篩選回空集合、寫入被拒絕、模型呼叫紀錄丟例外；背景工作用 `FixedOrganizationContext` | `apps/api/src/SmartAgri.Domain/Organizations/IOrganizationContext.cs:3-12`、`apps/api/src/SmartAgri.Infrastructure/Ai/ModelInvocationRecordingChatClient.cs:221-228`、`apps/api/src/SmartAgri.Api/Tenancy/JobOrganizationScope.cs:22-35` |
| `ModelInvocation` 有 `AccountId?`、`AssistantId?`、`Purpose`、`InputTokens?`、`OutputTokens?`、`At`，並有 `(OrganizationId, At)` 索引；沒有任何每月彙總或上限 | `apps/api/src/SmartAgri.Domain/Ai/ModelInvocation.cs:15-61`、`apps/api/src/SmartAgri.Infrastructure/Ai/ModelInvocationConfiguration.cs:37` |
| `AnswerOutcomeChannel` 只有 `chat`／`trial`／`test-run` | `apps/api/src/SmartAgri.Domain/Answers/AnswerOutcomeChannel.cs:10-25` |
| 後端沒有 CORS、Rate Limiter、`AddDataProtection`、CSP 或 forwarded headers 的設定；Data Protection 由 Identity／OpenIddict 隱含使用，金鑰環沒有設定位置，客戶 compose 也沒有掛載（容器重建後金鑰就換了） | `apps/api/src/SmartAgri.Api/Program.cs:137-138`；grep 無結果；`deploy/docker-compose.yml:69-70` |
| 沒有任何通知或寄信機制；#179 的自動停用是「存狀態、在畫面上顯示」 | grep `notif`、`smtp` 無結果；`apps/api/src/SmartAgri.Domain/Reports/ReportSchedule.cs:25-72` |
| `Organization` 只有 `Name`、`Code`、`TeamPermissionsSavedAt` | `apps/api/src/SmartAgri.Domain/Organizations/Organization.cs:7-59` |
| 最新 migration 是 `20261005111140_AddDatabaseArchive` | `apps/api/src/SmartAgri.Infrastructure/Migrations/` |
| API 的 Dockerfile 只有 .NET 建置與執行兩個 stage；客戶 compose 不提供 admin 靜態檔（admin 由 `ADMIN_SPA_ORIGIN` 另外部署） | `apps/api/src/SmartAgri.Api/Dockerfile:3-30`、`deploy/docker-compose.yml:29-75` |
| admin 的 `/use/:assistantId` 在 mock 模式給未登入訪客用，API 模式一律轉到 `/app/chat` 或登入頁 | `apps/admin/src/app/app.routes.ts:167-175`、`apps/admin/src/app/core/session/embedded-chat.guard.ts:20-33` |
| admin 初始 bundle 預算：warning 600 kB、error 1 MB；目前 589.34 kB | `apps/admin/project.json:34-46`、交接文件 §1 |
| mock 已有網站嵌入設定 UI 與型別：`WebsiteEmbedSettings { displayName, welcomeMessage, brandColor, position, allowedDomains }`，最多 5 個網域，`validateAllowedDomain()` 只接受網域、不接受協定或路徑 | `apps/admin/src/app/core/domain/publishing.model.ts:88-147`、`apps/admin/src/app/features/publishing/website-embed/` |
| API 模式把網站與 LINE 顯示成「官網嵌入與 LINE 對外發布將於後續版本開放」 | `apps/admin/src/app/core/repositories/hybrid-demo-repository.ts:1086`、`:2685-2714` |
| 對話相關前端元件沒有用 Angular Material，只用 `_tokens.scss` 的 CSS 變數；但 `ChatConversationComponent`（849 行）注入 `DEMO_REPOSITORY`、session、表單與轉人工服務；`AgUiChatRunner` 從 `hybrid-demo-repository.ts`（3145 行）匯入 `toChatMessage` | `apps/admin/src/app/features/assistant-use/conversation/chat-conversation.component.ts:38-42`、`:161-198`、`apps/admin/src/app/core/chat/ag-ui-chat-runner.ts:3`、`:55` |
| `@ag-ui/client` 1.0.0 鎖定精確版本，admin 以動態 import 載入 | `package.json:14`、`ag-ui-chat-runner.ts:30` |
| `libs/ui`、`libs/theme-pack` 都沒有對話元件 | `libs/ui/src/index.ts:1-7`、`libs/theme-pack/src/index.ts:1-4` |
| CI 的 `build` 跑 `nx run-many -t lint test build`，新的 Nx project 會自動納入；`e2e-api` 以 Fake 模型起 API（5153）與 admin（4200） | `.github/workflows/ci.yml:24`、`:72-155` |
| 開放的 issue 只有 #188；#41、#50 已關閉，但真實模型的驗收與 `MinScore` 校準沒做（`Retrieval:MinScore = 0.3` 是佔位值） | `gh issue list`；`docs/plans/2026-09-26-m2-owner-action-items.md:80-81`；`apps/api/src/SmartAgri.Application/Knowledge/Retrieval/KnowledgeRetrievalSettings.cs:14-22` |

### 2.1 新增依賴

**不新增任何 NuGet 或 npm 套件。** Rate Limiter、Data Protection、forwarded headers 都是 ASP.NET Core 內建；`apps/widget` 沿用 repo 既有的 Angular 22、`@nx/angular` 23.1.0 與 `@ag-ui/client` 1.0.0；`embed.js` 是手寫的原生 JS，不經打包工具。

---

## 3. 關鍵選擇

### A. 嵌入入口：iframe＋獨立的 Angular app（2026-10-06 決定）

```
客戶官網 https://shop.example.com
 └─ <script src="https://<api>/embed.js" data-assistant="<id>" async>   ← 原生 JS，≤ 5 kB
     └─ 右下角按鈕 ＋ <iframe src="https://<api>/use/<id>">
         └─ apps/widget（Angular，zoneless）
             ├─ POST /api/v1/public/assistants/{id}/visitor-sessions
             └─ POST /api/v1/public/assistants/{id}/chat/runs   （AG-UI SSE）
```

- **`apps/widget`**：standalone、zoneless、沒有 router、沒有 Material、沒有 oidc 與 admin 的 session。只做一件事：一個不分對話串的對話視窗。
- **`libs/chat`（新增，`@smart-agri/chat`）**：從 admin 抽出兩邊共用的部分：訊息呈現、串流中的回覆、引用清單與引用內容、AG-UI 事件解析（`AgUiChatRunner` 的核心，去掉對 `hybrid-demo-repository.ts` 的依賴）。表單、同意、轉人工、對話串清單仍留在 admin。元件只吃 CSS 變數，widget 自己提供一份精簡的 token 檔，品牌色由發布設定覆寫。
- **由 API 提供 widget**：`frame-ancestors` 要依每個助理的允許網域動態產生，靜態主機做不到；客戶 compose 也只有 API 一個對外服務。API 的 Dockerfile 加一個 Node stage 建置 widget，把產出放進 API 的靜態檔目錄。widget 與訪客 API 同源，所以訪客 API **完全不需要 CORS**。
- **預算**（寫進 `apps/widget/project.json`，CI 的 production build 會檢查）：
  - widget 初始 bundle：warning 120 kB、error 150 kB；
  - `@ag-ui/client` 照 admin 的作法在送出第一個問題時才動態載入，這個 chunk 另外量測、寫進 PR；
  - `embed.js`：≤ 5 kB（未壓縮），由單元測試檢查檔案大小。
  若 Slice 8 量到 `@ag-ui/client` 的 chunk 過大，退路是用 repo 既有的 `tools/agui-contract` 檢查一個手寫的 SSE 解析器；這個決定在 Slice 8 的 PR 中提出，不預先做。
- **admin 不變**：admin 的 `/use/:assistantId` 在 API 模式維持轉址到 `/app/chat/:assistantId`（它不是訪客入口）；mock 模式照舊，讓 Pages Demo 繼續展示訪客情境。

不採用的替代方案：Web Component 直接放進客戶頁面（客戶頁面的程式讀得到對話內容與訪客憑證，也可能被客戶網站自己的 CSP 擋掉）；Preact／Lit 等較輕的框架（多維護一套框架，對話元件不能共用）。

### B. 網域限制：`frame-ancestors` 依助理動態產生，訪客 API 只接受同源

ADR 寫的是「由伺服器以 CORS 與 `frame-ancestors` 限制」。在 iframe 架構下，兩者的分工如下（本計畫的 PR 一併在 [對外管道 ADR](../adr/2026-09-25-public-channel-protection.md) 補充）：

- **`frame-ancestors` 擋「誰能嵌入」**。`GET /use/{assistantId}` 依該助理的允許網域回 `Content-Security-Policy: frame-ancestors https://shop.example.com https://www.example.com; …`。助理不存在、網站頻道未發布或清單為空時回 `frame-ancestors 'none'`，畫面固定是「這個對話視窗目前無法使用」，兩種情況的狀態碼與內容逐位元組相同，不洩漏助理是否存在。
- **「CORS」擋「誰能從瀏覽器直接呼叫訪客 API」**。訪客 API 不註冊任何 CORS 政策，跨來源的瀏覽器請求在 preflight 就失敗；伺服器另外檢查：帶有 `Origin` 標頭、且不等於 API 本身來源的請求一律 `403`。這樣惡意網站就無法繞過 iframe，在自己的頁面上直接使用訪客 API。
- **網域格式**沿用 mock 的 `validateAllowedDomain()`：只填主機名稱、小寫、最多 5 個、不支援萬用字元與連接埠，`frame-ancestors` 一律產生 `https://<網域>`。`www.example.com` 與 `example.com` 要分別列出。Development／Testing 環境可以用 `PublicChannels:AllowLocalhostAncestors=true` 額外允許 `http://localhost:*`，Production 設成 true 會拒絕啟動。
- **直接開啟 `/use/{id}`**（不在 iframe 裡）：見決定 D。
- **誠實的限制**：這些都是**瀏覽器端**的限制。任何人都能用腳本直接呼叫訪客 API，真正擋住濫用的是第 3 節 E、F 的頻率限制與用量上限。這一點寫進部署文件。

### C. 發布閘門與「目前能不能對外回覆」（2026-10-06 決定）

網站頻道有一個由擁有者設定的狀態 `draft`（已存設定、未發布）／`published`／`paused`（擁有者暫停），另外在每次讀取時推導一個**實際服務狀態**，不儲存：

| 實際服務狀態 | 條件（依序判斷） | 訪客看到 | admin 看到 |
| --- | --- | --- | --- |
| `not-published` | 頻道不是 `published`，或允許網域為空 | iframe 被擋（`frame-ancestors 'none'`） | 未發布 |
| `paused` | 頻道 `paused`，或助理 `paused` | 「目前暫停服務」 | 已暫停 |
| `suspended-acceptance` | 驗收狀態是 `failed`、`not-accepted`，或 `outdated` 且**最近一次完成的執行**有未通過的題目 | 「目前暫停服務」 | 需要處理：驗收未通過，附題組頁連結 |
| `suspended-quota` | 組織當月用量已達上限（E） | 「目前暫停服務」 | 需要處理：本月用量已達上限 |
| `serving` | 其餘情況（包含 `passed`，以及「最近一次完成的執行全部通過」的 `outdated`） | 正常對話 | 已發布 |

- **發布動作**（`POST …/publishing/website:publish`）額外要求驗收狀態「現在」就是 `passed`；`outdated` 時要等重跑完成才能發布。失敗時回 `422`，`errors` 逐項列出原因（`acceptance`、`allowed-domains`、`assistant-paused`，以及決定 B 的 `knowledge-ownership`），什麼都不寫入。
- **「過期」照常服務**：文件改版、設定變更都會自動排入重跑，過期通常只維持幾分鐘。重跑結果未通過就自動轉成 `suspended-acceptance`；之後重跑通過，就自動回到 `serving`，不需要重新發布。
- 推導用的資料與 M3.5 相同（`AssistantAcceptanceRules`），只是多回傳「最近一次完成的執行是否全數通過」。訪客端每次建立工作階段、每次送出問題都重新判斷，不做快取，所以狀態改變會在下一個問題就生效。
- 對訪客，`paused`、`suspended-*` 顯示同一句話，不揭露原因。

### D. 訪客身分與訪客 API（2026-10-06 決定：只做知識庫問答，不保存內容）

- **訪客工作階段**：`POST /api/v1/public/assistants/{id}/visitor-sessions`（匿名）在實際服務狀態為 `serving` 時回 `201 { token, expiresAt, assistant: { displayName, welcomeMessage, brandColor, showCitations } }`；其他情況回逐位元組相同的 `403 { reason: "public-assistant" }`。
  - `token` 是以 Data Protection（`ITimeLimitedDataProtector`，用途字串固定）保護的 `{ visitorId, assistantId, organizationId, issuedAt }`，有效 12 小時；`visitorId` 是伺服器產生的隨機 GUID，**不寫入資料庫**，只用於頻率限制的分區。
  - widget 把 token 放在 iframe 自己的 `sessionStorage`（瀏覽器會依頂層網站分區），關掉分頁就消失；過期時 widget 自動重新建立一次。
- **訪客驗證方式**：新增驗證 scheme `Visitor`，讀 `Authorization: Visitor <token>`，產生 visitor、組織、助理三個 claim。`/api/v1/public/*` 這一組只接受這個 scheme，不接受成員的 bearer token，避免兩種身分混用；`ClaimsOrganizationContext` 從 visitor 的組織 claim 取得組織，既有的查詢篩選、寫入防護、模型呼叫紀錄都不必改。token 裡的 `assistantId` 必須等於路由上的 id，否則 `403`。
- **訪客對話**：`POST /api/v1/public/assistants/{id}/chat/runs`，body 與回應格式與成員對話相同（`RunAgentInput` → AG-UI SSE）。
  - 只送 `smartagri.reply`，不送 `smartagri.thread`、`smartagri.form-check`。
  - 前文一律取自 `RunAgentInput.messages`（上限 20 則，與 `keepConversations=false` 相同），引用永遠只從本次檢索產生。**不寫入任何對話串或訊息**，不管助理的 `keepConversations` 設定為何。
  - 處理器只組合 `GroundedAnswerService`：表單請求、數據庫查詢、轉人工的服務根本不在這條路徑上（由架構測試檢查端點類別沒有依賴這些服務），不是靠旗標關掉。
  - `GroundedAnswerRequest.AccountId` 改成 `Guid?`；訪客的模型呼叫記成新的 `Purpose = public-answer`、`AccountId = null`；回覆統計新增 `AnswerOutcomeChannel.Website`（`website`）。
  - 查無資料時照舊回 `no-result`，文字是助理的 `refusalMessage`。訪客端沒有轉人工按鈕，擁有者應把聯絡窗口寫在 `refusalMessage` 或 `nextSteps` 裡（發布頁加上這個提示）。
  - 錯誤：開始串流前 `401`（token 無效或過期）、`403 public-assistant`（實際服務狀態不是 `serving`）、`409 chat-run-in-progress`（同一個訪客已有執行中的回覆）、`422`（問題過長）、`429`（第 3 節 E，附 `Retry-After`）、`503 chat-not-configured／chat-unavailable`；串流開始後的錯誤照舊送 `RUN_ERROR`。

### E. 頻率限制

ASP.NET Core 內建的 Rate Limiter，只套用在 `/api/v1/public/*`，數值全部放在 `PublicChannels:RateLimits`（預設值見決定 C）：

| 分區 | 對象 | 演算法 |
| --- | --- | --- |
| 每個 IP 建立工作階段 | `visitor-sessions` | fixed window |
| 每個訪客 | `chat/runs` | sliding window（每分鐘、每小時兩層） |
| 每個 IP | `chat/runs` | sliding window |
| 每個助理 | `chat/runs` | sliding window＋同時執行數上限 |

- 超過時回 `429 { reason: "rate-limited" }` 加 `Retry-After`；widget 顯示「問題太頻繁了，請稍後再試」與倒數。
- **IP 取得**：部署在反向代理後面時，要設定 `PublicChannels:TrustedProxies`（IP 或網段），才會採用 `X-Forwarded-For`；沒設定時用連線來源 IP。部署文件要寫明：沒設定的話，所有訪客會被當成同一個 IP。
- Rate Limiter 的計數放在單一 API 程序的記憶體裡，符合目前「每個部署一個 API 容器」的形式；多個執行個體時各自計數，寫進風險。

### F. 組織每月 token 上限（2026-10-06 決定：部署設定＋營運指令，站內提示）

- **上限來源**：`Organization.MonthlyTokenLimit`（`long?`，null 表示用部署預設值）；部署預設值是 `PublicChannels:DefaultMonthlyTokenLimit`。M5a **沒有**設定畫面；營運端用 CLI 指令調整：
  `dotnet SmartAgri.Api.dll set-token-limit --organization <code> --tokens <n>|default`（比照既有的 `migrate`、`eval-retrieval` 指令）。
- **用量**：組織當月（依 `Statistics:TimeZone` 的月份）所有**對話模型**呼叫的 `InputTokens + OutputTokens` 總和，包含組織內部的對話、試問、驗收重跑、表單判斷、報表摘要；不含嵌入模型。理由是成本由整個組織共用；暫停的只有對外回覆，所以內部用量多不會影響內部使用。
  - 供應商沒有回傳用量時記為 null、不估算（M3 決定），這類呼叫不會被計入；寫進風險。
  - 以 `(OrganizationId, At)` 索引加總；訪客端每次判斷時查詢，結果在記憶體快取 30 秒。上限可能被「判斷之後才完成」的回覆略微超過，屬可接受的誤差。
- **狀態**：`normal`（< 80%）／`near`（≥ 80%）／`exceeded`（≥ 100%）。`exceeded` 時實際服務狀態變成 `suspended-quota`；下個月自動恢復，或營運端調高上限後立即恢復。
- **站內提示**：`GET /api/v1/organization/usage`（登入＋`manage-publishing`）回 `{ month, usedTokens, limitTokens, state }`。admin 在「發布管道」頁與助理的「發布」頁顯示用量；`near`、`exceeded` 時在首頁顯示橫幅（只對有 `manage-publishing` 的帳號）。不做 Email：目前沒有任何寄信的基礎設施（第 2 節）。
- 橫幅元件必須很小：admin 初始 bundle 只剩約 11 kB 空間，Slice 11 的 PR 要附 `--skip-nx-cache` 的 production build 數字。

### G. 機敏設定：金鑰環與只寫欄位

- **金鑰環**：`AddDataProtection().SetApplicationName("SmartAgri").PersistKeysToFileSystem(DataProtection:KeysPath)`，並以 `DataProtection:CertificatePath` 的憑證加密金鑰。Production 缺任一項拒絕啟動。客戶 compose 新增 volume `smartagri-dataprotection-keys` 掛在 `/app/keys`，憑證沿用 `deploy/certs/` 的掛載方式。
  - 這也修正一個既有問題：目前金鑰環沒有固定位置，容器重建後 Identity 的登入 cookie 全部失效；M5a 之後訪客憑證也依賴它。
  - 部署文件寫明：金鑰環與憑證要和資料庫一起備份；遺失時，已存的機敏設定要重新輸入、訪客需要重新開啟視窗。
- **只寫欄位**：Domain 新增值物件 `ProtectedSecret`（密文＋末四碼＋設定時間），Application 新增 `ISecretProtector`（Infrastructure 以 Data Protection 實作，每種用途一個 purpose 字串）；API 的檢視型別只有 `SecretStatusView { configured, lastFour, updatedAt }`，沒有任何能讀回明文的端點。M5a 只建立型別與測試，第一個實際欄位是 M5b 的 LINE Channel Secret／Access Token。

### H. 前端（admin）

- **網站頻道改用 API**：沿用 mock 已有的 `website-embed` 元件與 `WebsiteEmbedSettings`，`HybridDemoRepository` 覆寫網站頻道的讀取、儲存、發布、暫停；LINE 在 API 模式維持「將於後續版本開放」。
- **發布狀態對應**：mock 的 `PublishingChannelStatus`（`not-configured`／`testing`／`published`／`needs-attention`／`paused`）對應第 3 節 C：`draft` → `testing`，`serving` → `published`，`suspended-*` → `needs-attention`（附原因），`paused` → `paused`。mock 一併改成同樣的原因欄位，兩種模式共用型別。
- **嵌入程式碼**：`<script src="{PublicChannels:PublicBaseUrl}/embed.js" data-assistant="{id}" async></script>`；`PublicBaseUrl` 是 API 對外可連的網址，由部署設定（Production 要發布網站頻道時必填，否則發布回 `422 public-base-url`）。
- **安裝偵測不做對外連線**：mock 的「檢查安裝」原本暗示伺服器去抓客戶網頁；這在地端環境可能連不到外網，也有 SSRF 風險。改成被動偵測：載入器把 `location.origin` 傳給 widget，widget 建立工作階段時帶上，伺服器若它屬於允許網域，就更新該網域的 `LastSeenAt`。admin 顯示「最後一次在 shop.example.com 偵測到：10/06 14:03」。這個值只供參考，不是安全判斷。

---

## 4. 資料模型

所有新實體都實作 `IOrganizationScoped`，`OrganizationModelTests` 會自動檢查。

| 實體 | 主要欄位 | 說明 |
| --- | --- | --- |
| `AssistantWebsiteChannel`（新增） | AssistantId（PK，複合外鍵到同組織的助理，連帶刪除）、DisplayName（≤ 30）、WelcomeMessage（≤ 120）、BrandColor、Position、State（`draft`／`published`／`paused`）、PublishedAt?、PublishedByAccountId?、UpdatedAt、Revision | 每個助理最多一列；限制值與 mock 的 `MAX_WEBSITE_NAME_LENGTH` 等常數相同 |
| `AssistantWebsiteDomain`（新增） | AssistantId、Domain（小寫主機名稱，≤ 253）、LastSeenAt?、AddedAt | `(AssistantId, Domain)` 唯一；每個助理最多 5 列，由 Application 規則在寫入前檢查 |
| `Organization`（修改） | 加 `MonthlyTokenLimit long?` | null 表示用部署預設值；`>= 0` 的 CHECK |
| `ModelInvocation`（修改） | `Purpose` 加 `public-answer` | `AccountId` 本來就可為 null |
| `AnswerOutcome`（修改） | `Channel` 加 `website` | 仍然不存內容 |

- **不新增訪客資料表**：`visitorId` 只存在於 token 與 Rate Limiter 的記憶體分區，不寫進任何資料表；`ModelInvocation` 與 `AnswerOutcome` 都不記錄訪客。
- **助理刪除**：網站頻道與網域連帶刪除；已嵌入的頁面之後得到 `frame-ancestors 'none'`。
- `AssistantStatus` 不新增 `published`：發布是頻道的狀態，不是助理的狀態（一個助理可以同時在平台內分享與網站上使用）。Domain 註解一併更新。

---

## 5. Vertical slices

### 軌道 A｜前置（第一批，與軌道 B、C 平行）

#### Slice 0a｜#41／#50 真實模型補做
- **內容：**
  - 以 OpenAI 嵌入模型處理一份示範文件，在 Aspire 儀表板確認嵌入呼叫與 `ModelInvocation` 紀錄（#41 最後一項）。
  - 以真實模型跑 `eval-retrieval`，報告寫進 `docs/evals/<日期>-retrieval-<模型>.md`，附建議的 `MinScore`。
  - 金鑰只從 repo 外的檔案載入；任何檢查只輸出變數名稱與值的長度。
- **驗收：** 評測報告已 commit；報告寫明題庫版本、模型、hit@5、查無資料題的結果與建議門檻。
- **依賴：** 無。

#### Slice 0b｜依評測結果調整 `Retrieval:MinScore`
- **內容：** 更新 `appsettings.json` 與 `KnowledgeRetrievalSettings.DefaultMinScore` 及其註解；`eval-answers` 以新門檻重跑一次，結果附在 PR。
- **驗收：** 後端測試全過；報告與設定值一致。
- **依賴：** Slice 0a。

#### #188｜API 模式數據庫清單顯示連接的助理
已建票，照原 issue 的驗收條件做。

### 軌道 B｜後端

#### Slice 1｜Data Protection 金鑰環與只寫的機敏欄位
- **內容：**
  - 第 3 節 G 的金鑰環設定、Production 啟動檢查、compose volume 與憑證掛載、`.env.example` 與 README 的備份說明。
  - `ProtectedSecret`、`ISecretProtector`、`SecretStatusView`。
- **驗收：**
  - 同一個金鑰目錄重建 host 後，先前保護的資料仍可解開；換一個空目錄則解不開，並得到明確的例外。
  - Production 缺 `KeysPath` 或憑證時拒絕啟動（沿用 `StartupFailureTests` 的作法）。
  - `SecretStatusView` 的 JSON 序列化結果不含明文；`lastFour` 對少於 4 個字元的值回 null。
  - `docker compose up` 實際啟動過一次，重啟容器後既有的登入 cookie 仍有效。
- **依賴：** 無。

#### Slice 2｜網站頻道資料模型、設定與發布 API
- **內容：**
  - 第 4 節的 `AssistantWebsiteChannel`、`AssistantWebsiteDomain` 與 migration。
  - Application 的 `WebsiteChannelServing.Evaluate(...)`：第 3 節 C 的實際服務狀態推導（用量狀態先以參數傳入，Slice 3 接上）。`AssistantAcceptanceRules` 多回傳「最近一次完成的執行是否全數通過」。
  - Endpoint（`manage-publishing`＋擁有者，與平台內分享相同的 `AssistantPublishingPolicy`）：
    - `GET`／`PUT /api/v1/assistants/{id}/publishing/website`（設定與網域；`PUT` 帶 `revision`，不一致回 `409`；驗證失敗回 `422` 逐欄錯誤、完全不寫入）；
    - `POST /api/v1/assistants/{id}/publishing/website:publish`（閘門，第 3 節 C）；
    - `PUT /api/v1/assistants/{id}/publishing/website/paused`；
    - `POST /api/v1/assistants/{id}/publishing/website:unpublish`（回到 `draft`，保留設定）。
  - `GET {id}/publishing` 的 `Website` 改成真實的 `WebsiteChannelView`（含實際服務狀態、原因、各網域的 `lastSeenAt`、嵌入程式碼）；`Line` 維持佔位。
  - 同步更新 `openapi/v1.json` 與前端型別。
- **驗收（整合測試）：**
  - 驗收 `not-accepted`、`failed`、`outdated` 時發布得到 `422`，`errors` 含 `acceptance`，資料庫不變；`passed` 時成功。
  - 允許網域為空時發布得到 `422 allowed-domains`。
  - 已發布後，重跑結果未通過 → `suspended-acceptance`；再重跑通過 → `serving`；過程中不必重新發布。
  - `outdated` 且最近一次完成的執行全數通過 → `serving`；最近一次完成的執行有未通過 → `suspended-acceptance`。
  - 組織 B 的 token 讀組織 A 的網站頻道，與不存在的 id 得到相同的 `403 publishing`。
  - 網域驗證與前端 `validateAllowedDomain()` 同規則（同一組案例在前後端各跑一次）。
- **依賴：** 無。

#### Slice 3｜每月用量與上限
- **內容：**
  - `Organization.MonthlyTokenLimit` 與 migration；`PublicChannels:DefaultMonthlyTokenLimit`。
  - Application 的 `OrganizationTokenUsage`：當月對話模型 token 總和與 `normal`／`near`／`exceeded`，30 秒快取；接上 Slice 2 的實際服務狀態。
  - CLI `set-token-limit`。
  - `GET /api/v1/organization/usage`（登入＋`manage-publishing`）。
- **驗收：**
  - 依 `Statistics:TimeZone` 的月份邊界加總（以 `Asia/Taipei` 的月初 00:00 前後各一筆測試）。
  - 嵌入模型的呼叫不計入；`InputTokens` 為 null 的呼叫視為 0。
  - 用量達上限時實際服務狀態為 `suspended-quota`；用 CLI 調高後，快取過期就恢復 `serving`。
  - CLI 對不存在的組織代碼回非零結束碼且不寫入。
- **依賴：** Slice 2（接續分支：兩者都有 migration）。

#### Slice 4｜訪客工作階段與訪客對話端點
- **內容：** 第 3 節 B 的同源檢查與第 3 節 D 的全部內容：`Visitor` 驗證 scheme、組織 claim、`visitor-sessions`、`chat/runs`、`GroundedAnswerRequest.AccountId` 改為可為 null、`public-answer`、`AnswerOutcomeChannel.Website`、被動安裝偵測（`LastSeenAt`）。
- **驗收（整合測試，Fake 模型）：**
  - 實際服務狀態不是 `serving`（每一種原因各一案）時，建立工作階段與送出問題都得到逐位元組相同的 `403 public-assistant`。
  - 成員的 bearer token 呼叫 `/api/v1/public/*` 得到 `401`；訪客 token 呼叫成員端點得到 `401`。
  - 助理 A 的訪客 token 用在助理 B 的路由得到 `403`；竄改或過期的 token 得到 `401`。
  - 帶 `Origin: https://evil.example` 的請求得到 `403`；沒有 `Origin` 或同源時正常。
  - 一次完整的訪客問答之後：`ChatThreads`、`ChatMessages` 的筆數不變（即使助理 `keepConversations=true`）；`ModelInvocations` 多一筆 `public-answer`、`AccountId` 為 null；`AnswerOutcomes` 多一筆 `website`。
  - 助理連接了數據庫、表單判斷設成 `Model` 時，訪客的問題不觸發任何表單或查詢工具（模型收到的工具清單為空）。
  - 架構測試：訪客端點的處理器沒有依賴表單、數據庫查詢與轉人工的服務。
  - 用 `@ag-ui/client` 解析訪客端點的實際輸出（擴充 `tools/agui-contract`）。
- **依賴：** Slice 1、Slice 2、Slice 3。

#### Slice 5｜頻率限制與反向代理
- **內容：** 第 3 節 E；`PublicChannels:RateLimits`、`TrustedProxies`；`429` 契約寫進 OpenAPI。
- **驗收：**
  - 以極小的測試設定，各分區超過上限時得到 `429` 與 `Retry-After`，其他分區不受影響。
  - 成員的 `/api/v1/assistants/*` 對話不受任何訪客分區影響。
  - 沒有設定 `TrustedProxies` 時忽略 `X-Forwarded-For`；設定後才採用。
- **依賴：** Slice 4。

#### Slice 6｜由 API 提供 widget 與 `embed.js`
- **內容：**
  - `GET /use/{assistantId}`：widget 的 `index.html`，依第 3 節 B 送出 `frame-ancestors` 與其他安全標頭（`default-src 'self'`、`Referrer-Policy`、`X-Content-Type-Options`）；未發布時的統一畫面。widget 的建置要關掉會產生 inline 事件處理器的 critical CSS 內嵌，否則與 CSP 衝突。
  - `/widget/*` 靜態資源（含雜湊的檔案長期快取）與 `/embed.js`（短期快取）。
  - `PublicChannels:PublicBaseUrl`、`AllowLocalhostAncestors`、`Widget:RootPath`。
  - API Dockerfile 加 Node stage 建置 widget；`.dockerignore` 同步。
- **驗收：**
  - 整合測試逐一比對標頭：已發布且有兩個網域 → `frame-ancestors https://a.example https://b.example`；未發布、不存在、清單為空 → `'none'`，且兩者回應逐位元組相同。
  - 網域在設定頁被移除後，下一次請求的標頭就不含它（不快取）。
  - `docker compose build` 與 `up` 實際跑過，`curl` 驗證 `/use/{id}` 與 `/embed.js` 回 `200`、標頭正確。
- **依賴：** Slice 2、Slice 8、Slice 9（要有產出可以提供）。

### 軌道 C｜前端（Slice 7 起可與軌道 B 平行）

#### Slice 7｜抽出 `libs/chat`
- **內容：** 第 3 節 A 的共用部分搬到 `libs/chat`；`toChatMessage` 移出 `hybrid-demo-repository.ts`；admin 改為從 lib 匯入，行為不變。
- **驗收：** `nx test admin`、`tsc -p apps/admin/tsconfig.spec.json`、mock 與 API 模式的 Cypress 全過；admin 初始 bundle 不增加（附 `--skip-nx-cache` 的前後數字）；`libs/chat` 不 import 任何 admin 的檔案（eslint `@nx/enforce-module-boundaries` 檢查）。
- **依賴：** 無。

#### Slice 8｜`apps/widget` 訪客對話視窗
- **內容：** 建立 Nx app；建立工作階段、問答、引用、查無資料、暫停服務、`429` 倒數、token 過期自動重建；歡迎訊息與品牌色；前文放在 `sessionStorage`；鍵盤操作與螢幕閱讀器標示；360 px 寬的手機版。預算寫進 `project.json`。
- **驗收：** 單元測試涵蓋每一種狀態；production build 符合預算，PR 附初始與 `@ag-ui/client` chunk 的大小；axe 檢查沒有違規。
- **依賴：** Slice 7（API 契約以 Slice 4 的 OpenAPI 為準，可以先用假的 HTTP 回應開發）。

#### Slice 9｜`embed.js` 載入器
- **內容：** 手寫原生 JS（ES2019、無依賴）：讀 `data-assistant`，插入啟動按鈕與 iframe，開關、位置、品牌色、手機版全螢幕、`Esc` 關閉並把焦點還給按鈕；把 `location.origin` 傳給 iframe（第 3 節 H）；同一頁重複貼兩次只初始化一次。
- **驗收：** jsdom 單元測試；`tsc --checkJs` 通過；檔案大小 ≤ 5 kB 的測試。
- **依賴：** 無。

#### Slice 10｜admin 網站頻道改用 API
- **內容：** 第 3 節 H：Hybrid 覆寫、發布閘門的錯誤顯示（驗收未通過時連到題組頁）、實際服務狀態與原因、嵌入程式碼、安裝偵測、發布確認對話框列出會被訪客檢索到的知識庫（決定 B）。mock 的型別與資料一併更新。
- **驗收：** Hybrid 測試至少一條用實際 API JSON 的 fixture；`nx test admin`；mock 模式的 `publishing.cy.ts` 照過；admin 初始 bundle 不增加。
- **依賴：** Slice 2、Slice 6（嵌入程式碼的網址）。

#### Slice 11｜admin 用量提示
- **內容：** 第 3 節 F 的用量顯示與首頁橫幅；mock 模式提供固定的示範用量。
- **驗收：** `normal`／`near`／`exceeded` 三種狀態的單元測試；沒有 `manage-publishing` 的帳號看不到橫幅；admin 初始 bundle 仍在 600 kB 以內（附數字）。
- **依賴：** Slice 3。

### 軌道 D｜驗收

#### Slice 12｜API 模式 E2E：發布到訪客對話
- **內容：**
  - CI 的 `e2e-api` 先建置 widget，由 API 提供；新增 spec：擁有者設定網域 → 驗收未通過時發布被擋 → 跑題組通過後發布 → 訪客開啟 `/use/{id}` 問答並看到引用 → 擁有者暫停後訪客看到暫停服務。
  - 以 CLI 把測試組織的上限設成極小值，確認訪客看到暫停服務、admin 出現橫幅。
  - `frame-ancestors` 由 `cy.request` 檢查標頭（Cypress 會移除 CSP 標頭，真正的瀏覽器阻擋由 Slice 13 手動驗收）。
  - 本機只用臨時資料庫，不 migrate 共用的 `smartagri`。
- **驗收：** CI 的 `e2e-api` 全綠；新 spec 在本機以 `--spec` 跑過。
- **依賴：** Slice 4、5、6、8、10、11。

#### Slice 13｜部署文件與真實網域驗收
- **內容：**
  - README 與 `deploy/.env.example`：Data Protection 金鑰與憑證的存放、備份、遺失時的影響；`PublicBaseUrl`、`TrustedProxies`、頻率限制與預設上限；`set-token-limit` 用法；「瀏覽器限制擋不住腳本」的說明。
  - 在負責人提供的測試網域上手動驗收（真實對話模型）：允許的網域能嵌入並問答；不在清單的網域被瀏覽器擋下（截圖附 DevTools 的 CSP 錯誤）；手機寬度可用；暫停與恢復。
  - 更新 `docs/handoff/route-screen-matrix.md` 已過時的 `/use` 描述與 mapping §2.5。
- **驗收：** 手動驗收紀錄寫進 `docs/evals/` 或 PR；文件中的指令都實際執行過。
- **依賴：** Slice 12；負責人的測試網域（第 7 節）。

---

## 6. 審查發現的對應

| 審查項目 | 在 M5a 的處理 |
| --- | --- |
| 待補 5：驗收未通過時不得對外發布 | 發布閘門與自動暫停（第 3 節 C，Slice 2） |
| 待補 5：權限變更後重跑題組 | M3.5 沒有把知識庫分享變更列為重跑觸發條件；決定 B 讓對外回答只用擁有者自己的知識庫，分享變更就不影響對外內容 |
| 對外上線：誰核准發布、如何暫停與回滾、額度用完通知誰 | 發布者＝擁有者且有 `manage-publishing`；暫停與取消發布（Slice 2）；用量提示給 `manage-publishing` 的帳號（Slice 3、11）；網域與嵌入測試結果（Slice 13） |
| 客服：查無資料或高風險問題要能轉人工 | 訪客端 M5a 不做轉人工（2026-10-06 決定），以 `refusalMessage`／`nextSteps` 提供聯絡方式，發布頁提示擁有者填寫 |
| 客服：對外回答不得引用舊政策 | 沿用知識庫的生效版本規則；文件改版會讓驗收過期並自動重跑，未通過就自動暫停 |
| 營運追蹤不揭露私人對話 | 訪客對話完全不保存，只有不含內容的模型呼叫紀錄與回覆統計（`website` 頻道） |
| route-screen-matrix §5.2：`?embed=1` 沒有安全意義 | 安全限制完全由伺服器的 `frame-ancestors` 與同源檢查負責（第 3 節 B） |
| mapping §4.2：LINE 憑證原文回傳 | Slice 1 建立只寫欄位，M5b 套用 |

---

## 7. 決定事項、風險與待辦

**已決定（2026-10-06，負責人回覆開場提問）：**

1. **嵌入形式**：iframe＋新的 Angular app `apps/widget`，由 API 提供並送出 `frame-ancestors`；客戶網站貼原生 JS 載入器。共用的對話元件抽到 `libs/chat`。
2. **訪客範圍**：只做知識庫問答，不保存任何對話內容；表單請求、數據庫查詢、轉人工都不開放。
3. **發布後的閘門**：發布當下必須「通過」；之後「過期」照常服務，「未通過」或「尚未驗收」自動暫停，重跑通過後自動恢復。
4. **用量上限**：部署預設值＋營運 CLI 調整，組織不能自行修改；用到 80% 在 admin 顯示提示，超過時暫停對外回覆；不做 Email。

**已決定（2026-10-06，負責人對待確認項回覆「照建議」）：**

- **A. ADR 與用語文件隨本計畫更新**，已在本計畫的 PR 中完成：
  - [對外管道 ADR](../adr/2026-09-25-public-channel-protection.md) 新增「補充（2026-10-06）」，寫明 iframe 架構下 CORS 與 `frame-ancestors` 的分工（第 3 節 B），以及用量的計算範圍（第 3 節 F）；
  - [機敏設定 ADR](../adr/2026-09-25-secrets-storage.md) 的「影響」補上金鑰環的位置；
  - `docs/glossary.md` 新增「官網嵌入」「允許網域」「訪客」「對外發布」。
- **B. 對外回答只能使用「助理擁有者自己擁有」的知識庫。** 目前助理可以連接別人以 `Public`（全組織）或 `SpecificAccounts` 分享給擁有者的知識庫；那些知識庫的擁有者同意的是「組織內分享」，不是放到網路上。因此發布條件加上「連接的知識庫全部由助理擁有者擁有」，不符合時發布回 `422 knowledge-ownership` 並列出這些知識庫；已發布後才連接別人的知識庫，則實際服務狀態轉為暫停並提示原因。這樣驗收題組測到的知識範圍與訪客看到的一致。
  - 不採用的替代方案 1：發布時只顯示清單、由擁有者自行確認（知識庫擁有者沒有同意的機會）。
  - 不採用的替代方案 2：在知識庫加「允許對外助理使用」的開關，由知識庫擁有者決定（較彈性，但多一個 Slice，驗收題組與對外內容也要另外對齊）；等有「中央知識庫由專人維護」的客戶需求時再做。
- **C. 預設數值**（全部可由部署設定覆寫）：
  - 每月 token 上限預設 2,000,000；
  - 每個 IP 每分鐘建立 10 個工作階段；
  - 每個訪客每分鐘 6 題、每小時 60 題；
  - 每個 IP 每分鐘 20 題；
  - 每個助理每分鐘 120 題、同時執行 10 個；
  - 訪客 token 有效 12 小時；問題長度上限沿用成員對話的設定。
- **D. 不在 iframe 裡直接開啟 `/use/{id}` 時顯示「請從官網開啟這個對話視窗」。** 伺服器看 `Sec-Fetch-Dest: document` 判斷；沒有這個標頭的舊瀏覽器照常顯示。這不是安全機制（第 3 節 B 的限制），而是避免助理在擁有者沒有同意的情況下變成任何人都能分享的公開連結。「公開連結」若有需求，之後做成另一個頻道。擁有者要預覽時，用平台內的 `/app/chat/:assistantId`（同一套回答流程）或自己的測試網域。
- **E. 用量包含組織內部的所有對話模型呼叫**（第 3 節 F）。替代方案是只計算對外頻道的用量：組織內部的用量就完全不受控，但 ADR 的目的是控制模型費用。
- **F. 安裝偵測改成被動**（第 3 節 H），不由伺服器連到客戶網站。

**待負責人提供：**

- 一個可以嵌入測試的網域（Slice 13；不擋其他 Slice）。
- 上線前：3–5 份去識別化的真實文件（不擋 M5a 開發）。
- M5b 才需要：LINE 官方帳號與 Messaging API channel。

**技術風險：**

1. **瀏覽器限制擋不住腳本**：`frame-ancestors`、同源檢查都只在瀏覽器有效。對策：頻率限制與用量上限是最後防線；部署文件寫明，並在 Slice 5 的測試中以「不帶 Origin 的直接呼叫」驗證頻率限制仍然生效。
2. **用量計算有盲點**：供應商不回傳用量時記為 null、不計入；檢查與完成之間的回覆可能讓用量略超過上限。對策：部署文件要求使用會回傳用量的供應商；`OutputTokens` 為 null 的比例做成 OpenTelemetry 指標。
3. **單一程序的頻率限制**：多個 API 執行個體時各自計數。目前部署形式是一個容器；改成多個時再評估分散式計數。
4. **反向代理設定錯誤**：沒設 `TrustedProxies` 時所有訪客共用一個 IP 分區，很快就會被 `429`。對策：部署文件與啟動時的警告日誌（Production 且收到 `X-Forwarded-For` 但沒有設定）。
5. **「過期」可能持續很久**：重跑工作失敗（例如模型暫時不可用）時，狀態一直是 `outdated`，對外服務依舊以上一次的通過結果運作。對策：admin 在過期超過 24 小時時顯示提示；不自動暫停（依決定 3）。
6. **widget 的 bundle**：Angular 加上 `@ag-ui/client` 可能超過預算。對策：Slice 8 先量測；`@ag-ui/client` 延後載入；必要時改用手寫解析器並由契約檢查保護（第 3 節 A）。
7. **第三方 iframe 的瀏覽器限制**：Safari 等瀏覽器會分區或限制 iframe 內的儲存。對策：只用 `sessionStorage`，不依賴 cookie；Slice 13 在 Safari 與 Chrome 各手動驗收一次。
8. **提示注入與知識外洩**：對外之後，任何人都能嘗試讓模型吐出知識庫內容。對策：沿用引用驗證；決定 B 限制知識範圍；發布確認對話框明確列出「訪客可以問到的知識庫」。
9. **E2E 測不到真正的 CSP 阻擋**：Cypress 會移除 CSP 標頭。對策：標頭以整合測試與 `cy.request` 驗證，瀏覽器實際阻擋留給 Slice 13 手動驗收。

---

## 8. 不在 M5a 範圍

- LINE（M5b）：Webhook 簽章驗證、LINE 使用者對話、憑證設定畫面（只寫欄位的型別在 M5a 建好）。
- 訪客的表單請求、匿名提交、數據庫查詢、轉人工（2026-10-06 決定）。
- 保存訪客對話、讓擁有者檢視對外對話；對話保存期限與自動刪除的組織設定（交接文件 §3.2「之後另排」）。
- 組織自行設定用量上限的畫面、Email 或其他站外通知。
- 公開連結（不經嵌入的獨立對話頁，決定 D）、萬用字元網域、帶連接埠的網域。
- 知識庫層級的「允許對外助理使用」開關（決定 B 的替代方案 2）。
- 人機驗證（CAPTCHA）：對外管道 ADR 決定第一版不做。
- 依組織選擇模型的設定畫面；組織自備的模型 API 金鑰（只寫欄位的型別已就緒）。
- 本機多語嵌入模型的評測（等完全地端部署的客戶）。
- 分散式頻率限制、多個 API 執行個體。
