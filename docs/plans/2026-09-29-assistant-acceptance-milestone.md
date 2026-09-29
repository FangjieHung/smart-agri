# M3.5 助理驗收與維護：實作計畫

**日期：** 2026-09-29
**狀態：** 已確認（2026-09-29）。第 7 節的決定事項已照建議定案，可以拆票；issue 標題前綴用「M3.5｜」。
**依據：**
- [里程碑 ADR](../adr/2026-09-25-milestone-order.md) 的補充（2026-09-27）
- [業務流程審查](../reviews/2026-09-26-project-review-and-backlog.md)：待補功能 5、「問題未解決時的處理」、「營運追蹤」
- [M3 計畫](2026-09-27-backend-milestone-3-in-platform-chat.md) 決定 E
- [M3 結案](2026-09-29-m3-closeout.md)
- ADR：grounded-answers、observability、withdrawal-and-retention、assistant-workspace-model、testing-and-banned-dependencies

**拆票方式：** 與 M2、M3 相同。每個 Slice 都是可以單獨合併的垂直切片，各自附測試與驗收條件。

---

## 1. 目標與非目標

**目標：** 把「文件處理完成」和「助理可以上線」分開。內容負責人要能用可保存的題組驗證助理，政策或文件改版時要能持續維護，也要知道問題出在哪裡。

> 擁有者為助理建立題組，分成常見、例外、應拒答三類 → 按「全部重跑」看到每題是否通過 → 未通過的題目建立處理事項並指派負責人 → 文件改版確認生效後，受影響的助理自動重跑 → 成員在對話中找不到答案時，可以主動「轉給專人」 → 管理者在營運追蹤看到查無資料、錯誤引用與待處理事項的趨勢，但看不到任何人的私人對話內容。

具體完成標準：

1. **題組**：每個助理有一組可保存的測試題（常見、例外、應拒答），每題寫明預期的回覆類型與應被引用的文件。
2. **全部重跑**：
   - 以背景工作執行題組。結果逐題保存：實際回覆類型、實際引用的文件、拒絕原因、是否通過，並留下歷史。
   - 通過與否依「回覆類型與引用的文件」這類結構化的條件判斷，不比對模型的文字，因為模型輸出本來就不固定。
3. **自動重跑**：
   - 助理連接的知識庫有版本確認生效、緊急停用或恢復時，自動重跑受影響的助理。
   - 助理自己的來源或回答規則改變時，也自動重跑。
   - 同一個助理已有排隊中的重跑時，不再重複排入。
4. **驗收狀態**：
   - 每個助理顯示驗收狀態：尚未驗收／通過／未通過／已過期（知識或設定在上次重跑後改變過）。
   - 這個里程碑只顯示、不阻擋組織內使用。M5 對外發布會以「通過且未過期」作為閘門。
5. **處理事項**：
   - 可以從「未通過的測試題」或「成員主動轉給專人的對話」建立處理事項，並指派負責人。
   - 有狀態：待處理、處理中、已解決；也有處理紀錄。
   - 首頁的「待處理事項」卡片接上真實資料。
6. **轉人工**：成員在對話中遇到查無資料或不滿意的回答，可以按「轉給專人」。送出前畫面清楚顯示會分享哪些內容（這一問一答），成員同意後，才建立處理事項。
7. **營運追蹤**：
   - 依助理、依日期，顯示查無資料、引用被拒絕、各回覆類型的數量；
   - 顯示知識庫的處理失敗與逾期待確認；
   - 顯示處理事項的數量與平均處理時間。
   - **全部不含對話內容**。題目文字只會出現在題組裡，或成員主動轉交的內容裡。

**非目標：** 見第 8 節。

---

## 2. 現況事實（2026-09-29，基準 `37101d9`）

