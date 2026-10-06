# 後端 Milestone 6｜組織設定：實作計畫

**日期：** 2026-10-06
**狀態：** 已確認（2026-10-06，負責人回覆「照建議」），可以拆票。決定 A、B、E、F 見第 7 節；C、D 屬於 M7，記在 [M7 計畫](2026-10-06-backend-milestone-7-cases.md)。issue 標題前綴用「M6｜」。
**依據：** [LLM 供應商 ADR](../adr/2026-09-25-llm-providers-and-data-residency.md) 與[撤回與保存期限 ADR](../adr/2026-09-25-withdrawal-and-retention.md) 的「補充（2026-10-06）」、[里程碑 ADR](../adr/2026-09-25-milestone-order.md) 補充（2026-10-06）、[案件 ADR](../adr/2026-10-06-cases-and-handoff.md)（管理者以角色判斷）、[背景工作 ADR](../adr/2026-09-25-background-jobs-on-postgresql.md)、[M3.5 計畫](2026-09-29-assistant-acceptance-milestone.md) 第 7 節決定 D（`AnswerOutcome` 的保存）、[M5a 計畫](2026-10-06-backend-milestone-5a-website-embed.md)（每月 token 上限、營運指令）、`docs/glossary.md`（管理者、保存期限、處理事項）、`docs/handoff/mock-to-api-mapping.md`。
**拆票方式：** 與 M5a 相同。每個 Slice 都是可單獨合併的垂直切片，各自附測試與驗收條件；「依賴」只列硬依賴；會改 migration 或 `openapi/v1.json` 的 Slice 用接續分支依序做。

---

## 1. 目標與非目標

**目標（M6 交付）：** 管理者可以替整個組織選對話模型，並設定對話保存期限：

> 管理者打開「系統設定」→ 在「對話模型」選一個部署提供的模型，新對話立即改用它；助理的驗收頁提示「上次測試使用模型 X，現在是 Y」，建議重跑題組 → 在「保存期限」選 90 天，確認畫面寫出大約會刪除幾串對話；7 天緩衝期內可以改回，之後每天自動清理過期的對話 → 某個助理不再需要保存對話時，擁有者在助理設定關閉「保存對話」；管理者在系統設定的「對話保存」看到各助理已保存的對話串數，按「立即刪除」清掉既有的對話。

具體完成標準：

1. **模型清單**：部署設定可以列出多個對話模型，每個有自己的 `MaxOutputTokens`、`ReasoningEffort`、逾時。現有的單一 `Ai:Chat` 設定不用改，照常運作，並成為部署預設（第 3 節 A）。
2. **依組織解析**：回答、對外回答、試問、題組重跑、數據庫查詢、表單請求、定期報表摘要，全部用組織選的模型；背景工作也一樣。題組紀錄的是實際用到的模型（第 3 節 B）。
3. **管理者限定**：換模型、改保存期限、立即刪除既有對話，只有角色是 `smb-admin` 的帳號能做（決定 A）。其他人得到一致的 `403`。
4. **活動紀錄**：每次換模型、改保存期限、清理與立即刪除，都寫一筆 `OrganizationActivity`（誰、何時、做了什麼），不含對話內容。
5. **保存期限**：30／90／180／365 天或永久，預設永久。縮短有 7 天緩衝期，延長立即生效。每天清理一次：整串刪除過期的對話串；`AnswerOutcome` 依自己的時間刪除（決定 B）。
6. **立即刪除既有對話**：管理者在系統設定的「對話保存」清單（或助理設定）確認後，刪除所有成員在該助理上的對話串（決定 E）。處理事項的問答副本不受影響。
7. **admin**：系統設定頁新增「對話模型」與「對話保存」（保存期限、各助理已保存的對話與「立即刪除」）；驗收頁提示模型變更；助理設定的開關旁加上「立即刪除」或請聯絡管理者的說明。mock 與 Hybrid 共用同一個契約。

**非目標：** 見第 8 節。

---

## 2. 現況事實（2026-10-06，基準 `master` `956f4a5`）

