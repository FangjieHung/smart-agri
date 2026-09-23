# Mock → API 對照表（`DemoRepository` 全方法）

**適用版本**：`apps/admin` 分支 `master`。介面宣告位置：`apps/admin/src/app/core/repositories/demo-repository.ts:227-519`。

## 0. 這份文件的定位

這份文件是 **`DemoRepository` 全部 57 個方法的單一索引**，回答四個問題：

1. 這個方法對應哪個 endpoint、需要什麼授權、成功時回什麼。
2. 哪些錯誤是「使用者可以在畫面上自己解決的」（可恢復），哪些不是（不可恢復）。
3. 換成真後端時，**除了 adapter 以外**還要動哪些前端檔案。
4. 契約從同步改成非同步時，具體會壞掉什麼。

**逐功能的深度細節不在這裡。** 請求／回應型別、列舉值中文對照、狀態機、驗證規則、權限規則、「哪些是假的」，全部寫在 `docs/handoff/tasks-6-10-backend-handoff.md`：

| 功能區 | 深度章節 |
| --- | --- |
| 建立助理精靈 | `tasks-6-10-backend-handoff.md` 第 2 節 |
| 知識庫 | 第 3 節 |
| 資料庫、表單與追蹤 | 第 4 節 |
| 終端對話與同意流程 | 第 5 節 |
| 發布管道 | 第 6 節 |
| 尚未被畫面呼叫的方法 | 第 7 節 |
| 後端必須自行決定的缺口 | 第 8 節 |
| 共用回應契約與 `permission-denied` 硬規則 | 第 1.5、1.6 節 |

其餘兩份文件：`docs/handoff/ai-assistant-backend-integration-handoff.md`（總表與驗收清單）、`docs/handoff/route-screen-matrix.md`（路由 × 畫面 × 狀態 × e2e）。

---

## 1. 欄位定義

### 1.1 授權需求代碼

`viewerAccountId` 目前是**每個方法的第一個參數**（例如 `demo-repository.ts:214-216`）。正式 API 一律改由 session 推導，request 裡不得再出現 accountId；下表的「授權」就是 session 建立後還要額外滿足的條件。

| 代碼 | 意義 | Demo 中的判斷位置 |
| --- | --- | --- |
| `S` | 只需要有效 session | `core/session/demo-session.guard.ts:9-12` |
| `S+MA` | 另需 `manage-assistants` 權限 | `account.model.ts:24` |
| `S+MD` | 另需 `manage-data-sources` 權限 | `account.model.ts:25` |
| `S+OWN` | 另需是該助理／知識庫／資料庫的**擁有者** | 見對應章節的權限規則 |
| `S+DM` | 另需是該資料庫的**指定資料管理者**（`dataManager`） | `tasks-6-10-backend-handoff.md` 第 4.5 節 |
| `S+USE` | 另需對該助理有**使用**權限（擁有／團隊分享／開放外部客戶） | `tasks-6-10-backend-handoff.md` 第 5.4 節 |
| `—` | Demo 專用，**正式 API 不得提供** | — |

> **注意**：`manage-publishing` 這個權限值存在（`account.model.ts:26`）但發布類方法目前**只檢查擁有者、沒有檢查它**。下表寫 `S+OWN` 是描述現狀，不是建議；正式版要不要改成 `S+OWN且MA/MP`，見 `tasks-6-10-backend-handoff.md` 第 8 節第 9 點。

### 1.2 可恢復 vs 不可恢復

| 類別 | 定義 | 畫面行為 |
| --- | --- | --- |
| **可恢復** | 使用者在**同一個畫面上**修正後重送就會成功 | 保留已填內容，顯示逐欄錯誤或錯誤摘要，焦點移到第一個出錯欄位（`shared/ui/error-summary.ts:5-11`） |
| **不可恢復** | 使用者在這個畫面上做什麼都沒用 | 整塊換成 `app-state-panel`（`shared/ui/state-panel/state-panel.component.ts:8` 的 `error` / `permission-denied`），或導回 `/login` |

HTTP 對應的通則（每個方法的特例寫在表中）：

| 情況 | HTTP | 類別 |
| --- | --- | --- |
| 欄位驗證失敗 | `422` + `{ errors?, message }` | 可恢復 |
| 未同意／狀態前置條件未達成（例如 LINE 未測試就啟用） | `422` | 可恢復 |
| 樂觀鎖衝突（目前**尚未實作**，見第 4.3 節） | `409` | 可恢復 |
| 速率限制 | `429` + `Retry-After` | 可恢復 |
| 無權限 **或** 資源不存在 | `403` + `{ reason, message }`，**兩者內容必須完全相同** | 不可恢復 |
| Session 逾時／未登入 | `401` | 不可恢復（導回 `/login`） |
| 伺服器或下游錯誤 | `5xx` | 不可恢復（可提供「重試」按鈕，但不是同畫面修正） |