| 事實 | 出處 |
| --- | --- |
| 試問只掛在精靈草稿上，已建立的助理沒有試問 endpoint | `apps/api/src/SmartAgri.Api/Assistants/AssistantDraftEndpoints.cs:156-157`、`:316` |
| `GroundedAnswerService.AnswerAsync` 回傳最終回覆與完整的檢索結果（段落、分數、門檻），可以直接用於重跑題組 | `apps/api/src/SmartAgri.Application/Answers/GroundedAnswerModels.cs:275` |
| 回覆類型 `company-data`／`general-knowledge`／`no-result`；拒絕原因 `below-threshold`／`citation-out-of-range`／`no-citation`／`cannot-answer`／`empty-answer` | `GroundedAnswerModels.cs:101-140` |
| 引用帶有文件、版本號與 `VersionEffectiveFrom` | `GroundedAnswerModels.cs:153-206` |
| 回覆與拒絕原因的指標只寫到 OpenTelemetry counter，不會存進資料庫，也無法查詢趨勢 | `apps/api/src/SmartAgri.Application/Answers/GroundedAnswerTelemetry.cs:37`、`:41`、`:83-90` |
| `ModelInvocation` 只記稽核欄位、不存內容；已有 `TrialAnswer`、`GenerateAnswer` 兩種用途 | `apps/api/src/SmartAgri.Domain/Ai/ModelInvocation.cs:11-14`；`ModelInvocationPurpose.cs:26` |
| `eval-answers` 是開發用的 CLI，題庫是 JSON（`expectedKind`、`expectedCitedDocuments`、`followUpOf`），不分組織、不分助理 | `apps/api/eval/answers/README.md`；`apps/api/README.md:797-808` |
| 背景工作佇列支援 `RunAfter`，handler 用 `AddJobHandler<T>("kind")` 註冊 | `apps/api/src/SmartAgri.Domain/Jobs/BackgroundJob.cs:69`；`apps/api/src/SmartAgri.Api/Jobs/JobServiceCollectionExtensions.cs:37` |
| 版本確認生效只寫活動紀錄，沒有排入任何背景工作 | `apps/api/src/SmartAgri.Api/Knowledge/KnowledgeReviewEndpoints.cs:236-284` |
| 可以用 `AssistantKnowledgeBase` 反查「哪些助理連接了這個知識庫」 | `apps/api/src/SmartAgri.Domain/Assistants/AssistantKnowledgeBase.cs` |
| 沒有任何題組、處理事項或工作項目的實體 | grep 無結果 |
| 私人對話的護欄：助理擁有者看不到其他成員的對話內容、對話數量，也看不到對話串名稱 | `apps/api/src/SmartAgri.Application/Chat/ChatThreadAccess.cs:9`、`:17-18` |
| 現有的 `ReadOwnTracking` 權限，意思是「查看自己提交的追蹤紀錄」（數據庫表單），跟這個里程碑無關 | `apps/api/src/SmartAgri.Domain/Accounts/AccountPermission.cs:33` |
| 前端的試問建議題組是全域寫死的 3 題，不分助理，API 模式也沒有換成 API | `mock-demo-repository.ts:1776-1780`；`demo-seed.ts:403-431` |
| 助理分析 `AssistantAnalyticsView` 只有對話數、已解決數、滿意度，完全是 mock；API 模式顯示「後續版本提供」 | `apps/admin/src/app/core/domain/conversation.model.ts:76-82`；`assistant-detail-page.component.ts:135-141` |
| 首頁的「待處理事項」卡片是寫死的靜態文字 | `apps/admin/src/app/features/home/home-page.component.html:59-67` |
| `/app/activity`（「對話與回報紀錄」）仍是 placeholder | `apps/admin/src/app/app.routes.ts:114-118` |

版本查證：這個里程碑不引入新的套件。

---

## 3. 關鍵選擇

**題組與執行結果存進資料庫，通過與否只看結構化條件。** 題組欄位沿用 `eval-answers` 的格式（預期回覆類型、應被引用的文件），並加上分類：常見、例外、應拒答。

每題通過的條件：
- 實際回覆類型等於預期；
- 預期是 `company-data` 時，應被引用的文件都出現在引用中。

模型的文字不比對，每次輸出本來就不同。題組是擁有者自己寫的測試資料，不是成員的私人對話，所以執行結果**可以保存實際的回答文字與引用**，方便擁有者檢查。

**重跑由背景工作執行，並有成本上限。**
- 新 job 種類 `assistants.run-test-set`，payload 只放 `runId`。
- 每題呼叫 `GroundedAnswerService.AnswerAsync`，新增 `ModelInvocationPurpose.AssistantTest` 以區分用量。
- 每個助理最多 50 題。
- 同一個助理若已有排隊中或執行中的重跑，就不再排入新的，只把那一次標成「需要再跑一次」。
- worker 的並行數維持 1。

**自動重跑的觸發點：**
- 知識庫版本確認生效：排入「連接了這個知識庫、而且有題組」的所有助理。
- 文件緊急停用或恢復：同上。
- 助理的來源或回答規則改變（`PATCH settings`、連接或解除來源）。
- 如果版本的生效日期在未來，就用 `RunAfter = EffectiveFrom` 排程，等生效時才重跑。

