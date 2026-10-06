# Mock → API 對照表（`DemoRepository` 全方法）

**適用版本**：`apps/admin` 分支 `master`。介面宣告位置：`apps/admin/src/app/core/repositories/demo-repository.ts:258-599`。

## 0. 這份文件的定位

這份文件是 **`DemoRepository` 全部 61 個方法的單一索引**，回答四個問題：

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

`viewerAccountId` 目前是**每個方法的第一個參數**（例如 `demo-repository.ts:277-279`）。正式 API 一律改由 session 推導，request 裡不得再出現 accountId；下表的「授權」就是 session 建立後還要額外滿足的條件。

| 代碼 | 意義 | Demo 中的判斷位置 |
| --- | --- | --- |
| `S` | 只需要有效 session | `core/session/demo-session.guard.ts:9-12` |
| `S+MA` | 另需 `manage-assistants` 權限 | `account.model.ts:24` |
| `S+MD` | 另需 `manage-data-sources` 權限 | `account.model.ts:25` |
| `S+OWN` | 另需是該助理／知識庫／資料庫的**擁有者** | 見對應章節的權限規則 |
| `S+DM` | 另需是該資料庫的**指定資料管理者**（`dataManager`） | `database-access.ts:22-29`（`canReadConsentedRecords()`） |
| `S+MP` | 另需 `manage-publishing` 權限 | `account.model.ts:26` |
| `S+RC` | 另需 `read-consented-submissions` 權限 | `account.model.ts:27` |
| `S+AF` | 另需 `submit-authorized-forms` 權限 | `account.model.ts:29` |
| `S+USE` | 另需對該助理有**使用**權限（擁有／團隊分享／開放外部客戶） | `tasks-6-10-backend-handoff.md` 第 5.4 節 |
| `—` | Demo 專用，**正式 API 不得提供** | — |

> **`manage-publishing` 已經真的被檢查了**（`account.model.ts:26`）。判斷點是 `canManagePublishing()`（`publishing-channels.ts:116-124`），規則為**擁有者 ＋ 這個權限**，由 `publishingTarget()`（`mock-demo-repository.ts:3034-3041`）套用到三個管道的所有讀寫與 `/app/channels` 總覽，所以下表的發布類方法一律標 `S+OWN+MP`。收回權限**不影響**已經在用的人：那一題仍由 `canOpenInPlatform()` 決定。

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
| `listAssistantTemplates()` `:398` | `GET /api/v1/assistant-templates` | `S` | `200` `AssistantTemplateView[]` | `429` | `401`／`5xx` | `features/assistants/assistant-wizard/assistant-draft.store.ts:98` |
| `listConnectableSources(viewer)` `:400-402` | `GET /api/v1/connectable-sources` | `S+MA` | `200` `ConnectableSourceView[]` | `429` | `401`／`403 assistant-draft`／`5xx` | `assistant-draft.store.ts:106` |
| `listTrialQuestions()` `:403` | `GET /api/v1/trial-questions` | `S` | `200` `TrialQuestionView[]` | `429` | `401`／`5xx` | `assistant-draft.store.ts:115` |
| `previewTrialAnswer(viewer, request)` `:405-408` | `POST /api/v1/assistant-drafts/trial-answers` | `S+MA` | `200` `TrialAnswerView` | `422`（試問文字為空）／`429`／LLM 逾時 | `401`／`403 assistant-draft`／`5xx` | `assistant-draft.store.ts:245` |
| `getAssistantDraft(viewer)` `:410-412` | `GET /api/v1/assistant-drafts/me` | `S+MA` | `200` `SavedAssistantDraftView` 或 `null` | — | `401`／`403 assistant-draft`／`5xx` | `assistant-draft.store.ts:318`、`features/home/home-page.component.ts:37` |
| `saveAssistantDraft(viewer, draft)` `:413-416` | `PUT /api/v1/assistant-drafts/me` | `S+MA` | `200` `SavedAssistantDraftView` | `409`（多分頁編輯，目前無版本欄位）／`429` | `401`／`403 assistant-draft`／`5xx` | `assistant-draft.store.ts:305` |
| `discardAssistantDraft(viewer)` `:417` | `DELETE /api/v1/assistant-drafts/me` | `S+MA` | `204`（契約目前宣告 `void`） | — | `401`／`403`／`5xx` | **畫面未直接呼叫**；由 mock 在 `createAssistantFromDraft` 成功後內部呼叫（`mock-demo-repository.ts:1342`） |
| `createAssistantFromDraft(viewer, draft)` `:419-422` | `POST /api/v1/assistants` | `S+MA` | `201` `AssistantConfigurationView` | `422` `AssistantDraftFieldError[]`（逐欄）／`429` | `401`／`403 assistant-draft`／`5xx` | `assistant-draft.store.ts:278` |

`discardAssistantDraft` 是**唯一沒有回傳信封、也沒有權限檢查**的方法（`demo-repository.ts:417`）。正式版必須加授權；要不要改成回傳 `RepositoryView<void>`，見第 4.2 節。

### 2.2 知識庫（6 個方法）

深度：`tasks-6-10-backend-handoff.md` 第 3 節。

| 方法（契約行號） | 建議 endpoint | 授權 | 成功 | 可恢復錯誤 | 不可恢復錯誤 | 前端呼叫位置 |
| --- | --- | --- | --- | --- | --- | --- |
| `listKnowledgeBaseSummaries(viewer)` `:424-426` | `GET /api/v1/knowledge-bases` | `S` | `200` `KnowledgeBaseSummaryView[]` | `429` | `401`／`5xx` | `features/knowledge/knowledge-list/knowledge-list-page.component.ts:28` |
| `getKnowledgeBaseDetail(viewer, id)` `:431-434` | `GET /api/v1/knowledge-bases/{id}` | `S+OWN` | `200` `KnowledgeBaseDetailView` | `429` | `401`／`403 knowledge-base`／`5xx` | `features/knowledge/knowledge-detail/knowledge-detail-page.component.ts:84` |
| `addDemoKnowledgeDocument(viewer, kbId)` `:436-439` | 正式版改為檔案上傳：`POST /api/v1/knowledge-bases/{id}/documents`（見第 3.2 節） | `S+OWN` | `201` `KnowledgeDocumentView`（`queued`） | `413` 檔案過大／`415` 格式不支援／`422` 檔名重複／`429` | `401`／`403 knowledge-base`／`5xx` | `knowledge-detail-page.component.ts:117` |
| `advanceKnowledgeDocument(viewer, kbId, docId)` `:441-445` | **正式 API 不得存在** | `—` | — | — | — | `knowledge-detail-page.component.ts:159`（整段應移除，見第 3.3 節） |
| `retryKnowledgeDocument(viewer, kbId, docId)` `:447-451` | `POST /api/v1/knowledge-bases/{id}/documents/{docId}/retry` | `S+OWN` | `200` `KnowledgeDocumentView` | `409`（文件已在處理中）／`429` | `401`／`403 knowledge-base`／`5xx` | `knowledge-detail-page.component.ts:128` |
| `updateKnowledgeSharing(viewer, kbId, sharing)` `:452-456` | `PUT /api/v1/knowledge-bases/{id}/sharing` | `S+OWN` | `200` `KnowledgeSharingView` | `422`（只有 `message`，無逐欄 errors）／`409`／`429` | `401`／`403 knowledge-base`／`5xx` | `knowledge-detail-page.component.ts:139` |

### 2.3 資料庫、表單與追蹤（15 個方法）

深度：`tasks-6-10-backend-handoff.md` 第 4 節。