**硬規則**：`403` 的 `message` 不得包含資源名稱、擁有者或任何可推測存在性的資訊。詳見 `tasks-6-10-backend-handoff.md` 第 1.6 節。

---

## 2. 對照表

「前端呼叫位置」欄位是**換成非同步時必須跟著改的檔案**，路徑一律省略 `apps/admin/src/app/` 前綴。行號在本文件撰寫時逐一以 `sed -n` 核對過。

### 2.1 建立助理精靈（8 個方法）

深度：`tasks-6-10-backend-handoff.md` 第 2 節。

| 方法（契約行號） | 建議 endpoint | 授權 | 成功 | 可恢復錯誤 | 不可恢復錯誤 | 前端呼叫位置 |
| --- | --- | --- | --- | --- | --- | --- |
| `listAssistantTemplates()` `:307` | `GET /api/v1/assistant-templates` | `S` | `200` `AssistantTemplateView[]` | `429` | `401`／`5xx` | `features/assistants/assistant-wizard/assistant-draft.store.ts:98` |
| `listConnectableSources(viewer)` `:309-311` | `GET /api/v1/connectable-sources` | `S+MA` | `200` `ConnectableSourceView[]` | `429` | `401`／`403 assistant-draft`／`5xx` | `assistant-draft.store.ts:106` |
| `listTrialQuestions()` `:312` | `GET /api/v1/trial-questions` | `S` | `200` `TrialQuestionView[]` | `429` | `401`／`5xx` | `assistant-draft.store.ts:115` |
| `previewTrialAnswer(viewer, request)` `:314-317` | `POST /api/v1/assistant-drafts/trial-answers` | `S+MA` | `200` `TrialAnswerView` | `422`（試問文字為空）／`429`／LLM 逾時 | `401`／`403 assistant-draft`／`5xx` | `assistant-draft.store.ts:245` |
| `getAssistantDraft(viewer)` `:319-321` | `GET /api/v1/assistant-drafts/me` | `S+MA` | `200` `SavedAssistantDraftView` 或 `null` | — | `401`／`403 assistant-draft`／`5xx` | `assistant-draft.store.ts:318`、`features/home/home-page.component.ts:37` |
| `saveAssistantDraft(viewer, draft)` `:322-325` | `PUT /api/v1/assistant-drafts/me` | `S+MA` | `200` `SavedAssistantDraftView` | `409`（多分頁編輯，目前無版本欄位）／`429` | `401`／`403 assistant-draft`／`5xx` | `assistant-draft.store.ts:305` |
| `discardAssistantDraft(viewer)` `:326` | `DELETE /api/v1/assistant-drafts/me` | `S+MA` | `204`（契約目前宣告 `void`） | — | `401`／`403`／`5xx` | **畫面未直接呼叫**；由 mock 在 `createAssistantFromDraft` 成功後內部呼叫（`mock-demo-repository.ts:1070`） |
| `createAssistantFromDraft(viewer, draft)` `:328-331` | `POST /api/v1/assistants` | `S+MA` | `201` `AssistantConfigurationView` | `422` `AssistantDraftFieldError[]`（逐欄）／`429` | `401`／`403 assistant-draft`／`5xx` | `assistant-draft.store.ts:278` |

`discardAssistantDraft` 是**唯一沒有回傳信封、也沒有權限檢查**的方法（`demo-repository.ts:326`）。正式版必須加授權；要不要改成回傳 `RepositoryView<void>`，見第 4.2 節。

### 2.2 知識庫（6 個方法）

深度：`tasks-6-10-backend-handoff.md` 第 3 節。

| 方法（契約行號） | 建議 endpoint | 授權 | 成功 | 可恢復錯誤 | 不可恢復錯誤 | 前端呼叫位置 |
| --- | --- | --- | --- | --- | --- | --- |
| `listKnowledgeBaseSummaries(viewer)` `:333-335` | `GET /api/v1/knowledge-bases` | `S` | `200` `KnowledgeBaseSummaryView[]` | `429` | `401`／`5xx` | `features/knowledge/knowledge-list/knowledge-list-page.component.ts:28` |
| `getKnowledgeBaseDetail(viewer, id)` `:340-343` | `GET /api/v1/knowledge-bases/{id}` | `S+OWN` | `200` `KnowledgeBaseDetailView` | `429` | `401`／`403 knowledge-base`／`5xx` | `features/knowledge/knowledge-detail/knowledge-detail-page.component.ts:84` |
| `addDemoKnowledgeDocument(viewer, kbId)` `:345-348` | 正式版改為檔案上傳：`POST /api/v1/knowledge-bases/{id}/documents`（見第 3.2 節） | `S+OWN` | `201` `KnowledgeDocumentView`（`queued`） | `413` 檔案過大／`415` 格式不支援／`422` 檔名重複／`429` | `401`／`403 knowledge-base`／`5xx` | `knowledge-detail-page.component.ts:117` |
| `advanceKnowledgeDocument(viewer, kbId, docId)` `:350-354` | **正式 API 不得存在** | `—` | — | — | — | `knowledge-detail-page.component.ts:159`（整段應移除，見第 3.3 節） |
| `retryKnowledgeDocument(viewer, kbId, docId)` `:356-360` | `POST /api/v1/knowledge-bases/{id}/documents/{docId}/retry` | `S+OWN` | `200` `KnowledgeDocumentView` | `409`（文件已在處理中）／`429` | `401`／`403 knowledge-base`／`5xx` | `knowledge-detail-page.component.ts:128` |
| `updateKnowledgeSharing(viewer, kbId, sharing)` `:361-365` | `PUT /api/v1/knowledge-bases/{id}/sharing` | `S+OWN` | `200` `KnowledgeSharingView` | `422`（只有 `message`，無逐欄 errors）／`409`／`429` | `401`／`403 knowledge-base`／`5xx` | `knowledge-detail-page.component.ts:139` |