**驗收狀態從資料推導，不另外存。**

| 狀態 | 條件 |
| --- | --- |
| 尚未驗收 | 沒有任何題目，或從未跑過 |
| 通過 | 最近一次完成的重跑全部通過 |
| 未通過 | 最近一次完成的重跑有任何一題失敗 |
| 已過期 | 最近一次重跑之後，助理連接的知識庫有新生效版本或停用，或助理設定改變過 |

這個里程碑只顯示狀態，不擋組織內使用。

**處理事項只涵蓋「助理品質與轉人工」，但模型要能延伸。**
- 實體 `AssistantIssue`：
  - 來源：`test-failure` 或 `handoff`；
  - 狀態：`open` → `in-progress` → `resolved`；
  - 另有負責人、到期日、處理紀錄（`AssistantIssueEvent`）。
- 待補功能 6 的「跨部門工作項目」要等數據庫里程碑之後另寫 ADR。那時再決定是否把 `AssistantIssue` 併入通用的工作項目。欄位命名與狀態機會刻意保持通用，降低之後遷移的成本（第 7 節決定 A）。

**轉人工是成員主動、明確同意的分享。**
- 對話中的助理回覆旁新增「轉給專人」，在這些情況出現：
  - 回覆是 `no-result`；
  - 成員覺得回答沒幫助；
  - 助理規則允許時，一律可用。
- 送出前顯示：這一則問題與回覆、會交給誰（助理擁有者或指定的處理人）、對方看得到什麼。成員確認後，才把這一問一答的**複本**存進處理事項。處理事項本身不連回對話串，處理人不能從它進入私人對話。
- 成員之後可以在處理事項看到處理結果。
- 不保存對話的助理也能轉人工，因為處理事項存的是複本，不依賴對話紀錄。

**營運追蹤只存「結果」，不存內容。**
- 新表 `AnswerOutcome`：每次正式對話回覆完成時寫入一列，記錄：
  - 助理；
  - 管道（平台內對話、試問、題組重跑）；
  - 回覆類型、拒絕原因；
  - 引用的文件 id（文件 id 不是對話內容）；
  - 時間。
- **不記錄問題、回答或對話串 id。**
- 營運畫面彙總以下數據：
  - 依助理、依日期的回覆類型與拒絕原因數量；
  - 最常被引用的文件；
  - 知識庫處理失敗與逾期待確認的數量，這部分直接查現有的知識庫表；
  - 處理事項的待處理數量與平均處理時間。
- 「哪些題目常查無資料」這個需求，只用題組的失敗題與成員主動轉交的內容來呈現（第 7 節決定 B）。

**權限。**
- 題組管理、重跑、驗收狀態：`manage-assistants`，而且只有擁有者能操作，與助理設定相同。
- 處理事項：
  - 助理擁有者看得到該助理的全部處理事項；
  - 負責人看得到指派給自己的處理事項；
  - 成員看得到自己轉交的處理事項，內容限於處理狀態與回覆。
  - 能被指派的人，需要新的權限 `handle-assistant-issues`（第 7 節決定 C）。
- 營運追蹤（組織層級彙總）：`manage-assistants`。

**前端。** Mock 與 API 兩種模式都實作，讓 Pages Demo 也能展示。
- 助理詳情新增「驗收」分頁：題組、全部重跑、結果與歷史、驗收狀態。
- 新增「處理事項」頁 `/app/issues`：清單、篩選、詳情、指派、處理紀錄。首頁的「待處理事項」卡片接上處理事項。
- 對話頁新增「轉給專人」與同意畫面。
- 助理詳情的「使用統計」與組織層級的「營運追蹤」接真實資料，取代目前的 mock analytics。
- `/app/activity`（對話與回報紀錄）顯示「我轉交的處理事項」。

---

## 4. 資料模型

所有實體都實作 `IOrganizationScoped`。

