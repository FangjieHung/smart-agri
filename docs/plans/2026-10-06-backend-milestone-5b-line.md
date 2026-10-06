# 後端 Milestone 5b｜LINE：實作計畫

**日期：** 2026-10-06
**狀態：** 已確認（2026-10-06）。第 7 節開場的四項由負責人決定，A–F 由負責人回覆「照建議」定案，可以拆票。issue 標題前綴用「M5b｜」。
**依據：** [M5a 結案與 M5b 交接](2026-10-06-m5a-closeout-m5b-handoff.md) §4、[M5a 計畫](2026-10-06-backend-milestone-5a-website-embed.md)（發布閘門、防濫用、只寫欄位的設計）、`docs/adr/`（public-channel-protection、secrets-storage、authentication、withdrawal-and-retention、testing-and-banned-dependencies）、`docs/handoff/ai-assistant-backend-integration-handoff.md` §3.1、`docs/handoff/tasks-6-10-backend-handoff.md` §6、`docs/handoff/mock-to-api-mapping.md` §2.5／§3.4／§4.2；LINE 官方文件（2026-10-06 查證，見第 2.1 節）。
**拆票方式：** 與 M5a 相同。每個 Slice 都是可單獨合併的垂直切片，各自附測試與驗收條件；「依賴」只列硬依賴；會改 migration 或 `openapi/v1.json` 的 Slice 用接續分支依序做。

---

## 1. 目標與非目標

**目標（M5b 交付）：** 助理擁有者可以把通過驗收的助理接上組織自己的 LINE 官方帳號：

> 在助理的「發布」頁貼上 LINE 的 Channel ID、Channel Secret 與 Channel Access Token → 系統自動設定 Webhook 網址並測試連線 → 驗收狀態為「通過」時啟用 → LINE 使用者在一對一聊天，或在群組裡 @ 官方帳號提問，得到附引用（Flex 卡片）的回答；資料裡找不到時回「查無資料」與聯絡方式。驗收未通過、用量超過上限或擁有者暫停時，LINE 回覆自動暫停，組織內部與官網頻道照常。

具體完成標準：

1. **憑證只寫不讀**：Channel Secret 與 Access Token 以 M5a 的 `ProtectedSecret` 加密保存，API 只回「已設定」與末四碼；前端永遠取不到完整值（機敏設定 ADR）。
2. **Webhook**：`POST` 端點以 Channel Secret 對**原始 request body** 驗證 `x-line-signature`（HMAC-SHA256、base64），失敗直接拒絕（對外管道 ADR）。驗證通過後 2 秒內回 `200`，問答在背景處理；以 `webhookEventId` 去重。
3. **同一套把關**：沿用 M5a 的發布閘門（啟用時驗收必須「通過」）與實際服務狀態（過期照常服務、未通過、別人的知識庫、超過用量時自動暫停）、頻率限制與每月 token 上限。
4. **對話**：一對一聊天與群組裡被 @ 時回答（2026-10-06 決定）。前文只暫存在記憶體（每段對話最近 20 則、閒置 30 分鐘清除），資料庫不寫任何對話內容（2026-10-06 決定）。
5. **回覆**：先送 LINE 的「輸入中」動畫（只支援一對一），答案以 reply 送出；超過時限才改用 push 補送（2026-10-06 決定；群組見決定 A）。引用以 Flex 卡片呈現（2026-10-06 決定），依助理的「顯示引用」設定。
6. **admin**：LINE 設定頁改用 API；不再顯示憑證原文；「傳送測試訊息」改成「測試連線」（決定 B）。

**非目標：** 見第 8 節。

---

## 2. 現況事實（2026-10-06，基準：M5a 結案後的 `master`）