### 2.3 資料庫、表單與追蹤（7 個方法）

深度：`tasks-6-10-backend-handoff.md` 第 4 節。

| 方法（契約行號） | 建議 endpoint | 授權 | 成功 | 可恢復錯誤 | 不可恢復錯誤 | 前端呼叫位置 |
| --- | --- | --- | --- | --- | --- | --- |
| `listDatabaseTemplates(viewer)` `:367-369` | `GET /api/v1/database-templates` | `S+MD` | `200` `DatabaseTemplateView[]` | `429` | `401`／`403 database`／`5xx` | `features/databases/database-list/database-list-page.component.ts:29` |
| `listDatabaseSummaries(viewer)` `:371-373` | `GET /api/v1/databases` | `S` | `200` `DatabaseSummaryView[]` | `429` | `401`／`5xx` | `database-list-page.component.ts:24` |
| `createDatabaseFromTemplate(viewer, input)` `:374-377` | `POST /api/v1/databases` | `S+MD` | `201` `DatabaseSummaryView` | `422`（只有 `message`）／`429` | `401`／`403 database`／`5xx` | `database-list-page.component.ts:55` |
| `getDatabaseDetail(viewer, id)` `:382-385` | `GET /api/v1/databases/{id}` | `S+OWN` | `200` `DatabaseDetailView` | `429` | `401`／`403 database`／`5xx` | `features/databases/database-detail/database-detail-page.component.ts:78` |
| `updateDatabaseFields(viewer, id, fields)` `:387-391` | `PUT /api/v1/databases/{id}/fields` | `S+OWN` | `200` `DatabaseFieldView[]` | `422` `DatabaseFieldError[]`（逐欄）／`409`／`429` | `401`／`403 database`／`5xx` | `database-detail-page.component.ts:111` |
| `previewDatabaseEntry(viewer, id, answers)` `:393-397` | `POST /api/v1/databases/{id}/entries:preview` | `S+OWN` | `200` `DatabaseTrialPreviewView`（**不建立紀錄**） | `422` `DatabaseFieldError[]`／`429` | `401`／`403 database`／`5xx` | `database-detail-page.component.ts:130` |
| `getDatabaseTracking(viewer, id)` `:402-405` | `GET /api/v1/databases/{id}/tracking` | `S+DM` | `200` `DatabaseTrackingView`（差異與文案**由伺服端算好**） | `429` | `401`／`403 database-records`（非資料管理者）／`403 database`（不存在或非擁有者）／`5xx` | `database-detail-page.component.ts:86` |

`getDatabaseTracking` 是全表唯一會回傳**兩種不同 reason** 的方法：`database-records` 代表「這個資料庫你看得到，但收集紀錄不給你看」，`database` 代表「不存在或不是你的」。兩者的畫面呈現不同，不能合併。

### 2.4 終端對話與同意流程（8 個方法）

深度：`tasks-6-10-backend-handoff.md` 第 5 節。