| 實體 | 主要欄位 | 說明 |
| --- | --- | --- |
| `AssistantTestCase` | Id、AssistantId、Question、Category（`common`／`exception`／`should-refuse`）、ExpectedKind、ExpectedDocumentIds（jsonb）、FollowUpOfId?、Ordinal、CreatedAt、UpdatedAt | 每個助理最多 50 題；刪除助理時連帶刪除 |
| `AssistantTestRun` | Id、AssistantId、Trigger（`manual`／`knowledge-changed`／`assistant-changed`）、Status（`queued`／`running`／`completed`／`failed`）、RerunRequested、QueuedAt、StartedAt?、CompletedAt?、PassedCount、FailedCount、PromptVersion、Model、MinScore | 保留最近 N 次，例如 20 次 |
| `AssistantTestResult` | RunId、TestCaseId、QuestionSnapshot、ActualKind、AnswerText、CitedDocumentIds（jsonb）、RejectionReason?、Passed、FailureReason（`kind-mismatch`／`missing-document`） | 題組是擁有者寫的測試資料，可以保存文字 |
| `AssistantIssue` | Id、AssistantId、Source（`test-failure`／`handoff`）、Status（`open`／`in-progress`／`resolved`）、Title、AssigneeAccountId?、ReporterAccountId?、DueAt?、TestResultRef?、SharedQuestion?、SharedAnswer?、ResolutionNote?、CreatedAt、ResolvedAt? | 轉人工時，才保存成員同意分享的問答複本 |
| `AssistantIssueEvent` | IssueId、Action（created／assigned／status-changed／commented）、ActorAccountId、At、Note? | 處理紀錄 |
| `AnswerOutcome` | Id、AssistantId?、Channel（`chat`／`trial`／`test-run`）、ReplyKind、RejectionReason?、CitedDocumentIds（jsonb）、At | **不含**問題、回答、帳號與對話串 id |
| `ModelInvocationPurpose`（修改） | 新增 `AssistantTest` | |
| `AccountPermission`（修改） | 新增 `HandleAssistantIssues` | 後端與前端 4 處同步修改 |

---

## 5. Vertical slices

### 軌道 A｜題組與重跑（後端）

#### Slice 1｜題組 CRUD 與已建立助理的試問

- **內容：**
  - 實體 `AssistantTestCase` 與 migration。
  - `GET`／`POST`／`PATCH`／`DELETE /api/v1/assistants/{id}/test-cases`：`S+MA+OWN`，上限 50 題，題目最多 2000 字，應被引用的文件必須屬於助理連接的知識庫。
  - `POST /api/v1/assistants/{id}/trial-answers`：已建立助理的單題試問，回傳形狀與草稿試問相同。
  - 提供匯入與匯出：與 `eval-answers` 的 `questions.json` 格式互轉，方便把開發題庫帶進來。
- **驗收：**
  - 別人的助理與不存在的助理，回位元組相同的 403；
  - 第 51 題回 422；
  - 應被引用的文件不屬於這個助理時，回 422；
  - 試問低於門檻時不呼叫模型。
- **依賴：** 無。

#### Slice 2｜全部重跑與結果歷史

- **內容：**
  - 實體 `AssistantTestRun`、`AssistantTestResult`。
  - job `assistants.run-test-set`；`POST /api/v1/assistants/{id}/test-runs` 排入一次重跑。
  - `GET …/test-runs` 列出歷史；`GET …/test-runs/{runId}` 看逐題結果。
  - 判斷規則寫在 Application 層，例如 `AssistantTestJudge`。
  - 新增 `ModelInvocationPurpose.AssistantTest`。
- **驗收**（整合測試，使用 `FakeChatClient`）：
  - 預期 `company-data`，而且引用對的文件：通過；
  - 引用錯的文件：`missing-document`；
  - 預期 `no-result` 卻回答了：`kind-mismatch`；
  - 已有排隊中的重跑時，再次觸發只把 `RerunRequested` 設成 true；
  - job 失敗時，run 標成 `failed`，不影響其他助理。
- **依賴：** Slice 1。

#### Slice 3｜自動重跑與驗收狀態

- **內容：**
  - 以下操作會排入受影響助理的重跑：版本確認生效（生效日在未來時設定 `RunAfter`）、文件停用或恢復、助理的設定或來源改變。
  - `GET /api/v1/assistants`、`…/settings` 帶出 `acceptanceStatus`。
- **驗收：**
  - 確認生效一個版本，只排入連接了該知識庫、而且有題組的助理；
  - 生效日在未來時，`RunAfter` 等於生效日；
  - 狀態依第 3 節的規則推導，四種狀態各有測試。
- **依賴：** Slice 2。

### 軌道 B｜處理事項與轉人工（後端）

#### Slice 4｜處理事項