| 事實 | 出處 |
| --- | --- |
| 每個部署只有一個對話模型：`ChatModelOptions`（`Ai:Chat`）只有一組 Provider／Model／ApiKey，以及 `MaxOutputTokens`、`TimeoutSeconds`、`ReasoningEffort`；驗證寫在 `Validate` | `apps/api/src/SmartAgri.Infrastructure/Ai/ChatModelOptions.cs:36-72`、`:61`、`:65`、`:72`、`:92-148` |
| `ChatClientProvider` 是 singleton，從 `Ai:Chat` 建立；每次呼叫的預設值（輸出上限、推理強度）在建立時套上 | `apps/api/src/SmartAgri.Api/Ai/ChatServiceCollectionExtensions.cs:35`、`apps/api/src/SmartAgri.Infrastructure/Ai/ChatClientProvider.cs:56-108` |
| 程式碼取得模型的唯一接縫：scoped `IChatClient`，由 `CreateClient` 包上 `ModelInvocationRecordingChatClient`，紀錄的組織取自範圍內的 `IOrganizationContext` | `apps/api/src/SmartAgri.Api/Ai/ChatServiceCollectionExtensions.cs:38-41`、`:50-65` |
| 呼叫紀錄的供應商與模型來自 `ChatClientProvider`；`GetService(ChatClientMetadata)` 也回它的模型 | `apps/api/src/SmartAgri.Infrastructure/Ai/ModelInvocationRecordingChatClient.cs:208-214`、`:233-238`、`:305-311` |
| 用途共 9 種（2 種嵌入、7 種對話） | `apps/api/src/SmartAgri.Domain/Ai/ModelInvocationPurpose.cs:8-56` |
| 對話用途的呼叫點：回答 `ChatRunEndpoints.cs:329`（預設用途 `generate-answer`）、數據庫查詢 `ChatDatabaseQueries.cs:199`、表單請求 `ChatFormRequestTool.cs:69`、試問 `AssistantEndpoints.cs:1034-1040` 與 `AssistantDraftEndpoints.cs:346`、對外回答 `VisitorChatRunEndpoints.cs:150` | `apps/api/src/SmartAgri.Api/Chat/`、`Assistants/`、`PublicChannels/` |
| 題組重跑（背景工作）直接讀 `ChatClientProvider.Model` 記進 `AssistantTestRun.Model`，不是實際用到的模型 | `apps/api/src/SmartAgri.Api/Assistants/RunAssistantTestSetHandler.cs:49`、`:99`、`:126-133`；`apps/api/src/SmartAgri.Domain/Assistants/AssistantTestRun.cs:76`、`:136` |
| 定期報表摘要（背景工作）用 scoped `IChatClient`；回應沒有模型名稱時退回 `ChatClientProvider.Model` | `apps/api/src/SmartAgri.Api/Reports/SummarizeDatabaseReportHandler.cs:75-83` |
| 對話入口用 `ChatClientProvider.IsConfigured` 判斷「部署有沒有對話模型」 | `apps/api/src/SmartAgri.Api/Chat/ChatRunEndpoints.cs:182`、`:236`；`apps/api/src/SmartAgri.Api/PublicChannels/VisitorChatRunEndpoints.cs:77` |
| 評測指令直接使用 `ChatClientProvider` | `apps/api/src/SmartAgri.Api/Answers/Evaluation/EvalAnswersCommand.cs:84`、`apps/api/src/SmartAgri.Api/Chat/Evaluation/EvalFormRequestsCommand.cs:46` |
| 測試以 `services.AddScoped<IChatClient>(…)` 換掉模型；報表測試另外註冊一個 `ChatClientProvider` | `apps/api/tests/SmartAgri.Api.Tests/Chat/ChatRunEndpointsTests.cs:344`、`apps/api/tests/SmartAgri.Api.Tests/Reports/PeriodicReportEndpointsTests.cs:1067` |
| 背景工作一定有組織；執行時在該組織的範圍內解析處理器 | `apps/api/src/SmartAgri.Domain/Jobs/BackgroundJob.cs:45`、`apps/api/src/SmartAgri.Api/Jobs/JobRunner.cs:179`、`apps/api/src/SmartAgri.Api/Tenancy/JobOrganizationScope.cs:27` |
| 沒有 cron。週期工作的作法是「處理完排下一個」，以 compare-and-set 防止重複排入 | `apps/api/src/SmartAgri.Api/Jobs/JobServiceCollectionExtensions.cs:17-43`、`apps/api/src/SmartAgri.Api/Reports/ReportScheduleService.cs:68-75`、`apps/api/src/SmartAgri.Api/Reports/GenerateDatabaseReportHandler.cs:118-151` |
| 統計的日界以 `Statistics:TimeZone`（預設 `Asia/Taipei`）計算 | `apps/api/src/SmartAgri.Api/Databases/StatisticsOptions.cs:16-25` |
| `Organization` 已有組織層級設定的先例：`TeamPermissionsSavedAt`、`MonthlyTokenLimit` | `apps/api/src/SmartAgri.Domain/Organizations/Organization.cs:55-72` |
| 活動紀錄的先例：`KnowledgeActivity`（action、actor、at、jsonb detail），actor 沒有外鍵 | `apps/api/src/SmartAgri.Domain/Knowledge/KnowledgeActivity.cs:20-48`、`apps/api/src/SmartAgri.Infrastructure/Knowledge/KnowledgeActivityConfiguration.cs:27` |
| 授權只有權限 policy；API 沒有任何地方以角色把關。團隊 API 用 `manage-assistants` | `apps/api/src/SmartAgri.Api/Authorization/PermissionPolicies.cs:17-40`、`apps/api/src/SmartAgri.Api/Team/TeamEndpoints.cs:93-94` |
| 角色 `smb-admin`／`internal-employee`／`external-customer`；`/me` 回傳角色；畫面上 `smb-admin` 叫「管理者」 | `apps/api/src/SmartAgri.Domain/Accounts/AccountRole.cs`、`apps/api/src/SmartAgri.Api/Accounts/MeEndpoints.cs:21-36`、`apps/admin/src/app/core/domain/team.model.ts:17-21` |
| 權限每個請求從資料庫讀；角色另外寫進存取權杖 | `apps/api/src/SmartAgri.Api/Authorization/AccountPermissions.cs:20-35`、`apps/api/src/SmartAgri.Api/Authentication/ConnectEndpoints.cs:147` |
| `403` 原因與前端 `REPOSITORY_PERMISSION_DENIED_REASONS` 一一對應，兩邊手動同步 | `apps/api/src/SmartAgri.Api/Errors/ForbiddenReason.cs:3-18`、`apps/admin/src/app/core/repositories/demo-repository.ts:127-148` |
| 組織用量 API 用 `manage-publishing` | `apps/api/src/SmartAgri.Api/Organizations/OrganizationUsageEndpoints.cs:27-31` |
| 對話串刪除會連帶刪除訊息與引用（資料庫層 cascade）；沒有其他資料表指向對話串或訊息 | `apps/api/src/SmartAgri.Infrastructure/Chat/ChatMessageConfiguration.cs:45-49`、`ChatMessageCitationConfiguration.cs:33-37` |
| 對話串的索引都以 `AccountId` 開頭，沒有 `(OrganizationId, LastActivityAt)` | `apps/api/src/SmartAgri.Infrastructure/Chat/ChatThreadConfiguration.cs:23-38` |
| `LastActivityAt` 是最後一則訊息的時間 | `apps/api/src/SmartAgri.Domain/Chat/ChatThread.cs:58`、`:81-85` |
| `AnswerOutcome` 刻意不記錄對話串；註解寫明「保存期限出現時一起加清理工作」；已有 `(OrganizationId, At)` 索引 | `apps/api/src/SmartAgri.Domain/Answers/AnswerOutcome.cs:12-21`、`:55`；`apps/api/src/SmartAgri.Infrastructure/Answers/AnswerOutcomeConfiguration.cs:68-69` |
| 助理的 `KeepConversations`；關閉時對話清單變空，但既有對話串不刪除 | `apps/api/src/SmartAgri.Domain/Assistants/Assistant.cs:67`、`:216-219`；`apps/api/src/SmartAgri.Api/Chat/ChatEndpoints.cs:215` |
| 目前只能逐串刪除自己的對話 | `apps/api/src/SmartAgri.Api/Chat/ChatEndpoints.cs:174`、`:328-329` |
| 題組紀錄的批次刪除先例（`ExecuteDeleteAsync`） | `apps/api/src/SmartAgri.Api/Assistants/AssistantTestRunQueue.cs:155-172` |
| admin 的開關文字是「保留使用者自己的對話紀錄」，說明寫「要真正移除，請由對話的所有人在自己的對話紀錄中刪除」 | `apps/admin/src/app/features/assistants/components/answer-rules-form/answer-rules-form.component.html:55-60`（用在助理的規則分頁與建立精靈） |
| 系統設定頁有團隊面板與「外觀設定」（`lib-setting-row`），路由已 lazy load | `apps/admin/src/app/features/settings/pages/settings-page.component.html`、`apps/admin/src/app/app.routes.ts:177-183` |
| 團隊面板的資料讀取寫法（`rxResource`、`RepositoryView`） | `apps/admin/src/app/features/settings/components/team-panel/team-panel.component.ts:51-68` |
| Hybrid 覆寫團隊 API 的先例 | `apps/admin/src/app/core/repositories/hybrid-demo-repository.ts:643-706` |
| 題組紀錄的 `model` 已在 API 型別中，畫面還沒顯示 | `apps/admin/src/app/core/api/api-schema.ts:6134`、`apps/admin/src/app/features/assistants/assistant-detail/tabs/acceptance-tab/assistant-acceptance-tab.component.ts:73`、`:92` |
| admin 初始 bundle 預算：警告 600 kB、錯誤 1 MB；上次 589.23 kB | `apps/admin/project.json:35-40`、[M5a 結案](2026-10-06-m5a-closeout-m5b-handoff.md) |
| CI `e2e-api`：API 在 5153、admin 在 4200，模型用 `Fake`（`fake-chat-dev`）；API 模式 spec 放在 `src/e2e-api/`；E2E 以 `cy.exec` 呼叫 API 的一次性子指令（`set-token-limit`） | `.github/workflows/ci.yml:72-171`、`apps/admin-e2e/cypress.api.config.ts:20`、`apps/admin-e2e/src/e2e-api/website-embed-api.cy.ts:30-37` |
| 部署 compose 以 `CHAT_*` 變數對應 `Ai__Chat__*` | `deploy/docker-compose.yml:63-70` |