| 方法（契約行號） | 建議 endpoint | 授權 | 成功 | 可恢復錯誤 | 不可恢復錯誤 | 前端呼叫位置 |
| --- | --- | --- | --- | --- | --- | --- |
| `listChatThreads(viewer, assistantId)` `:411-414` | `GET /api/v1/assistants/{id}/chat/conversations` | `S+USE` | `200` `ChatThreadListView`（依最後活動由新到舊） | `429` | `401`／`403 assistant-use`／`5xx` | `features/assistant-use/workspace-chat/workspace-chat-page.component.ts:64` |
| `createChatThread(viewer, assistantId)` `:416-419` | `POST /api/v1/assistants/{id}/chat/conversations` | `S+USE` | `201` `AssistantChatView`（空白對話） | `429` | `401`／`403 assistant-use`／`5xx` | `workspace-chat-page.component.ts:112` |
| `renameChatThread(viewer, assistantId, threadId, title)` `:421-426` | `PATCH /api/v1/assistants/{id}/chat/conversations/{threadId}` | `S+USE` + thread 屬於 viewer | `200` `ChatThreadSummaryView` | `422`（標題為空或過長，只有 `message`）／`429` | `401`／`403 assistant-use`／`403 chat-thread`／`5xx` | `workspace-chat-page.component.ts:124` |
| `deleteChatThread(viewer, assistantId, threadId)` `:428-432` | `DELETE /api/v1/assistants/{id}/chat/conversations/{threadId}` | `S+USE` + thread 屬於 viewer | `200` `ChatThreadListView`（**剩下的清單**，不是 `204`） | `429` | `401`／`403 assistant-use`／`403 chat-thread`／`5xx` | `workspace-chat-page.component.ts:134` |
| `getAssistantChat(viewer, assistantId, threadId?)` `:444-448` | `GET /api/v1/assistants/{id}/chat`（`?conversation=` 選填） | `S+USE` | `200` `AssistantChatView` | `429` | `401`／`403 assistant-use`／`403 chat-thread`／`5xx` | `features/assistant-use/conversation/chat-conversation.component.ts:121` |
| `sendChatMessage(viewer, assistantId, text, threadId?)` `:453-458` | `POST /api/v1/assistants/{id}/chat/messages` | `S+USE` | `200` `AssistantChatView`（**整份對話**） | `422`（訊息為空，只有 `message`）／`429`／LLM 逾時 | `401`／`403 assistant-use`／`403 chat-thread`／`5xx` | `chat-conversation.component.ts:158` |
| `reviewChatForm(viewer, assistantId, formId, answers)` `:460-465` | `POST /api/v1/assistants/{id}/chat/forms/{formId}:review` | `S+USE` | `200` `ChatFormReviewView`（**不建立紀錄**） | `422` `DatabaseFieldError[]`／`429` | `401`／`403 assistant-use`／`5xx` | `chat-conversation.component.ts:204` |
| `submitChatForm(viewer, assistantId, submission, threadId?)` `:470-475` | `POST /api/v1/assistants/{id}/chat/forms/{formId}/submissions` | `S+USE` | `200` `AssistantChatView`（含收據訊息） | `422` `DatabaseFieldError[]`，**未勾選同意也是 `422`**／`429` | `401`／`403 assistant-use`／`403 chat-thread`／`5xx` | `chat-conversation.component.ts:226` |

兩個必須保留的行為：

- **`threadId` 省略時的語意**（契約 `demo-repository.ts:447`、`:457`、`:474`）：`getAssistantChat` 開啟最後活動的那一段、`sendChatMessage` 寫進同一段、沒有任何對話時開新的一段。`/use/:assistantId` 永遠不傳 `threadId`。
- **同意勾選在欄位驗證之後才檢查**，否則使用者會先看到「請勾選同意」而不是「電話格式錯誤」。

### 2.5 發布管道（10 個方法）

深度：`tasks-6-10-backend-handoff.md` 第 6 節。**每個助理固定三個管道，不能新增或刪除**（契約註解 `demo-repository.ts:251`、`:255`）。

| 方法（契約行號） | 建議 endpoint | 授權 | 成功 | 可恢復錯誤 | 不可恢復錯誤 | 前端呼叫位置 |
| --- | --- | --- | --- | --- | --- | --- |
| `listPublishingChannels(viewer)` `:252-254` | `GET /api/v1/publishing/channels` | `S` | `200` `PublishingChannelView[]` | `429` | `401`／`5xx` | `features/assistants/assistant-list/assistant-list-page.component.ts:31` |
| `listChannelOverview(viewer)` `:256-258` | `GET /api/v1/publishing/overview` | `S` | `200` `AssistantChannelsView[]` | `429` | `401`／`5xx` | `features/publishing/channel-overview/channel-overview-page.component.ts:26` |
| `getAssistantPublishing(viewer, assistantId)` `:263-266` | `GET /api/v1/assistants/{id}/publishing` | `S+OWN` | `200` `AssistantPublishingView` | `429` | `401`／`403 publishing`／`5xx` | `features/publishing/assistant-publishing/assistant-publishing.component.ts:49` |
| `updatePlatformSharing(viewer, assistantId, accountIds)` `:268-272` | `PUT /api/v1/assistants/{id}/publishing/platform` | `S+OWN` | `200` `PlatformSharingView` | `422` `PublishingFieldError[]`／`409`／`429` | `401`／`403 publishing`／`5xx` | `features/publishing/platform-sharing/platform-sharing.component.ts:41` |
| `updateWebsiteEmbed(viewer, assistantId, settings)` `:274-278` | `PUT /api/v1/assistants/{id}/publishing/website` | `S+OWN` | `200` `WebsiteEmbedView`（網域變更**會重置安裝檢查**） | `422` `PublishingFieldError[]`（網域格式）／`409`／`429` | `401`／`403 publishing`／`5xx` | `features/publishing/website-embed/website-embed.component.ts:135` |
| `checkWebsiteInstallation(viewer, assistantId)` `:280-283` | `POST /api/v1/assistants/{id}/publishing/website:check-installation` | `S+OWN` | `200` `WebsiteEmbedView` | `429`；外部網站無回應時**必須是成功回應 + `installCheck: 'not-detected'`**，不是 `5xx` | `401`／`403 publishing`／`5xx` | `website-embed.component.ts:155` |
| `saveLineSettings(viewer, assistantId, input)` `:285-289` | `PUT /api/v1/assistants/{id}/publishing/line` | `S+OWN` | `200` `LineSetupView`（**會重置測試與啟用狀態**） | 逐欄錯誤寫在 `LineSetupView` 內而非 `422`（見下方註） | `401`／`403 publishing`／`5xx` | `features/publishing/line-setup/line-setup.component.ts:88` |
| `sendLineTestMessage(viewer, assistantId)` `:291-294` | `POST /api/v1/assistants/{id}/publishing/line:test` | `S+OWN` | `200` `LineSetupView`（結果寫在 `lastTest`） | `429`；**LINE 端失敗要回成功 + `lastTest` 失敗紀錄**，不是 `5xx` | `401`／`403 publishing`／`5xx` | `line-setup.component.ts:102` |
| `activateLineChannel(viewer, assistantId)` `:296-299` | `POST /api/v1/assistants/{id}/publishing/line:activate` | `S+OWN` | `200` `LineSetupView` | `422` `PublishingFieldError[]`（**未通過測試就啟用**） | `401`／`403 publishing`／`5xx` | `line-setup.component.ts:109` |
| `setPublishingChannelPaused(viewer, assistantId, type, paused)` `:301-306` | `PUT /api/v1/assistants/{id}/publishing/{type}/paused` | `S+OWN` | `200` `PublishingChannelView`（**只影響這一個管道**） | `409`／`429` | `401`／`403 publishing`／`5xx` | `assistant-publishing.component.ts:74` |