- **內容：**
  - 實體 `AssistantIssue`、`AssistantIssueEvent`。
  - 新權限 `handle-assistant-issues`。
  - Endpoint：
    - `POST /api/v1/assistants/{id}/issues`：從測試結果建立；
    - `GET /api/v1/issues`：依角色篩選，分為我擁有的助理、指派給我的、我轉交的；
    - `GET`／`PATCH /api/v1/issues/{id}`：指派、改狀態、加註；
    - `GET /api/v1/issues/summary`：首頁卡片用。
- **驗收：**
  - 三種角色各自只看得到該看的處理事項；
  - 其他人查詢時，回應與「不存在」相同；
  - 指派對象必須具備 `handle-assistant-issues`，而且屬於同一個組織；
  - 每次狀態變更都留下處理紀錄。
- **依賴：** Slice 2。

#### Slice 5｜轉人工

- **內容：**
  - `POST /api/v1/assistants/{id}/chat/handoffs`：body 是問題、回覆 id 與成員確認的分享內容。
  - 後端從該成員自己的對話讀出那一問一答，再存成複本，**不採用前端送來的文字**，避免偽造內容。
  - 不保存對話的助理，改用本次請求帶上的問答，並記錄它不可驗證。
  - 負責人預設是助理擁有者。
- **驗收：**
  - 成員只能轉交自己的訊息；
  - 處理事項不含對話串 id，處理人無法進入對話；
  - 擁有者看得到複本，但仍然看不到該成員的其他對話；
  - 轉交人看得到處理狀態。
- **依賴：** Slice 4。

### 軌道 C｜營運追蹤（後端）

#### Slice 6｜回覆結果紀錄與營運彙總

- **內容：**
  - 實體 `AnswerOutcome`：在對話串流完成、試問、題組重跑時寫入，不含內容。
  - `GET /api/v1/assistants/{id}/analytics`：依日期彙總，取代前端的 mock。
  - `GET /api/v1/operations/summary`：組織層級，涵蓋：
    - 各助理的查無資料率與引用被拒絕率；
    - 最常被引用的文件；
    - 知識庫處理失敗與逾期待確認；
    - 處理事項的待處理數與平均處理時間。
- **驗收：**
  - 用一個測試斷言 `AnswerOutcome` 沒有任何可以放文字的欄位；
  - 彙總數字與寫入的列一致；
  - 非 `manage-assistants` 的帳號拿不到組織層級的彙總；
  - 保存期限跟隨對話保存設定（withdrawal-and-retention ADR），不過這個表不含內容，是否另設期限列入第 7 節決定 D。
- **依賴：** 無。可以和軌道 A 平行。

### 軌道 D｜前端

#### Slice 7｜助理詳情的「驗收」分頁

題組 CRUD、匯入與匯出、全部重跑（顯示排隊中與執行中）、逐題結果、歷史、驗收狀態徽章（也顯示在我的助理清單）。
- **依賴：** Slice 1–3。

#### Slice 8｜處理事項頁與首頁卡片

`/app/issues` 的清單、篩選、詳情、指派與處理紀錄；首頁「待處理事項」卡片改接 summary；從測試結果一鍵建立處理事項。
- **依賴：** Slice 4、7。

#### Slice 9｜對話中的「轉給專人」

同意畫面清楚列出分享內容與分享對象；`/app/activity` 顯示「我轉交的處理事項」。
- **依賴：** Slice 5。

#### Slice 10｜使用統計與營運追蹤畫面

助理詳情的使用統計、組織層級的營運追蹤頁，資料都是彙總後的數字，不含內容。
- **依賴：** Slice 6。

### 軌道 E｜品質

#### Slice 11｜API 模式 E2E

新增 `assistant-acceptance-api.cy.ts`：
- 建立題組 → 全部重跑 → 看到通過與未通過；
- 從未通過的題目建立處理事項並指派；
- 確認生效一個新版本 → 驗收狀態變成「已過期」並自動重跑；
- 成員轉給專人 → 擁有者在處理事項看到複本。
- **依賴：** Slice 1–10。

---

## 6. 審查發現的對應