---

## 3. 關鍵選擇

### A. 對話模型清單（部署設定）

- 現有的 `Ai:Chat`（Provider、Endpoint、Model、ApiKey、MaxOutputTokens、TimeoutSeconds、ReasoningEffort）不變，是**部署預設**。新增兩個選填欄位：`Id`（預設等於 `Model`）與 `DisplayName`（預設等於 `Model`）。
- 新增 `Ai:Chat:Models`：**額外**的模型，每個項目的欄位與上面相同，各自有輸出上限、推理強度與逾時。
- 向下相容：沒有 `Models` 時，清單就是一個項目（部署預設）。現有的環境變數、`deploy/docker-compose.yml` 與客戶的 `.env` 都不用改。
- 驗證（啟動時失敗）：
  - 有 `Models` 時，`Ai:Chat` 本身必須有設定（部署預設不可空白）；
  - `Id` 不可重複，只能用英數字、`-`、`_`、`.`；
  - 每個項目各自套用現有規則（`Fake` 只能在 Development／Testing、金鑰、Endpoint）；
  - `Provider` 空白的項目視為沒有設定、略過。這讓 compose 可以預留空白的第二組變數。
- 啟動時的設定報告（`ChatConfigurationReporter`）列出每個項目的 id、供應商、模型，不印金鑰。
- 每個項目一個 `ChatClientProvider`（singleton，依項目建立一次）。

### B. 依組織解析模型