`saveLineSettings` 的回傳型別是 `RepositoryView<LineSetupView>`（`demo-repository.ts:289`），**不是** `...Result` union——逐欄錯誤是包在 `LineSetupView` 裡回傳的，不走 `validation-failed`。只有 `activateLineChannel` 才有獨立的 `ActivateLineChannelResult`（`:196-198`）。後端若改成 `422`，`line-setup.component.ts:88` 的分支要一起改。

### 2.6 助理與基礎資料（12 個方法）

前七個已被畫面使用，後五個目前**沒有任何功能元件呼叫**（語意見 `tasks-6-10-backend-handoff.md` 第 7 節）。

| 方法（契約行號） | 建議 endpoint | 授權 | 成功 | 可恢復錯誤 | 不可恢復錯誤 | 前端呼叫位置 |
| --- | --- | --- | --- | --- | --- | --- |
| `listUsableAssistants(viewer)` `:217-219` | `GET /api/v1/assistants?usable=true` | `S` | `200` `AssistantSummaryView[]` | `429` | `401`／`5xx` | `features/home/home-page.component.ts:21`、`:29`、`workspace-chat-page.component.ts:86` |
| `listAssistantConfigurations(viewer)` `:214-216` | `GET /api/v1/assistants` | `S+MA` | `200` `AssistantConfigurationView[]` | `429` | `401`／`5xx` | `assistant-list-page.component.ts:30`、`features/assistants/assistant-detail/assistant-detail-page.component.ts:106` |
| `getAssistantAnalytics(viewer, assistantId)` `:290-293` | `GET /api/v1/assistants/{id}/analytics` | `S+OWN` | `200` `AssistantAnalyticsView`（匿名統計） | `429` | `401`／`403 assistant-configuration`／`5xx` | `assistant-detail-page.component.ts:117` |
| `getAssistantSettings(viewer, assistantId)` `:244-247` | `GET /api/v1/assistants/{id}/settings` | `S+MA+OWN` | `200` `AssistantSettingsView`（設定＋已連接來源＋回答規則） | `429` | `401`／`403 assistant-configuration`／`5xx` | `features/assistants/assistant-detail/assistant-settings.store.ts:51` |
| `updateAssistantSettings(viewer, assistantId, patch)` `:252-256` | `PATCH /api/v1/assistants/{id}/settings` | `S+MA+OWN` | `200` `AssistantSettingsView` | `422` 逐欄 `AssistantSettingsFieldError[]`（驗證失敗時**完全不寫入**） | `401`／`403 assistant-configuration`／`5xx` | `assistant-settings.store.ts:133`（概覽）、`:139`（回答與記錄） |
| `setAssistantSourceConnection(viewer, assistantId, source, connected)` `:261-266` | `PUT`／`DELETE /api/v1/assistants/{id}/sources/{type}/{sourceId}` | `S+MA+OWN`＋來源必須是 viewer 看得到的 | `200` `AssistantSettingsView` | `422`（來源不可見、或會解除最後一個來源） | `401`／`403 assistant-configuration`／`5xx` | `assistant-settings.store.ts:152` |
| `getAssistantSources(viewer, assistantId)` `:235-238` | `GET /api/v1/assistants/{id}/sources` | `S+OWN` | `200` `AssistantSourceReference[]` | `429` | `401`／`403 assistant-configuration`／`5xx` | **未被呼叫**（已連接來源改由 `getAssistantSettings` 一併回傳） |
| `listAccounts()` `:213` | `GET /api/v1/share-targets`（**不要做成帳號目錄**） | `S`；回傳範圍需另外設計授權 | `200` `AccountView[]` | `429` | `401`／`5xx` | **未被呼叫**；`/login` 的三個身分是寫死的（`features/demo-login/demo-login-page.component.ts:18-22`） |
| `listKnowledgeBases(viewer)` `:224-226` | 由 `listKnowledgeBaseSummaries` 取代，**不需要獨立 endpoint** | `S` | `200` `KnowledgeBaseView[]` | — | `401`／`5xx` | **未被呼叫** |
| `listDatabases(viewer)` `:227-229` | 由 `listDatabaseSummaries` 取代，**不需要獨立 endpoint** | `S` | `200` `DatabaseView[]` | — | `401`／`5xx` | **未被呼叫** |
| `listPrivateConversations(viewer)` `:230-232` | `GET /api/v1/conversations` | `S` | `200` `PrivateConversationView[]` | `429` | `401`／`5xx` | **未被呼叫**（`/app/activity` 仍是 placeholder） |
| `getConversation(viewer, conversationId)` `:233-236` | `GET /api/v1/conversations/{id}` | `S` + 對話屬於 viewer | `200` `PrivateConversationView` | `429` | `401`／`403 private-conversation`／`5xx` | **未被呼叫** |