| 方法（契約行號） | 建議 endpoint | 授權 | 成功 | 可恢復錯誤 | 不可恢復錯誤 | 前端呼叫位置 |
| --- | --- | --- | --- | --- | --- | --- |
| `listDatabaseTemplates()` **已換成 API（#142）** | `GET /api/v1/database-templates` | `S+MD` | `200` `DatabaseTemplateView[]` | `429` | `401`／`403 database`（建立用訊息）／`5xx` | `features/databases/database-list/database-list-page.component.ts` |
| `listDatabaseSummaries()` **已換成 API（#142）** | `GET /api/v1/databases` | `S` | `200` `DatabaseSummaryView[]`（只列自己擁有的，舊到新） | `429` | `401`／`5xx` | `database-list-page.component.ts` |
| `createDatabaseFromTemplate(input)` **已換成 API（#142）** | `POST /api/v1/databases` `{ templateId, name, purpose? }` | `S+MD` | `201` `DatabaseSummaryView`＋`Location` | `422`（`message`＋`errors.templateId`／`name`／`purpose`）／`429` | `401`／`403 database`／`5xx`（畫面保留輸入可重試） | `database-list-page.component.ts` |
| `getDatabaseDetail(id)` **已換成 API（#142）** | `GET /api/v1/databases/{id}` | `S+OWN` | `200` `DatabaseDetailView`（`summary`＋目前的 `form`＋`access`） | `429` | `401`／`403 database`（不存在、別人的、別的組織的逐位元組相同）／`404`（id 不是 GUID，路由不符）／`5xx` | `features/databases/database-detail/database-detail-page.component.ts` |
| `updateDatabaseFields(id, fields, baseFormVersion)` **已換成 API（#143）** | `PUT /api/v1/databases/{id}/form` `{ baseVersionNumber, fields }` | `S+OWN` | `200` `DatabaseFormView`（新版本；內容與目前版本相同時回目前版本、不新增） | `422`（`message`＋`errors` 鍵 `fields`／`fields[i]`／`fields[i].label`／`.options`／`.scale`／`.unit`／`.type`／`.id`／`baseVersionNumber`）／`409 form-version-changed`／`429` | `401`／`403 database`／`5xx`（畫面保留草稿可重試） | `database-detail-page.component.ts` `saveFields` |
| `updateDatabaseAccess(id, dataManagerAccountIds)`（改為 `Observable`，不再傳 viewer；issue #144） | `PUT /api/v1/databases/{id}/access` `{ dataManagerAccountIds }`（完整清單） | `S+OWN` | `200` `DatabaseAccessView` | `422`（`message`＋`errors.dataManagerAccountIds`：不認得或別組織的帳號）／`409 database-access-conflict`（同時有人也在改） | `401`／`403 database`（不存在、別組織、非擁有者——含資料管理者——同一則）／`5xx` | `features/databases/database-access/database-access.component.ts` |
| `previewDatabaseEntry(id, answers)` **已換成 API（#143）** | `POST /api/v1/databases/{id}/form/preview` `{ answers }`（欄位 id → 字串或字串陣列） | `S+OWN` | `200` `DatabaseTrialPreviewView`（`saved: false`、`formVersion`、`entries`；**不建立紀錄**） | `422`（`errors` 鍵 `answers.<欄位 id>`）／`429` | `401`／`403 database`／`5xx` | `database-detail-page.component.ts` `runTrial` |
| `getDatabaseSubmissionForm(id)` **API（#145）** | `GET /api/v1/databases/{id}/submission-form` | `S+AF` | `200` `DatabaseSubmissionFormView`（目的、接收單位、目前實際可查看者、敏感資料提示、目前表單與版本） | `429` | `401`／`403 authorized-form`（不存在、別組織、無權限逐位元組相同）／`404`（id 不是 GUID）／`5xx` | `features/databases/database-submission/database-submission-page.component.ts` |
| `reviewDatabaseSubmission(id, formVersion, answers)` **API（#145）** | `POST /api/v1/databases/{id}/submission-form/review` `{ formVersionNumber, answers }` | `S+AF` | `200` `DatabaseTrialPreviewView`（**不建立紀錄**） | `422`（`answers.<欄位 id>`）／`409 form-version-changed`／`429` | `401`／`403 authorized-form`／`5xx`（保留答案可再試） | 同上 `review` |
| `submitDatabaseEntry(id, { submissionId, formVersion, consent, answers })` **API（#145）** | `POST /api/v1/databases/{id}/submissions` | `S+AF`＋`consent === true` | `201` `DatabaseSubmissionReceiptView`＋`Location`；同一 `submissionId` 同內容重送 `200` 同一張回執 | `422`（欄位、缺編號或版本；**未同意也是 `422`**，`reason: consent-required`，在欄位錯誤之後才檢查）／`409 form-version-changed`／`409 submission-key-reused`／`429` | `401`／`403 authorized-form`／`5xx`（保留答案、以**同一個** `submissionId` 重試） | 同上 `submit` |
| `getDatabaseSubmissionReceipt(submissionId)` **API（#145）** | `GET /api/v1/submissions/{id}` | `S`＋提交者本人 | `200` `DatabaseSubmissionReceiptView` | `429` | `401`／`403 authorized-form`（不存在或不是自己的，同一則）／`5xx` | 同上（`?receipt=<id>`） |
| `listOwnDatabaseSubmissions()` **API（#146）** | `GET /api/v1/submissions` | `S`（只依提交者本人，不看 `read-own-tracking`） | `200` `DatabaseOwnSubmissionListView`（自己的提交，新到舊，**含已撤回的軌跡**，不含內容；`withdrawnAt` 有效時為 `null`） | `429` | `401`／`5xx`（畫面可重試） | `features/activity/own-submissions/own-submissions.component.ts` |
| `withdrawDatabaseSubmission(submissionId)` **API（#146）** | `POST /api/v1/submissions/{id}/withdrawal` | `S`＋提交者本人 | `200` `DatabaseSubmissionReceiptView`（`entries: []`、`withdrawnAt`；**再撤回一次也是 `200` 同一份**） | `429` | `401`／`403 submission-withdrawal`（不存在、別人的——含資料管理者——別組織的同一則）／`404`（id 不是 GUID）／`5xx`（同一交易，失敗即無變更，可再按一次） | 同上 |
| `getDatabaseTracking(id)`（改為 `Observable`，不再傳 viewer；**API（#146）**） | `GET /api/v1/databases/{id}/tracking` | `S+DM+RC`（`DatabaseRecordReaders`，每次請求重查） | `200` `DatabaseTrackingView`：依追蹤對象（＝提交帳號）分組，`records` 有效紀錄（含內容）、`withdrawals` 撤回軌跡（不含內容）；每位追蹤對象的 `comparison`（首次／上次／本次）由伺服器的固定查詢算好（**#147**）；定期報表（#150）不在這裡，見下方 `listDatabaseReports` | `429` | `401`／`403 database-records`（看得到但不能讀）／`403 database`（看不到，同不存在）／`404`（id 不是 GUID）／`5xx`（畫面可重試，與「沒有紀錄」分開） | `database-detail-page.component.ts`（`trackingResource`） |
| `getDatabasePeriodSummary(id, {period, subjectId})` **API（#147，新增）** | `GET /api/v1/databases/{id}/queries/period-summary?period=…[&subjectId=…]` | `S+DM+RC`（`DatabaseRecordReaders`，每次請求重查） | `200` `DatabasePeriodSummaryResult`：這一期與前一期的有效紀錄筆數與每個數字欄位的加總（`display`／`changeLabel` 已加單位）；沒有紀錄是 0 不是錯誤 | `429` | `401`／`403 database-records`／`403 database`（同 tracking）／`404`（id 不是 GUID）／`422`（不在定義內的參數、期間或對象，`errors.<參數>`；先過權限，所以無權者看不到 422）／`5xx` | `features/databases/period-summary/period-summary.component.ts` |
| `listDatabaseReports(id)` **API（#150，新增）** | `GET /api/v1/databases/{id}/reports` | `S+DM+RC`（`DatabaseRecordReaders.CanReadAsync`，每次請求重查） | `200` `DatabaseReportListView`：`schedules`（這個資料庫上各助理的有效排程與下一份報表產生日）＋`reports`（新到舊，最多 60 份，不含統計） | `429` | `401`／`403 database-records`（看得到但不能讀）／`403 database`（看不到，同不存在；對任何報表 id 都一樣）／`404`（id 不是 GUID）／`5xx`（畫面可重試，與「沒有報表」分開） | `features/databases/database-reports/database-reports.component.ts` |
| `getDatabaseReport(id, reportId)` **API（#150，新增）** | `GET /api/v1/databases/{id}/reports/{reportId}` | `S+DM+RC` | `200` `DatabaseReportView`：`report`＋`statistics`（固定查詢 `period-summary` 的結果原樣保存的快照；沒有產生的期間為 `null`）＋`aiSummary`（另外保存、標示「AI 摘要」） | `429` | 同上；能讀但這個 id 不是這個資料庫的報表：`403 database-report` | `database-report.component.ts` |
| `retryDatabaseReportSummary(id, reportId)` **API（#150，新增）** | `POST /api/v1/databases/{id}/reports/{reportId}/summary`（沒有本文） | `S+DM+RC` | `200` `DatabaseReportView`；只有 `failed`／`discarded` 的摘要變成 `pending` 並排入一個摘要工作，其他狀態原樣回傳（連按兩次只一次模型呼叫） | `429` | 同上 | `database-report.component.ts` `retry` |