- 新增 `ChatModelCatalog`（singleton）：清單、部署預設、依 id 取項目、`IsConfigured`。
- 新增 `OrganizationChatModelResolver`：讀組織的 `ChatModelId`，回傳實際使用的項目與原因（第 3 節 D 的 `source`）。每個範圍讀一次資料庫，不快取，所以換模型後的新對話立即生效。
- **接縫不變**：scoped `IChatClient` 仍由 `CreateClient` 建立，改成依範圍內的組織解析項目，再包上 `ModelInvocationRecordingChatClient`。所有用途自動改用組織的模型；背景工作本來就在工作的組織範圍內解析。測試用 `AddScoped<IChatClient>` 換模型的寫法照常有效。
- 呼叫紀錄（`ModelInvocation`）的供應商與模型取自實際用到的項目。
- 題組重跑改記實際用到的模型（不再讀 `ChatClientProvider.Model`）。報表摘要的退回值同樣改用解析結果。
- `ChatRunEndpoints`、`VisitorChatRunEndpoints` 的「部署有沒有對話模型」改問 `ChatModelCatalog.IsConfigured`。
- 評測指令（`eval-answers`、`eval-form-requests`）照舊用部署預設。
- M6-1 只做重構：組織還沒有 `ChatModelId` 欄位，一律得到部署預設，行為不變。

### C. 管理者檢查（決定 A）

- 新增集中的 `RequireOrganizationAdmin(ForbiddenReason)` endpoint filter：帳號角色必須是 `smb-admin`。
- 角色每個請求從資料庫讀，不用存取權杖裡的 claim（與權限的作法相同）。目前沒有 API 能改角色；之後加了，也不必等權杖過期。
- 新的 `403` 原因 `organization-settings`，訊息「只有管理者可以變更組織設定。」；前端的 `REPOSITORY_PERMISSION_DENIED_REASONS` 同步加入。
- 一致規則：不是管理者、資源不存在、屬於別的組織，一律回同樣的 `403`。
- 用在：換模型、改保存期限、預覽刪除數量、各助理已保存對話的清單、立即刪除既有對話；M7 的承辦組與案件類型也用它（M7-1 依賴 M6-2）。

### D. 組織模型設定 API

- `GET /api/v1/organization/chat-model`：組織內任何帳號都能讀（成員可以知道自己用的是哪個模型）。回傳 `OrganizationChatModelView`：
  - `options`：清單項目的 `id`、`displayName`、`model`；永遠不含金鑰、Endpoint；
  - `selectedId`：組織選的 id，沒選是 `null`；
  - `effective`：實際使用的項目（`id`、`displayName`、`model`）；
  - `source`：`selected`／`deployment-default`（沒選）／`removed`（選的項目已從清單移除，改用預設）；
  - `canChange`：目前帳號是不是管理者；
  - `lastChange`：最近一次變更的帳號顯示名稱與時間（找不到帳號名稱時顯示「已停用的帳號」，與其他畫面一致）；
  - `revision`。
- `PUT /api/v1/organization/chat-model`（管理者）：`{ modelId, revision }`，`modelId: null` 表示改回部署預設。
  - 不在清單的 id：`422`（`errors.modelId`）；`revision` 不是最新：`409`；
  - 與目前相同時不寫紀錄，直接回 `200`；
  - 寫入一筆 `OrganizationActivity`（`chat-model-changed`，detail 記前後的 id 與顯示名稱）。
- **驗收頁讀哪裡**：讀 `GET /api/v1/organization/chat-model` 的 `effective.model`，不放進 `/me`。理由：`/me` 在登入時讀一次就快取，換模型後會過時；`/me` 只描述「我是誰」，加組織設定會擴大每個畫面都依賴的契約。
- 比對以模型名稱（`AssistantTestRun.Model`）為準。兩個項目用同一個模型、只差推理強度時，不會提示（第 7 節技術風險 5）。

### E. 組織活動紀錄

- 新增 `OrganizationActivity`（第 4 節），只能新增，不提供修改或刪除。
- 仿照 `KnowledgeActivity`：`Action`、`ActorAccountId?`（系統動作為 `null`；沒有外鍵；系統目前不刪除帳號，日後若加入也不影響紀錄）、`At`、`Detail`（jsonb）。
- 動作：`chat-model-changed`、`retention-changed`、`retention-change-cancelled`、`retention-took-effect`、`retention-cleanup`、`conversations-purged`。
- detail 只放 id、數字、時間與顯示名稱，不放任何對話內容。
- M6 不做活動紀錄的瀏覽頁；設定畫面只顯示每個區塊的「上次變更」。

### F. 保存期限設定

- 選項：30、90、180、365 天，或永久（`null`）。預設永久。
- `GET /api/v1/organization/retention`：組織內任何帳號都能讀（成員的對話受它管理）。回傳 `days`、`pending`（`{ days, effectiveAt }` 或 `null`）、`options`、`canChange`、`lastChange`、`revision`。
- `GET /api/v1/organization/retention/preview?days=30`（管理者）：回傳 `threadCount`，即目前「最後一則訊息早於今天減 N 天」的對話串數。畫面寫「大約」：緩衝期過後實際數量會更多。
- `PUT /api/v1/organization/retention`（管理者）：`{ days, revision }`。
  - 不在選項內：`422`（`errors.days`）；`revision` 衝突：`409`；
  - **縮短**（含「永久」改成有天數）：存成 `pending`，`effectiveAt` = 現在加 7 天；目前的期限不變；
  - **延長**（含改成永久）：立即生效，並清掉 `pending`；
  - 送出目前生效的值：清掉 `pending`，即「改回」；
  - 每次都寫 `OrganizationActivity`（`retention-changed` 或 `retention-change-cancelled`）。
- 緩衝期內不刪除任何東西。緩衝期過後由每日清理把 `pending` 轉為生效，寫 `retention-took-effect`。

### G. 每日清理