### 2.7 結構化提交（3 個方法，皆未被畫面呼叫）

| 方法（契約行號） | 建議 endpoint | 授權 | 成功 | 可恢復錯誤 | 不可恢復錯誤 | 前端呼叫位置 |
| --- | --- | --- | --- | --- | --- | --- |
| `listManagedSubmissions(viewer)` `:237-239` | `GET /api/v1/submissions?role=manager` | `S+DM` | `200` `StructuredSubmissionView[]`（**只含 `consentStatus === 'consented'`**） | `429` | `401`／`5xx` | **未被呼叫**（收集紀錄改走 `getDatabaseTracking`） |
| `listOwnSubmissions(viewer)` `:240-242` | `GET /api/v1/submissions?role=self` | `S` | `200` `StructuredSubmissionView[]` | `429` | `401`／`5xx` | **未被呼叫** |
| `submitAuthorizedForm(viewer, input)` `:243-246` | `POST /api/v1/submissions` | `S`＋外部客戶＋`consent === true` | `201` `StructuredSubmissionView` | `422`（應改成這樣，見下方註） | `401`／`403 authorized-form`／`5xx` | **未被呼叫**（同意流程改走 `submitChatForm`） |

**必須修正的不一致**：`submitAuthorizedForm` 把「沒有勾選同意」當成 `permission-denied`，而 `submitChatForm` 把同一件事當成 `validation-failed`。正式版要挑一種，建議統一為 `422`。詳見 `tasks-6-10-backend-handoff.md` 第 7 節第 2 點。

### 2.8 Demo 情境切換器（3 個方法，正式 API 不得提供）

`DemoScenarioController`，契約 `demo-repository.ts:206-210`。

| 方法 | 建議 endpoint | 說明 |
| --- | --- | --- |
| `setScenario(scenario)` `:207` | **無** | 由 `?demoScenario=` 網址參數觸發（`core/repositories/demo-scenario-param.ts:9-17`），在 DI factory 中套用（`core/repositories/tokens.ts:19-22`） |
| `getScenario()` `:208` | **無** | — |
| `resetScenario()` `:209` | **無** | — |

HTTP adapter 可把三者實作成 no-op，或在正式建置中把整個 `DemoScenarioController` 從 `DemoRepository` 的 extends 清單拿掉（`demo-repository.ts:212`）。後者會讓 `?demoScenario=` 完全失效，這是預期行為。

---

## 3. 每個方法的「Demo 特有行為」速查

只列**換成真後端時語意會變、或畫面會壞掉**的項目。完整的「哪些是假的」在 `tasks-6-10-backend-handoff.md` 各節的 `.6` 小節。

### 3.1 回傳整份聚合而不是增量

`sendChatMessage`、`submitChatForm` 回傳**整份 `AssistantChatView`**，`deleteChatThread` 回傳**剩下的整份清單**。前端沒有任何本地合併邏輯，直接整份取代。後端若改成回增量，`chat-conversation.component.ts:158`、`:226` 與 `workspace-chat-page.component.ts:134` 都要改寫。

### 3.2 上傳完全不存在