**API 模式狀態（#142，2026-10-03）**：上表前四個方法已改成非同步契約（`Observable<…>`，不再傳 viewer），API 模式由 `hybrid-demo-repository.ts` 的同名覆寫走 HTTP，**讀取與建立（寫入）兩端都換了**，不會落到 mock 的資料庫。與 mock 的刻意差異：

- 後端摘要沒有 `recordCount`／`subjectCount`／`connectedAssistantNames`：讀紀錄需要資料管理者指定與帳號權限（#144／#146），在那之前任何回應都不透露數量；adapter 一律填 `null`／`[]`。（#177 起 `recordCount`／`subjectCount` 只對可讀者送出、其他人省略，adapter 把省略轉成 `null`；見 M4 設計文件第 16 節。）摘要多了 `owner`（mock 也補上）、`templateId`、`formVersion`、`createdAt`、`viewerCanManage`。
- 詳情的欄位在 `form.fields`（附 `form.versionNumber`、`form.createdAt`），不是頂層 `fields`；adapter 攤平成前端的 `fields`。
- 其餘的方法（`getDatabaseTracking`）在 API 模式**尚未提供**（`updateDatabaseFields`、`previewDatabaseEntry` 自 #143、`updateDatabaseAccess` 自 #144 起走 API，見下）。詳情的 `upcomingFeatures`（`records`、`assistant-connections`；mock 是空陣列）讓畫面顯示「將於後續版本開放」，不呼叫同步的 mock 方法；#145–#148 開放一項就從 `API_UPCOMING_DATABASE_FEATURES` 移除一項（`form-editing` 已於 #143、`data-managers` 已於 #144、`assistant-connections` 已於 #148 移除：詳情多 `connectedAssistants`、摘要多 `connectedAssistantNames`，都只列**目前帳號自己的**助理，與 mock 相同）。**#146 與 #148 合併後 `API_UPCOMING_DATABASE_FEATURES` 只剩 `trends`（#147），`DatabaseUpcomingFeature` 也只剩 `'trends'`。**
- **指定資料管理者（#144）**：
  - 詳情多一個 `access`（`DatabaseAccessView`）：`dataManagers`（**已指定**，含 `hasReadPermission`、`assignedAt`、`assignedBy`）與 `effectiveReaders`（**目前實際可讀**：已指定 **且** 現在具備 `read-consented-submissions`，擁有者的預覽）、`viewerIsDataManager`／`viewerCanReadRecords`／`viewerCanManageAccess`、`candidates`（同組織全部帳號與其帳號層級權限，**只有擁有者拿得到**，其他人為空陣列）、`lastChange`（最後一次指定變更的時間與操作人，從未變更為 `null`）。前端 `DatabaseAccessView` 對應 `dataManagers`／`effectiveReaders`／`savedAt`／`savedBy`。
  - 可見性：清單與詳情 = 自己擁有的 ＋ 自己有權讀紀錄的（指定 **且** 帳號有權限，`DatabaseAccess.ListedFor`）；資料管理者看到的是唯讀（摘要 `viewerCanManage: false`，前端隱藏表單編輯與試填、權限頁籤不顯示核取方塊），`ManageableBy` 仍只有擁有者。未同時具備兩者的人，清單沒有、詳情與 `PUT` 都是與「不存在」逐位元組相同的 `403 database`。mock 的 `listDatabaseSummaries`／`getDatabaseDetail` 同步放寬。
  - 帳號權限每次請求從資料庫讀（`RequestAccountPermissions`，不在 token 內），指定也每次查表：撤銷任一邊，同一個 token 的下一個請求就失效。「誰可讀紀錄」只有一個判斷點：`DatabaseRecordAccess`（Application，表達式）與 `DatabaseRecordReaders`（Api，`CanReadAsync`／`ReadableDatabaseIdsAsync`／`EffectiveReaderIdsAsync`），**#146／#147 回傳任何紀錄、數量或趨勢前必須走它**。
  - 建立資料庫時，建立者預設是唯一的資料管理者（與 mock 相同；擁有者沒有帳號權限仍讀不到，也可以把自己取消勾選）。`PUT` 以完整清單取代，重複 id 去重，什麼都沒變就不寫入；移除只刪指定、不動任何紀錄。
  - 稽核：`DatabaseDataManagers` 每列有 `AssignedByAccountId`／`AssignedAt`；每次新增或移除另 append 一列 `DatabaseDataManagerChanges`（帳號、指定或移除、操作人、時間，無外鍵所以帳號刪除後仍在）。

**API 模式狀態（#145，2026-10-03）**：同意提交與回執走 API，**讀寫兩端都換了**（Hybrid 覆寫走 HTTP，不寫 mock 的收集紀錄）。入口是表單連結 `/app/forms/{id}`（`submit-authorized-forms`，來源 `form-link`）；助理對話中的表單（`reviewChatForm`／`submitChatForm`，§2.4）是 #148，屆時重用伺服器的 `DatabaseSubmissionService`。重點：

- 提交前顯示的接收單位是「組織名稱（數據庫名稱）」，可查看者是**目前實際可讀**的帳號（已指定且具備 `read-consented-submissions`）；回執保存送出當下的這些內容與欄位快照（名稱、型別、顯示值），表單之後改版不變。mock 的接收單位也改用「安心商行（數據庫名稱）」。
- 冪等：前端每一份填寫產生一個 `submissionId`（`crypto.randomUUID()`），失敗重試沿用；伺服器以「提交者＋提交編號」唯一索引保證只有一筆，同編號不同內容 `409 submission-key-reused`。mock 同。
- `§2.7` 的 `submitAuthorizedForm` 不再需要：表單連結的正式契約是 `submitDatabaseEntry`，同意不足統一為 `422`（解決 §2.7 的不一致）。
- `getDatabaseTracking`（收集紀錄時間軸與趨勢）當時仍未提供，`API_UPCOMING_DATABASE_FEATURES` 保留 `records`；伺服器已有給資料管理者的 `GET /api/v1/databases/{id}/records`（`403 database` 看不到數據庫、`403 database-records` 看得到但不能讀）。**#146 已接上時間軸與撤回（見上方 #146 段落）。**
- 資料模型與接點：[`docs/plans/2026-10-03-m4-142-database-templates.md`](../plans/2026-10-03-m4-142-database-templates.md) 第 8 節。

**API 模式狀態（#146，2026-10-03）**：查看紀錄與撤回走 API，**讀寫兩端都換了**（Hybrid 的 `listOwnDatabaseSubmissions`／`withdrawDatabaseSubmission`／`getDatabaseTracking` 走 HTTP，不讀寫 mock 的收集紀錄與回執）。重點：

- 撤回＝同一個交易內刪除該筆的 entries（值與欄位快照）並設 `WithdrawnAt`；軌跡（提交者、數據庫、表單版本、來源、回執編號、送出／撤回時間）與 `ConsentTerms`（當時告知的條款，非填寫內容）保留。冪等：再撤回回 `200` 同一份；並行撤回只有一個生效，全部回同一份。
- 回執多 `withdrawnAt`（有效時 `null`，一律送出）；已撤回的回執 `entries` 為空。用已撤回那份填寫的 `submissionId` 重送：同數據庫／版本／來源回 `200` 已撤回的回執、不寫入；否則 `409 submission-key-reused`。
- 收集紀錄頁籤在 API 模式開放；`API_UPCOMING_DATABASE_FEATURES` 移除 `records`、改列 `trends`（#147 已接上並移除 `trends`，見下方 #147 段落）。
- mock 的同步本體改名 `readDatabaseTracking(viewer, id)`，只給 mock 內部與單元測試（例如 Hybrid 模式下仍是 mock 的對話提交）；畫面一律用非同步的 `getDatabaseTracking`。
- 追蹤對象＝提交的帳號；有效紀錄的唯一定義 `DatabaseActiveRecords`（#147 共用）。完整語意與資料保留規則：[`docs/plans/2026-10-03-m4-142-database-templates.md`](../plans/2026-10-03-m4-142-database-templates.md) 第 9 節。

**API 模式狀態（#147，2026-10-03）**：趨勢比較與期間統計走 API（Hybrid 的 `getDatabaseTracking` 照轉伺服器算好的 `comparison`，新增的 `getDatabasePeriodSummary` 走固定查詢 `period-summary`），數字都由伺服器計算，前端不重算。重點：