| 事實 | 出處 |
| --- | --- |
| `ProtectedSecret`（密文、末四碼、設定時間）、`ISecretProtector`、`SecretStatusView` 已就緒，但還沒有任何資料表或程式使用 | `apps/api/src/SmartAgri.Domain/Secrets/ProtectedSecret.cs:17-73`、`apps/api/src/SmartAgri.Application/Secrets/ISecretProtector.cs:15-26`、`apps/api/src/SmartAgri.Api/Secrets/SecretStatusView.cs:14-20` |
| 網站頻道的實際服務狀態由 `WebsiteChannelServing.Evaluate` 推導，輸入包含網站專屬的「頻道狀態」與「網域數」 | `apps/api/src/SmartAgri.Application/Assistants/WebsiteChannelServing.cs:13-65`、`apps/api/src/SmartAgri.Api/Assistants/AssistantWebsiteChannelEndpoints.cs:432-451` |
| 訪客問答以 `GroundedAnswerService` 處理，用途 `public-answer`、回覆統計 `website`；`GroundedAnswerService.ChannelFor` 遇到沒對應的用途會丟例外 | `apps/api/src/SmartAgri.Api/PublicChannels/VisitorChatRunEndpoints.cs:138-150`、`apps/api/src/SmartAgri.Application/Answers/GroundedAnswerService.cs:385-393` |
| 新增 `ModelInvocationPurpose` 必須歸類為計入用量或嵌入，由測試強制 | `apps/api/src/SmartAgri.Application/Organizations/OrganizationTokenUsageRules.cs:33-46` |
| `PublicAssistantLookup` 只依助理 id 跨組織查出組織 id（允許關掉組織篩選的三處之一） | `apps/api/src/SmartAgri.Infrastructure/Assistants/PublicAssistantLookup.cs:17-35`、`apps/api/tests/SmartAgri.Api.Tests/Tenancy/IgnoreQueryFiltersSourceTests.cs:34-45` |
| 頻率限制以 ASP.NET Core Rate Limiter 套在訪客端點，分區依訪客、IP、助理 | `apps/api/src/SmartAgri.Api/PublicChannels/PublicRateLimiting.cs` |
| 後端目前沒有任何對外的 `HttpClient`（模型呼叫走 OpenAI SDK） | grep 無結果；`apps/api/src/SmartAgri.Infrastructure/Ai/ChatClientProvider.cs:81-93` |
| 背景工作佇列是 PostgreSQL，輪詢 2 秒、至少一次、重試起始 30 秒 | `docs/adr/2026-09-25-background-jobs-on-postgresql.md`、`apps/api/src/SmartAgri.Api/Jobs/JobOptions.cs` |
| `ChatThread` 屬於帳號，放不下 LINE 使用者；沒有任何保存期限設定或清除工作 | `apps/api/src/SmartAgri.Domain/Chat/ChatThread.cs:11-46`、`docs/adr/2026-09-25-withdrawal-and-retention.md` |
| 發布 API 的 `Line` 仍是 `NotAvailablePublishingChannelView`（「LINE 對外發布將於後續版本開放。」） | `apps/api/src/SmartAgri.Api/Assistants/AssistantEndpoints.cs:102-110`、`:1066-1067` |
| mock 已有 LINE 設定元件與流程：四個欄位（官方帳號 ID、Channel ID、Secret、Token）、逐欄檢查、測試訊息、確認啟用；`LineSetupView` 目前**原文回傳** Secret 與 Token | `apps/admin/src/app/core/domain/publishing.model.ts:201-276`、`apps/admin/src/app/features/publishing/line-setup/`、`apps/admin/src/app/core/repositories/publishing-channels.ts:272-323` |
| API 模式下 LINE 一律顯示「將於後續版本開放」；`setPublishingChannelPaused('line')` 不發請求、直接回權限不足 | `apps/admin/src/app/core/repositories/hybrid-demo-repository.ts:1142`、`:2827-2828` |
| 外部服務失敗不當成 HTTP 錯誤，而是 `200` 加上失敗的檢查結果 | `docs/handoff/mock-to-api-mapping.md` §3.4 |
| mock E2E：`publishing.cy.ts` 走完整 LINE 流程；`accessibility.cy.ts` 檢查 LINE 錯誤摘要的焦點 | `apps/admin-e2e/src/e2e/publishing.cy.ts:148-183`、`apps/admin-e2e/src/e2e/accessibility.cy.ts:96-105` |

### 2.1 LINE Messaging API（2026-10-06 查證；官方文件原始碼 `line/line-developers-docs-source`）