`addDemoKnowledgeDocument(viewer, kbId)` **沒有檔案參數**（`demo-repository.ts:345-348`），只建一筆假紀錄。換成真上傳時契約一定要改，至少要：

- 方法簽章加上檔案或上傳 token 參數。
- 多一個上傳進度的來源（`XMLHttpRequest` progress、或預簽名 URL 的兩段式流程）。
- `413` / `415` 這兩個目前不存在的錯誤類別需要新的畫面文案。

### 3.3 處理進度靠按鈕手動推進

`advanceKnowledgeDocument` 是 Demo 專用的「下一步」按鈕（`knowledge-detail-page.component.ts:159`）。正式版要整段移除，並改為輪詢或推播。這會讓 `knowledge-detail-page.component.ts` 的 `revision` 遞增邏輯（`:121`、`:132`、`:143`、`:163`）需要重新設計成「伺服端推來新狀態就更新」。

### 3.4 外部服務都沒有真的連出去

`checkWebsiteInstallation`、`sendLineTestMessage` 都是本地模擬。它們的共同契約要求是：**外部服務失敗不可以變成 HTTP 錯誤**，必須回 `200` 加上失敗的結果欄位（`installCheck: 'not-detected'` / `lastTest` 失敗紀錄），否則畫面會把「檢查完成、結果是沒裝」誤顯示成「檢查失敗」。

### 3.5 沒有任何分頁

全部 list 類方法都一次回全部，簽章裡沒有任何分頁參數。詳見 `ai-assistant-backend-integration-handoff.md` 第 5.1 節。

---

## 4. 前端替換位置

### 4.1 唯一的 DI 注入點

```
apps/admin/src/app/core/repositories/tokens.ts:7-26
```

整個 Demo 只有這一個 provider。替換步驟：

1. **新增** `apps/admin/src/app/core/repositories/http-demo-repository.ts`，實作 `DemoRepository`（`demo-repository.ts:212-476`）。
2. 把 `tokens.ts:11-24` 的 factory 改成回傳新 adapter，並拿掉 `readDemoScenario` 那三行（`:19-22`）。
3. **不要改** `demo-repository.ts` 的介面，除非確實要改契約（要改的清單見第 4.2 節）。
4. **保留** `mock-demo-repository.ts` 與 `demo-seed*.ts`：六個 `mock-demo-repository*.spec.ts` 是契約的可執行規格，新 adapter 應該能通過同一組行為測試。

`core/repositories/local-storage-repository.ts` 與 `core/repositories/repository.ts` 是**沒有任何人使用的舊程式**（找不到資料時會 `throw`，語意與本契約不同）。不要拿它當參考。

### 4.2 替換時建議一併修改的契約

| 契約位置 | 現狀 | 建議 |
| --- | --- | --- |
| 所有方法的 `viewerAccountId` 第一個參數 | 例 `demo-repository.ts:214-216` | 全部移除，viewer 由 session 推導 |
| `AccountId` 等 id 的字面值 union | `core/domain/account.model.ts:1-4` | 放寬成 `string`，否則後端無法回傳任何新 id |
| `discardAssistantDraft` 回傳 `void` | `demo-repository.ts:326` | 改成 `RepositoryView<void>`，才能表達 `403` |
| `DemoScenarioController` 被 `DemoRepository` extends | `demo-repository.ts:212` | 正式建置移除 |
| `DemoKeyValueStorage` | `demo-repository.ts:201-204` | HTTP adapter 不需要；草稿若要離線編輯可沿用同樣 key 格式 |
| LINE 憑證原文回傳 | `LineSetupView` | 改成只回末四碼與「是否已設定」，見 `tasks-6-10-backend-handoff.md` 第 8 節第 10 點 |

### 4.3 同步 → 非同步：具體會壞掉什麼

**這是替換工作量的主體。** 契約目前**全部同步**：`listAccounts(): RepositoryView<readonly AccountView[]>`（`demo-repository.ts:213`）。改成 `Observable<...>` 或 `Promise<...>` 後：

#### (1) 15 個注入點、45 個呼叫點都要改

呼叫點以 `rg -n --glob '!*.spec.ts' -o 'repository\.[a-zA-Z]+\(' apps/admin/src/app/features` 可重現（扣掉兩個 `*.testing.ts` 中的輔助呼叫後為 45 處）。

注入 `DEMO_REPOSITORY` 的功能檔案共 15 個：

```
features/home/home-page.component.ts:16
features/assistants/assistant-list/assistant-list-page.component.ts:24
features/assistants/assistant-detail/assistant-detail-page.component.ts:39
features/assistants/assistant-wizard/assistant-draft.store.ts:71
features/knowledge/knowledge-list/knowledge-list-page.component.ts:24
features/knowledge/knowledge-detail/knowledge-detail-page.component.ts:67
features/databases/database-list/database-list-page.component.ts:19
features/databases/database-detail/database-detail-page.component.ts:64
features/assistant-use/conversation/chat-conversation.component.ts:80
features/assistant-use/workspace-chat/workspace-chat-page.component.ts:39
features/publishing/channel-overview/channel-overview-page.component.ts:21
features/publishing/assistant-publishing/assistant-publishing.component.ts:32
features/publishing/platform-sharing/platform-sharing.component.ts:15
features/publishing/website-embed/website-embed.component.ts:44
features/publishing/line-setup/line-setup.component.ts:29
```