- **固定查詢**（`DatabaseFixedQueries`／`DatabaseFixedQueryService`，Application／Api）：`record-count`、`field-sum`、`period-summary`、`subject-comparison`，各自只接受自己定義的參數（`period`／`from`＋`to`／`fieldId`／`subjectId`），不接受運算式或 SQL；端點只是入口，#149（對話工具）與 #150（排程）直接呼叫同一個應用服務。每次呼叫重新套用組織、指定與帳號權限、只算有效紀錄（撤回即排除）。
- **紀錄不足**：`comparison.status = 'insufficient-records'`（少於 2 筆，或有 2 筆以上但沒有任何數字／量尺欄位累積 2 個值）時沒有 `metrics`，畫面不畫趨勢。mock 的後一種情況也改成 `insufficient-records`（原本回 `available` 但沒有指標，畫面是空的趨勢）。
- **與 mock 的刻意差異**：同一個欄位 id 若型別或單位改過，只比較／加總「目前這組型別與單位」的值，不混；mock 的比較同步這樣算（`compareRecords`）。日期一律是統計時區（`Statistics:TimeZone`，預設 `Asia/Taipei`）的曆日，週從週一；前一期是完整的前一期；前端時間軸日期標籤同樣以統計時區的曆日顯示（`statisticsDay`；#177 起時區取自 `/me` 的 `statisticsTimeZone`）。
- `API_UPCOMING_DATABASE_FEATURES` 移除 `trends`、新增 `periodic-reports`（#150）；**#150 已移除 `periodic-reports`，清單現在是空的**（見下方 #150 段落）。
- 完整契約、參數規則與給 #149／#150 的接點：[`docs/plans/2026-10-03-m4-142-database-templates.md`](../plans/2026-10-03-m4-142-database-templates.md) 第 10 節。

**API 模式狀態（#150，2026-10-03）**：站內定期報表走 API，**設定、排程、查看與重試摘要都換了**（Hybrid 覆寫走 HTTP，mock 的快照只在 mock 模式出現）。第一版只在站內查看，不寄 Email 或 LINE。重點：

- **設定**：`PATCH /api/v1/assistants/{id}/settings` 的 `rules.periodicReport`（`off`／`weekly`／`monthly`，省略不變），設定回應的 `rules.periodicReport` 讀回。報告的是助理的**寫入對象**（`rules.dataWriteDatabaseId`，必須是已連接、且擁有者目前可使用的資料庫）：沒有寫入對象就設定週期是 `422`（`errors.periodicReport`）；改寫入對象，排程跟著移過去；清掉寫入對象，報表關閉。mock 的 `validateAssistantSettings` 同步（欄位 `periodicReport`）。畫面標籤改為「是否定期產生報表？」（選項不變：不需要／每週一次／每月一次）。
- **報表**：每一期一份快照（`DatabaseReportView`）：`statistics` 是固定查詢 `period-summary` 的結果原樣保存、之後不再重算（撤回只影響之後產生的報表）；`aiSummary` 另外保存並固定標示「AI 摘要」，狀態 `not-requested`／`pending`／`ready`／`failed`／`discarded`，只有 `ready` 才有 `text`。這一期或前一期沒有紀錄時 `dataState = insufficient-records`（紀錄不足）：統計照存，不顯示變化、圖表趨勢與 AI 摘要。期間到了但擁有者已不能讀取、或助理已不再連接：`status = skipped` 與 `skipReason`，不含統計。
- **查看**：與時間軸相同的權限（指定資料管理者 **且** 具備 `read-consented-submissions`），每次請求重查；擁有者沒有額外權利。看不到資料庫是 `403 database`、看得到但不能讀是 `403 database-records`（對真的與假的報表 id 逐位元組相同）；能讀但 id 不是這個資料庫的報表是 `403 database-report`（`RepositoryPermissionDeniedReason` 多 `database-report`）。
- **畫面**：資料庫詳情多「定期報表」頁籤（排程、報表清單、選中的一份：統計表、長條圖、AI 摘要區）；趨勢比較不再放報表。`DatabaseTrackingView.periodicReports`、`PeriodicReportView`、`buildPeriodicReport` 移除；`DatabaseUpcomingFeature` 成為 `never`、`API_UPCOMING_DATABASE_FEATURES` 為空。
- 資料模型、排程、摘要保護與保留規則：[`docs/plans/2026-10-03-m4-142-database-templates.md`](../plans/2026-10-03-m4-142-database-templates.md) 第 13 節。

**API 模式狀態（#143，2026-10-03）**：`updateDatabaseFields`、`previewDatabaseEntry` 已改成 `Observable` 契約（不再傳 viewer），Hybrid 覆寫走 HTTP，**讀寫兩端都換了**（寫入與試填都由伺服器驗證，mock 的欄位清單不參與）。契約變更與刻意差異：

- `updateDatabaseFields` 多一個 `baseFormVersion`（取自 `DatabaseDetailView.formVersion`，也就是 API 的 `form.versionNumber`）；結果多一種 `conflict`（`409 form-version-changed`，沒有寫入，畫面提示重新載入）；成功時回 `{ formVersion, fields }`，不再只回欄位。mock 也記版本號、也回 `conflict`，並且「內容與目前版本相同就不新增版本」，與 API 一致。
- 逐欄錯誤：API 的 `errors` 鍵 `fields[i].…` 的 `i` 是送出的陣列位置，adapter 轉回 `fields[i].id`；整份表單的錯誤（沒有欄位、超過 50 個、缺 `baseVersionNumber`）`fieldId` 是 `null`。`message` 是第一個錯誤，mock 同（舊的「還有欄位需要修正」摘要改為各畫面自己的標題）。
- 欄位 id 是跨版本的穩定鍵：保留的欄位（即使改名、換順序）沿用 id；送出沒有 id 的欄位由伺服器配 `field-<12 碼>`；**先前版本有、目前版本已移除的 id 不能再用**（`422 fields[i].id`），所以編輯器新增欄位改用隨機 id（不再是 `field-custom-<欄位數>`）。
- 試填與日後的正式提交（#145）共用 `DatabaseAnswerRules.Validate`（`SmartAgri.Application.Databases`）：訊息、日期必須是存在的日期、數字顯示格式（`1,200 元`）與 mock 的 `evaluateTrial` 一致；mock 也改成同樣的日期規則。試填永遠對**目前最新版本**驗證，不寫任何資料表。
- 兩邊的上限一致：欄位名稱 100 字、選項最多 30 個且各 100 字且不重複、單位 20 字、量尺說明 20 字、表單 50 個欄位。
- 資料模型、版本化與後續工單的介面見 [`docs/plans/2026-10-03-m4-142-database-templates.md`](../plans/2026-10-03-m4-142-database-templates.md)。

`DatabaseTrackingView` 的每個 `TrackedSubjectView` 除了 `records` 還有 `withdrawals`（`database.model.ts:287-295`）：已撤回同意的紀錄不出現在 `records`、也不計入 `comparison`，只以不含內容的軌跡列在 `withdrawals`。撤回讓某位追蹤對象剩下不到 2 筆時，`comparison` 會回到 `insufficient-records`。全部撤回的追蹤對象仍留在清單裡（筆數 0），不會無聲消失。

`DatabaseTrackingView` 原本有的 `periodicReports`（mock 依助理規則推算下次回報日期、再把比較摘要搬過來）已於 #150 移除：定期報表改成每一期一份保存下來的快照，走 `listDatabaseReports`／`getDatabaseReport`（見下方 #150 段落），mock 也產生同樣形狀的快照。

`getDatabaseTracking` 是全表唯一會回傳**兩種不同 reason** 的方法：`database-records` 代表「這個資料庫你看得到，但收集紀錄不給你看」，`database` 代表「不存在或不是你的」。兩者的畫面呈現不同，不能合併。

### 2.4 終端對話與同意流程（9 個方法）

深度：`tasks-6-10-backend-handoff.md` 第 5 節。