| 事實 | 影響 |
| --- | --- |
| 簽章：`x-line-signature` = base64(HMAC-SHA256(Channel Secret, **原始 body**))；不公布來源 IP | 讀原始 bytes 後才驗證，不能先反序列化；不能用 IP 白名單 |
| Webhook 要在約 2 秒內回 `200`；重送預設關閉，重送時 `webhookEventId` 與 reply token 不變（`deliveryContext.isRedelivery`） | 問答改背景處理；以 `webhookEventId` 去重 |
| reply token 只能用一次、約 1 分鐘內有效；reply **免費**，一次最多 5 個訊息物件 | 先 reply；逾時才 push |
| push 依「收到的人數」計算額度：一對一 1 則，群組＝成員數；台灣輕用量方案每月 200 則免費，2026-11-01 起中用量方案不可超量，超過額度時回 `429` 且不送出 | 群組補送很貴（決定 A）；admin 要看得到補送次數 |
| 「輸入中」動畫 `POST /v2/bot/chat/loading/start`（5–60 秒），只支援一對一，群組回 `400` | 群組不送動畫 |
| 被 @ 時 `message.mention.mentionees[]` 有一筆 `isSelf: true` | 群組只在這時回答 |
| userId 以 provider 為範圍（`U` + 32 位十六進位）；群組裡只有 iOS／Android 使用者帶 userId | 以「助理＋聊天對象 id（userId／groupId／roomId）」區分對話 |
| 文字訊息上限 5,000 個 UTF-16 單位；Flex bubble JSON 30 KB、carousel 50 KB 最多 12 張、`altText` 1,500 字 | 長答案截斷；引用卡片限張數與長度 |
| `GET /v2/bot/info` 驗證 Token 並回傳 bot 的 userId 與 basicId；Secret 沒有直接的檢查 API；`PUT /v2/bot/channel/webhook/endpoint` 設定 Webhook 網址；`POST /v2/bot/channel/webhook/test` 由 LINE 送一個**有簽章**的測試事件到我們的端點並回報結果（每小時 60 次） | 「測試連線」＝ Token 檢查＋設定網址＋ webhook test（同時驗證 Secret 與網路可達） |
| Console 的 Verify 按鈕送 `{"destination":"U…","events":[]}`，同樣有簽章 | 空事件也要回 `200` |
| **沒有官方 .NET SDK**；官方提供 OpenAPI 規格（`line/line-openapi`），社群 .NET 套件多已封存 | 自己寫一層薄的 typed `HttpClient`，不引入第三方套件 |
| LINE 使用者資料政策：除內部使用者 id 外，保存「LINE 使用者資訊」（含使用者傳的訊息）超過 24 小時須告知使用者；官方帳號需有隨時可查看的隱私權政策 | 前文只放記憶體、閒置 30 分鐘清除；部署文件提醒客戶準備隱私權政策，並揭露訊息會送交模型供應商處理 |

---

## 3. 關鍵選擇

### A. LINE 頻道的資料與憑證

- 每個助理最多一個 LINE 頻道：`AssistantLineChannel`（第 4 節），沿用網站頻道的狀態 `draft`／`published`／`paused`、`Revision` 與「發布閘門＋實際服務狀態」。
- 四個欄位沿用 mock：官方帳號 ID（`@…`）、Channel ID（10 位數字）、Channel Secret（32 位十六進位）、Channel Access Token（長效型，至少 40 字元、無空白）。前兩個一般欄位；後兩個以 `ProtectedSecret` 保存（purpose `line.channel-secret`、`line.access-token`），API 只回 `SecretStatusView`。
- **只寫的操作方式**：`PUT …/publishing/line` 的 `channelSecret`、`accessToken` 是選填；空值表示「不變更」，有值才取代。任何憑證變更都會清除連線測試結果，並把已啟用的頻道退回需要重新測試（沿用 mock「儲存會重設測試」的規則）。
- Token 用 LINE 的長效型 Channel Access Token（與 mock 一致，客戶最容易取得）；不採用「以 Secret 換短效 token」，以免每個請求多一次 OAuth 呼叫。

### B. 測試連線與啟用（決定 B、C）

「儲存並檢查」之後，「測試連線」依序執行，結果逐項回傳（外部服務失敗是 `200`＋失敗項目，§3.4 慣例）：

1. `GET /v2/bot/info` 驗證 Token；回傳的 basicId 必須等於使用者填的官方帳號 ID，記下 bot 的 userId（Webhook 的 `destination` 必須等於它）。
2. `PUT /v2/bot/channel/webhook/endpoint` 把 Webhook 網址設成 `{PublicBaseUrl}/api/v1/line/webhook/{assistantId}`（決定 C）。
3. `POST /v2/bot/channel/webhook/test`：LINE 送一個有簽章的測試事件到我們的端點；端點以保存的 Secret 驗證通過，LINE 才回報成功——一次同時確認 Secret 正確、網址可達、憑證有效。