| 審查項目 | 在這個里程碑的處理 |
| --- | --- |
| 待補 5：每個助理可保存的常見、例外、應拒答題 | Slice 1、7 |
| 待補 5：試問顯示檢索段落、來源版本與引用 | Slice 1（已建立助理的試問）、7；精靈草稿的試問在 M3 已完成 |
| 待補 5：答錯或查無結果時可以建立處理事項並指派 | Slice 4、8 |
| 待補 5：文件改版或權限變更後重跑 | Slice 3 |
| 待補 5：未通過時保留內部使用，或阻止對外發布 | Slice 3 的驗收狀態；阻止對外發布在 M5 以這個狀態實作 |
| 待補 5：營運指標（處理失敗、過期待審、查無資料、錯誤引用的趨勢），不揭露私人對話 | Slice 6、10 |
| 問題未解決時的處理（轉人工） | Slice 5、9；串接外部客服系統不在範圍內 |
| 營運追蹤：哪些題目查無資料、誰處理、何時重驗 | 題目：題組失敗題與成員轉交的內容。誰處理：處理事項的負責人。何時重驗：重跑歷史與自動重跑 |

---

## 7. 決定事項、風險與待辦

**已決定：** 2026-09-29，負責人選擇 M3 之後先規劃這個里程碑，排在 M4 數據庫之前。

**已決定（2026-09-29，負責人回覆「按建議」）：**

- **A. 處理事項先做成範圍窄的 `AssistantIssue`，不先寫待補功能 6 的工作項目 ADR。**
  - 里程碑 ADR 說兩者「可能是同一概念」，但待補功能 6 排在 M4 之後，還需要表單與結構化紀錄才能定義清楚。
  - 現在先做只涵蓋助理品質與轉人工的處理事項，欄位與狀態機保持通用。已在[里程碑 ADR](../adr/2026-09-25-milestone-order.md) 的「補充（2026-09-29）」寫明：待補功能 6 的 ADR 要決定是否併入。
- **B. 營運追蹤不顯示成員的問題文字。**
  - 「哪些題目常查無資料」只用題組的失敗題與成員主動轉交的內容來呈現。
  - 不採用的替代方案：在 k 位以上不同帳號問過同一題時才顯示題目。它仍有隱私風險，也要處理語意相近的問題。
- **C. 新增權限 `handle-assistant-issues`，作為可以被指派處理事項的資格。**
  - 不採用的替代方案：只有助理擁有者能處理。實務上客服主管與助理建立者常常不是同一人。
- **D. `AnswerOutcome` 的保存期限。**
  - 跟隨組織的對話保存設定：預設永久；組織設定 N 天時，一併刪除。
  - 雖然它不含內容，但仍是使用紀錄。
- **E. 里程碑名稱。**
  - 稱為「M3.5 助理驗收與維護」，文件與 issue 前綴用「M3.5｜」，不去更動 M4、M5 的既有編號。

**待使用者提供**（沿用[負責人待辦事項](2026-09-26-m2-owner-action-items.md)）：

- OpenAI 金鑰。在使用 Fake 模型時，題組的通過與未通過只能驗證流程，無法反映真實的答題品質。
- 真實模型下的題組結果與 `MinScore` 校正，要等金鑰到位才能做。

**技術風險：**

1. **成本**：一次確認生效可能觸發多個助理、各 50 題的重跑。對策：
   - 已有排隊中的重跑時合併，不重複排入；
   - worker 並行數為 1；
   - 用 `ModelInvocation` 記錄 `AssistantTest` 用量；
   - 必要時再加組織的每日上限（第 8 節）。
2. **模型輸出不穩定**：同一題在不同次重跑可能得到不同的回覆類型，例如貼近門檻的題目。對策：
   - 判斷只看結構；
   - 結果顯示分數與門檻，讓擁有者看出是「接近門檻」的題目；
   - 重跑時固定 `temperature=0`，如果供應商支援。
3. **轉人工的內容來源**：必須從該成員自己的對話讀出問答，不採用前端送來的文字，避免偽造內容或藉此讀取別人的對話。
4. **前端範圍比表面看起來大**：analytics 與首頁卡片目前都只是空殼，要從零做。

---

## 8. 不在這個里程碑範圍

- M5 對外發布的閘門實作本身：這個里程碑只提供驗收狀態。
- 串接外部客服系統或工單系統、Email 或 LINE 通知、SLA 與自動升級。
- 待補功能 6 的跨部門工作項目。
- 組織的每日模型用量上限與費用報表：這個里程碑只記錄用量。
- 由文件自動產生題組或 FAQ 草稿，這需要模型生成。
- 題目的語意分群、問題熱門度分析：涉及私人對話內容，見第 7 節決定 B。
- 真實模型的評測與門檻校正：要等 OpenAI 金鑰。