| 方法（契約行號） | 建議 endpoint | 授權 | 成功 | 可恢復錯誤 | 不可恢復錯誤 | 前端呼叫位置 |
| --- | --- | --- | --- | --- | --- | --- |
| `listChatThreads(viewer, assistantId)` `:515-518` | `GET /api/v1/assistants/{id}/chat/conversations` | `S+USE` | `200` `ChatThreadListView`（依最後活動由新到舊） | `429` | `401`／`403 assistant-use`／`5xx` | `features/assistant-use/workspace-chat/workspace-chat-page.component.ts:64` |
| `createChatThread(viewer, assistantId)` `:520-523` | `POST /api/v1/assistants/{id}/chat/conversations` | `S+USE` | `201` `AssistantChatView`（空白對話） | `429` | `401`／`403 assistant-use`／`5xx` | `workspace-chat-page.component.ts:112` |
| `renameChatThread(viewer, assistantId, threadId, title)` `:525-530` | `PATCH /api/v1/assistants/{id}/chat/conversations/{threadId}` | `S+USE` + thread 屬於 viewer | `200` `ChatThreadSummaryView` | `422`（標題為空或過長，只有 `message`）／`429` | `401`／`403 assistant-use`／`403 chat-thread`／`5xx` | `workspace-chat-page.component.ts:124` |
| `deleteChatThread(viewer, assistantId, threadId)` `:532-536` | `DELETE /api/v1/assistants/{id}/chat/conversations/{threadId}` | `S+USE` + thread 屬於 viewer | `200` `ChatThreadListView`（**剩下的清單**，不是 `204`） | `429` | `401`／`403 assistant-use`／`403 chat-thread`／`5xx` | `workspace-chat-page.component.ts:134` |
| `getAssistantChat(viewer, assistantId, threadId?)` `:548-552` | `GET /api/v1/assistants/{id}/chat`（`?conversation=` 選填） | `S+USE` | `200` `AssistantChatView` | `429` | `401`／`403 assistant-use`／`403 chat-thread`／`5xx` | `features/assistant-use/conversation/chat-conversation.component.ts:121` |
| `sendChatMessage(viewer, assistantId, text, threadId?)` `:557-562` | `POST /api/v1/assistants/{id}/chat/messages` | `S+USE` | `200` `AssistantChatView`（**整份對話**） | `422`（訊息為空，只有 `message`）／`429`／LLM 逾時 | `401`／`403 assistant-use`／`403 chat-thread`／`5xx` | `chat-conversation.component.ts:158` |
| `reviewChatForm(viewer, assistantId, formId, formVersion, answers)` **API（#148，`Observable`）** | `POST /api/v1/assistants/{id}/chat/forms/{databaseId}/review` `{ formVersionNumber, answers }` | `S+USE`＋助理仍連接且擁有者仍可使用該資料庫 | `200` `DatabaseTrialPreviewView`（**不建立紀錄**；前端轉成 `ChatFormReviewView`） | `422`（`answers.<欄位 id>`）／`409 form-version-changed`／`429` | `401`／`403 assistant-use`／`403 assistant-form`／`5xx` | `chat-conversation.component.ts` `reviewForm` |
| `submitChatForm(viewer, assistantId, { formId, formVersion, submissionId, answers, consent }, threadId?)` **API（#148，`Observable`）** | `POST /api/v1/assistants/{id}/chat/forms/{databaseId}/submissions` `{ submissionId, formVersionNumber, consent, answers, threadId? }` | 同上＋`consent === true` | `201` `ChatFormSubmissionView`（`receipt`＝#145 的回執、`message`＝對話中的收據訊息）；同一 `submissionId` 同內容重送 `200` 同一張 | `422`（欄位；**未同意也是 `422`** `consent-required`）／`409 form-version-changed`／`409 submission-key-reused`／`409 chat-run-in-progress`／`429` | `401`／`403 assistant-use`／`403 chat-thread`／`403 assistant-form`／`5xx`（以同一個 `submissionId` 重試） | `chat-conversation.component.ts` `confirmConsent` |
| `withdrawChatSubmission(viewer, assistantId, recordId, threadId?)` **API（#146＋#148，`Observable`）** | `POST /api/v1/submissions/{id}/withdrawal`（與 `withdrawDatabaseSubmission` 同一條；`recordId` 是 `record-<提交 id>`，Hybrid 去掉前綴） | `S`＋**提交者本人** | `200` `DatabaseSubmissionReceiptView` → 前端結果是收據撤回後的 `SubmissionWithdrawalView`（`withdrawn`）；**再撤回一次也是 `200`**（mock 是 `validation-failed`） | `429` | `401`／`403 submission-withdrawal`（不存在、別人的——含資料管理者——同一則訊息）／`404`（不是 GUID）／`5xx`（無變更，可再按一次） | `chat-conversation.component.ts` `confirmWithdraw` |

三個必須保留的行為：

- **`threadId` 省略時的語意**（契約 `demo-repository.ts:551`、`:561`、`:578`）：`getAssistantChat` 開啟最後活動的那一段、`sendChatMessage` 寫進同一段、沒有任何對話時開新的一段。`/use/:assistantId` 永遠不傳 `threadId`。
- **同意勾選在欄位驗證之後才檢查**，否則使用者會先看到「請勾選同意」而不是「電話格式錯誤」。
- **撤回只有提交者本人做得到**：`403 submission-withdrawal` 對「紀錄不存在」與「紀錄是別人的」回同一則訊息，不含任何填寫內容。資料管理者沒有代為撤回或代為刪除的 endpoint；若正式版要提供，請另開一個帶理由欄位與稽核的動作，不要共用這一條。撤回後紀錄不會消失，而是清空內容並留下軌跡（見 `tasks-6-10-backend-handoff.md` 第 5.8 節）。

**API 模式狀態（#148，2026-10-03）**：對話中的表單請求、確認、同意與回執走 API，**讀寫兩端都換了**（Hybrid 覆寫走 HTTP，表單 id 是 API 的資料庫 GUID，不經 mock 的種子清單；mock 的收集紀錄不參與）。

- **表單請求從哪裡來**：`POST .../chat/runs`（AG-UI）的 `smartagri.reply` 可能是 `kind: "form-request"`，`reply.form` 是伺服器的 `ChatFormRequestView`（`id`＝資料庫、`title`、`formVersion`、`fields`、`consent{recipient, purpose, viewers, sensitiveNotice, withdrawalNotice}`）；`purpose` 是助理設定的收集目的。觸發：問題含「填寫／填表／表單／回報／登記…」**且**助理有寫入對象、擁有者仍可使用它——由編排層決定、不呼叫模型。重新讀取（`GET chat`）時每次重新授權，已無法使用就是 `form: null`（前端顯示「這份表單目前無法使用」）。
- **收據訊息**：`kind: "submission-receipt"`，`reply.receipt` 是 #145 的 `DatabaseSubmissionReceiptView`，伺服器依保存的提交 id **即時讀取**；對話表裡只存提交 id 與資料庫 id，文字只有接收單位與回執編號，**不含任何填寫值**。前端轉成 `recipient`／`entries`，`recordId` 是 `record-<提交 id>`；`withdrawal` 依回執狀態決定（#146 接上）：`withdrawnAt` 為 null 是 `available`（說明含回執編號，提交者本人可在收據上撤回），有值是 `withdrawn`（顯示撤回日期，`entries` 為空）；讀不到回執（`receipt: null`）才是 `unavailable`。撤回後保存的對話重新讀取即顯示已撤回（對話表不變）；不保存對話時，畫面以撤回結果就地更新本頁的收據。
- **不保存對話時**：紀錄照樣寫進資料庫；收據訊息是暫時的（`message` 只回在這次回應），畫面直接接在對話後面，不重新讀取。
- `submitChatForm` 的結果改成 `{ message, threadId }`（不再是整段對話）；`reviewChatForm` 多 `formVersion`、結果多 `conflict`；`ChatFormView` 多 `formVersion`；`form-request` 的 `form` 可為 `null`；`RepositoryPermissionDeniedReason` 多 `assistant-form`。mock 同步：同一個 `submissionId` 重送回同一張收據、版本不符回 `conflict`、表單已不可用回 `assistant-form`。
- 設定：`AssistantSettingsView` 多 `databaseIds`；`rules` 多 `dataWriteDatabaseId`（`null`＝不寫入）與 `dataWritePurpose`；`PATCH .../settings` 的 `rules.dataWriteDatabaseId` 送 `""` 清除、省略不變；`setAssistantSourceConnection` 的資料庫走 `PUT`／`DELETE .../sources/database/{id}`；`GET /api/v1/connectable-sources` 也列出資料庫（自己擁有，或被指定為資料管理者且具備 `read-consented-submissions`）。建立精靈在 API 模式仍只列知識庫（由草稿建立時後端仍拒絕資料庫來源）。定期回報（`rules.periodicReport`）自 #150 起走 API（見下方 #150 段落）。
- 詳細設計：[`docs/plans/2026-10-03-m4-142-database-templates.md`](../plans/2026-10-03-m4-142-database-templates.md) 第 9 節。

**API 模式狀態（#149，2026-10-03）**：對話中查詢已連接數據庫的紀錄走 API（`POST .../chat/runs` 的 `smartagri.reply` 可能是 `kind: "database-query"`），Hybrid 只轉型別，數字不重算。