**啟用**（`POST …/publishing/line:publish`）的條件 = 三項測試都通過＋M5a 的發布閘門（驗收 `passed`、助理未暫停、知識庫都是擁有者的、`PublicBaseUrl` 已設定）。失敗回 `422`，逐項列出原因。

設定頁同時提醒擁有者在 LINE Official Account Manager 開啟「Webhook」、關閉「自動回應訊息」，並在群組使用時開啟「允許加入群組」。（這些開關是否能用 API 設定未確認，先以說明處理。）

### C. Webhook 端點

- `POST /api/v1/line/webhook/{assistantId}`：匿名，不經訪客驗證，也不受訪客 API 的同源檢查（LINE 的伺服器不送 `Origin`）。
- 處理順序：
  1. 讀原始 body（上限 1 MB）；
  2. 以 `PublicAssistantLookup` 依助理 id 找出組織（不新增關掉組織篩選的地方）；
  3. 解出 Secret，以固定時間比較驗證 `x-line-signature`；助理不存在、沒有 LINE 頻道、Secret 未設定、簽章不符，**一律回同樣的 `401`**；
  4. `destination` 必須等於保存的 bot userId；
  5. 依 `webhookEventId` 去重（記憶體保存 10 分鐘）；
  6. 把事件交給背景處理器，立即回 `200`。
- 端點本身另有每個助理的請求上限，擋住大量偽造請求的計算成本（HMAC 很便宜，但讀 body 與解密仍要花資源）。
- 頻道未啟用（草稿）時，有效簽章的事件仍回 `200`，但不回答（webhook test 在啟用前就要能通過）。

### D. 背景處理與回覆（2026-10-06 決定：reply 為主、逾時改 push）

- **不用 PostgreSQL 工作佇列**：它輪詢 2 秒、至少一次、重試從 30 秒起跳，而 reply token 一分鐘就失效。改用程序內的有界佇列（`System.Threading.Channels`）加一個 `BackgroundService`，每個助理限制同時處理數。程序重啟時佇列中的事件會遺失；這些事件的 reply token 本來就會失效，可以接受。
- **每個事件**：
  - `message`（文字）：一對一直接處理；群組／聊天室只在 `mentionees` 有 `isSelf: true` 時處理，並把 @ 的文字從問題中移除。
  - `message`（非文字）：一對一回固定訊息「目前只能回答文字問題。」；群組不回應。
  - `follow`、`join`：回歡迎訊息（決定 D）。
  - `unfollow`、`leave`：清除該對象的記憶體前文。
  - `unsend`：從記憶體前文移除那一則。
  - 其他事件（含 `postback`、`memberJoined`、`messageEdited`）：忽略。未知事件不報錯。
- **每個問題**：
  1. 重新判斷實際服務狀態，不是 `serving` 就 reply「目前暫停服務」（reply 免費，不呼叫模型）；
  2. 頻率限制（第 3 節 F）；超過時 reply「問題太頻繁了，請稍後再試」，同一個分區每個時間窗只回一次，避免洗版；
  3. 一對一送「輸入中」動畫；
  4. `GroundedAnswerService.AnswerAsync`，前文取自記憶體（第 3 節 E），用途 `line-answer`、回覆統計 `line`，`AccountId = null`；
  5. 組訊息（第 3 節 G）；
  6. 從收到事件起算，在期限內（預設 50 秒）用 reply 送出；超過或 reply 失敗時，一對一改用 push 補送（決定 A：群組不補送）。
- **補送次數**：每次 push 記一筆不含內容的 OpenTelemetry 指標，並累計在頻道上「本月補送次數」，顯示在 admin。
- LINE 回 `429`（額度用完或頻率）時，記錄並放棄這一則，不重試。

### E. 前文只在記憶體（2026-10-06 決定）

