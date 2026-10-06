# 後端 Milestone 7｜案件：實作計畫

**日期：** 2026-10-06
**狀態：** 已確認（2026-10-06，負責人回覆「照建議」），可以拆票。第 7 節的 C、D、F 由負責人定案；A 定義在 M6 計畫。G–U 是本計畫的建議，負責人 2026-10-06 回覆「照建議」，一併確認；其中 H 改為不接受早於現在的時限。issue 標題前綴用「M7｜」。
**依據：** [案件 ADR](../adr/2026-10-06-cases-and-handoff.md)、[M6 計畫](2026-10-06-backend-milestone-6-org-settings.md)（決定 A：管理者的判斷；M6-2：管理者判斷與 `OrganizationActivity`）、[用語表](../glossary.md)（案件、承辦組、案件類型、案件負責人、處理事項、轉給專人、管理者）、[業務流程審查](../reviews/2026-09-26-project-review-and-backlog.md)待補功能 6、`docs/handoff/mock-to-api-mapping.md`、`docs/handoff/route-screen-matrix.md`、[#164 表單請求觸發評測](../evals/2026-10-05-164-form-request-trigger.md)。
**拆票方式：** 與 M5a、M5b 相同。每個 Slice 都是可單獨合併的垂直切片，各自附測試與驗收條件；「依賴」只列硬依賴；會改 migration 或 `openapi/v1.json` 的 Slice 用接續分支依序做。M7 大部分 Slice 同時包含後端與前端，一張票做完一個使用者看得到的功能。

---

## 1. 目標與非目標

**目標（M7 交付）：** 助理不只回答 SOP，也能把「需要做事」的問題變成有人負責、有狀態、有時限的案件：

> 同仁在對話中說「冷藏庫溫度一直降不下來，請派人來看」→ 助理提議開一件「設備報修」案件，預先帶入標題與說明 → 同仁修改後確認，案件進入設備組的「待受理」→ 設備組的阿明受理，成為案件負責人 → 需要換零件，阿明把案件轉給採購組 → 採購組的小芳受理、完成並填寫處理結果。案件逾期時，案件負責人在側欄「案件」旁看到逾期數字；管理者在瓶頸統計看到各類型、各承辦組的件數、逾期件數與平均處理時間。

具體完成標準：

1. **承辦組與案件類型**：管理者建立承辦組、管理成員；定義案件類型（說明、預設承辦組、預設處理時限、啟用／停用）。成員異動都留紀錄。
2. **三個來源**：平台內手動建立；助理在對話中提議、使用者確認後建立；數據庫紀錄送出後自動建立。另有處理事項上的「另開案件」。
3. **狀態只有五個**：待受理、處理中、待補件、已完成、已取消。所有流轉以一張動作表檢查「誰、在哪個狀態、可以做什麼」，不允許任意改狀態。每次交接（受理、轉組）都記下誰、何時。
4. **助理不能改變案件狀態**：助理只能提議建立；所有狀態變更都由人操作。
5. **可見性單獨判斷**：建立者、目前承辦組的成員、曾經擔任案件負責人的人、管理者。外部客戶不能建立、也看不到案件。這條判斷有自己的測試，包含 ADR 的「阿明」轉組情境。
6. **逾期在讀取時判斷**（決定 D）：側欄數字、首頁卡片與清單的「逾期」篩選；不另建通知資料，也不排每日作業。
7. **瓶頸統計**：管理者依案件類型與承辦組看件數、逾期件數與平均處理時間，可以點開個別案件；永遠不含對話文字。
8. **不保存對話內容**：從對話建立的案件只保存使用者確認過的標題與說明，加上對話串的連結。案件永久保存。

**非目標：** 見第 8 節。

---

## 2. 現況事實（2026-10-06，基準 `master`）

| 事實 | 出處 |
| --- | --- |
| 處理事項 `AssistantIssue` 是 M7 最接近的範本：兩個工廠方法（題組未通過、轉給專人）、保存問答副本、`EventCount` 是並行控制欄位；事件 `AssistantIssueEvent` 有 `Ordinal`，只增不改 | `apps/api/src/SmartAgri.Domain/Assistants/AssistantIssue.cs:19-30`、`:95`、`:151`、`:260-265`；`AssistantIssueEvent.cs:50`、`:60-70` |
| 處理事項的狀態可以任意互換（已解決的可以重開），案件不能照抄 | `apps/api/src/SmartAgri.Domain/Assistants/AssistantIssueStatus.cs:5-7`、`AssistantIssue.cs:220-247` |
| 處理事項 API：看不到、不存在、別的組織一律 `403 assistant-issue`；並行修改 `409 issue-changed`；不能指派的人 `422 assignee-not-eligible`；清單的 `scope` 有 `all`／`owned`／`assigned`／`forwarded` | `apps/api/src/SmartAgri.Api/Assistants/AssistantIssueEndpoints.cs:139`、`:291-297`、`:500-506`、`:518-521`、`:540-552`；`apps/api/src/SmartAgri.Api/Errors/ForbiddenReason.cs:183-185` |
| 處理事項的逾期在讀取時計算（`DueAt < now` 且未解決），首頁卡片顯示「已逾期 N 筆」 | `AssistantIssueEndpoints.cs:330-341`；`apps/admin/src/app/features/home/home-page.component.html:73` |
| 營運追蹤的處理事項數字：未解決件數，以及「建立 → 解決」的平均小時數 | `AssistantIssueEndpoints.cs:480-497`；`apps/api/src/SmartAgri.Api/Operations/OperationsSummaryEndpoints.cs:49`、`:167-176` |
| 營運追蹤的日期範圍 `AnswerAnalyticsRange` 以 **UTC** 日界計算，最多 180 天；數據庫統計另用 `Statistics:TimeZone`（預設 `Asia/Taipei`） | `apps/api/src/SmartAgri.Application/Answers/AnswerAnalyticsRange.cs:3-30`；`apps/api/src/SmartAgri.Api/Databases/StatisticsOptions.cs:16-21` |
| 處理事項指向帳號的外鍵是同組織的複合外鍵、`Restrict`；資料管理者指向帳號是 `Cascade` | `apps/api/src/SmartAgri.Infrastructure/Assistants/AssistantIssueConfiguration.cs:10-16`、`:62-74`；`apps/api/src/SmartAgri.Infrastructure/Databases/DatabaseDataManagerConfiguration.cs:19-34` |
| 角色有 `smb-admin`／`internal-employee`／`external-customer`；後端目前沒有任何以角色判斷的端點（M6-2 新增管理者判斷）。團隊端點以 `manage-assistants` 把關 | `apps/api/src/SmartAgri.Domain/Accounts/AccountRole.cs:11-21`；`AccountPermission.cs:12-39`；`apps/api/src/SmartAgri.Api/Team/TeamEndpoints.cs:92-94` |
| 外部客戶有 `submit-authorized-forms`，會送出數據庫紀錄 | `apps/api/src/SmartAgri.Infrastructure/Seeding/DevelopmentSeedData.cs:67-72` |
| **沒有停用或刪除帳號的 API**；「已停用的帳號」只是查不到名稱時的備援，寫了兩份；處理事項的 `AccountNamesAsync` 沒有備援 | `apps/api/src/SmartAgri.Api/Databases/DatabaseSubmissionService.cs:95`、`DatabaseEndpoints.cs:610`；`AssistantIssueEndpoints.cs:639-651`；`apps/api/src/SmartAgri.Infrastructure/Accounts/Account.cs:19-47` |
| 資料管理者以「整份名單取代」更新，每次增減寫一筆 `DatabaseDataManagerChange`（帳號 id 不設外鍵，紀錄比帳號活得久）；候選人清單只給擁有者 | `apps/api/src/SmartAgri.Api/Databases/DatabaseEndpoints.cs:171`、`:551-583`、`:660-674`；`apps/api/src/SmartAgri.Domain/Databases/DatabaseDataManager.cs:18-58`、`DatabaseDataManagerChange.cs:14-36` |
| 對話中的表單請求：觸發方式 `Chat:FormRequests:Trigger`（`Keyword`／`Model`），先判斷數據庫查詢、再表單、最後一般回答；模型模式在選擇前送 `CUSTOM smartagri.form-check` | `apps/api/src/SmartAgri.Api/Chat/ChatFormRequestOptions.cs:6-39`；`ChatRunEndpoints.cs:85-115`、`:302-315`、`:544-566`；`ChatFormRequestTool.cs` |
| 部署預設已改用 `Model` 觸發；#164 的評測 CLI 是 `eval-form-requests` | `deploy/docker-compose.yml:73`；`docs/evals/2026-10-05-164-form-request-trigger.md` 第 3、5 節 |
| 助理連結數據庫：一筆 `AssistantDatabase` 是必要條件，每次使用時重新授權 | `apps/api/src/SmartAgri.Domain/Assistants/AssistantDatabase.cs:6-38`；`apps/api/src/SmartAgri.Api/Assistants/AssistantEndpoints.cs:277-290`、`:801-851`；`apps/api/src/SmartAgri.Application/Assistants/AssistantDatabaseAccess.cs` |
| 對話訊息以欄位保存回覆種類的快照（`FormDatabaseId`、`SubmissionId`、`DatabaseQueryJson`），讀取時重新授權 | `apps/api/src/SmartAgri.Domain/Chat/ChatMessage.cs:136-149`；`ChatReplyKind.cs:19-34` |
| 新增 `ModelInvocationPurpose` 必須歸類為計入用量或嵌入，由測試強制 | `apps/api/src/SmartAgri.Domain/Ai/ModelInvocationPurpose.cs:47`；`apps/api/src/SmartAgri.Application/Organizations/OrganizationTokenUsageRules.cs:33-46` |
| 送出紀錄：`SubmitAsync` 在一次 `SaveChanges` 寫入紀錄與欄位；重送先走 replay（`:205-211`），同鍵競爭失敗時清掉追蹤再 replay（`:256-263`）；三個入口共用 | `apps/api/src/SmartAgri.Api/Databases/DatabaseSubmissionService.cs:187-267`；`DatabaseSubmissionEndpoints.cs:193`、`:291`；`apps/api/src/SmartAgri.Api/Chat/ChatFormEndpoints.cs:308` |
| 撤回保留紀錄列、刪除欄位內容、設定 `WithdrawnAt` | `DatabaseSubmissionService.cs:388-432`；`apps/api/src/SmartAgri.Domain/Databases/DatabaseSubmission.cs:43`、`:100` |
| 對話串屬於帳號，助理擁有者也看不到別人的對話；admin 的對話網址是 `/app/chat/:assistantId/:conversationId` | `apps/api/src/SmartAgri.Domain/Chat/ChatThread.cs:11-46`；`apps/admin/src/app/app.routes.ts:160` |
| 訪客問答走獨立的端點，不經過 `ChatRunEndpoints` | `apps/api/src/SmartAgri.Api/PublicChannels/VisitorChatRunEndpoints.cs` |
| 背景工作是每個組織自己串下一筆（定期報表）；M7 不需要 | `apps/api/src/SmartAgri.Api/Reports/GenerateDatabaseReportHandler.cs:13-31`；`ReportScheduleService.cs:70` |
| 每個資料表都必須 `IOrganizationScoped`，由測試自動檢查 | `apps/api/tests/SmartAgri.Api.Tests/Tenancy/OrganizationModelTests.cs:23-41` |
| admin 處理事項頁：清單＋詳情，以 `?issue=` 選取；可指派的人在前端過濾；狀態文字在兩個頁面各寫一份 | `apps/admin/src/app/features/issues/issues-page.component.ts:28`、`:56-61`、`:67-69`；`apps/admin/src/app/features/activity/activity-page.component.ts:31-33` |
| `AssistantIssuesRepository` 是獨立的 root service，以 `apiMode` 分流；mock 只保存在這次工作階段。巨大的 `mock-demo-repository.ts` 有 6,072 行 | `apps/admin/src/app/core/repositories/assistant-issues.repository.ts:77-82`、`:129` |
| 側欄是靜態清單，`NavLeaf` 沒有數字欄位；所有路由都是 `loadComponent` | `apps/admin/src/app/layout/side-nav/nav-item.model.ts:1-5`；`apps/admin/src/app/app.ts:29-38`；`app.routes.ts` |
| admin 初始 bundle 警告門檻 600 kB（上次 589.23 kB）；widget 210／240 kB，widget 也用 `libs/chat` | `apps/admin/project.json:35-40`；`apps/widget/project.json:28-32` |
| 設定頁目前只有外觀與團隊 | `apps/admin/src/app/features/settings/pages/settings-page.component.ts` |
| `libs/chat` 的回覆種類是聯集型別，`REPLY_KIND_LABELS` 是 `Record<ChatReplyKind, string>`（加種類就必須加標籤）；`chat-message` 元件在 lib 裡也渲染 admin 專用的種類 | `libs/chat/src/lib/chat-view.model.ts:165-218`、`:224`；`chat-message-mapper.ts:23-30`；`message/chat-message.component.html:45`、`:102-110` |
| 對話元件 849 行；「轉給專人」確認視窗寫在元件裡；同樣的確認視窗標記另有兩份 | `apps/admin/src/app/features/assistant-use/conversation/chat-conversation.component.html:81`、`:152`、`:275-290`；`.ts:268-271`；`conversation-rail.component.html`、`own-submissions.component.html` |
| API 模式 E2E 放在 `apps/admin-e2e/src/e2e-api/`，以 `nx run admin-e2e:e2e-api` 執行；Cypress task 只有 `rememberValue`／`recallValue`，無法改伺服器時間 | `apps/admin-e2e/cypress.api.config.ts:20`、`:36-48` |

---

## 3. 關鍵選擇

共同規則：

- 所有端點都要登入，且呼叫者必須是內部帳號（`smb-admin` 或 `internal-employee`）。外部客戶一律 `403 case`。
- 案件不存在、別的組織的、看不到的，**一律回同樣的 `403 case`**。看得到但不能做這個動作，回 `403 case-action`。
- 狀態動作是 `POST …/{id}:<動作>`，請求帶 `eventCount`（畫面上那一版）。不一致，或同時有人修改，回 `409 case-changed`（決定 I）。
- 欄位錯誤回 `422`，沿用 `ApiErrors.ValidationFailed` 的格式。
- 管理者以角色判斷（M6 計畫決定 A）。下文寫「管理者」限定的端點，一律用 M6-2 的 `RequireOrganizationAdmin` 與 `403 organization-settings`；不是管理者、不存在、別的組織，回應相同。
- 新的 `403` 原因 `case`、`case-action` 要加進前端的 `REPOSITORY_PERMISSION_DENIED_REASONS`（`apps/admin/src/app/core/repositories/demo-repository.ts:127`）。

### A. 承辦組（決定 G）

- `GET /api/v1/case-groups`：內部帳號都能讀（建立案件、轉組時要選）。回傳名稱、是否封存、成員（名稱）。
- `POST /api/v1/case-groups`、`PUT /api/v1/case-groups/{id}`（改名）：管理者。名稱 1–40 字，同組織不重複（`422`）。
- `POST /api/v1/case-groups/{id}:archive`、`:unarchive`：管理者。不刪除，因為案件永久保存並指向承辦組。仍有未結案件，或是某個啟用中類型的預設承辦組時，不能封存（`422 case-group-in-use`，列出原因）。
- `PUT /api/v1/case-groups/{id}/members`：管理者，整份名單取代（比照資料管理者）。只能放同組織的內部帳號；外部客戶或不認得的帳號 `422 member-not-eligible`，整份不儲存。每個增減寫一筆成員異動紀錄。
- `GET /api/v1/case-groups/{id}/member-changes`：管理者，看成員異動歷史。
- 承辦組的建立、改名、封存記入 M6-2 的 `OrganizationActivity`（決定 P）。

### B. 案件類型（決定 H、O）

- `GET /api/v1/case-types`：內部帳號都能讀；預設只回啟用中的，管理者可加 `includeInactive=true`。
- `POST /api/v1/case-types`、`PUT /api/v1/case-types/{id}`：管理者。欄位：名稱（1–40 字，同組織不重複）、說明（0–500 字；助理靠它判斷該用哪一類）、預設承辦組（必須是未封存的承辦組，否則 `422 case-group-archived`）、預設處理時限、啟用中。
- **處理時限以小時保存**，範圍 1–2,160 小時（90 天），畫面可以用「天」或「小時」輸入。以日曆時間計算，不算工作日（決定 H）。
- 停用、不刪除：停用的類型不能用來建立新案件，也不會被助理提議；既有案件不受影響。某個數據庫的「送出後自動開案」正在用的類型不能停用（`422 case-type-in-use`，列出數據庫）。

### C. 案件與可見性

- `POST /api/v1/cases`：內部帳號。欄位：`typeId`、`groupId`、`dueAt`（依類型預先帶入，可修改）、標題（1–120 字）、說明（0–4,000 字），以及選填的連結：`databaseId`＋`submissionId`、`assistantId`＋`threadId`、`previousCaseId`（「另開新案」）。
  - 停用的類型 `422 case-type-inactive`；封存的承辦組 `422 case-group-archived`。
  - 連結必須是呼叫者現在讀得到的：紀錄要能讀（`DatabaseRecordAccess`），對話串要是自己的，舊案件要看得到且已結案；否則 `422 link-not-available`。
  - `dueAt` 不能早於現在：`422 due-in-past`（`errors.dueAt`）；畫面在送出前就提示「時限不能早於現在」（決定 H）。
- `GET /api/v1/cases`：只回看得到的。篩選：`scope`（`all`／`created`／`owned`／`my-groups`）、`status`（預設未結案：待受理、處理中、待補件）、`overdue=true`、`typeId`、`groupId`。第一版不分頁（決定 Q）。
- `GET /api/v1/cases/{id}`：案件、事件、名稱，以及連結的狀態：
  - 數據庫紀錄：`available`／`withdrawn`（顯示「紀錄已撤回」）／`unavailable`；另回「你能不能讀這筆紀錄」。
  - 對話串：只回「你能不能開啟」；別人的、已被保存期限刪除的，一律顯示「這個對話無法開啟」。案件從不回傳對話文字。
  - 處理事項：只回 id 與「你能不能開啟」。
- **可見性**是一個單獨的、可測試的判斷 `CaseVisibility.VisibleTo(callerId, isAdmin)`（EF 運算式）：
  - 建立者；
  - 目前承辦組的成員；
  - 曾經擔任案件負責人的人（從事件推導：曾有一筆以他為負責人的「受理」）；
  - 管理者。
- 帳號名稱改用一個共用的查詢（找不到時顯示「已停用的帳號」），取代 `DatabaseSubmissionService`、`DatabaseEndpoints` 兩份備援字串與處理事項的查詢（決定 C）。

### D. 流轉（一張動作表，決定 I、J）

| 動作 | 端點 | 誰 | 允許的狀態 | 結果 |
| --- | --- | --- | --- | --- |
| 受理 | `:accept` | 目前承辦組的成員 | 待受理 | 成為案件負責人，處理中 |
| 待補件 | `:request-info`（必填說明） | 案件負責人 | 處理中 | 待補件 |
| 補充 | `POST …/comments` | 建立者、案件負責人 | 未結案 | 新增說明；建立者在待補件時補充，自動回到處理中 |
| 繼續處理 | `:resume` | 案件負責人 | 待補件 | 處理中（決定 J） |
| 完成 | `:complete`（必填處理結果） | 案件負責人 | 處理中、待補件 | 已完成 |
| 取消 | `:cancel` | 受理前：建立者（原因選填）或管理者（必填原因）。受理後：案件負責人或管理者（必填原因） | 未結案 | 已取消 |
| 轉組 | `:transfer`（目標承辦組，說明選填） | 案件負責人、管理者 | 處理中、待補件（管理者也可以在待受理時轉） | 待受理，清空案件負責人 |
| 調整時限 | `:set-due`（說明選填；新時限不能早於現在） | 案件負責人 | 未結案 | 改時限，留紀錄 |

- 任何不在表上的組合：身分不符 `403 case-action`；狀態不符 `409 case-changed`（畫面只顯示可以做的動作，狀態不符代表別人剛改過）。
- 必填欄位空白 `422 resolution-required`／`reason-required`／`note-required`；轉到封存的承辦組或原承辦組 `422`；調整時限早於現在 `422 due-in-past`（決定 H）。
- 不能重新開啟。已結案的案件顯示「另開新案」，帶入舊案件的類型、標題與連結（`previousCaseId`）。
- 案件負責人被移出承辦組後，仍是案件負責人，直到轉組或結案（ADR：曾任負責人的人仍看得到）。
- 每個動作寫一筆 `CaseEvent`（`Ordinal` 遞增、只增不改）；`Case.EventCount` 是並行控制欄位，比照 `AssistantIssue`。

### E. 逾期提示（決定 D）

- 逾期 = `DueAt < now` 且狀態不是已完成、已取消。待補件照樣計時（ADR）。
- `GET /api/v1/cases/attention`：回傳呼叫者需要注意的逾期件數：
  - 自己是案件負責人的逾期案件；
  - 自己所屬承辦組裡、還在待受理的逾期案件。
- 管理者不因為是管理者而多算；管理者從瓶頸統計看逾期。
- 側欄「案件」旁顯示這個數字（0 不顯示）；首頁「案件」卡片顯示同樣的數字與「待我受理」件數；清單的「逾期」篩選用同一個逾期定義，範圍是你看得到的案件；側欄數字等於「我負責的」加上「我的承辦組待受理」兩個篩選的逾期件數。
- 側欄的程式要小：只在 shell 放一個 signal 與數字樣式；讀取數字的程式以動態 `import()` 載入，換頁時更新，不設計時器（決定 R）。
- **E2E 製造逾期**：API 不接受早於現在的時限（決定 H），所以新增營運子指令 `case-set-due --organization <id> --case <id> --due <ISO 時間>`，直接改時限並寫一筆沒有 Actor 的 `due-changed` 事件。只能在 Development／Testing 執行，其他環境拒絕並以非 0 結束碼結束。比照 `set-token-limit` 與 M6 的 `retention-cleanup --as-of`，由 API 模式 E2E 以 `cy.exec` 呼叫（M7-11）。

### F. 瓶頸統計（決定 K）

- `GET /api/v1/cases/statistics?from=&to=`：管理者。日期範圍沿用 `AnswerAnalyticsRange`（預設 30 天、最多 180 天、UTC 日界），與營運追蹤一致。
- 依「案件類型 × 目前承辦組」各一列：未結案件數（現在）、逾期件數（現在）、期間內完成件數、期間內取消件數、平均處理時間。
- **平均處理時間 = 建立 → 完成的小時數**，只算完成時間落在期間內的案件，歸到完成時的類型與承辦組；已取消的不算。與處理事項的「建立 → 解決」同一種定義。
- 每一列可以點開對應的案件清單（管理者看得到所有案件）。回應永遠不含對話文字或案件說明。

### G. 處理事項「另開案件」（決定 N）

- `POST /api/v1/issues/{issueId}:open-case`：誰能更新這個處理事項，誰就能做（`AssistantIssueEndpoints.Visible`）；呼叫者也必須是內部帳號。
- 請求同建立案件（類型、承辦組、時限、標題、說明）；畫面預先帶入處理事項的標題與問題副本，可以修改。
- 同一次 `SaveChanges`：建立案件（來源 `assistant-issue`、建立者是操作的人）、處理事項以新的結案方式 `not-assistant-issue` 結案、處理事項新增事件 `case-opened`、兩邊互相記下對方的 id。
- 已解決的處理事項 `409 issue-changed`（請重新整理）；已經另開過案件的，`409` 並回傳那件案件的 id。

### H. 提議階段與助理提議開案（決定 L、T、U）

- **M7-8 先重構**：把 `ChatRunEndpoints` 裡「先找可提議的東西、再依觸發方式決定、最後組成回覆」的表單步驟，抽成一個可以放多個提議的「提議階段」。表單是第一個實作，行為完全不變；觸發設定仍是 `Chat:FormRequests:Trigger`（ADR：沿用表單請求的觸發方式），不改名，以免已部署的 `CHAT_FORM_REQUEST_TRIGGER` 失效。
- **優先順序**：數據庫查詢 → 表單 → 案件 → 一般回答；每則回覆最多一個提議。
- **助理設定「可提議的案件類型」**：預設為空。`PUT`／`DELETE /api/v1/assistants/{id}/sources/case-type/{caseTypeId}`（助理擁有者＋`manage-assistants`，比照數據庫的連結）；只能選啟用中的類型。每次提議時重新檢查：類型仍啟用、仍在清單上、提問者是內部帳號。
- **觸發**：
  - 模型模式：表單沒有成立時，再做一次案件選擇呼叫（新用途 `case-proposal`，計入用量），提供這個助理可提議的類型（名稱＋說明）；模型選一個類型並草擬標題與說明，伺服器重新檢查類型、截斷長度。模型失敗時退回關鍵字判斷。
  - 關鍵字模式：問題命中開案關鍵字（如報修、維修、申請、請款、退貨、派人、安排），而且問題含有某個類型的名稱，或助理只有一個可提議的類型時，才提議；標題取問題的前 120 字。
- **回覆**：新的回覆種類 `case-proposal`，文字「這件事可以開一件「設備報修」案件，請確認內容。」並附可編輯的標題與說明、類型、承辦組、處理時限。對話訊息保存提議的快照（類型 id、標題、說明、狀態、確認後的案件 id），讀取時重新檢查類型。
- **確認**：`POST /api/v1/assistants/{id}/chat/case-proposals/{messageId}:confirm`（帶編輯後的標題、說明）建立案件：建立者是提問者，來源 `chat-proposal`，連結這個對話串；`:dismiss` 只記下「不用了」。同一則提議只能確認一次（`409`）。
- **訪客永遠不會收到提議**：官網與 LINE 走 `VisitorChatRunEndpoints`，不經過提議階段（加測試鎖住）。
- 前端：提議卡片放在 admin 的對話元件（比照表單流程），`libs/chat` 只加種類與標籤，不在 lib 渲染卡片，避免 widget bundle 變大。

### I. 數據庫送出後自動開案（決定 M）

- `PUT /api/v1/databases/{id}/auto-case`（`{ caseTypeId | null }`）：管理者。只能選啟用中的類型。
- `DatabaseSubmissionService.SubmitAsync` 在寫入紀錄的同一次 `SaveChanges`（`:250-254`）加入案件與 `created` 事件。replay（`:205-211`）不建立案件；同鍵競爭失敗時（`:256-263`）`ChangeTracker.Clear()` 會一併丟掉這次的案件。所以重送不會重複開案。
- **沒有建立者**：來源 `database-submission`，畫面顯示「由數據庫「X」自動建立」。送出者不因此看得到案件（送出者可能是外部客戶）。
- 標題「{數據庫名稱}：新紀錄」，說明是固定文字「由數據庫送出自動建立，內容請開啟紀錄查看。」，**不複製紀錄內容**，讓撤回時內容真的消失。處理時限與承辦組依類型。
- 設定頁提示「承辦組中有 N 人無法讀取這個數據庫的紀錄」：案件可見性不會讓人讀到紀錄（ADR：可見性與紀錄權限分開判斷）。
- 紀錄撤回後，案件保留，連結顯示「紀錄已撤回」。

### J. 前端（admin）

- 新路由 `/app/cases`（lazy）：清單＋詳情，以 `?case=<id>` 選取（比照 `/app/issues`）；管理者另有 `?view=statistics` 的瓶頸統計。
- 側欄在「處理事項」之後加「案件」；首頁加「案件」卡片。
- 設定頁加「承辦組」「案件類型」兩個區塊，只有管理者看得到。
- 新的 `CaseSettingsRepository`（承辦組與案件類型）與 `CasesRepository`：獨立的 root service、`apiMode` 分流，比照 `AssistantIssuesRepository`。mock 的範例資料放在這兩個檔案裡，不加進 `demo-seed.ts`（後端有測試會讀它）。
- 狀態文字集中在一個函式（不要再像處理事項那樣寫兩份）。

---

## 4. 資料模型

| 實體 | 主要欄位 | 說明 |
| --- | --- | --- |
| `CaseGroup`（新增） | Id、OrganizationId、Name、ArchivedAt?、CreatedAt、UpdatedAt | 名稱同組織唯一；不刪除 |
| `CaseGroupMember`（新增） | (GroupId, AccountId) PK、OrganizationId、AddedByAccountId、AddedAt | 指向承辦組 `Cascade`；指向帳號是同組織的複合外鍵、`Restrict`（決定 C） |
| `CaseGroupMemberChange`（新增） | Id、OrganizationId、GroupId、AccountId、Added、ChangedByAccountId、ChangedAt | 只增不改；帳號 id 不設外鍵（比照 `DatabaseDataManagerChange`） |
| `CaseType`（新增） | Id、OrganizationId、Name、Description、DefaultGroupId、DefaultDueHours、IsActive、CreatedAt、UpdatedAt | 預設承辦組 `Restrict`；不刪除 |
| `Case`（新增） | Id、OrganizationId、TypeId、GroupId、Status、Title、Description、Origin（`manual`／`chat-proposal`／`database-submission`／`assistant-issue`）、CreatedByAccountId?、OwnerAccountId?、DueAt、ThreadAssistantId?、ThreadId?、DatabaseId?、SubmissionId?、AssistantIssueId?、PreviousCaseId?、Resolution?、CancelReason?、CreatedAt、UpdatedAt、AcceptedAt?、CompletedAt?、CancelledAt?、EventCount | 類型、承辦組 `Restrict`；建立者、負責人是同組織的複合外鍵 `Restrict`；`PreviousCaseId` 指向同組織的案件 `Restrict`；對話串、紀錄、處理事項只存 id、不設外鍵（會被刪除或隨助理刪除），讀取時判斷；`EventCount` 是並行控制欄位 |
| `CaseEvent`（新增） | Id、OrganizationId、CaseId、Ordinal、Action（`created`／`accepted`／`info-requested`／`commented`／`resumed`／`completed`／`cancelled`／`transferred`／`due-changed`）、ActorAccountId?、At、Note?、Status?、OwnerAccountId?、FromGroupId?、ToGroupId?、DueAt? | `(CaseId, Ordinal)` 唯一；自動開案的 `created` 沒有 Actor；「曾任案件負責人」從 `accepted` 推導 |
| `AssistantCaseType`（新增） | AssistantId、CaseTypeId、OrganizationId、CreatedAt | 隨助理刪除；比照 `AssistantDatabase` |
| `Database`（修改） | 加 AutoCaseTypeId? | 指向案件類型 `Restrict` |
| `AssistantIssue`（修改） | 加 ResolutionKind?（`fixed`／`not-assistant-issue`）、LinkedCaseId? | 解決時設定、重開時清空（與 `ResolutionNote` 相同） |
| `AssistantIssueEventAction`（修改） | 加 `case-opened` | |
| `ChatMessage`（修改） | 加 CaseProposalJson?、ProposedCaseId? | `ChatReplyKind` 加 `case-proposal` |
| `ModelInvocation`（修改） | `Purpose` 加 `case-proposal` | 計入每月用量 |

- 全部新增的實體都實作 `IOrganizationScoped`，由 `OrganizationModelTests` 自動檢查。
- 不新增任何保存對話內容的欄位。

---

## 5. Vertical slices

### 軌道 A｜設定（管理者）

#### Slice M7-1｜承辦組
- **內容：**
  - `CaseGroup`、`CaseGroupMember`、`CaseGroupMemberChange` 與 migration；第 3 節 A 的端點；建立、改名、封存記入 `OrganizationActivity`（`case-group-created`／`case-group-renamed`／`case-group-archived`／`case-group-unarchived`，detail 記 id 與名稱）。
  - 共用的帳號名稱查詢（找不到時「已停用的帳號」），換掉兩份既有的備援字串（決定 C）。
  - 設定頁「承辦組」區塊（只有管理者看得到）：建立、改名、封存／取消封存、編輯成員（候選人只有內部帳號）、成員異動歷史。
  - `CaseSettingsRepository`（mock＋Hybrid）。
- **驗收：**
  - 非管理者讀得到清單，但任何寫入都是 `403 organization-settings`；跨組織的 id 與不存在的 id 回應相同。
  - 成員名單整份取代：同一次請求有外部客戶或別組織的帳號時 `422 member-not-eligible`，整份不儲存；每個增減一筆異動紀錄。
  - 封存與取消封存可以來回操作；封存的承辦組不出現在一般清單的可選項目。「仍被使用時不能封存」的檢查隨 M7-2（類型的預設承辦組）與 M7-3（未結案件）加入。
  - 名稱重複 `422`。
- **依賴：** M6-2（管理者判斷、`OrganizationActivity`）。

#### Slice M7-2｜案件類型
- **內容：** `CaseType` 與 migration；第 3 節 B 的端點；設定頁「案件類型」區塊（名稱、說明、預設承辦組、預設處理時限［天／小時］、啟用中）；封存承辦組時加上檢查：是某個啟用中類型的預設承辦組就 `422 case-group-in-use`；類型的建立與修改記入 `OrganizationActivity`（`case-type-created`／`case-type-updated`）。
- **驗收：**
  - 時限範圍 1–2,160 小時，邊界值測試；畫面以天輸入時換算正確。
  - 預設承辦組封存中 `422 case-group-archived`；承辦組是啟用中類型的預設值時不能封存。
  - 停用的類型不出現在預設清單；管理者加 `includeInactive=true` 才看得到。
- **依賴：** M7-1。

### 軌道 B｜案件

#### Slice M7-3｜手動建立與檢視案件
- **內容：**
  - `Case`、`CaseEvent` 與 migration；`POST`／`GET /api/v1/cases`、`GET /api/v1/cases/{id}`（第 3 節 C），先只有 `created` 事件。
  - `CaseVisibility` 單獨的判斷與測試。
  - 封存承辦組時加上檢查：仍有未結案件就 `422 case-group-in-use`。
  - admin `/app/cases`（lazy）：清單、詳情、「建立案件」表單（選類型 → 帶入承辦組與時限，可改）、連結到紀錄與對話串（只顯示「能不能開啟」）。側欄加「案件」（先不顯示數字）。
  - `CasesRepository`（mock＋Hybrid）；狀態文字集中一處。
- **驗收：**
  - 可見性測試：建立者、目前承辦組成員、管理者看得到；其他內部帳號、外部客戶、別的組織得到相同的 `403 case`。
  - 外部客戶不能建立（`403 case`）。
  - 停用的類型、封存的承辦組、讀不到的紀錄、別人的對話串、早於現在的時限（`due-in-past`），各自 `422`。
  - 詳情不含任何對話文字；連結的對話串被刪除後顯示「這個對話無法開啟」。
  - 有未結案件的承辦組不能封存（`422 case-group-in-use`）。
- **依賴：** M7-2。

#### Slice M7-4｜案件流轉
- **內容：** 第 3 節 D 的動作表與端點；事件、並行控制；詳情頁依身分與狀態顯示可做的動作、事件時間軸（交接軌跡）、「另開新案」。
- **驗收：**
  - 動作表的每一列都有「可以」與「不可以」的測試（身分不符 `403 case-action`、狀態不符 `409 case-changed`）。
  - `eventCount` 過期 `409`；兩人同時受理只有一人成功。
  - 建立者在待補件時補充，自動回到處理中；案件負責人補充則不會。
  - 調整時限早於現在 `422 due-in-past`。
  - **阿明情境**：設備組的阿明受理後轉給採購組，阿明仍看得到；沒受理過的設備組成員看不到；採購組成員看得到。
  - 已結案的案件沒有任何動作，只有「另開新案」；新案件連結舊案件。
- **依賴：** M7-3。

#### Slice M7-5｜逾期提示
- **內容：** 第 3 節 E：`GET /api/v1/cases/attention`、清單的 `overdue=true`；側欄數字（`NavLeaf` 加選填的數字，讀取程式以動態 `import()` 載入）；首頁「案件」卡片。
- **驗收：**
  - 以可注入的時鐘測試：逾期與未逾期、待補件照樣計時、已完成／已取消不算。
  - 案件負責人只算自己的；待受理的逾期案件算給該承辦組的每個成員；管理者不多算。
  - admin 初始 bundle 增加不超過 1 kB（附前後數字）。
- **依賴：** M7-4。

#### Slice M7-6｜瓶頸統計
- **內容：** 第 3 節 F：`GET /api/v1/cases/statistics`；`/app/cases?view=statistics`（只有管理者有這個分頁），每列可點開篩選後的清單。
- **驗收：**
  - 平均處理時間只算期間內完成的案件、從建立算到完成；取消不算；沒有完成件數時顯示「—」。
  - 轉組過的案件算在目前（完成時）的承辦組。
  - 非管理者 `403 organization-settings`；回應不含案件說明或對話文字（測試檢查 JSON）。
- **依賴：** M7-4。

#### Slice M7-7｜處理事項「另開案件」
- **內容：** 第 3 節 G；處理事項頁的「另開案件」按鈕與對話框（預先帶入標題與問題副本）；處理事項顯示「非助理問題」與案件連結，案件顯示來源處理事項。
- **驗收：**
  - 誰能更新處理事項，誰就能另開；其他人 `403 assistant-issue`。
  - 案件、結案、事件、雙向連結在同一個交易；任何一步失敗都不留下半套。
  - 已解決的處理事項 `409`；重複另開 `409` 並回傳既有案件 id。
  - 營運追蹤的處理事項數字照舊計算（`not-assistant-issue` 也算已解決）。
  - 加了 `case-opened` 事件與新的結案方式：掃描前後端與 Cypress 寫死的列舉個數（#126 的教訓）。
- **依賴：** M7-3。

### 軌道 C｜對話與數據庫

#### Slice M7-8｜提議階段重構（先重構）
- **內容：**
  - 把 `ChatRunEndpoints` 的表單觸發（`:302-315`）與優先順序步驟（`:544-566`）抽成可放多個提議的「提議階段」；表單是第一個實作。`Chat:FormRequests:Trigger` 不改名。
  - 三份重複的確認視窗標記（對話元件、對話清單、我的提交）抽成一個共用元件。之後的 Slice 若在它合併後動工就使用它；不是硬依賴。
- **驗收：**
  - 行為不變：既有的表單請求測試、#171 的 `form-check` 事件順序測試、`chat-database-query` 測試全部不改就通過。
  - `eval-form-requests`（#164 的評測 CLI）以 `--trigger keyword` 跑出的結果與重構前相同。
  - 確認視窗的焦點、Escape、`aria-*` 不變；`accessibility.cy.ts` 等命中的 mock spec 通過。
- **依賴：** 無，可以立即開始。

#### Slice M7-9｜助理提議開案
- **內容：** 第 3 節 H：`AssistantCaseType` 與 migration、助理設定頁「可提議的案件類型」、提議階段的案件實作（關鍵字＋模型）、`case-proposal` 回覆種類與快照、確認／不用了端點、admin 對話裡的提議卡片；`libs/chat` 加種類與標籤。
- **驗收：**
  - 預設不提議；加入類型後才提議。類型停用或從清單移除後，舊的提議卡片顯示「無法建立」，確認得到 `422`。
  - 優先順序：統計問題走數據庫查詢；有表單且命中時給表單；都沒有時才提議案件；一則回覆最多一個提議。
  - 確認後建立的案件只有使用者確認的標題與說明、連結這個對話串；同一則提議第二次確認 `409`。
  - 外部客戶提問、訪客頻道，都不會提議（測試鎖住）。
  - 模型模式多一筆 `case-proposal` 的 `ModelInvocation`；模型失敗退回關鍵字。
  - widget 的初始 bundle 數字不變或附上差異（預算 210／240 kB）。
- **依賴：** M7-3、M7-8。

#### Slice M7-10｜數據庫送出後自動開案
- **內容：** 第 3 節 I：`Database.AutoCaseTypeId` 與 migration、設定端點（變更記入 `OrganizationActivity` 的 `database-auto-case-changed`）、`SubmitAsync` 同一次 `SaveChanges` 建立案件、數據庫設定頁的「送出後自動開案」（只有管理者看得到，含讀取權限提示）、案件詳情的「紀錄已撤回」。
- **驗收：**
  - 三個送出入口都會開案；同一個 `submissionId` 重送只開一件；同鍵競爭時只有一件。
  - 案件沒有建立者；送出者（含外部客戶）看不到案件。
  - 案件不含任何紀錄內容；撤回後顯示「紀錄已撤回」，案件照常流轉。
  - 數據庫正在用的類型不能停用（`422 case-type-in-use`）。
- **依賴：** M7-3。

### 軌道 D｜驗收

#### Slice M7-11｜API 模式 E2E
- **內容：** 新 spec `apps/admin-e2e/src/e2e-api/cases-api.cy.ts`：
  - 管理者建立兩個承辦組與一個類型 → 同仁手動建立案件 → 承辦組成員受理 → 轉組 → 另一組受理、完成；原承辦組的其他成員看不到。
  - 對話提議（CI 用 Fake 模型或 Keyword 觸發）→ 確認 → 案件出現在清單、連結對話串。
  - 表單送出 → 自動開案。
  - 處理事項 → 另開案件。
  - 逾期：建立案件後以 `cy.exec` 執行 `case-set-due --due <過去的時間>`（第 3 節 E、決定 H）→ 側欄數字、首頁卡片與「逾期」篩選都出現；管理者的瓶頸統計看到逾期件數。
  - 營運子指令 `case-set-due` 在這張票新增（第一個需要逾期案件的是這份 E2E；M7-5 以可注入的時鐘測試）。
- **驗收：** 新 spec 在本機以 `npx nx run admin-e2e:e2e-api --spec=src/e2e-api/cases-api.cy.ts` 跑過；CI 的 `e2e-api` 全綠；`case-set-due` 在 Production 拒絕執行（測試）。
- **依賴：** M7-4、M7-5、M7-6、M7-7、M7-9、M7-10。

#### Slice M7-12｜真實模型評估與文件
- **內容：**
  - 案件提議的評測（比照 #164）：題庫（應提議、不應提議、應給表單而不是案件、模糊題），關鍵字與真實模型各跑一次，報告寫進 `docs/evals/`，並建議是否需要調整關鍵字或提示。
  - 更新 `docs/handoff/mock-to-api-mapping.md`（新增案件一節）、`docs/handoff/route-screen-matrix.md`（`/app/cases`）；部署文件若有新設定就補上。
- **驗收：** 評測報告列出漏觸與誤觸比率與逐題結果；兩份 handoff 文件更新；金鑰不出現在任何輸出或文件。
- **依賴：** M7-11。

---

## 6. 審查發現的對應

| 項目 | 在 M7 的處理 |
| --- | --- |
| 待補功能 6：從助理答案或表單建立案件 | M7-9（助理提議）、M7-10（送出後自動開案）；另有手動建立（M7-3）與處理事項另開（M7-7） |
| 待補功能 6：選擇流程類型、負責部門、處理時限 | 案件類型（M7-2）＋承辦組（M7-1），不建部門（ADR） |
| 待補功能 6：必要欄位 | 不依類型自訂欄位；結構化資料用 M4 的表單與數據庫（ADR），M7-10 連結紀錄 |
| 待補功能 6：草稿、待受理…已取消等狀態 | 五個狀態，不保留草稿（ADR）；M7-4 |
| 待補功能 6：交接保留操作人與時間 | `CaseEvent`（M7-4） |
| 待補功能 6：只看得到被指派或被授權的案件與附件；三種權限分開判斷 | `CaseVisibility`（M7-3、M7-4）；紀錄與對話串仍由各自的權限判斷（第 3 節 C、I）；第一版沒有附件 |
| 待補功能 6：助理不能自行變更付款、核准或人事 | 助理只能提議，狀態只由人改（M7-9；第 3 節 D） |
| 待補功能 6：逾期、退回或資料不足時通知負責人 | 逾期：M7-5（站內，決定 D）；資料不足＝待補件（M7-4） |
| 待補功能 6：管理者檢視不含私人對話全文的流程瓶頸 | M7-6 |
| ADR「案件與處理事項是兩個概念」與「另開案件」 | M7-7 |
| ADR「承辦組，不建部門」 | M7-1 |
| ADR「案件類型」 | M7-2 |
| ADR「案件內容」「案件從哪裡來」 | M7-3、M7-9、M7-10 |
| ADR「助理提議開案」 | M7-8、M7-9、M7-12 |
| ADR「助理不能改變案件狀態」 | M7-9（只建立）、M7-4 |
| ADR「數據庫送出後自動開案」 | M7-10 |
| ADR「狀態與流轉」 | M7-4 |
| ADR「處理時限」 | M7-2、M7-4（調整時限留紀錄） |
| ADR「逾期通知」 | M7-5 |
| ADR「誰看得到案件」 | M7-3、M7-4（阿明情境） |
| ADR「瓶頸統計」 | M7-6 |
| ADR「第一版只給組織內部帳號」 | 所有端點檢查內部帳號（第 3 節共同規則）；`read-own-tracking` 不接 |
| ADR「保存」 | 案件不刪除；帳號名稱備援（M7-1）；連結不設外鍵（第 4 節） |

---

## 7. 決定事項、風險與待辦

**已決定（2026-10-06，負責人回覆「照建議」）：**

- **A. 管理者以角色（`smb-admin`）判斷**，不是某一項權限。定義與實作在 [M6 計畫](2026-10-06-backend-milestone-6-org-settings.md)（M6-2）。M7 用在承辦組、案件類型、管理者取消與轉組、可見性、瓶頸統計與自動開案設定。
- **C. 帳號只停用、不刪除**，畫面顯示「已停用的帳號」（沿用 `DatabaseSubmissionService.cs:95`、`DatabaseEndpoints.cs:610` 的用詞）。案件與承辦組成員指向帳號的外鍵用 `Restrict`，比照 `AssistantIssue`；抽出一個共用的帳號名稱查詢，找不到時用這個備援（M7-1）。
- **D. 逾期在讀取時判斷**（`DueAt` 已過且未結案）：側欄「案件」旁的數字、首頁卡片、清單的「逾期」篩選。不建通知資料表，也不排每日作業。案件負責人看自己的；待受理的案件算給該承辦組的所有成員；管理者只在瓶頸統計看到逾期。shell／側欄的程式要盡量小（它在初始 bundle）。
- **F. M6 與 M7 的計畫同時寫完，issue 現在就開**，只標真正的依賴。唯一跨里程碑的依賴是 M7-1 ← M6-2（管理者判斷與 `OrganizationActivity`）。

**本計畫的建議，已決定（2026-10-06，負責人回覆「照建議」；H 依負責人回覆修正）：**

- **G. 承辦組只能建立、改名、封存／取消封存，不刪除。** 有未結案件或是啟用中類型的預設承辦組時不能封存。成員只能是內部帳號。替代方案：允許刪除沒有任何案件的承辦組。
- **H. 處理時限以小時保存、以日曆時間計算**（1–2,160 小時，畫面可用天輸入），從建立時起算，轉組不重設。建立與調整時都不能填早於現在的時間（`422 due-in-past`）；API 模式 E2E 以只限 Development／Testing 的營運子指令 `case-set-due` 製造逾期案件（第 3 節 E；M7-11 新增）。替代方案：以工作日計算（需要每個組織的假日資料，第一版沒有）；或允許填過去的時間補登（不採用）。
- **I. 狀態動作帶 `eventCount`，不一致回 `409 case-changed`；看得到但不能做回 `403 case-action`。** 替代方案：照處理事項只靠資料庫的並行控制（看舊畫面的人仍可能成功做出動作）。
- **J. 案件負責人可以在待補件時「繼續處理」；管理者在受理前也能取消（必填原因）。** 理由：對方可能用電話補件；自動開的案件沒有建立者，否則沒有人能取消或恢復。ADR 的表已補上這兩項（2026-10-06）。
- **K. 平均處理時間 = 建立 → 完成**，只算期間內完成的案件，歸到完成時的類型與承辦組；已取消的不算；日期範圍沿用營運追蹤的 `AnswerAnalyticsRange`（UTC 日界）。替代方案：受理 → 完成（看不到等待受理的時間，而那正是瓶頸之一）。
- **L. 提議的優先順序：數據庫查詢 → 表單 → 案件 → 一般回答**，每則回覆最多一個提議。模型模式下，案件是表單之後的另一次選擇呼叫（用途 `case-proposal`）。替代方案：表單與案件放進同一次呼叫，讓模型二選一（少一次呼叫，但改變表單的既有行為與用量歸類）。
- **M. 自動開的案件沒有建立者**，送出者不因此看得到案件，案件也不複製紀錄內容。理由：送出者可能是外部客戶（第一版不給外部客戶看案件）；不複製內容，撤回才真的有效。替代方案：送出者是內部帳號時當建立者。
- **N. 處理事項另開案件：新增結案方式 `not-assistant-issue` 與事件 `case-opened`，兩邊互存 id。** 預先帶入處理事項的標題與問題副本，操作的人可以修改後再建立。
- **O. 案件類型停用、不刪除**；數據庫自動開案正在用的類型不能停用。
- **P. 承辦組與案件類型的設定變更記入 M6-2 的 `OrganizationActivity`**；成員異動另有自己的歷史表（比照資料管理者）。若 M6-2 的 `OrganizationActivity` 形狀不適合，改為只用成員歷史表。
- **Q. 案件清單第一版不分頁**，預設篩選未結案。
- **R. 側欄數字的讀取程式動態載入，換頁時更新，不設計時器。**
- **S. 對話串、數據庫紀錄、處理事項的連結不設外鍵**，讀取時判斷能不能開啟；舊案件的連結（另開新案）設外鍵。
- **T. 關鍵字模式只在命中開案關鍵字，且問題含某個類型名稱或助理只有一個可提議類型時提議。**
- **U. 助理擁有者可以從所有啟用中的類型挑「可提議的案件類型」**，不需要管理者同意；每次提議時重新檢查。替代方案：只有管理者能設定（較嚴，但管理者要逐一設定每個助理）。

**待負責人提供：**

- M7-12 的真實模型評測需要模型金鑰（金鑰檔已存在；任何輸出都不印出金鑰）。M7-1 到 M7-11 用 Fake 模型，不需要。

**技術風險：**

1. **可見性判斷寫錯**：多一個條件就外洩，少一個就看不到該辦的事。對策：`CaseVisibility` 是單獨的運算式，清單、詳情、動作、統計的點開都用它；測試涵蓋每一類人與阿明的轉組情境；看不到與不存在的回應位元組相同。
2. **跨組織**：所有新實體 `IOrganizationScoped`，外鍵都是同組織的複合外鍵；`OrganizationModelTests` 會自動檢查；每個端點都測別組織的 id。
3. **admin 初始 bundle**：上次 589.23 kB，離警告門檻 600 kB 只剩約 11 kB。新頁面一律 lazy；側欄數字的程式動態載入；每張前端票附 `--skip-nx-cache` 的 production build 數字。
4. **`libs/chat` 會影響 widget**：widget 預算 210／240 kB，也用 `libs/chat`。對策：lib 只加種類與標籤，提議卡片放在 admin；M7-9 附 widget 的 bundle 數字。
5. **模型提議的準確度**：誤觸會讓使用者每次都看到開案卡片，漏觸則案件開不出來。對策：使用者一定要確認；M7-12 以真實模型評測後再調整。
6. **後端測試會讀前端檔案**：`apps/admin/src/app/core/domain/` 與 seed 有變動時，後端的 `SmartAgri.Domain.Tests` 可能紅。對策：前端票都跑它；mock 範例資料放在新的 repository，不放 `demo-seed.ts`。
7. **新增列舉值**：`AssistantIssueEventAction`、處理事項結案方式、`ChatReplyKind`、`ModelInvocationPurpose`、回覆種類標籤，都可能有測試寫死個數（#126 的教訓，含 Cypress）。對策：加值時 `grep -rn` 舊的個數與相鄰的值（前後端與 e2e），跑命中的 spec。
8. **自動開案的人讀不到紀錄**：承辦組成員不一定是資料管理者。對策：設定頁提示人數；不讓案件可見性擴大紀錄權限。
9. **清單不分頁**：案件永久保存，件數會一直增加。對策：預設只列未結案；量大時再加分頁。
10. **UTC 日界**：統計沿用營運追蹤的 UTC 日界，台灣時間早上 8 點才換日。對策：與營運追蹤一致，畫面註明；之後若營運追蹤改用 `Statistics:TimeZone`，一起改。

---

## 8. 不在 M7 範圍

- 訪客（官網、LINE）建立案件：需要訪客的身分與聯絡方式，另寫 ADR。
- 外部客戶建立或追蹤自己的案件；`read-own-tracking` 維持不接行為。
- Email 或其他站外通知；每日提醒作業。
- 待補件期間暫停處理時限；工作日與假日計算。
- 依案件類型自訂欄位；案件附件。
- 重新開啟已完成的案件（改用「另開新案」）。
- 把 `AssistantIssue` 併入案件，或兩者互相轉換。
- 助理改變案件狀態。
- 案件清單分頁、全文搜尋、匯出。