- **何時是查詢**：問題含統計詞（幾筆、幾次、筆數、次數、總共、加總、合計、統計、趨勢…）且助理有連接、擁有者仍可使用的數據庫。編排層把固定查詢（#147 的四個）當成工具交給模型，模型只選工具與參數（數據庫與欄位都是伺服器列出的 enum）；伺服器驗證參數、以**提問者本人**的權限執行（被指定為資料管理者＋`read-consented-submissions`），回答的文字與數字由伺服器依結果組成，模型看不到結果。優先序：查詢 → 表單請求（#148）→ 一般回答。
- **`reply.databaseQuery`**（`ChatDatabaseQueryView`，其他種類為 `null`）：`status`（`answered`／`no-data`／`insufficient-data`／`not-available`／`rejected`／`failed`）、`databaseId`／`databaseName`（資料來源）、`query`／`queryLabel`、`period`／`previousPeriod`（`DatabaseQueryPeriodView`，前端轉成 `DatabasePeriodRangeView`）、`subjectOnly`、`figures[]`（`metric`、`value`、`display`、`previousDisplay`、`changeLabel`）、`message`。前端型別是 `{ kind: 'database-query', text, query: ChatDatabaseQueryView }`。
- **拒絕不洩漏**：沒有可查的數據庫、模型指定了不在清單的數據庫（不存在、他組織、未連接）、執行時已撤銷指定或權限，一律 `not-available`、同一句 `目前無法查詢：…`、`databaseId`／`databaseName` 為 `null`。定義外參數或未知工具是 `rejected`（不執行）；查詢失敗是 `failed`；選工具的模型呼叫失敗是串流的 `RUN_ERROR chat-unavailable`。
- **保存與重新讀取**：保存對話時存回答文字與結構化快照；`GET chat` 每次重新檢查該數據庫仍可用且提問者仍可讀，否則讀回 `not-available`。查詢回答**不能轉人工**（`POST .../chat/handoffs` 回 `403 chat-thread`，畫面不顯示按鈕），不保存對話時也不當成前文送回。
- **mock**：固定回覆 `chat-order-count`（「近 30 天有幾筆訂單問題回報？」，排在 `chat-order-issue` 前）以 `summarizePeriod` 計算 `database-orders` 的近 30 天筆數，文字與後端相同；只有能讀這個資料庫紀錄的帳號（mock 是 `account-smb-admin`）得到數字並看到這個建議問題，其他人得到同一個 `not-available`。
- 詳細設計：[`docs/plans/2026-10-03-m4-142-database-templates.md`](../plans/2026-10-03-m4-142-database-templates.md) 第 12 節。

### 2.5 發布管道（10 個方法）

深度：`tasks-6-10-backend-handoff.md` 第 6 節。**每個助理固定三個管道，不能新增或刪除**（契約註解 `demo-repository.ts:342`、`:346`）。

**API 模式狀態（M5a #194–#202，2026-10-06）**：平台內與**官網**管道已走 API（Hybrid 覆寫），**LINE 仍是 mock／「將於後續版本開放」，留到 M5b**。下表「建議 endpoint」欄對已實作的方法寫的是**實際的 endpoint**（見 `apps/api/openapi/v1.json`），而且契約已經與原本的 mock 版不同：多了 `publishWebsite`、`unpublishWebsite`、`getOrganizationUsage` 三個方法，`checkWebsiteInstallation` 已**移除**（見下）。細節見表後的「官網管道（M5a）」。

| 方法（契約行號） | 建議 endpoint | 授權 | 成功 | 可恢復錯誤 | 不可恢復錯誤 | 前端呼叫位置 |
| --- | --- | --- | --- | --- | --- | --- |
| `listPublishingChannels(viewer)` `:343-345` | **已實作（無專屬 endpoint）**：後端沒有總覽端點，Hybrid 讀 `GET /api/v1/assistants` 後逐一讀 `GET /api/v1/assistants/{id}/publishing`，展平三個管道；被拒（`403`）的助理略過 | `S` | `200` `PublishingChannelView[]` | `429` | `401`／`5xx` | `features/assistants/assistant-list/assistant-list-page.component.ts:31` |
| `listChannelOverview(viewer)` `:347-349` | **已實作（無專屬 endpoint）**：同上，每個助理一筆 `AssistantChannelsView` | `S` | `200` `AssistantChannelsView[]` | `429` | `401`／`5xx` | `features/publishing/channel-overview/channel-overview-page.component.ts:26` |
| `getAssistantPublishing(viewer, assistantId)` `:354-357` | **已實作** `GET /api/v1/assistants/{id}/publishing`（平台內、官網完整，LINE 是「尚未開放」的佔位） | `S+OWN+MP` | `200` `AssistantPublishingView` | `429` | `401`／`403 publishing`／`5xx` | `features/publishing/assistant-publishing/assistant-publishing.component.ts:49` |
| `updatePlatformSharing(viewer, assistantId, accountIds)` `:359-363` | **已實作** `PUT /api/v1/assistants/{id}/publishing/platform` | `S+OWN+MP` | `200` `PlatformSharingView` | `422` `PublishingFieldError[]`／`409`／`429` | `401`／`403 publishing`／`5xx` | `features/publishing/platform-sharing/platform-sharing.component.ts:41` |
| `updateWebsiteEmbed(viewer, assistantId, settings, revision)` `:365-369` | **已實作** `PUT /api/v1/assistants/{id}/publishing/website`（整份取代；`revision` 不是最新時 `409`→`conflict`） | `S+OWN+MP` | `200` `WebsiteEmbedView`（被移除的網域連同它的 `lastSeenAt` 一起消失） | `422` `PublishingFieldError[]`（網域格式）／`409`／`429` | `401`／`403 publishing`／`5xx` | `features/publishing/website-embed/website-embed.component.ts:135` |
| ~~`checkWebsiteInstallation(viewer, assistantId)`~~ **已移除（M5a）** | ~~`POST …/publishing/website:check-installation`~~ 不做：改成被動的 `lastSeenAt`（見下） | — | — | — | — | 原 `website-embed.component.ts:155`；畫面改為顯示「安裝偵測」清單 |
| `saveLineSettings(viewer, assistantId, input)` `:376-380` **（未實作，M5b）** | `PUT /api/v1/assistants/{id}/publishing/line` | `S+OWN+MP` | `200` `LineSetupView`（**會重置測試與啟用狀態**） | 逐欄錯誤寫在 `LineSetupView` 內而非 `422`（見下方註） | `401`／`403 publishing`／`5xx` | `features/publishing/line-setup/line-setup.component.ts:88` |
| `sendLineTestMessage(viewer, assistantId)` `:382-385` **（未實作，M5b）** | `POST /api/v1/assistants/{id}/publishing/line:test` | `S+OWN+MP` | `200` `LineSetupView`（結果寫在 `lastTest`） | `429`；**LINE 端失敗要回成功 + `lastTest` 失敗紀錄**，不是 `5xx` | `401`／`403 publishing`／`5xx` | `line-setup.component.ts:102` |
| `activateLineChannel(viewer, assistantId)` `:387-390` **（未實作，M5b）** | `POST /api/v1/assistants/{id}/publishing/line:activate` | `S+OWN+MP` | `200` `LineSetupView` | `422` `PublishingFieldError[]`（**未通過測試就啟用**） | `401`／`403 publishing`／`5xx` | `line-setup.component.ts:109` |
| `setPublishingChannelPaused(viewer, assistantId, type, paused)` `:392-397` | **已實作（platform、website）** `PUT /api/v1/assistants/{id}/publishing/platform/paused` 與 `PUT /api/v1/assistants/{id}/publishing/website/paused`（`type=line` 仍未實作） | `S+OWN+MP` | `200` `PublishingChannelView`（**只影響這一個管道**） | `409`／`429` | `401`／`403 publishing`／`5xx` | `assistant-publishing.component.ts:74` |

**官網管道（M5a）**——契約新增與實際的 endpoint（`AssistantWebsiteChannelEndpoints`，皆 `S+OWN+MP`，非擁有者與不存在同一個 `403 publishing`）：