- 以「助理 id＋聊天對象 id（userId／groupId／roomId）」為鍵，保存最近 20 則問答文字（與網站訪客的上限相同），閒置 30 分鐘清除；以 `IMemoryCache` 設上限（例如 10,000 段對話），超過時淘汰最舊的。
- 資料庫不寫任何對話內容或 LINE 的 id；`ModelInvocation`、`AnswerOutcome` 也不記錄 LINE 使用者。因此不觸發 LINE 政策的 24 小時告知義務。
- 群組共用一段前文（成員彼此看得到問答，前文也是公開的）。
- 單一程序的前提與 M5a 的頻率限制相同，寫進部署文件。

### F. 頻率限制

LINE 的請求都來自 LINE 的伺服器，不能用 IP 分區；改在背景處理器裡用程式化的 `PartitionedRateLimiter`，數值放在 `PublicChannels:RateLimits:Line*`（預設值見決定 E）：

| 分區 | 對象 |
| --- | --- |
| 每個 LINE 使用者（一對一） | 每分鐘、每小時兩層 |
| 每個群組／聊天室 | 每分鐘 |
| 每個助理 | 每分鐘＋同時處理數 |

每月 token 上限沿用 M5a：LINE 的問答（`line-answer`）計入組織用量，超過時 LINE 回覆暫停，官網同時暫停，組織內部不受影響。

### G. 訊息格式（2026-10-06 決定：引用用 Flex 卡片）

- **回答**：一則純文字訊息。移除 `[n]` 標記改成「（來源 1）」之類的可讀標示，超過 5,000 個 UTF-16 單位時截斷並加「…」。
- **引用**（助理開啟「顯示引用」時）：第二則訊息用 Flex carousel，每份被引用的文件一張 bubble（最多 5 張），內容是知識庫名稱、文件名稱與原文摘錄（摘錄限 200 字）；`altText` 是「參考來源：文件 A、文件 B」。控制 JSON 在 30 KB／50 KB 之內，超過時減少摘錄長度。
- **查無資料**：拒答訊息＋訪客版的下一步（不出現「請聯絡這個助理的管理者」），與官網一致。
- **一般知識**：沿用 `general-knowledge` 的提示文字。

### H. LINE API 用戶端

- 自寫 `LineMessagingClient`（typed `HttpClient`，`IHttpClientFactory`）：只實作用到的端點（bot info、設定／讀取 Webhook 網址、webhook test、reply、push、loading），逾時 10 秒，記錄回應的 `x-line-request-id`，不記錄訊息內容或 Token。
- 基底網址可設定（`Line:ApiBaseUrl`），測試與 E2E 指向假的 LINE 伺服器；Production 必須是官方網址。
- 不引入任何 LINE SDK：沒有官方 .NET SDK，社群套件多已封存（testing-and-banned-dependencies ADR 要求確認維護團隊）。

### I. 前端（admin）

- `line-setup` 改用 API：Hybrid 覆寫讀取、儲存、測試連線、啟用、暫停、取消啟用；mock 改成同一個契約。
- Secret／Token 欄位：已設定時顯示「已設定・末四碼 1234」與「更換」；不再有「顯示」切換（前端拿不到原文）。
- 「傳送測試訊息」改為「測試連線」，逐項顯示三個檢查與失敗原因（決定 B）；顯示實際的 Webhook 網址（唯讀，附複製）；實際服務狀態與原因對應比照網站頻道；顯示本月補送次數與群組設定提醒。
- mock 的 `publishing.cy.ts`、`accessibility.cy.ts` 依新流程更新。

---

## 4. 資料模型

| 實體 | 主要欄位 | 說明 |
| --- | --- | --- |
| `AssistantLineChannel`（新增） | AssistantId（PK，複合外鍵到同組織的助理，連帶刪除）、OfficialAccountId、ChannelId、ChannelSecret（`ProtectedSecret`，3 欄）、AccessToken（`ProtectedSecret`，3 欄）、BotUserId?、WelcomeMessage、State（`draft`／`published`／`paused`）、ConnectionCheckedAt?、ConnectionChecks（jsonb：三項檢查的結果與訊息）、PublishedAt?、PublishedByAccountId?、PushFallbackMonth?、PushFallbackCount、UpdatedAt、Revision | `IOrganizationScoped`；憑證變更會清除連線測試結果 |
| `ModelInvocation`（修改） | `Purpose` 加 `line-answer` | 計入每月用量 |
| `AnswerOutcome`（修改） | `Channel` 加 `line` | 不含內容 |