另有五個測試輔助檔也提供同一個 token，簽章改動後要一併調整：`features/knowledge/knowledge.testing.ts:21`、`features/databases/databases.testing.ts:21`、`features/publishing/publishing.testing.ts:17`、`features/assistant-use/assistant-use.testing.ts:29`、`features/assistants/assistant-wizard/assistant-wizard.testing.ts:28`。

#### (2) 讀取：`computed()` 直接呼叫的寫法會整個失效

現在的讀取一律長這樣（`knowledge-detail-page.component.ts:80-86`）：

```ts
protected readonly view = computed(() => {
  this.revision();
  const accountId = this.session.activeAccountId();
  return accountId ? this.repository.getKnowledgeBaseDetail(accountId, this.knowledgeBaseId()) : null;
});
```

`computed()` **必須同步求值**，所以不能直接放 `Observable`。要改成 `toSignal(... switchMap ...)` 或 resource 形式，並讓 `initialValue` 是 `{ status: 'loading' }`。

**好消息**：`RepositoryView` 的 union 本身**不需要改**——`loading` 這個成員（`demo-repository.ts:96-98`）已經存在，所有模板都已經有 `status === 'loading'` 分支（見 `route-screen-matrix.md` 的狀態欄）。也就是說 `loading` 從「情境切換器產生」換成「請求生命週期產生」時，**模板不用動**。

#### (3) `revision` 遞增的重新讀取模式要重新設計

五個元件用「異動成功後遞增 `revision` signal，讓 `computed` 重跑」來刷新畫面：

```
features/knowledge/knowledge-detail/knowledge-detail-page.component.ts:72、:81、:121、:132、:143、:163
features/databases/database-detail/database-detail-page.component.ts:68、:76、:123
features/assistant-use/conversation/chat-conversation.component.ts:101、:117、:269
features/assistant-use/workspace-chat/workspace-chat-page.component.ts:45、:59、:95、:113、:126、:136
features/publishing/assistant-publishing/assistant-publishing.component.ts:40、:47、:66
```

同步時這是零成本的重算；非同步時**每一次遞增都是一次網路請求**。必須決定：

- 寫入成功的回應能不能直接當成新狀態（多數寫入方法已經回傳完整 view，可以免掉重讀）。
- 重讀期間要不要顯示 `loading`（會閃爍），還是保留舊資料（要多一個「更新中」的視覺狀態，目前沒有）。

#### (4) 寫入：同步分支要改成訂閱，而且要擋重複送出

現在的寫入是「呼叫 → 立刻拿到 result → `if (result.status === ...)` 分支」（例 `knowledge-detail-page.component.ts:117-122`）。非同步後每一處都要：

- 改成 `subscribe` / `await`，並用 `takeUntilDestroyed` 收尾。
- 加**進行中旗標**把按鈕設成 disabled——目前所有送出按鈕都沒有 in-flight 狀態，因為同步時不可能連點兩次。
- 處理「請求還沒回來，使用者已經離開這個路由」的情況。

#### (5) 樂觀 vs 悲觀更新

目前全部是**悲觀**（等回應才更新畫面），而且因為同步所以沒有延遲。轉非同步後若維持悲觀，聊天送出會有明顯空窗；若改樂觀則需要 rollback 路徑，目前一條都沒有。建議與畫面延遲期待一起決定，見 `ai-assistant-backend-integration-handoff.md` 第 5.2 節。

#### (6) e2e 的固定假設會鬆動

13 個 Cypress spec 目前都假設「畫面同步就緒」，沒有任何 `cy.intercept` 等待。轉非同步後需要補等待條件，否則會出現不穩定測試。spec 清單見 `route-screen-matrix.md` 第 4 節。

---

## 5. 覆蓋率自查

| 項目 | 數量 |
| --- | --- |
| `demo-repository.ts` 宣告的方法總數 | 57（54 個資料方法 + 3 個情境切換方法） |
| 本文件對照表已涵蓋 | 57 |
| 已被功能元件呼叫 | 44 |
| 契約已定義但功能元件未呼叫 | 10（第 2.6、2.7 節標示「未被呼叫」者，加上僅由 mock 內部呼叫的 `discardAssistantDraft`） |
| 正式 API 不得存在 | 4（`advanceKnowledgeDocument` + 3 個情境切換方法） |

驗證方式：

```bash
grep -nE "^  [a-zA-Z]+(\(|<)" apps/admin/src/app/core/repositories/demo-repository.ts
```

輸出的每一個方法名稱都必須能在本文件搜尋到。