| 方法 | endpoint | 回應與錯誤 |
| --- | --- | --- |
| `getAssistantPublishing`（官網部分） | `GET /api/v1/assistants/{id}/publishing/website` | `200` 官網頻道：設定、狀態 `draft`／`published`／`paused`、**實際服務狀態** `servingState`（`not-published`／`paused`／`suspended-acceptance`／`suspended-knowledge`／`suspended-quota`／`serving`，每次讀取即時推導、不儲存）、`embedCode`、各網域的 `lastSeenAt`、驗收狀態與擋住發布的知識庫 |
| `updateWebsiteEmbed` | `PUT …/publishing/website` | `200`；`422` 欄位錯誤（網域格式、最多 5 個）；`409 website-revision-conflict`（`revision` 不是最新） |
| `publishWebsite`（**新增**） | `POST …/publishing/website:publish` | `200`；`422 website-publish-refused`，`errors` 逐項列出 `acceptance`（驗收現在必須是 `passed`）、`allowed-domains`、`assistant-paused`、`knowledge-ownership`、`public-base-url`，什麼都沒寫入（前端轉成 `PublishWebsiteResult.failures`） |
| `setPublishingChannelPaused(…, 'website', …)` | `PUT …/publishing/website/paused` | `200`；尚未發布（或從未儲存）時 `422 website-not-published` |
| `unpublishWebsite`（**新增**） | `POST …/publishing/website:unpublish` | `200`，回到 `draft` |
| `getOrganizationUsage`（**新增**） | `GET /api/v1/organization/usage` | `200 { month, usedTokens, limitTokens, state }`（`normal`／`near`／`exceeded`）；`403` 沒有 `manage-publishing`＝畫面不顯示 |

- **`checkWebsiteInstallation` 移除、改被動偵測**：原本的「檢查安裝」暗示伺服器去抓客戶網頁；地端可能連不到外網、也有 SSRF 風險。現在訪客視窗建立工作階段時帶上嵌入頁面的 `location.origin`，伺服器若它屬於允許網域（`https`、預設連接埠）就更新該網域的 `lastSeenAt`，admin 顯示「最後一次在 shop.example.com 偵測到」。這個值只供參考，不是安全判斷；外部網站無回應不再是一種後端狀態。
- **狀態對應**（契約型別 `PublishingChannelStatus` 不變）：`draft`→`testing`、`serving`→`published`、`suspended-*`→`needs-attention`（附原因與處理連結）、`paused`→`paused`。mock 已同步改成同樣的原因欄位，兩種模式共用型別。
- **嵌入碼**：`<script src="{PUBLIC_BASE_URL}/embed.js" data-assistant="{id}" async></script>`；`PublicChannels:PublicBaseUrl` 沒設定時 `embedCode` 為 `null`、發布回 `422`。部署與營運面見 `deploy/README.md`。
- 官網訪客端（`/api/v1/public/*`、`/use/{id}`）不是 `DemoRepository` 的方法，見 `route-screen-matrix.md` 第 5.2 節。

`saveLineSettings` 的回傳型別是 `RepositoryView<LineSetupView>`（`demo-repository.ts:380`），**不是** `...Result` union——逐欄錯誤是包在 `LineSetupView` 裡回傳的，不走 `validation-failed`。只有 `activateLineChannel` 才有獨立的 `ActivateLineChannelResult`（`:242-244`）。後端若改成 `422`，`line-setup.component.ts:88` 的分支要一起改。

### 2.6 助理、團隊與基礎資料（14 個方法）

前九個已被畫面使用，後五個目前**沒有任何功能元件呼叫**（語意見 `tasks-6-10-backend-handoff.md` 第 7 節）。

| 方法（契約行號） | 建議 endpoint | 授權 | 成功 | 可恢復錯誤 | 不可恢復錯誤 | 前端呼叫位置 |
| --- | --- | --- | --- | --- | --- | --- |
| `listUsableAssistants(viewer)` `:280-282` | `GET /api/v1/assistants?usable=true` | `S` | `200` `AssistantSummaryView[]` | `429` | `401`／`5xx` | `features/home/home-page.component.ts:21`、`:29`、`workspace-chat-page.component.ts:86` |
| `listAssistantConfigurations(viewer)` `:277-279` | `GET /api/v1/assistants` | `S+MA` | `200` `AssistantConfigurationView[]` | `429` | `401`／`5xx` | `assistant-list-page.component.ts:30`、`features/assistants/assistant-detail/assistant-detail-page.component.ts:106` |
| `getAssistantAnalytics(viewer, assistantId)` `:338-341` | `GET /api/v1/assistants/{id}/analytics` | `S+OWN` | `200` `AssistantAnalyticsView`（匿名統計） | `429` | `401`／`403 assistant-configuration`／`5xx` | `assistant-detail-page.component.ts:117` |
| `getAssistantSettings(viewer, assistantId)` `:292-295` | `GET /api/v1/assistants/{id}/settings` | `S+MA+OWN` | `200` `AssistantSettingsView`（設定＋已連接來源＋回答規則） | `429` | `401`／`403 assistant-configuration`／`5xx` | `features/assistants/assistant-detail/assistant-settings.store.ts:51` |
| `updateAssistantSettings(viewer, assistantId, patch)` `:300-304` | `PATCH /api/v1/assistants/{id}/settings` | `S+MA+OWN` | `200` `AssistantSettingsView` | `422` 逐欄 `AssistantSettingsFieldError[]`（驗證失敗時**完全不寫入**） | `401`／`403 assistant-configuration`／`5xx` | `assistant-settings.store.ts:133`（概覽）、`:139`（回答與記錄） |
| `setAssistantSourceConnection(viewer, assistantId, source, connected)` `:309-314` | `PUT`／`DELETE /api/v1/assistants/{id}/sources/{type}/{sourceId}` | `S+MA+OWN`＋來源必須是 viewer 看得到的 | `200` `AssistantSettingsView` | `422`（來源不可見、或會解除最後一個來源） | `401`／`403 assistant-configuration`／`5xx` | `assistant-settings.store.ts:152` |
| `getAssistantSources(viewer, assistantId)` `:283-286` | `GET /api/v1/assistants/{id}/sources` | `S+OWN` | `200` `AssistantSourceReference[]` | `429` | `401`／`403 assistant-configuration`／`5xx` | **未被呼叫**（已連接來源改由 `getAssistantSettings` 一併回傳） |
| `listAccounts()` `:259` | `GET /api/v1/share-targets`（**不要做成帳號目錄**） | `S`；回傳範圍需另外設計授權 | `200` `AccountView[]` | `429` | `401`／`5xx` | **未被呼叫**；`/login` 的三個身分是寫死的（`features/demo-login/demo-login-page.component.ts:18-22`） |
| `getTeam(viewer)` `:265` | `GET /api/v1/team` | `S+MA` | `200` `TeamView` | `429` | `401`／`403 team`／`5xx` | `features/settings/components/team-panel/team-panel.component.ts:36` |
| `updateMemberPermissions(viewer, memberId, permissions)` `:272-276` | `PUT /api/v1/team/members/{id}/permissions` | `S+MA` | `200` `TeamView` | `422`／`409`／`429` | `401`／`403 team`／`5xx` | `features/settings/components/team-panel/team-panel.component.ts:86` |
| `listKnowledgeBases(viewer)` `:315-317` | 由 `listKnowledgeBaseSummaries` 取代，**不需要獨立 endpoint** | `S` | `200` `KnowledgeBaseView[]` | — | `401`／`5xx` | **未被呼叫** |
| `listDatabases(viewer)` `:318-320` | 由 `listDatabaseSummaries` 取代，**不需要獨立 endpoint** | `S` | `200` `DatabaseView[]` | — | `401`／`5xx` | **未被呼叫** |
| `listPrivateConversations(viewer)` `:321-323` | `GET /api/v1/conversations` | `S` | `200` `PrivateConversationView[]` | `429` | `401`／`5xx` | **未被呼叫**（`/app/activity` 仍是 placeholder） |
| `getConversation(viewer, conversationId)` `:324-327` | `GET /api/v1/conversations/{id}` | `S` + 對話屬於 viewer | `200` `PrivateConversationView` | `429` | `401`／`403 private-conversation`／`5xx` | **未被呼叫** |

**已換成 API（#11）**：這兩個方法已改成非同步契約（`getTeam(): Observable<…>`、`updateMemberPermissions(memberId, permissions): Observable<…>`，不再傳 viewer）。API 模式由 `core/repositories/hybrid-demo-repository.ts` 走 HTTP，其餘方法沿用 mock；後續各區照同一個模式替換。

`getTeam`／`updateMemberPermissions` 是新增的一組方法，403 用新的 `team` reason（`RepositoryPermissionDeniedReason`，`demo-repository.ts:103`）：不具備 `manage-assistants` 的帳號一律得到同一則不含成員名稱或權限內容的訊息。

### 2.7 結構化提交（3 個方法，皆未被畫面呼叫）