- 不新增任何保存對話或 LINE id 的資料表。
- 實際服務狀態：把 `WebsiteChannelServing` 一般化成兩個頻道共用的推導（輸入改為「頻道已設定且可用」＋其餘共同條件），列舉值沿用 `not-published`／`paused`／`suspended-*`／`serving`。

---

## 5. Vertical slices

### 軌道 A｜後端

#### Slice 1｜LINE 頻道資料模型與設定 API
- **內容：**
  - `AssistantLineChannel` 與 migration；第一個使用 `ProtectedSecret` 的資料表（EF complex property）。
  - 實際服務狀態一般化（第 4 節）。
  - Endpoint（`manage-publishing`＋擁有者；不存在、別人的、別的組織一律 `403 publishing`）：
    - `GET`／`PUT /api/v1/assistants/{id}/publishing/line`（`revision` 衝突 `409`；逐欄驗證 `422`；Secret／Token 空值表示不變更）；
    - `PUT …/line/paused`；`POST …/line:unpublish`。
  - `GET …/publishing` 的 `line` 改為真實的 `LineChannelView`：一般欄位、兩個 `SecretStatusView`、Webhook 網址、檢查結果、實際服務狀態與原因、本月補送次數。
  - 同步更新 `openapi/v1.json` 與前端型別。
- **驗收（整合測試）：**
  - 任何回應的 JSON 都不含 Secret／Token 原文，也不含密文；`lastFour` 正確。
  - 只更新一般欄位時，憑證不變；更換 Token 會清除連線測試結果，已啟用的頻道回到「需要重新測試」。
  - 欄位格式與 mock 的規則相同（同一份案例 JSON 在前後端各跑一次，比照 #194）。
  - 跨組織、別人的、不存在的 id 得到相同的 `403 publishing`。
- **依賴：** 無。

#### Slice 2｜LINE API 用戶端、測試連線與啟用
- **內容：** 第 3 節 H 的 `LineMessagingClient`；測試用的假 LINE 伺服器（`HttpMessageHandler`，可以模擬成功、`401`、`429`、逾時）；`POST …/line:test`（第 3 節 B 的三項檢查）；`POST …/line:publish`（啟用閘門）。
- **驗收：**
  - 三項檢查各自的成功與失敗都回 `200`＋逐項結果（外部服務失敗不是 HTTP 錯誤）；Token 錯誤時不會進行後兩項。
  - basicId 與填寫的官方帳號 ID 不符時，該項失敗並說明。
  - 啟用時三項未全通過、驗收未通過、知識庫不屬於擁有者、`PublicBaseUrl` 未設定，各自得到 `422` 的對應原因。
  - 記錄中沒有 Token 或 Secret（以測試擷取 log 檢查）。
- **依賴：** Slice 1。

#### Slice 3｜Webhook 端點
- **內容：** 第 3 節 C；程序內佇列與 `BackgroundService` 的骨架（先只處理 `follow`／`join` 的歡迎訊息、`unfollow`／`leave`／`unsend` 的前文清除、非文字訊息的固定回覆）。
- **驗收：**
  - 以官方文件的範例（含空 `events` 的 Verify 請求）驗證簽章計算正確；body 任何一個位元組不同就是 `401`。
  - 不存在的助理、沒有 LINE 頻道、Secret 未設定、簽章錯誤，回應完全相同。
  - `destination` 不符時忽略事件。
  - 同一個 `webhookEventId` 只處理一次（含 `isRedelivery`）。
  - 端點在處理器很慢時仍立即回 `200`（測試以假的慢處理器量測）。
  - 草稿狀態的頻道：webhook test 的事件回 `200`，但不回答。
  - `IgnoreQueryFiltersSourceTests` 不變（重用 `PublicAssistantLookup`）。
- **依賴：** Slice 1、Slice 2。