- 工作種類 `retention-cleanup`，每個組織一條「處理完排下一個」的工作鏈（同定期報表），在 `Statistics:TimeZone` 的每天 03:00 執行。
- **何時開始排**：組織第一次設定有天數的期限（含 `pending`）時排入。現有的組織都是永久，所以不需要 migration 補排。
- **安全網**：啟動時的 reconciler 找出「有天數或有 `pending`、卻沒有待執行清理工作」的組織，補排一個。理由：工作在重試用完後會停在失敗，鏈就斷了；migration 不適合寫工作的 payload 與時區計算。
- **防重複**：`Organization.RetentionCleanupNextRunAt` 以 compare-and-set 前進；只有前進成功的那次才排下一個。重複送達的工作不會刪兩次，也不會排出兩條鏈。
- 每次執行：
  1. `pending` 已到 `effectiveAt` 時轉為生效；
  2. 期限是永久時什麼都不刪，只排下一次；改成永久後，鏈在下一次執行時停止；
  3. 截止點 = 當地今天 00:00 減 N 天（換成 UTC）；
  4. 刪除 `LastActivityAt` 早於截止點的對話串（訊息與引用由資料庫連帶刪除）；
  5. 刪除 `At` 早於截止點的 `AnswerOutcome`（決定 B），不論通道；
  6. 分批刪除（每批 1,000 串，各自一個交易），避免一次鎖住大量資料。
- 刪除數不是 0 時，寫一筆彙總的 `retention-cleanup`（「刪除 N 串對話、M 筆回答紀錄」）。兩者都是 0 時不寫，只記 log 與 OpenTelemetry 指標。
- 新增索引 `ChatThreads(OrganizationId, LastActivityAt)`。
- 不受影響：`ModelInvocation`、處理事項的問答副本、定期報表、數據庫紀錄、題組紀錄。
- 營運子指令 `retention-cleanup --organization <id> [--as-of <時間>]`：立即執行一次。`--as-of` 只能在 Development／Testing 使用，給 E2E 模擬「N 天後」。
- 測試以可注入的 `TimeProvider` 控制時間。

### H. 立即刪除既有對話（決定 E）

- `GET /api/v1/organization/retention/assistants`（管理者）：組織內每個助理一列，回傳助理 id、名稱、「保存對話」開關的狀態、已保存的對話串數（`threadCount`）、成員數（`accountCount`）與最後活動時間（`lastActivityAt`，沒有對話時是 `null`）。給系統設定的「對話保存」清單與確認框用。只有數字，不含對話內容：助理擁有者本來也讀不到成員的對話。
- `GET /api/v1/assistants/{id}/chat/conversations/summary`：回傳 `threadCount` 與 `accountCount`，給助理設定的按鈕與說明用。管理者，或讀得到這個助理設定的人都能讀（非管理者要顯示「既有的 N 串對話…」）；其他人 `403 assistant-configuration`。同樣只有數字。
- `POST /api/v1/assistants/{id}/chat/conversations:purge`（管理者）：刪除該助理所有成員的對話串，回傳 `deletedThreadCount`。
  - 權限只看管理者，不看助理的擁有者或使用權；
  - 助理不存在、屬於別的組織、不是管理者，一律 `403 organization-settings`；
  - 沿用第 3 節 G 的分批刪除；
  - 寫入 `OrganizationActivity`（`conversations-purged`，detail 記助理 id、名稱與刪除串數）；
  - 不影響 `AnswerOutcome`（不含內容，依保存期限到期）與處理事項的問答副本。
- 「保存對話」開或關都可以執行。

### I. 前端（admin）

- 系統設定頁新增兩個區塊，放在團隊面板之後、外觀設定之前，沿用 `lib-setting-row`：
  - **對話模型**：清單有多個項目時，管理者看到選單；清單只有一個時，只顯示「目前使用：X」。`source` 是 `removed` 時顯示「原本選的模型已不再提供，目前使用部署預設 X」。
  - **對話保存**：
    - 保存期限：選項、確認對話框（寫出「大約會刪除 N 串對話」）、緩衝期提示（「將於 10/13 起改為 30 天」）與「改回」按鈕；固定說明「已轉給專人的問答會保留在處理事項中」（M6-6）。
    - 各助理已保存的對話（只有管理者看得到）：組織內每個助理一列，顯示已保存的對話串數、最後活動時間與「保留使用者自己的對話紀錄」開關的狀態；每列有「立即刪除」。只顯示數字，不顯示對話內容（M6-5）。
  - 非管理者：「對話模型」與保存期限都是唯讀（顯示目前的模型與保存期限），不顯示選單與按鈕；看不到各助理的清單。
  - 每個區塊顯示「上次變更」。
- 驗收頁：最近一次題組紀錄的 `model` 與目前的 `effective.model` 不同時，顯示「上次測試使用模型 X，現在是 Y。建議重跑題組。」。
- 助理設定的「保留使用者自己的對話紀錄」開關旁：
  - 管理者看到同樣的「立即刪除」按鈕；
  - 非管理者看到「既有的 N 串對話會保留到保存期限；要立即刪除，請聯絡管理者」，取代舊說明「要真正移除，請由對話的所有人在自己的對話紀錄中刪除」；
  - 關閉開關時，確認框寫明已保存的對話不會刪除。
- 「立即刪除」的確認框（兩個入口共用）：寫出助理名稱、對話串數、受影響的成員數，以及「已轉給專人的問答會保留在處理事項中」；必須勾選「我了解刪除後無法復原」才能刪除，不用輸入助理名稱。受影響的成員不會收到通知。
- mock 與 Hybrid 共用新契約；Hybrid 測試使用實際錄下的 API 回應。
- 新區塊都在已 lazy load 的頁面裡，預期不影響初始 bundle；每張前端票仍附數字。

---

## 4. 資料模型