| 方法（契約行號） | 建議 endpoint | 授權 | 成功 | 可恢復錯誤 | 不可恢復錯誤 | 前端呼叫位置 |
| --- | --- | --- | --- | --- | --- | --- |
| `listManagedSubmissions(viewer)` `:328-330` | `GET /api/v1/submissions?role=manager` | `S+DM+RC` | `200` `StructuredSubmissionView[]`（**只含 `consentStatus === 'consented'`**） | `429` | `401`／`5xx` | **未被呼叫**（收集紀錄改走 `getDatabaseTracking`） |
| `listOwnSubmissions(viewer)` `:331-333` | `GET /api/v1/submissions?role=self` | `S` | `200` `StructuredSubmissionView[]` | `429` | `401`／`5xx` | **未被呼叫** |
| `submitAuthorizedForm(viewer, input)` `:334-337` | `POST /api/v1/submissions` | `S+AF`＋`consent === true` | `201` `StructuredSubmissionView` | `422`（應改成這樣，見下方註） | `401`／`403 authorized-form`／`5xx` | **未被呼叫**（同意流程改走 `submitChatForm`） |

**（#145 已解決：表單連結改用 `submitDatabaseEntry`，未同意一律 `422`，見 §2.3。）** 原先的不一致：`submitAuthorizedForm` 把「沒有勾選同意」當成 `permission-denied`，而 `submitChatForm` 把同一件事當成 `validation-failed`。正式版要挑一種，建議統一為 `422`。詳見 `tasks-6-10-backend-handoff.md` 第 7 節第 2 點。

### 2.8 Demo 情境切換器（3 個方法，正式 API 不得提供）

`DemoScenarioController`，契約 `demo-repository.ts:252-256`。

| 方法 | 建議 endpoint | 說明 |
| --- | --- | --- |
| `setScenario(scenario)` `:253` | **無** | 由 `?demoScenario=` 網址參數觸發（`core/repositories/demo-scenario-param.ts:9-17`），在 DI factory 中套用（`core/repositories/tokens.ts:19-22`） |
| `getScenario()` `:254` | **無** | — |
| `resetScenario()` `:255` | **無** | — |

HTTP adapter 可把三者實作成 no-op，或在正式建置中把整個 `DemoScenarioController` 從 `DemoRepository` 的 extends 清單拿掉（`demo-repository.ts:258`）。後者會讓 `?demoScenario=` 完全失效，這是預期行為。

---

## 3. 每個方法的「Demo 特有行為」速查

只列**換成真後端時語意會變、或畫面會壞掉**的項目。完整的「哪些是假的」在 `tasks-6-10-backend-handoff.md` 各節的 `.6` 小節。

### 3.1 回傳整份聚合而不是增量

`sendChatMessage`、`submitChatForm` 回傳**整份 `AssistantChatView`**，`deleteChatThread` 回傳**剩下的整份清單**。前端沒有任何本地合併邏輯，直接整份取代。後端若改成回增量，`chat-conversation.component.ts:158`、`:226` 與 `workspace-chat-page.component.ts:134` 都要改寫。

### 3.2 上傳完全不存在

`addDemoKnowledgeDocument(viewer, kbId)` **沒有檔案參數**（`demo-repository.ts:436-439`），只建一筆假紀錄。換成真上傳時契約一定要改，至少要：

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

1. **新增** `apps/admin/src/app/core/repositories/http-demo-repository.ts`，實作 `DemoRepository`（`demo-repository.ts:258-599`）。
2. 把 `tokens.ts:11-24` 的 factory 改成回傳新 adapter，並拿掉 `readDemoScenario` 那三行（`:19-22`）。
3. **不要改** `demo-repository.ts` 的介面，除非確實要改契約（要改的清單見第 4.2 節）。
4. **保留** `mock-demo-repository.ts` 與 `demo-seed*.ts`：六個 `mock-demo-repository*.spec.ts` 是契約的可執行規格，新 adapter 應該能通過同一組行為測試。

`core/repositories/local-storage-repository.ts` 與 `core/repositories/repository.ts` 是**沒有任何人使用的舊程式**（找不到資料時會 `throw`，語意與本契約不同）。不要拿它當參考。

### 4.2 替換時建議一併修改的契約

| 契約位置 | 現狀 | 建議 |
| --- | --- | --- |
| 所有方法的 `viewerAccountId` 第一個參數 | 例 `demo-repository.ts:277-279` | 全部移除，viewer 由 session 推導 |
| `AccountId` 等 id 的字面值 union | `core/domain/account.model.ts:1-4` | 放寬成 `string`，否則後端無法回傳任何新 id |
| `discardAssistantDraft` 回傳 `void` | `demo-repository.ts:417` | 改成 `RepositoryView<void>`，才能表達 `403` |
| `DemoScenarioController` 被 `DemoRepository` extends | `demo-repository.ts:258` | 正式建置移除 |
| `DemoKeyValueStorage` | `demo-repository.ts:247-250` | HTTP adapter 不需要；草稿若要離線編輯可沿用同樣 key 格式 |
| LINE 憑證原文回傳 | `LineSetupView` | 改成只回末四碼與「是否已設定」，見 `tasks-6-10-backend-handoff.md` 第 8 節第 10 點。**基礎建設已在 M5a 備好**：後端有「只寫不讀」的機敏設定型別（`ProtectedSecret`／`ISecretProtector`，Data Protection 金鑰環加密）與檢視型別 `SecretStatusView { configured, lastFour, updatedAt }`，沒有任何端點能讀回明文；第一個實際使用的欄位是 M5b 的 LINE Channel Secret／Access Token，到時 `LineSetupView` 改用這個形狀。M5a 本身沒有欄位使用它 |

### 4.3 同步 → 非同步：具體會壞掉什麼

**這是替換工作量的主體。** 契約目前**全部同步**：`listAccounts(): RepositoryView<readonly AccountView[]>`（`demo-repository.ts:259`）。改成 `Observable<...>` 或 `Promise<...>` 後：

#### (1) 16 個注入點、51 個呼叫點都要改

呼叫點以 `rg -n --glob '!*.spec.ts' -o 'repository\.[a-zA-Z]+\(' apps/admin/src/app/features` 可重現（扣掉兩個 `*.testing.ts` 中的輔助呼叫後為 51 處）。

注入 `DEMO_REPOSITORY` 的功能檔案共 16 個：

```
features/home/home-page.component.ts:16
features/assistants/assistant-list/assistant-list-page.component.ts:24
features/assistants/assistant-detail/assistant-detail-page.component.ts:39
features/assistants/assistant-detail/assistant-settings.store.ts:35
features/assistants/assistant-wizard/assistant-draft.store.ts:71
features/knowledge/knowledge-list/knowledge-list-page.component.ts:24
features/knowledge/knowledge-detail/knowledge-detail-page.component.ts:67
features/databases/database-list/database-list-page.component.ts:19
features/databases/database-detail/database-detail-page.component.ts:68
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

**好消息**：`RepositoryView` 的 union 本身**不需要改**——`loading` 這個成員（`demo-repository.ts:110-112`）已經存在，所有模板都已經有 `status === 'loading'` 分支（見 `route-screen-matrix.md` 的狀態欄）。也就是說 `loading` 從「情境切換器產生」換成「請求生命週期產生」時，**模板不用動**。

#### (3) `revision` 遞增的重新讀取模式要重新設計

五個元件用「異動成功後遞增 `revision` signal，讓 `computed` 重跑」來刷新畫面：

```
features/knowledge/knowledge-detail/knowledge-detail-page.component.ts:72、:81、:121、:132、:143、:163
features/databases/database-detail/database-detail-page.component.ts:72、:80、:141
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

14 個 Cypress spec 目前都假設「畫面同步就緒」，沒有任何 `cy.intercept` 等待。轉非同步後需要補等待條件，否則會出現不穩定測試。spec 清單見 `route-screen-matrix.md` 第 4 節。

---

## 5. 覆蓋率自查

| 項目 | 數量 |
| --- | --- |
| `demo-repository.ts` 宣告的方法總數 | 60（57 個資料方法 + 3 個情境切換方法） |
| 本文件對照表已涵蓋 | 60 |
| 已被功能元件呼叫 | 47 |
| 契約已定義但功能元件未呼叫 | 10（第 2.6、2.7 節標示「未被呼叫」者，加上僅由 mock 內部呼叫的 `discardAssistantDraft`） |
| 正式 API 不得存在 | 4（`advanceKnowledgeDocument` + 3 個情境切換方法） |

驗證方式：

```bash
grep -nE "^  [a-zA-Z]+(\(|<)" apps/admin/src/app/core/repositories/demo-repository.ts
```

輸出的每一個方法名稱都必須能在本文件搜尋到。