#### Slice 4｜LINE 問答
- **內容：** 第 3 節 D 的問題處理、第 3 節 E 的記憶體前文、第 3 節 F 的頻率限制、第 3 節 G 的訊息格式；`line-answer` 用途（計入用量）與 `line` 回覆統計；群組的提及判斷；reply 期限與 push 補送；補送次數。
- **驗收（以假的 LINE 伺服器與 Fake 模型）：**
  - 一對一提問：先送「輸入中」，再以 reply 送出「回答＋Flex 引用」；關閉「顯示引用」時只有回答。
  - 群組裡沒有 `isSelf` 的訊息不回答；被 @ 時回答，問題不含 @ 的文字；群組不送「輸入中」。
  - 追問時，模型收到前一輪的前文；閒置 30 分鐘後前文清除（以可注入的時鐘測試）；`unsend` 從前文移除。
  - 暫停、驗收未通過、超過用量時回「目前暫停服務」，不呼叫模型。
  - 頻率限制：各分區超過時回一次提示，之後同一時間窗不再回覆，也不呼叫模型。
  - 回答超過期限時，一對一改用 push，補送次數＋1；群組依決定 A 處理。
  - 資料庫的對話串、訊息筆數不變；`ModelInvocations` 多一筆 `line-answer`、`AccountId` 為 null；`AnswerOutcomes` 多一筆 `line`。
  - 5,000 字截斷與 Flex 大小限制的邊界測試。
- **依賴：** Slice 3。

### 軌道 B｜前端

#### Slice 5｜admin LINE 設定改用 API
- **內容：** 第 3 節 I；mock 與 Hybrid 共用新契約；更新 mock 的 `publishing.cy.ts`、`accessibility.cy.ts`、`responsive.cy.ts`。
- **驗收：** Hybrid 測試使用實際錄下的 API 回應（含檢查失敗、啟用被拒的 `422`）；前端任何地方都不會出現憑證原文；`nx test admin`、`tsc` 通過；admin 初始 bundle 附數字。
- **依賴：** Slice 1、Slice 2。

### 軌道 C｜驗收

#### Slice 6｜API 模式 E2E
- **內容：** CI 的 `e2e-api` 加入假的 LINE 伺服器（小型 Node 程式，記錄收到的 reply／push）；新 spec：擁有者在 admin 填憑證 → 測試連線 → 驗收未通過時啟用被拒 → 跑題組通過後啟用 → Cypress 以 `cy.task` 產生**有簽章**的 webhook 請求模擬 LINE 使用者提問 → 檢查假伺服器收到的 reply（文字＋Flex）→ 暫停後收到「目前暫停服務」。
- **驗收：** CI 的 `e2e-api` 全綠；新 spec 在本機以 `--spec` 跑過。
- **依賴：** Slice 4、Slice 5。

#### Slice 7｜部署文件與真實 LINE 驗收
- **內容：**
  - `deploy/README.md`：LINE 官方帳號與 Messaging API channel 的準備、Official Account Manager 的設定（Webhook、自動回應、群組）、額度與補送的說明、隱私權政策提醒（揭露訊息會送交模型供應商）。
  - 在負責人的 LINE 官方帳號上實機驗收（Cloudflare 通道＋真實模型）：一對一問答與 Flex 引用、群組 @ 問答、加好友歡迎訊息、非文字訊息、暫停與恢復、逾時補送（以人為延遲觸發一次）。驗收紀錄寫進 `docs/evals/`。
- **依賴：** Slice 6；負責人的 LINE 官方帳號。

---

## 6. 審查發現的對應

| 項目 | 在 M5b 的處理 |
| --- | --- |
| tasks-6-10 §8 #10：憑證加密、只回末四碼 | Slice 1（`ProtectedSecret`） |
| tasks-6-10 §8 #11：真實 Webhook 驗證與重送 | Slice 3（簽章、`webhookEventId` 去重） |
| integration-handoff §3.1：LINE 使用者與網站訪客是否同一種身分 | 都是匿名、不保存內容；LINE 以聊天對象 id 區分前文，不建立帳號（authentication ADR） |
| integration-handoff §3.1：「先測試再啟用」、儲存會重設測試 | 保留（第 3 節 A、B） |
| mapping §3.4：外部服務失敗不是 HTTP 錯誤 | 測試連線回 `200`＋逐項結果 |

---

## 7. 決定事項、風險與待辦

**已決定（2026-10-06，負責人回覆開場提問）：**

1. **前文只在記憶體暫存**：每段對話最近 20 則、閒置 30 分鐘清除；資料庫不寫任何對話內容。
2. **以 reply 為主，逾時改 push**：先送「輸入中」，期限內 reply，逾時才 push；補送次數顯示在 admin。
3. **也支援群組**：只在被 @ 時回答。
4. **引用用 Flex 卡片**。