| 實體 | 主要欄位 | 說明 |
| --- | --- | --- |
| `Organization`（修改） | `ChatModelId?`（≤ 64）、`RetentionDays?`、`PendingRetentionDays?`、`PendingRetentionEffectiveAt?`、`RetentionCleanupNextRunAt?`、`SettingsRevision` | `ChatModelId` 不設外鍵（清單在部署設定）；`RetentionDays` 為 `null` 表示永久；`SettingsRevision` 給兩個設定 API 的 `409` |
| `OrganizationActivity`（新增） | Id、OrganizationId、Action、ActorAccountId?、At、Detail（jsonb） | `IOrganizationScoped`；只新增；actor 沒有外鍵；索引 `(OrganizationId, Action, At)` |
| `ChatThread`（修改） | 索引 `(OrganizationId, LastActivityAt)` | 每日清理用 |
| `BackgroundJob`（不改結構） | 新工作種類 `retention-cleanup`，payload `{ runAt }` | 每個組織一條工作鏈 |
| `AssistantTestRun`（不改結構） | `Model` 改記實際用到的模型 | M6-1 |

- 不新增「刪了哪些對話串」的資料表（撤回與保存期限 ADR：只留彙總）。
- 不新增模型清單的資料表：清單是部署設定，資料庫只存組織選的 id。

---

## 5. Vertical slices

### 軌道 A｜後端

#### Slice 1（M6-1）｜對話模型清單與依組織解析模型（先重構）
- **內容：**
  - 第 3 節 A 的 `Ai:Chat:Models` 與驗證；第 3 節 B 的 `ChatModelCatalog`、解析器與 `CreateClient` 的改寫。
  - 題組重跑與報表摘要改記實際用到的模型；對話入口改問 `ChatModelCatalog.IsConfigured`。
  - 評測指令照舊用部署預設。
- **驗收：**
  - 只有 `Ai:Chat` 的現有設定（含 `ci.yml` 與 compose 的寫法）照常啟動，清單是一個項目。
  - 清單有兩個項目時，每個項目的輸出上限、推理強度與逾時各自生效（以假的底層 client 檢查呼叫參數）。
  - 驗證：`Models` 有值但預設空白、id 重複、項目的 `Fake` 出現在 Production，都讓啟動失敗並寫出原因；`Provider` 空白的項目被略過。
  - 所有用途與兩種背景工作都經過解析器（測試以「組織選到第二個項目」的替身解析器檢查 `ModelInvocation.Model` 與 `AssistantTestRun.Model`）。
  - 行為不變：既有測試全部通過，不修改斷言。
- **依賴：** 無。

#### Slice 2（M6-2）｜組織模型設定 API（含管理者檢查、組織活動紀錄）
- **內容：** 第 3 節 C、D、E；`Organization.ChatModelId`、`SettingsRevision` 與 `OrganizationActivity` 的 migration；解析器改讀 `ChatModelId`。
- **驗收：**
  - 管理者換模型後，新的回答與題組重跑使用新模型；`ModelInvocation` 記到新模型。
  - 內部同仁、外部客戶呼叫 `PUT` 得到與「別的組織」相同的 `403 organization-settings`；`GET` 都能讀，`canChange` 正確。
  - 檢查讀的是資料庫的角色：測試直接改資料庫中的角色後，同一個存取權杖立即失去權限。
  - 選的項目從清單移除後，`effective` 是部署預設、`source` 是 `removed`，呼叫照常。
  - 回應不含金鑰與 Endpoint。
  - 每次變更寫一筆活動紀錄；相同值不寫。
- **依賴：** M6-1。

#### Slice 4（M6-4）｜保存期限 API 與每日清理
- **內容：** 第 3 節 F、G；`Organization` 的保存期限欄位、`ChatThreads` 索引的 migration；工作處理器、reconciler、營運子指令；更新 `AnswerOutcome` 的保存註解。
- **驗收（以可注入的時鐘）：**
  - 縮短後 7 天內不刪除任何東西；第 7 天之後的第一次清理才刪；緩衝期內改回就不刪。
  - 延長立即生效，清掉 `pending`。
  - 期限 30 天時，最後一則訊息在截止點前的對話串連同訊息、引用被刪除；截止點之後的完整保留，不會只刪半串。
  - `AnswerOutcome` 依自己的 `At` 刪除，網站通道的也一樣（決定 B）。
  - `ModelInvocation`、處理事項的問答副本、定期報表、數據庫紀錄不變。
  - 日界依 `Statistics:TimeZone`（以 UTC 與台北差一天的邊界案例測試）。
  - 同一個工作重複送達：只刪一次、只排一個下一次；reconciler 補排斷掉的鏈，不會重複。
  - 刪除數大於 0 時寫一筆彙總活動紀錄，0 時不寫。
  - 預覽的數量與實際刪除數一致（同一個時間點）。
- **依賴：** M6-2。

#### Slice 5（M6-5）｜立即刪除既有對話（前後端）
- **內容：** 第 3 節 H 的三個 endpoint；第 3 節 I 系統設定「對話保存」的各助理清單、助理設定的按鈕與說明文字、關閉開關的確認文字、共用的確認框；mock 與 Hybrid。
  - 「對話保存」區塊與 M6-6 的保存期限共用：哪張票後合併，就把自己的內容併進同一個區塊，不另開第二個。