**已決定（2026-10-06，負責人對待確認項回覆「照建議」）：**

- **A. 群組裡逾時不補送 push**：push 依收到的人數計算額度，在 30 人的群組補送一次就是 30 則；一對一照決定 2 補送。替代方案：群組也補送（每次消耗群組人數的額度），或由擁有者在設定頁選擇。
- **B. 「傳送測試訊息」改為「測試連線」。** 用 LINE 的 webhook test API，同時驗證 Token、Secret 與網址可達，不消耗訊息額度，也不需要先知道擁有者的 LINE userId。替代方案是保留「傳送測試訊息」：需要擁有者先加官方帳號好友並傳一則訊息讓系統記下 userId，而且 push 會消耗額度。
- **C. 由系統自動設定 Webhook 網址**（`PUT /v2/bot/channel/webhook/endpoint`），設定頁仍顯示網址。替代方案：只顯示網址，由擁有者自己貼到 LINE Developers Console（多一個容易出錯的步驟）。
- **D. LINE 頻道有自己的歡迎訊息欄位**（加好友、被邀進群組時送出，≤ 120 字，預設「您好！有任何問題都可以直接問我。在群組裡請 @ 我再提問。」）。替代方案：沿用網站頻道的歡迎訊息（網站頻道可能沒有設定，而且群組需要提醒要 @）。
- **E. 預設數值**（全部可由部署設定覆寫）：每個 LINE 使用者每分鐘 6 題、每小時 60 題（同網站訪客）；每個群組每分鐘 10 題；每個助理每分鐘 120 題、同時處理 10 題；reply 期限 50 秒；記憶體前文 20 則、閒置 30 分鐘、最多 10,000 段對話。
- **F. ADR 與用語隨本計畫更新**，已在本計畫的 PR 中完成：對外管道 ADR 補充 LINE 的簽章、去重、頻率限制分區與「群組只在被提及時回答」；機敏設定 ADR 補上第一個使用者；authentication ADR 註明 LINE 使用者不建立帳號；`docs/glossary.md` 新增「LINE 頻道」「測試連線」「補送」。

**待負責人提供：**

- LINE 官方帳號與 Messaging API channel（Slice 7；開發期間 Slice 1–6 用假的 LINE 伺服器，不需要）。
- 實機驗收時，一支裝有 LINE 的手機，以及一個可以邀官方帳號加入的測試群組。

**技術風險：**

1. **reply 期限**：模型偶爾超過 50 秒時就會補送 push。對策：OpenTelemetry 記錄每題耗時與補送次數；`ReasoningEffort=None` 已讓 `gpt-6-luna` 平均約 2 秒。
2. **程序內佇列**：重啟會遺失處理中的事件；單一程序的前提與 M5a 相同。對策：部署文件註明；重啟時使用者最多少收到一則回答。
3. **Official Account Manager 的開關**：「自動回應訊息」若沒關，使用者會同時收到 LINE 的自動回應。能否以 API 檢查未確認。對策：設定頁說明，Slice 7 實機確認。
4. **群組的隱私**：群組成員都看得到彼此的問答；前文也是整個群組共用。對策：設定頁說明；群組裡不回覆任何個人化資訊（本來就只回答知識庫內容）。
5. **LINE 規格變動**：reply token 時限、計費方式可能調整。對策：數值都可設定；`LineMessagingClient` 集中處理，測試用假伺服器。
6. **偽造請求**：任何人都能打 Webhook 網址。對策：簽章不符一律 `401` 且不洩漏原因；端點有每個助理的請求上限；body 大小上限。

---

## 8. 不在 M5b 範圍

- 保存 LINE 對話供擁有者檢視、對話保存期限與自動刪除（屬於「之後另排」的組織設定）。
- 圖片、語音、檔案等非文字訊息的理解；Rich menu、LIFF、LINE Login。
- 擁有者以 LINE 推播行銷訊息（broadcast、narrowcast、multicast）。
- LINE 的表單請求、數據庫查詢與轉人工（與官網訪客相同的限制）。
- 多個 API 執行個體之間共用佇列、前文與頻率限制。
- 以 LINE 的 Profile API 取得使用者名稱或頭像（不需要，也避免 24 小時告知義務）。