- **驗收：**
  - 清單列出組織內每個助理的串數、成員數、最後活動時間與開關狀態；回應不含對話內容（測試檢查 JSON）。
  - 管理者從清單或助理設定執行後，該助理所有成員的對話串都消失；其他助理的不變。
  - 刪除與清單：非管理者（含助理擁有者）得到 `403 organization-settings`；別的組織的助理也一樣。
  - 助理擁有者讀得到自己助理的 `summary`，畫面顯示「既有的 N 串對話會保留到保存期限；要立即刪除，請聯絡管理者」。
  - 處理事項的問答副本與 `AnswerOutcome` 不變。
  - 寫一筆 `conversations-purged` 活動紀錄；不通知受影響的成員。
  - 確認框顯示助理名稱、串數、成員數與處理事項的說明；沒有勾選「我了解刪除後無法復原」時不能刪除。
  - 關閉開關的確認框寫明已保存的對話不會刪除。
- **依賴：** M6-2。

### 軌道 B｜前端

#### Slice 3（M6-3）｜admin 模型設定畫面與驗收頁提示
- **內容：** 第 3 節 I 的「對話模型」區塊與驗收頁提示；`organization-settings` 加入前端的 `403` 原因；mock 與 Hybrid。
- **驗收：** 一個項目時只顯示「目前使用」；多個項目時管理者可以切換；非管理者唯讀；移除提示；驗收頁在模型不同時提示、相同時不提示；Hybrid 測試使用錄下的 API 回應；admin 初始 bundle 附數字。
- **依賴：** M6-2。

#### Slice 6（M6-6）｜admin 保存期限畫面
- **內容：** 第 3 節 I「對話保存」區塊的保存期限；mock 與 Hybrid。區塊與 M6-5 的各助理清單共用，後合併的票併進同一個區塊。
- **驗收：** 選項、確認框的數量、緩衝期提示與「改回」、處理事項的固定說明；非管理者唯讀；Hybrid 測試使用錄下的 API 回應（含 `409`、`422`）；admin 初始 bundle 附數字。
- **依賴：** M6-4。

### 軌道 C｜驗收

#### Slice 7（M6-7）｜API 模式 E2E
- **內容：** CI `e2e-api` 的 API 加一個 `Fake` 的第二個模型（`Ai__Chat__Models__0__*`）。新 spec：
  1. 管理者跑一次題組 → 換成第二個模型 → 驗收頁出現「上次測試使用模型…」→ 重跑後提示消失；
  2. 內部同仁看到唯讀的設定；
  3. 管理者設定 30 天 → 看到緩衝期提示 → 以 `cy.exec` 執行 `retention-cleanup --as-of <38 天後>` → 舊對話串消失；
  4. 管理者在系統設定的「對話保存」清單立即刪除某個助理的對話 → 該助理的對話紀錄清空。
- **驗收：** 新 spec 在本機以 `--spec` 跑過；CI 的 `e2e-api` 全綠。
- **依賴：** M6-3、M6-5、M6-6。

#### Slice 8（M6-8）｜部署文件與真實模型驗收
- **內容：**
  - `deploy/README.md`：模型清單的寫法、預設模型、每個項目的推理強度（例如 `gpt-6-luna` 要 `None` 才能用工具）、移除模型的影響、保存期限與每日清理的說明。
  - `deploy/.env.example` 與 `deploy/docker-compose.yml`：預留一組空白的第二個模型變數（空白時略過）。
  - 以兩個真實模型實際切換：試問、題組、表單請求、報表摘要都記到新模型；驗收紀錄寫進 `docs/evals/`。
  - 更新 `docs/handoff/mock-to-api-mapping.md`。
- **依賴：** M6-7；負責人提供第二個真實模型。

> 跨里程碑的唯一硬依賴：M7-1 依賴 M6-2（管理者檢查與活動紀錄）。其餘 M7 的票不等 M6。

---

## 6. 審查發現的對應

| 項目（ADR 補充 2026-10-06） | 在 M6 的處理 |
| --- | --- |
| LLM：組織只能選對話模型，嵌入模型每個部署一個 | M6-1、M6-2；嵌入不改 |
| LLM：可選的模型由部署提供；沒選用部署預設 | 第 3 節 A；M6-1 |
| LLM：清單只有一個時只顯示「目前使用：X」 | M6-3 |
| LLM：營運者移除正在用的模型 → 改用預設並提示 | M6-2 的 `source: removed`；M6-3 的提示 |
| LLM：所有對話用途都用組織的模型 | M6-1（含兩種背景工作） |
| LLM：只有管理者能換、新對話立即生效、寫活動紀錄 | 決定 A；M6-2 |
| LLM：不強制重新驗收，助理頁提示「上次測試使用模型 X，現在是 Y」 | M6-3 |
| LLM：用量上限不依模型加權 | 不改 `OrganizationTokenUsageRules` |
| LLM：不保存模型呼叫內容 | 不改；`ModelInvocation` 本來就不含內容 |
| 保存期限：組織設定、選項、預設永久 | M6-4、M6-6 |
| 保存期限：整串刪除，連同訊息與引用 | M6-4 |
| 保存期限：`AnswerOutcome` 依自己的時間刪除 | 決定 B；M6-4；更新 `AnswerOutcome.cs` 的註解 |
| 保存期限：縮短的確認數量與 7 天緩衝期 | M6-4、M6-6 |
| 保存期限：每天清理、只留彙總紀錄 | M6-4 |
| 保存期限：關閉「保存對話」後既有對話依期限到期；「立即刪除既有對話」只有管理者 | 決定 E；M6-5 |
| 保存期限：只有管理者能改 | 決定 A；M6-4 |
| 保存期限：不受影響的資料、畫面寫明處理事項 | M6-4 的驗收；M6-6 的固定說明 |
| `AnswerOutcome.cs:19-21`「保存期限出現時一起加清理工作」 | M6-4 |

---

## 7. 決定事項、風險與待辦

**已決定（2026-10-06，負責人回覆「照建議」）：**

- **A. 「管理者限定」是集中的角色檢查（`smb-admin`），不是一項權限。** 用在組織模型、保存期限、立即刪除；M7 的承辦組與案件類型也用它。理由：`manage-assistants` 也會給負責建助理的同仁，他們不該能換模型或縮短保存期限。目前 API 沒有任何以角色把關的地方（團隊 API 用的是 `manage-assistants`）。
- **B. `AnswerOutcome` 依自己的 `At` 對照同一個截止點刪除，不是「隨對話串刪除」。** 它刻意不記錄對話串（`AnswerOutcome.cs:12-21`）。這也讓官網通道的回答紀錄受保存期限管控。
- **E. 「立即刪除既有對話」只有管理者能執行。** 它會刪掉所有成員在該助理上的對話。
- **F. M6 與 M7 的計畫現在都寫好；兩者的 issue 現在都開，只列真正的依賴。** 跨里程碑的依賴只有 M7-1 ← M6-2。
- C、D 屬於 M7，見 [M7 計畫](2026-10-06-backend-milestone-7-cases.md)。

**寫計畫時的選擇（2026-10-06 負責人回覆「照建議」）：**

1. `Ai:Chat` 本身是部署預設；`Ai:Chat:Models` 只放額外的模型；`Provider` 空白的項目略過（第 3 節 A）。
2. 舊的單一設定，項目 id 預設等於模型名稱（日後改成清單寫法時，id 不變）。
3. 驗收頁讀 `GET /api/v1/organization/chat-model`，不放進 `/me`（第 3 節 D）。
4. 兩個設定的 `GET` 組織內任何帳號都能讀；非管理者在畫面上唯讀（第 3 節 D、F、I）。
5. 清理工作鏈在第一次設定有天數的期限時才排入，加上啟動時的 reconciler；不用 migration 補排（第 3 節 G）。
6. 清理刪除數是 0 時不寫活動紀錄。
7. 「改回」以 `PUT` 送出目前生效的值，不另開 endpoint。
8. 立即刪除不刪 `AnswerOutcome`；「保存對話」開或關都能執行。
9. M6 不做活動紀錄的瀏覽頁，只顯示「上次變更」。
10. 立即刪除的主要入口在系統設定的「對話保存」：列出組織內每個助理已保存的對話串數、最後活動時間與「保留使用者自己的對話紀錄」開關的狀態，每列有「立即刪除」。只有數字，不含對話內容。理由：不是擁有者的管理者打不開別人的助理設定。助理設定的開關旁，管理者看到同樣的按鈕；非管理者看到「既有的 N 串對話會保留到保存期限；要立即刪除，請聯絡管理者」。關閉開關時，確認框寫明已保存的對話不會刪除。確認框寫出助理名稱、串數、受影響的成員數與「已轉給專人的問答會保留在處理事項中」，必須勾選「我了解刪除後無法復原」，不用輸入名稱；受影響的成員不會收到通知。刪除的 API 不變（任何助理都只有管理者能刪）；清單另用 `GET /api/v1/organization/retention/assistants`（第 3 節 H）。

**待負責人提供：**

- M6-8 需要第二個真實模型與金鑰（金鑰檔已在本機；任何指令都不印出它的內容）。M6-1 到 M6-7 用 `Fake` 模型，不需要。

**技術風險：**

1. **設定的向下相容**：客戶的 `.env` 與 compose 都用 `Ai__Chat__*`。對策：`Ai:Chat` 的意義不變；M6-1 以現有的 `ci.yml` 與 compose 寫法做啟動測試。
2. **每個模型的推理強度限制不同**：`gpt-6-luna` 要 `ReasoningEffort=None` 才能在 Chat Completions 用工具（M4 #164）。對策：每個項目各自設定；M6-8 實機確認表單請求與數據庫查詢在兩個模型上都能用工具。
3. **既有組織的清理工作**：工作鏈可能因失敗而中斷。對策：啟動時的 reconciler；每次執行記指標，長時間沒有執行時可以從指標看出來。
4. **一次刪大量對話串**：一個交易刪幾十萬筆會鎖表、撐大 WAL。對策：每批 1,000 串、各自交易；新索引；第一次啟用時在部署文件提醒營運者，在離峰執行。
5. **兩個項目同一個模型名稱**：驗收頁以模型名稱比對，只差推理強度時不會提示。對策：部署文件建議不同設定用不同的 `DisplayName` 與 `Id`；需要時再改成記項目 id。
6. **測試寫死「一個部署一個模型」**：`ChatModelOptionsTests`、`ModelInvocationRecordingChatClientTests`、報表測試直接建立 `ChatClientProvider`。對策：M6-1 保留 `ChatClientProvider` 的公開形狀，只新增清單；這些測試不改斷言。

---

## 8. 不在 M6 範圍

- 組織自備金鑰（BYO）、依模型價格加權的用量上限。
- 助理層級的模型覆寫；組織選嵌入模型。
- 活動紀錄的瀏覽與搜尋頁面。
- 保存模型呼叫內容（ADR 已決定一律不保存）。
- 每個助理不同的保存期限；逐串的刪除軌跡。
- 官網訪客與 LINE 的對話保存（本來就不保存）。
- 定期報表、數據庫紀錄、處理事項的保存期限。
- 多個 API 執行個體之間協調清理工作（沿用單一程序的前提）。
- 案件與承辦組（M7）。
