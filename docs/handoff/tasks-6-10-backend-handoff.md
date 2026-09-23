# Tasks 6–10 後端介接交付文件（SME AI 助理 Demo）

適用範圍：`apps/admin` 前端 Demo 的五個功能區（建立助理精靈、知識庫、資料庫與追蹤、終端對話與同意、發布管道），對應計畫文件 `docs/plans/2026-09-18-sme-ai-assistant-ux-demo.md` 的 Task 6–10，以及 commit `2d09558`、`3232686`、`6987876`、`7932591`、`797be11`。

文件目的：讓後端工程師知道**前端已經假設了什麼**——資料形狀、狀態機、權限規則、驗證規則，以及**哪些行為是假的**、必須由後端真正實作。文件內所有型別與規則都可在程式碼中對照（以 `檔案路徑:行號` 標示）。

**這份文件是「功能區的深度細節」。** 全域的驗收清單、外部服務接點與非功能期待在 `docs/handoff/ai-assistant-backend-integration-handoff.md`；全部 54 個 repository 方法的 endpoint／授權／錯誤分類／替換檔案在 `docs/handoff/mock-to-api-mapping.md`；每條路由對應的畫面、狀態與 e2e 在 `docs/handoff/route-screen-matrix.md`。

---

## 1. 總覽

### 1.1 這份 Demo 不是安全機制

前端自己就宣告了這件事，這段字串會顯示在畫面上：

> `apps/admin/src/app/core/repositories/demo-repository.ts:478-479`
> `DEMO_SECURITY_NOTICE = '此 mock 僅用於視覺 Demo，不提供真實驗證與資料安全邊界，不使用真實資料，也不連接真實 AI。'`

計畫與設計文件的對應聲明：

- `docs/plans/2026-09-18-sme-ai-assistant-ux-demo.md:5`：「Demo 的帳號隔離與權限只用於視覺驗證，不宣稱具備正式安全性。」
- `docs/plans/2026-09-18-sme-ai-assistant-ux-demo.md:22`：「不建立後端、不連接真實 AI、不傳送真實文件、不保存真實個資。」
- `docs/plans/2026-09-18-sme-ai-assistant-ux-demo.md:629`：完成定義要求「不會把模擬登入、模擬權限或 mock 資料誤呈現為正式安全功能」。
- `docs/plans/2026-09-18-sme-ai-assistant-ux-demo-design.md:15`：「真實登入、AI、文件解析、資料儲存、官網嵌入與 LINE 串接不在此階段實作，改以 hand-off 文件定義正式介接需求。」
- `docs/plans/2026-09-18-sme-ai-assistant-ux-demo-design.md:268-275`：Demo 不實作清單（真實登入與權限驗證、文件解析與 AI 問答、正式資料庫儲存與真實檔案上傳、LINE API 與官網嵌入碼運作、AI 記憶與正式趨勢計算與資料刪除、付款與方案限制）。

**所有目前由前端執行的權限判斷，後端都必須自己再做一次完整的授權檢查。** 前端的檢查只是為了讓畫面呈現正確狀態，任何人都能改寫瀏覽器記憶體與 localStorage。

### 1.2 決策更新：路徑對照

`docs/plans/2026-09-18-sme-ai-assistant-ux-demo.md:11-16` 有一則覆蓋下方所有 Task 路徑的決策更新：獨立的 `frontend/` 工作區已廢止，Demo 已併入 `apps/admin`（commit `1fd2d95`），`apps/admin` 為唯一前端入口。閱讀 Task 6–10 原文時，需套用 `frontend/apps/assistant-demo/src/` → `apps/admin/src/`、`npx nx test assistant-demo` → `npx nx test admin` 等對照。本文件一律使用**現行路徑**。

### 1.3 資料目前存在哪裡

沒有後端。所有狀態存在瀏覽器的 `localStorage`（測試時換成記憶體實作 `apps/admin/src/app/core/repositories/memory-storage.ts:4-16`）。注入設定見 `apps/admin/src/app/core/repositories/tokens.ts:6-19`。

實際使用的 key 格式（前綴一律 `sme-demo:`）：

| localStorage key | 內容 | 是否依帳號隔離 | 定義位置 |
| --- | --- | --- | --- |
| `sme-demo:assistant-draft:<accountId>` | 建立助理精靈的草稿（`{ version: 1, savedAt, draft }`） | 是，key 內含 accountId | `mock-demo-repository.ts:173`、寫入 `:1012`、讀取 `:993` |
| `sme-demo:created-assistants` | 精靈建立出來的助理設定陣列（`AssistantConfigurationView[]`） | 否，靠物件內的 `ownerAccountId` 過濾 | `mock-demo-repository.ts:174`、寫入 `:1066`、讀取 `:2171` |
| `sme-demo:knowledge:<knowledgeBaseId>` | 該知識庫的文件清單與分享設定（`{ version: 1, documents, sharing }`） | 否，靠知識庫的 `ownerAccountId` 過濾 | `mock-demo-repository.ts:231`、寫入 `:2098`、讀取 `:2084` |
| `sme-demo:created-databases` | 由模板建立的資料庫（`{ view, collection }[]`） | 否，靠 `view.ownerAccountId` 過濾 | `mock-demo-repository.ts:295`、讀取 `:1985` |
| `sme-demo:database-fields:<databaseId>` | 表單設計儲存結果（`{ version: 1, savedAt, fields }`） | 否 | `mock-demo-repository.ts:296`、寫入 `:1312`、讀取 `:2018` |
| `sme-demo:chat:<accountId>:<assistantId>` | 這個帳號與這個助理的**多段對話**（`{ version: 2, threads }`，每段含 `id` / `title` / `titleSource` / `createdAt` / `updatedAt` / `messages`） | 是，key 內含 accountId | 前綴 `mock-demo-repository.ts:333`、型別 `:341-362`、組法 `:1690-1692`、寫入 `:1719-1727`、讀取 `:1710-1717` |
| `sme-demo:chat:<visitorId>:<assistantId>` | **未登入訪客**與這個助理的多段對話，結構與上一列完全相同 | 是，key 內含 visitorId。**而且不在 localStorage**——見下方說明 | 切換儲存 `mock-demo-repository.ts:1695-1697`、注入 `tokens.ts:15-17` |
| `sme-demo:chat-records` | 由對話送出的結構化紀錄（`DatabaseRecordFixture[]`） | 否，靠 `subjectId`（`subject-<accountId>` 或 `subject-<visitorId>`）區分 | `mock-demo-repository.ts:335`、寫入 `:1599`、讀取 `:1820` |
| `sme-demo:publishing:<assistantId>` | 三個發布管道的設定（`PublishingRecord`） | 否，靠助理的 `ownerAccountId` 過濾 | `mock-demo-repository.ts:334`、寫入 `:2349`、讀取 `:2339` |

另有一個與 Demo 無關的舊登入殘留：`apps/admin/src/app/core/auth/auth.service.ts:4` 的 `sa.auth.session`，不屬於 Demo 資料流，請後端忽略。

**注意**：只有草稿與對話兩種 key 真的把 accountId 編進 key。其他資料的「隔離」是在讀取時用物件欄位過濾出來的，任何人清掉或改寫 localStorage 都能繞過。

**對話 key 的版本**：`version: 1`（一個 (帳號, 助理) 只有一段對話）是舊格式，讀取時會就地包成一段 thread 再往下走（`normalizeStoredThreads()`，`mock-demo-repository.ts:391-414`），**不會回寫**，等下一次寫入才落地成 `version: 2`。正式後端不需要沿用這個遷移，但要知道舊 Demo 資料長這樣。

**沒有落地的對話**：助理的規則關閉「保存自己的對話」時，對話**完全不進 localStorage**，只留在 `MockDemoRepository` 實例的記憶體 Map（`mock-demo-repository.ts:493-497`），重新整理就消失。這是 `AssistantConfigurationView.keepOwnConversations`（`assistant.model.ts:52-58`）唯一會改變儲存行為的設定。

### 1.4 帳號隔離目前如何被模擬

1. 「登入」只是選擇三個固定帳號之一：`apps/admin/src/app/core/session/demo-session.service.ts:31-34` 的 `switchAccount()` 把 `activeAccountId` 寫進**記憶體 signal**（`:25`），並清空畫面狀態。
2. 路由守衛只檢查該 signal 有沒有值：`apps/admin/src/app/core/session/demo-session.guard.ts:5-8`，沒有就導到 `/login`。
3. **重新整理頁面即登出**——signal 不落地。這是後端接手時第一個要補的東西（見第 9 節）。
4. 每一個 repository 方法都把 `viewerAccountId: AccountId` 當成**第一個參數**由呼叫端傳入（例：`demo-repository.ts:210-212`）。正式 API 不可以這樣做：viewer 必須由 session／token 決定，絕不能從請求參數取得。

三個固定帳號與其權限（`apps/admin/src/app/core/repositories/demo-seed.ts:72-95`）：

| accountId | displayName | role | permissions |
| --- | --- | --- | --- |
| `account-smb-admin` | 安心商行管理者 | `smb-admin` | `manage-assistants`、`manage-data-sources`、`manage-publishing`、`read-consented-submissions` |
| `account-internal-employee` | 安心商行客服同仁 | `internal-employee` | `use-shared-assistants` |
| `account-external-customer` | 外部客戶 | `external-customer` | `submit-authorized-forms`、`read-own-tracking` |

型別定義在 `apps/admin/src/app/core/domain/account.model.ts:1-37`（`AccountId` 目前是三個字面值的 union，正式 API 應改為一般 id 字串——列入 Open questions）。

### 1.5 共用回應契約

所有讀取類方法都回傳同一個 union（`apps/admin/src/app/core/repositories/demo-repository.ts:86-113`）：

```ts
type RepositoryView<T> =
  | { status: 'ready';             data: T }
  | { status: 'loading' }
  | { status: 'partial-failure';   data: T; unavailable: readonly RepositoryUnavailableResource[]; message: string }
  | { status: 'permission-denied'; reason: RepositoryPermissionDeniedReason; message: string };
```

- `RepositoryUnavailableResource` 目前只有 `'knowledge-sync'`（`demo-repository.ts:76`），由 `partial-failure` 情境產生（`mock-demo-repository.ts:2265-2271`）。
- `loading` 與 `partial-failure` 目前**只由 Demo 情境切換器產生**，不是真實的網路狀態（`mock-demo-repository.ts:2253-2275`）。正式實作中 `loading` 由 HTTP 請求生命週期取代，`partial-failure` 則對應「主資料成功但某個下游資源失敗」的部分降級回應。

建議的 HTTP 對應：

| RepositoryView status | 建議 HTTP | 說明 |
| --- | --- | --- |
| `ready` | `200 OK`，body 即 `data` | — |
| `loading` | 不需要對應（改由請求狀態表達） | 前端自行管理 pending |
| `partial-failure` | `200 OK`，body 加上 `unavailable[]` 與 `message` | 主要資料仍需回傳 |
| `permission-denied` | `403 Forbidden`，body `{ reason, message }` | **不存在與無權限必須回傳完全相同的內容**（見 1.6） |
| `validation-failed` | `422 Unprocessable Entity`，body `{ errors, message }` | 各區的 errors 形狀不同，見各節 |

### 1.6 permission-denied 的 reason union 與「不得洩漏資源名稱」

`demo-repository.ts:78-89` 定義了十一個 reason：

| reason | 出現時機 | 訊息（程式碼中的字串） | 位置 |
| --- | --- | --- | --- |
| `scenario` | Demo 情境切換器強制模擬 | 此情境用於預覽權限不足的畫面狀態。 | `mock-demo-repository.ts:2259-2262` |
| `private-conversation` | 讀取非本人的私人對話 | 私人對話內容僅限對話所屬帳號查看。 | `mock-demo-repository.ts:612-615` |
| `assistant-configuration` | 非擁有者讀取助理設定／資料來源／匿名統計 | 只有助理擁有者可查看資料來源設定。／只有助理擁有者可查看匿名使用摘要。 | `mock-demo-repository.ts:553-556`、`:703-706` |
| `authorized-form` | 授權表單提交條件不符 | 只有已同意授權的外部客戶可提交這份表單。 | `mock-demo-repository.ts:657-660` |
| `assistant-draft` | 帳號沒有 `manage-assistants` | 只有可管理助理的帳號可以建立助理。 | `mock-demo-repository.ts:2247-2250` |
| `knowledge-base` | 知識庫不存在或非擁有者 | 你沒有這個知識庫的存取權限，或它已不存在。 | `mock-demo-repository.ts:2160-2163` |
| `database` | 資料庫不存在或非擁有者／無建立權限 | 你沒有這個資料庫的存取權限，或它已不存在。／只有可管理資料來源的帳號可以建立資料庫。 | `mock-demo-repository.ts:2069`、`:2064` |
| `database-records` | 非指定資料管理者讀取收集紀錄 | 只有指定的資料管理者可以查看收集紀錄。 | `mock-demo-repository.ts:1344-1347` |
| `assistant-use` | 助理不存在或無使用權限 | 你沒有使用這個助理的權限，或它已不存在。 | `mock-demo-repository.ts:1634` |
| `chat-thread` | 對話不存在、屬於其他帳號，或助理根本不保存對話 | 找不到這段對話，或它不屬於你的帳號。 | `mock-demo-repository.ts:1678-1680` |
| `publishing` | 助理不存在或非擁有者 | 你沒有這個助理的發布設定權限，或它已不存在。 | `mock-demo-repository.ts:2322` |

**硬規則**：「資源不存在」與「沒有權限」必須回傳**完全相同**的 reason 與 message，且訊息內**不得包含資源名稱、擁有者或任何可用來推測存在性的內容**。程式碼中有明確註解：`mock-demo-repository.ts:2158-2159`（知識庫）、`:2311`（發布）、`:1622`（對話使用）；契約註解見 `demo-repository.ts:255-258`、`:332-335`、`:374-377`、`:403-405`。設計文件依據：`docs/plans/2026-09-18-sme-ai-assistant-ux-demo-design.md:246`。

實作建議：後端對這幾類資源一律以 `403` + 固定文案回應，不要用 `404` 區分，也不要在 log 以外的地方回傳資源名稱。

---

## 2. 建立助理精靈（Task 6）

前端位置：`apps/admin/src/app/features/assistants/assistant-wizard/`，狀態集中在 `assistant-draft.store.ts`。

### 2.1 前端呼叫的方法 → 建議 endpoint

| repository 方法 | 前端呼叫位置 | 建議 REST endpoint | 成功回應 | 失敗回應 |
| --- | --- | --- | --- | --- |
| `listAssistantTemplates()` | `assistant-draft.store.ts:98` | `GET /api/v1/assistant-templates` | `200` `AssistantTemplateView[]` | — |
| `listConnectableSources(viewer)` | `assistant-draft.store.ts:106` | `GET /api/v1/connectable-sources` | `200` `ConnectableSourceView[]` | `403 assistant-draft`（無 `manage-assistants` 時目前回空陣列，見 2.5） |
| `listTrialQuestions()` | `assistant-draft.store.ts:115` | `GET /api/v1/trial-questions` | `200` `TrialQuestionView[]` | — |
| `previewTrialAnswer(viewer, request)` | `assistant-draft.store.ts:245` | `POST /api/v1/assistant-drafts/trial-answers` | `200` `TrialAnswerView` | `403 assistant-draft` |
| `getAssistantDraft(viewer)` | `assistant-draft.store.ts:318`、`features/home/home-page.component.ts:37` | `GET /api/v1/assistant-drafts/me` | `200` `SavedAssistantDraftView \| null` | `403 assistant-draft` |
| `saveAssistantDraft(viewer, draft)` | `assistant-draft.store.ts:305` | `PUT /api/v1/assistant-drafts/me` | `200` `SavedAssistantDraftView` | `403 assistant-draft` |
| `discardAssistantDraft(viewer)` | 由 `createAssistantFromDraft` 成功後內部呼叫（`mock-demo-repository.ts:1070`） | `DELETE /api/v1/assistant-drafts/me` | `204` | 目前**沒有回傳值**（`demo-repository.ts:322`），後端可自行定義 |
| `createAssistantFromDraft(viewer, draft)` | `assistant-draft.store.ts:278` | `POST /api/v1/assistants` | `201` `AssistantConfigurationView` | `422 validation-failed`（`AssistantDraftFieldError[]`）／`403 assistant-draft` |

viewer 一律改由 session 決定，不放在 path 或 body。

### 2.2 請求／回應型別

草稿本體（`apps/admin/src/app/core/domain/assistant-draft.model.ts:49-60`）：

```ts
interface AssistantDraft {
  templateId: AssistantTemplateId | null;   // 可選（可為 null）
  name: string;                             // 必填（trim 後不可為空）
  purpose: string;                          // 必填
  tone: AssistantTone;                      // 必填，預設 'friendly'
  audience: AssistantAudience | null;       // 必填（不可為 null）
  roleInstructions: string;                 // 可選（進階，預設收合）
  sources: readonly AssistantSourceReference[]; // 必填，至少一筆
  rules: AssistantAnswerRules;              // 必填
  testedQuestionIds: readonly TrialQuestionId[]; // 必填，至少一筆
  currentStep: AssistantWizardStep;         // 必填
}
```

回答規則（`assistant-draft.model.ts:34-42`）：

```ts
interface AssistantAnswerRules {
  knowledgeScope: AssistantKnowledgeScope;  // 必填
  refusalMessage: string;                   // 必填（trim 後不可為空）
  showCitations: boolean;                   // 必填
  keepOwnConversations: boolean;            // 必填
  dataWriteDatabaseId: DatabaseId | null;   // 可選
  dataWritePurpose: string;                 // 有 dataWriteDatabaseId 時必填
  periodicReport: PeriodicReportSchedule;   // 必填
}
```

空草稿預設值見 `assistant-draft.model.ts:138-159`，預設拒答訊息 `DEFAULT_REFUSAL_MESSAGE` 在 `:135-136`。

儲存後回傳（`assistant-draft.model.ts:62-65`）：`{ draft: AssistantDraft; savedAt: string }`，`savedAt` 是 ISO 8601 字串（`mock-demo-repository.ts:1009`）。

建立結果（`apps/admin/src/app/core/domain/assistant.model.ts:44-59`）：

```ts
interface AssistantConfigurationView {
  id: AssistantId;                 // 目前格式 `assistant-created-<毫秒時間戳>`，見 assistant.model.ts:9-12
  ownerAccountId: AccountId;
  name: string;
  purpose: string;
  status: AssistantStatus;         // 建立後固定為 'ready'，見 mock-demo-repository.ts:1054
  audience: AssistantAudience;
  sharedWithAccountIds: readonly AccountId[];  // 建立後為空陣列，見 :927
  knowledgeBaseIds: readonly KnowledgeBaseId[];
  databaseIds: readonly DatabaseId[];
  keepOwnConversations: boolean;   // 直接取自 draft.rules.keepOwnConversations，見 mock-demo-repository.ts:1063
}
```

`keepOwnConversations` 是**規則中唯一會改變執行期行為的一項**：false 時使用者與這個助理的對話完全不落地，也不會出現在對話紀錄側欄（見第 5.4 節）。其餘規則（`knowledgeScope` / `refusalMessage` / `showCitations` / `dataWrite*` / `periodicReport`）目前只被保存進草稿，終端對話並不讀取它們——這是第 2.6 節與第 5.6 節之間的落差，正式版必須把規則接到真正的回答流程上。舊的 `sme-demo:created-assistants` 資料沒有這個欄位時一律視為 true（`mock-demo-repository.ts:2185-2189`）。

試問請求／回應（`assistant-draft.model.ts:129-133`、`:111-127`）：

```ts
interface TrialAnswerRequest {
  questionId: TrialQuestionId;
  sources: readonly AssistantSourceReference[];
  rules: AssistantAnswerRules;
}

type TrialAnswerView =
  | { kind: 'company-data';      questionId: TrialQuestionId; text: string; citation: TrialCitationView | null }
  | { kind: 'general-knowledge'; questionId: TrialQuestionId; text: string }
  | { kind: 'no-answer';         questionId: TrialQuestionId; text: string };
```

### 2.3 列舉值與中文顯示名稱

四個步驟（`assistant-draft.model.ts:8-15`，顯示文案 `assistant-wizard-page.component.ts:33-36`）：

| union 值 | 步驟標籤 | 標題 |
| --- | --- | --- |
| `purpose` | 用途 | 選擇用途 |
| `sources` | 資料來源 | 連接資料來源 |
| `rules` | 回答規則 | 設定回答與記錄規則 |
| `test` | 試問確認 | 試問確認 |

用途模板 id（`assistant-draft.model.ts:17-23`）：`answer-customer-questions`、`search-company-data`、`onboard-new-employees`、`collect-periodic-reports`、`compare-changes`、`blank`（顯示標題與描述由 `demo-seed.ts` 的 `assistantTemplates` 提供，後端應改由設定檔或資料表供應）。

其他列舉：

| 型別 | union 值 | 中文顯示 | 定義 |
| --- | --- | --- | --- |
| `AssistantTone` | `friendly` / `professional` / `concise` | 親切／專業／精簡（顯示文案在步驟元件內） | `assistant-draft.model.ts:25` |
| `AssistantKnowledgeScope` | `company-data-only` / `allow-general-knowledge` | 嚴格：只依據已連接的資料回答／一般：允許補充一般知識並分區標示 | `assistant-draft.model.ts:27-30` |
| `PeriodicReportSchedule` | `off` / `weekly` / `monthly` | 關閉／每週／每月 | `assistant-draft.model.ts:32` |
| `AssistantAudience` | `account-members` / `authorized-external-customers` / `members-and-external-customers` | 內部團隊／已授權外部客戶／內部團隊與外部客戶 | `assistant.model.ts:16-19`、標籤 `components/assistant-card/assistant-card.component.ts:20-24` |
| `AssistantStatus` | `draft` / `ready` / `published` / `paused` | 草稿／可發布／已發布／已暫停 | `assistant.model.ts:14`、標籤 `assistant-card.component.ts:6-11` |
| `ConnectableSourceStatus` | `ready` / `processing` / `needs-attention` | 可使用／處理中／需要處理 | `assistant-draft.model.ts:77`、標籤 `steps/sources-step/sources-step.component.ts:21-25` |
| `ConnectableSourcePermission` | `owner` / `read-only` | 我建立的・可管理／唯讀連接 | `assistant-draft.model.ts:80`、標籤 `sources-step.component.ts:33-36` |
| `TrialAnswerView['kind']` | `company-data` / `general-knowledge` / `no-answer` | 根據你的資料／一般知識補充／資料中沒有答案 | `assistant-draft.model.ts:111-127`、設計依據 `...-design.md:188-192` |

### 2.4 驗證規則

逐步驗證函式：`assistant-draft.model.ts:176-218`（`validateAssistantDraftStep`）；完整驗證：`:220-226`（`validateAssistantDraft`，等於四步全跑一次）。

| 步驟 | 條件 | `field` | 錯誤訊息 | 行號 |
| --- | --- | --- | --- | --- |
| `purpose` | `name.trim() === ''` | `name` | 請輸入助理名稱。 | `:183-185` |
| `purpose` | `purpose.trim() === ''` | `purpose` | 請用一句話說明助理要幫忙完成的工作。 | `:186-188` |
| `purpose` | `audience === null` | `audience` | 請至少選擇一種使用對象。 | `:189-191` |
| `sources` | `sources.length === 0` | `sources` | 請至少加入一個知識庫或資料庫。 | `:194-196` |
| `rules` | `rules.refusalMessage.trim() === ''` | `refusalMessage` | 請填寫找不到資料時的回覆內容。 | `:199-201` |
| `rules` | `dataWriteDatabaseId !== null && dataWritePurpose.trim() === ''` | `dataWritePurpose` | 寫入資料庫前，請說明收集目的，使用者同意前會看到這段說明。 | `:202-210` |
| `test` | `testedQuestionIds.length === 0` | `trial` | 請至少試問一題，確認回答符合預期。 | `:213-215` |

錯誤格式（`assistant-draft.model.ts:161-173`）：

```ts
type AssistantDraftField = 'name' | 'purpose' | 'audience' | 'sources'
  | 'refusalMessage' | 'dataWritePurpose' | 'trial';
interface AssistantDraftFieldError { field: AssistantDraftField; message: string }
```

`createAssistantFromDraft` 的失敗回應（`mock-demo-repository.ts:1033-1038`）：

```ts
{ status: 'validation-failed', errors: AssistantDraftFieldError[], message: '還有必要設定尚未完成。' }
```

額外的伺服端行為（後端必須保留）：建立時會把草稿裡**已不可連接**的來源過濾掉，而不是報錯（`mock-demo-repository.ts:1042-1047`）。也就是說 `sources` 必須以伺服端當下的可連接清單重新驗證一次。

### 2.5 權限規則

- 讀寫草稿、試問、建立助理，都要求帳號具備 `manage-assistants` 權限（判斷式 `mock-demo-repository.ts:2238-2244`）；不符時回傳 `assistant-draft` 的 permission-denied（`:2247-2250`）。
- `listConnectableSources` 對無權限帳號**回傳空陣列而非 403**（`mock-demo-repository.ts:2202-2205`）。這是目前的實作行為，後端若要改成 403 需同步調整前端。
- 可連接來源只包含 viewer **自己擁有**的知識庫與資料庫（`:1761-1762`、`:1775-1776`）。知識庫標為 `owner`，資料庫一律標為 `read-only`（`:1769`、`:1783`）。
- 草稿依帳號隔離，key 內含 accountId（`mock-demo-repository.ts:993`、`:1012`），另一個帳號永遠看不到。

### 2.6 這一區哪些是假的

1. **試問回答完全來自 fixture**：`previewTrialAnswer` 依 `demo-seed.ts` 的 `trialQuestions` 查表，不呼叫任何 AI（`mock-demo-repository.ts:926-982`）。契約註解：`demo-repository.ts:309`「以固定 fixture 模擬試問回答，不連接真實 AI」。計畫依據：`...-demo.md:458`。
   - 只有當「問題有 `companyAnswer`」且「該知識庫屬於 viewer」且「草稿真的連了那個知識庫」時才回 `company-data`（`:805-820`）。
   - `showCitations` 為 false 時把 citation 設為 null（`:824-836`）——實際引用來源是否存在並未驗證。
   - 否則若允許一般知識且該題有 `generalAnswer`，回 `general-knowledge`（`:837-843`）。
   - 其餘一律回 `no-answer`，且**文字直接用草稿裡的 `refusalMessage`**（`:844-849`）。
2. **助理 id 由時間戳產生**：`assistant-created-<Date.now()>`，衝突時遞增（`mock-demo-repository.ts:2192-2200`）。正式後端請改用伺服端 id，並放寬 `assistant.model.ts:10` 的字面值型別。
3. **「加入資料來源」只建立 connection id，不複製資料**（設計 `...-design.md:100`、計畫 `...-demo.md:312`）。這是刻意的語意，後端應照做。
4. **草稿自動儲存沒有節流或衝突處理**：store 的 `commit()`（`features/assistants/assistant-wizard/assistant-draft.store.ts:300-313`）被套用模板、更新欄位、選擇對象、切換來源、修改規則、切換步驟與首次試問等每一個動作呼叫，每次都整份覆寫（`mock-demo-repository.ts:1008-1015`），沒有 debounce、沒有樂觀鎖、沒有版本比對。多裝置同時編輯會直接互相覆蓋。正式 API 需要 debounce 與版本欄位。
5. 損毀的草稿一律視為「沒有草稿」而不是報錯（`normalizeStoredDraft`，`mock-demo-repository.ts:195-227`）。

---

## 3. 知識庫（Task 7）

前端位置：`apps/admin/src/app/features/knowledge/`。

### 3.1 前端呼叫的方法 → 建議 endpoint

| repository 方法 | 前端呼叫位置 | 建議 REST endpoint | 成功回應 | 失敗回應 |
| --- | --- | --- | --- | --- |
| `listKnowledgeBaseSummaries(viewer)` | `knowledge-list/knowledge-list-page.component.ts:28` | `GET /api/v1/knowledge-bases` | `200` `KnowledgeBaseSummaryView[]` | — |
| `getKnowledgeBaseDetail(viewer, id)` | `knowledge-detail/knowledge-detail-page.component.ts:84` | `GET /api/v1/knowledge-bases/{id}` | `200` `KnowledgeBaseDetailView` | `403 knowledge-base` |
| `addDemoKnowledgeDocument(viewer, kbId)` | `knowledge-detail-page.component.ts:117` | `POST /api/v1/knowledge-bases/{id}/documents`（正式版為檔案上傳） | `201` `KnowledgeDocumentView` | `403 knowledge-base` |
| `advanceKnowledgeDocument(viewer, kbId, docId)` | `knowledge-detail-page.component.ts:159` | **不應存在於正式 API**（見 3.5） | `200` `KnowledgeDocumentView` | `403 knowledge-base` |
| `retryKnowledgeDocument(viewer, kbId, docId)` | `knowledge-detail-page.component.ts:128` | `POST /api/v1/knowledge-bases/{id}/documents/{docId}/retry` | `200` `KnowledgeDocumentView` | `403 knowledge-base` |
| `updateKnowledgeSharing(viewer, kbId, sharing)` | `knowledge-detail-page.component.ts:139` | `PUT /api/v1/knowledge-bases/{id}/sharing` | `200` `KnowledgeSharingView` | `422 validation-failed`（只有 `message`）／`403 knowledge-base` |

### 3.2 請求／回應型別

`apps/admin/src/app/core/domain/knowledge-base.model.ts`：

```ts
interface KnowledgeDocumentView {          // :31-39
  id: KnowledgeDocumentId;                 // `document-${string}`，Demo 產生的是 `document-demo-<時間戳>`
  kind: KnowledgeItemKind;                 // 'document' | 'faq'
  name: string;
  status: KnowledgeDocumentStatus;
  issue: string | null;                    // 只有 partially-readable / failed 有值，其餘必須是 null
  updatedAt: string;                       // ISO 8601
}

interface KnowledgeBaseSummaryView {       // :66-76
  id: KnowledgeBaseId; name: string; purpose: string;
  documentCount: number;                   // kind === 'document' 的數量
  faqCount: number;                        // kind === 'faq' 的數量
  statusCounts: Record<KnowledgeDocumentStatus, number>;  // 五種狀態各自的數量，必填且五個 key 都要有
  sharingScope: KnowledgeSharingScope;
  connectedAssistantNames: readonly string[];  // 只含 viewer 自己擁有的助理名稱
  updatedAt: string;
}

interface KnowledgeBaseDetailView {        // :78-85
  summary: KnowledgeBaseSummaryView;
  documents: readonly KnowledgeDocumentView[];
  connectedAssistants: readonly KnowledgeConnectedAssistantView[];  // { id, name, status }
  sharing: KnowledgeSharingView;
  shareTargets: readonly KnowledgeShareTargetView[];  // { id, displayName }，不含擁有者本人
}

interface KnowledgeSharingView {           // :43-49
  scope: KnowledgeSharingScope;            // 必填
  sharedWithAccountIds: readonly AccountId[];  // 只有 scope === 'specific-accounts' 時有意義
  allowOriginalDownload: boolean;          // 只有 scope === 'public' 時可為 true
}
```

`documentCount` / `faqCount` / `statusCounts` / `updatedAt` 的計算方式見 `mock-demo-repository.ts:2129-2156`；`updatedAt` 取「所有文件 updatedAt 與知識庫 lastSyncedAt 的最大值」（`:2135-2138`）。

### 3.3 文件五種狀態

union 定義 `knowledge-base.model.ts:19-24`，完整清單 `:87-93`，中文標籤與符號 `apps/admin/src/app/features/knowledge/components/knowledge-labels.ts:13-19`：

| union 值 | 中文顯示 | 符號 | 可被引用？ | 需要處理？ |
| --- | --- | --- | --- | --- |
| `queued` | 等待處理 | ○ | 否 | 否 |
| `processing` | 處理中 | ◐ | 否 | 否 |
| `ready` | 可使用 | ✓ | 是 | 否 |
| `partially-readable` | 部分內容無法讀取 | ! | **是**（可引用其餘可讀內容） | 是 |
| `failed` | 處理失敗 | ✕ | 否 | 是 |

判斷函式：`isUsableKnowledgeDocument()` `knowledge-base.model.ts:96-98`、`needsKnowledgeAttention()` `:100-102`。彙總文案（「N 項需要處理」「N 項處理中」「全部可使用」）在 `features/knowledge/components/knowledge-processing.ts:12-21`。

狀態轉移（目前只有前進與重試兩種）：

- 前進：`queued → processing → ready`，其餘狀態不動（轉移表 `mock-demo-repository.ts:246-249`，套用於 `:1143-1152`）。
- 重試：只有 `partially-readable` 或 `failed` 可以回到 `queued` 且清空 `issue`（`mock-demo-repository.ts:1154-1164`）。
- 沒有任何狀態能回到前一步，也沒有刪除文件的方法。

其他列舉：`KnowledgeItemKind` = `document`（文件）／`faq`（FAQ），標籤 `knowledge-labels.ts:21-24`。

### 3.4 分享範圍與權限規則

三種範圍（`knowledge-base.model.ts:41`，標籤 `knowledge-labels.ts:26-30`，說明文案 `components/sharing-panel/sharing-panel.component.ts:25-31`）：

| union 值 | 中文顯示 | 說明 |
| --- | --- | --- |
| `private` | 只有我 | 只有你可以查看、管理與連接。 |
| `specific-accounts` | 指定帳號／團隊 | 需至少選一個帳號 |
| `public` | 公開分享 | 任何帳號都能連接到自己的助理。 |

權限規則：

- 所有知識庫方法都要求 viewer 是**擁有者**（`ownedKnowledgeBase()`，`mock-demo-repository.ts:2072-2081`）。非擁有者與不存在回傳同一個 `knowledge-base` permission-denied（`:2160-2163`）。
- `connectedAssistants` 只列出 viewer 自己擁有且有連到這個知識庫的助理（`mock-demo-repository.ts:1096-1101`）——不會揭露其他帳號連了這個知識庫。
- `shareTargets` 是「除了自己以外的所有帳號」（`mock-demo-repository.ts:1104-1106`）。正式版需要真正的組織／團隊目錄，且要考慮目錄本身也是敏感資料。
- 儲存分享設定時會過濾掉不在合法目標內的 accountId（`mock-demo-repository.ts:1174-1182`）。
- `allowOriginalDownload` 只有 `public` 時才會被保留為 true，其餘一律強制 false（`mock-demo-repository.ts:1193`）。設計依據：`...-design.md:171-175`。

### 3.5 驗證規則

| 層 | 方法／位置 | 條件 | 回應 |
| --- | --- | --- | --- |
| 前端 | `components/sharing-panel/sharing-panel.component.ts:73` | `scope === 'specific-accounts' && this.selected().length === 0` | 畫面錯誤「請至少選擇一個帳號或團隊，才能使用指定分享。」（`:74`），不送出請求 |
| repository | `updateKnowledgeSharing` | `scope === 'specific-accounts'` 且過濾後的 `sharedWithAccountIds` 為空 | `{ status: 'validation-failed', message: '請至少選擇一個帳號或團隊。' }`（`mock-demo-repository.ts:1182-1187`） |

前端在送出前就會做一次正規化：非 `specific-accounts` 時清空 `sharedWithAccountIds`、非 `public` 時把 `allowOriginalDownload` 設為 false（`sharing-panel.component.ts:78-82`）。後端**不可以**倚賴這個前置處理，必須自己再做一次（repository 就是這樣做的，`mock-demo-repository.ts:1174-1182`、`:1193`）。

注意此區的 validation-failed 形狀與其他區不同：只有 `message`，**沒有 `errors` 陣列**（`demo-repository.ts:125-132`）。

### 3.6 這一區哪些是假的

1. **完全沒有檔案上傳**。`addDemoKnowledgeDocument` 只新增一筆 `queued` 狀態的紀錄，檔名從三個固定示範檔名輪流取用（`DEMO_DOCUMENT_NAMES`，`mock-demo-repository.ts:240-244`；套用於 `:1130`）。契約註解 `demo-repository.ts:340`；計畫 `...-demo.md:359`「檔案加入操作只模擬狀態進度，明確標示不會真正上傳」。畫面上有「Demo：不會真正上傳檔案」標示。
2. **文件處理進度由前端計時器假造**。repository 這一側只是一個每呼叫一次前進一格的狀態機（`NEXT_DOCUMENT_STATUS`，`mock-demo-repository.ts:246-249`）；真正「看起來在處理」的效果來自畫面的遞迴 `setTimeout`：`DEMO_PROCESSING_STEP_MS = 900`（`features/knowledge/knowledge-detail/knowledge-detail-page.component.ts:27`），排程函式 `scheduleStep()`（`:154-169`），由新增文件（`:117-123`）與重試（`:128-134`）啟動，離開頁面時清除（`:91-94`）。正式系統應改為：上傳後回 `queued`，由背景工作推進，前端以輪詢或推播取得狀態——**`advanceKnowledgeDocument` 這個 endpoint 不應該存在於正式 API，前端的計時器也要一併移除**。
   另注意 `updateKnowledgeDocument` 在狀態沒有真的改變時會**跳過寫入、也不更新 `updatedAt`**（`mock-demo-repository.ts:2116-2125`）。
3. **`issue` 文字是 seed 寫死的**（`demo-seed.ts` 的 `knowledgeDocuments`），沒有真正的解析錯誤分類。
4. **文件 id 由時間戳產生**：`document-demo-<Date.now()>`，衝突時遞增（`mock-demo-repository.ts:1120-1128`）。
5. **沒有刪除文件、沒有重新命名、沒有原始檔下載**的方法，即使 `allowOriginalDownload` 這個旗標已經存在於資料模型（`knowledge-base.model.ts:48`）。
6. 文件內容、切塊、索引、向量化全部不存在。

---

## 4. 資料庫、表單與追蹤（Task 8）

前端位置：`apps/admin/src/app/features/databases/`。業務計算集中在 `apps/admin/src/app/core/repositories/database-tracking.ts`（檔頭註解 `:17-20` 明講「畫面元件只顯示這些結果，不自行推導差異」）。

### 4.1 前端呼叫的方法 → 建議 endpoint

| repository 方法 | 前端呼叫位置 | 建議 REST endpoint | 成功回應 | 失敗回應 |
| --- | --- | --- | --- | --- |
| `listDatabaseTemplates(viewer)` | `database-list/database-list-page.component.ts:29` | `GET /api/v1/database-templates` | `200` `DatabaseTemplateView[]` | `403 database` |
| `listDatabaseSummaries(viewer)` | `database-list-page.component.ts:24` | `GET /api/v1/databases` | `200` `DatabaseSummaryView[]` | — |
| `createDatabaseFromTemplate(viewer, input)` | `database-list-page.component.ts:55` | `POST /api/v1/databases` | `201` `DatabaseSummaryView` | `422`（只有 `message`）／`403 database` |
| `getDatabaseDetail(viewer, id)` | `database-detail/database-detail-page.component.ts:78` | `GET /api/v1/databases/{id}` | `200` `DatabaseDetailView` | `403 database` |
| `updateDatabaseFields(viewer, id, fields)` | `database-detail-page.component.ts:111` | `PUT /api/v1/databases/{id}/fields` | `200` `DatabaseFieldView[]` | `422`（`DatabaseFieldError[]`）／`403 database` |
| `previewDatabaseEntry(viewer, id, answers)` | `database-detail-page.component.ts:130` | `POST /api/v1/databases/{id}/entries:preview` | `200` `DatabaseTrialPreviewView` | `422`（`DatabaseFieldError[]`）／`403 database` |
| `getDatabaseTracking(viewer, id)` | `database-detail-page.component.ts:86` | `GET /api/v1/databases/{id}/tracking` | `200` `DatabaseTrackingView` | `403 database-records`（非資料管理者）／`403 database` |

### 4.2 請求／回應型別

`apps/admin/src/app/core/domain/database.model.ts`：

```ts
interface CreateDatabaseInput {            // :118-121
  templateId: DatabaseTemplateId;          // 必填
  name: string;                            // 必填，trim 後 1–40 字
}

interface DatabaseFieldView {              // :58-69
  id: DatabaseFieldId;                     // `field-${string}`
  label: string;                           // 必填，trim 後不可為空，同一表單內不可重複
  type: DatabaseFieldType;                 // 必填，六選一
  required: boolean;                       // 必填
  options: readonly string[];              // 單選／多選必填且至少 2 個；其他類型固定為 []
  scale: DatabaseScaleRange | null;        // 量尺必填；其他類型固定為 null
  unit: string;                            // 只有 number 保留；其他類型固定為 ''
}

interface DatabaseScaleRange { min: number; max: number; minLabel: string; maxLabel: string }  // :51-56

interface DatabaseSummaryView {            // :123-134
  id: DatabaseId; name: string; purpose: string; templateName: string;
  fieldCount: number;
  recordCount: number | null;              // 非指定資料管理者時必須是 null，不得透露數量
  subjectCount: number | null;             // 同上
  connectedAssistantNames: readonly string[];
  updatedAt: string;
}

interface DatabaseDetailView {             // :153-158
  summary: DatabaseSummaryView;
  fields: readonly DatabaseFieldView[];
  connectedAssistants: readonly { id: AssistantId; name: string; status: AssistantStatus }[];
  access: {                                // :147-151
    owner: { id: AccountId; displayName: string };
    dataManagers: readonly { id: AccountId; displayName: string }[];
    viewerIsDataManager: boolean;
  };
}

type DatabaseTrialAnswers =                // :273-275
  Readonly<Partial<Record<DatabaseFieldId, string | readonly string[]>>>;
                                           // 多選傳陣列，其餘傳字串

interface DatabaseTrialPreviewView { saved: false; entries: readonly DatabaseRecordEntryView[] }  // :277-280
```

追蹤資料（`database.model.ts:200-267`）：

```ts
interface DatabaseTrackingView { databaseId: DatabaseId; subjects: readonly TrackedSubjectView[] }  // :264-267

interface TrackedSubjectView {             // :256-262
  id: TrackedSubjectId;                    // `subject-${string}`
  displayName: string;
  records: readonly DatabaseRecordView[];  // 由新到舊，只含明確同意提交的紀錄
  comparison: SubjectComparisonView;
}

type SubjectComparisonView =               // :243-254
  | { status: 'insufficient-records'; recordCount: number; message: string }
  | { status: 'available'; recordCount: number; summary: string; metrics: readonly MetricComparisonView[] };

interface MetricComparisonView {           // :224-241
  fieldId: DatabaseFieldId; label: string;
  first: ComparisonPointView; previous: ComparisonPointView; current: ComparisonPointView;
  changeFromPrevious: number; changeFromFirst: number;
  changeFromPreviousLabel: string; changeFromFirstLabel: string;  // 已格式化的帶號文字
  direction: TrendDirection;               // 'up' | 'down' | 'flat'
  points: readonly ComparisonPointView[];  // 由舊到新
  axis: { min: number; max: number };      // 量尺用量尺上下限；數字用紀錄的極值
  summary: string;                         // 已組好的中文摘要
}
```

紀錄的原始值是一個帶 `type` 判別式的 union（`database.model.ts:171-198`），`label` 是**提交當下的欄位名稱快照**（`:170`）——欄位改名不會回溯修改歷史紀錄。後端必須保留這個快照語意。

### 4.3 列舉值與中文顯示名稱

六種欄位類型（`database.model.ts:32-38`、清單 `:40-47`，標籤 `features/databases/database-labels.ts:3-10`）：

| union 值 | 中文顯示 |
| --- | --- |
| `text` | 文字 |
| `number` | 數字 |
| `date` | 日期 |
| `single-choice` | 單選 |
| `multiple-choice` | 多選 |
| `scale` | 量尺 |

計畫明確的非目標：「不加入條件跳題或公式」（`...-demo.md:396`、設計 `...-design.md:179`、程式碼註解 `database.model.ts:29`）。

其他列舉：

| 型別 | union 值 | 中文顯示 | 定義 |
| --- | --- | --- | --- |
| `DatabaseRecordSource` | `assistant-conversation` / `form-link` | 助理對話／表單連結 | `database.model.ts:168`、標籤 `database-labels.ts:12-15` |
| `DatabaseStatus` | `connected` / `disconnected` / `sync-error` | （對映到來源狀態：可使用／需要處理／需要處理） | `database.model.ts:14`、對映 `mock-demo-repository.ts:478-485` |
| `DatabaseAccessMode` | `read-only`（唯一值） | 唯讀 | `database.model.ts:16` |
| `TrendDirection` | `up` / `down` / `flat` | 上升／下降／持平 | `database.model.ts:222`、判斷 `database-tracking.ts:111` |
| `DatabaseTemplateId` | `template-customer-profile` / `template-periodic-report` / `template-satisfaction` / `template-progress` / `template-blank` | 由 seed 的 `databaseTemplates` 提供名稱與描述 | `database.model.ts:104-109` |

### 4.4 驗證規則

**表單欄位**（`database-tracking.ts:145-174`，儲存前會先正規化 `:131-143`）：

| 條件 | 錯誤訊息 | 行號 |
| --- | --- | --- |
| 欄位數為 0 | `{ fieldId: null, message: '表單至少需要一個欄位。' }` | `:146` |
| `type` 不在六種之內 | 不支援的欄位類型。 | `:150-152` |
| `label` 為空（trim 後） | 請填寫欄位名稱。 | `:153` |
| `label` 重複 | 欄位名稱不可重複。 | `:154` |
| 單選／多選選項少於 2 | 單選或多選至少需要 2 個選項。 | `:156-158` |
| 量尺 min/max 非整數或 `min >= max` | 量尺的最小值必須小於最大值。 | `:160-168` |
| 量尺 `max - min > 10` | 量尺最多 11 個刻度。 | `:169-171` |

正規化行為（`database-tracking.ts:131-143`）：`label` 與 `unit` 會 trim；非選擇類型的 `options` 強制清空；非量尺的 `scale` 強制 null；非數字的 `unit` 強制 `''`；空白選項會被濾掉。**後端必須做同樣的正規化，否則前端會拿到不一致的結構。**

**試填／填答驗證**（`database-tracking.ts:181-234`，`previewDatabaseEntry` 與對話表單共用）：

| 欄位類型 | 規則 | 錯誤訊息（前面加上 `「欄位名稱」`） | 行號 |
| --- | --- | --- | --- |
| 全部 | 必填但空白 | 為必填。 | `:194-198` |
| 全部 | 非必填且空白 | 不報錯，display 記為「未填寫」 | `:196` |
| `number` | `Number(text)` 必須是有限數 | 請輸入數字。 | `:202-207` |
| `date` | 必須符合 `/^\d{4}-\d{2}-\d{2}$/` | 請輸入日期。 | `:208-210` |
| `single-choice` | 必須是 `options` 之一 | 請從選項中選擇。 | `:211-213` |
| `multiple-choice` | 每一項都必須是 `options` 之一 | 請從選項中選擇。 | `:214-217` |
| `scale` | 必須是整數且落在 `[scale.min, scale.max]` | 請選擇 {min} 到 {max} 之間的分數。 | `:218-226` |

錯誤格式（`database.model.ts:95-98`）：`{ fieldId: DatabaseFieldId | null; message: string }`，`fieldId` 為 null 代表整份表單層級的錯誤。

**建立資料庫**（`mock-demo-repository.ts:1227-1236`）：模板不存在 → 「請選擇一個模板。」；名稱空白 → 「請輸入資料庫名稱。」；超過 40 字 → 「資料庫名稱請在 40 個字以內。」。此區的 validation-failed **只有 `message`**（`demo-repository.ts:134-141`）。

### 4.5 權限規則

- 建立資料庫與取得模板需要 `manage-data-sources` 權限（`mock-demo-repository.ts:1203-1208`、`:1225`，判斷式在 `canManageDataSources`）；不符回傳 `database` reason 與「只有可管理資料來源的帳號可以建立資料庫。」（`:2064`）。
- 讀寫單一資料庫需要 viewer 是**擁有者**（`ownedDatabase()`）；不存在與非擁有者回傳同一個訊息（`:1630`）。
- **收集紀錄另外一道關卡**：`getDatabaseTracking` 除了擁有者之外，還要求 viewer 在 `dataManagerAccountIds` 之內，否則回 `database-records`（`mock-demo-repository.ts:1341-1348`）。
- **摘要會依權限遮蔽數量**：非指定資料管理者時 `recordCount` 與 `subjectCount` 必須是 `null`（型別註解 `database.model.ts:129`，實作 `mock-demo-repository.ts:2042-2043`）。後端不可以回傳 0 代替 null——0 也是資訊。
- 追蹤資料**只包含 `consentStatus === 'consented'` 的紀錄**（`consentedRecords()`，`mock-demo-repository.ts:2023-2028`）。設計依據 `...-design.md:209`、`:211-221`。
- 由模板建立資料庫時，建立者自動成為唯一的資料管理者（`mock-demo-repository.ts:1250`）。

### 4.6 這一區哪些是假的

1. **趨勢比較是在 repository 層算好的，不是在畫面元件**：`compareRecords()` 位於 `apps/admin/src/app/core/repositories/database-tracking.ts:63-128`，由 `getDatabaseTracking` 呼叫（`mock-demo-repository.ts:1357`）。畫面只負責顯示 `summary`、`changeFromPreviousLabel` 等已格式化的字串。契約註解 `demo-repository.ts:394-401`；計畫 `...-demo.md:398-400`；設計 `...-design.md:181`「AI 只能把已算好的差異轉成文字，不負責計算數字」。**後端必須提供同樣預先算好的欄位**，否則前端要重寫。
2. **只有 `number` 與 `scale` 類型會產生比較指標**（`database-tracking.ts:75`）；文字、日期、選擇題不會出現在趨勢中。
3. **少於 2 筆紀錄不給趨勢結論**：回 `insufficient-records` 與「目前只有 N 筆紀錄，累積 2 筆以上才會顯示比較與趨勢。」（`database-tracking.ts:65-71`）。設計依據 `...-design.md:245`。
4. **數字格式化寫死 `en-US` locale**（`database-tracking.ts:22`），量尺的單位寫死為「分」（`:93`），持平時顯示「持平」（`:30`）。這些文案若要在地化，後端與前端需要一起決定放在哪一層。
5. **追蹤對象（subject）是硬對映到帳號的**：對話送出的紀錄一律以 `subject-<accountId>` 當作追蹤對象（`mock-demo-repository.ts:1593`、`:1827-1833`），沒有真正的「被追蹤對象」實體。設計文件明確區分這兩個概念（`...-design.md:191`），後端需要真的把它們拆開。
6. **資料庫 id 由時間戳產生**（`database-created-<毫秒>`），與助理相同。
7. **`tableCount` 固定為 1、`status` 固定為 `connected`、`accessMode` 固定為 `read-only`**（`mock-demo-repository.ts:1238-1247`）——沒有真正的外部資料庫連線。
8. 試填明確不建立紀錄：回應中 `saved` 是字面值 `false`（`database.model.ts:278`、`mock-demo-repository.ts:1334`）。

---

## 5. 終端對話與同意流程（Task 9）

前端位置：`apps/admin/src/app/features/assistant-use/`。對話 UI 只有一份實作（`conversation/chat-conversation.component.ts`），外面包兩層薄殼：

| 路由 | 元件 | 外框 |
| --- | --- | --- |
| `/use/:assistantId` | `chat-shell/chat-shell-page.component.ts:30`（`app.routes.ts:123-131`） | 單欄、**沒有**對話紀錄側欄。這個網址會被嵌入客戶官網、也會從 LINE 開啟，所以不能出現工作區外框。**不需要 Demo 身分**：掛的是 `embeddedChatGuard`，未登入訪客直接開得了（見 5.7 節） |
| `/use/:assistantId?embed=1` | 同上，`header` 傳 `'minimal'`（`chat-shell-page.component.ts:21`、`:42`） | 再收起頁首與返回連結，只留下對話本身與一個視覺隱藏的 `<h1>`，可直接放進 iframe。這是**匿名訪客的主要入口** |
| `/app/chat[/:assistantId[/:conversationId]]` | `workspace-chat/workspace-chat-page.component.ts:35`（`app.routes.ts:99-122`） | 工作區內：左側對話紀錄（`conversation-rail/conversation-rail.component.ts:28`），右側同一個對話元件（`header` 傳 `'none'`） |

其餘子元件不變：`message/`、`citation-drawer/`、`inline-form/`、`consent-confirmation/`。

### 5.1 前端呼叫的方法 → 建議 endpoint

| repository 方法 | 前端呼叫位置 | 建議 REST endpoint | 成功回應 | 失敗回應 |
| --- | --- | --- | --- | --- |
| `listChatThreads(viewer, assistantId)` | `workspace-chat-page.component.ts:64` | `GET /api/v1/assistants/{id}/chat/conversations` | `200` `ChatThreadListView` | `403 assistant-use` |
| `createChatThread(viewer, assistantId)` | `workspace-chat-page.component.ts:112` | `POST /api/v1/assistants/{id}/chat/conversations` | `200` `AssistantChatView`（空白的新對話） | `403 assistant-use` |
| `renameChatThread(viewer, assistantId, threadId, title)` | `workspace-chat-page.component.ts:124` | `PATCH /api/v1/assistants/{id}/chat/conversations/{threadId}` | `200` `ChatThreadSummaryView` | `422`（只有 `message`）／`403 assistant-use`／`403 chat-thread` |
| `deleteChatThread(viewer, assistantId, threadId)` | `workspace-chat-page.component.ts:134` | `DELETE /api/v1/assistants/{id}/chat/conversations/{threadId}` | `200` `ChatThreadListView`（剩下的清單） | `403 assistant-use`／`403 chat-thread` |
| `getAssistantChat(viewer, assistantId, threadId?)` | `conversation/chat-conversation.component.ts:121` | `GET /api/v1/assistants/{id}/chat`（`?conversation=` 選填） | `200` `AssistantChatView` | `403 assistant-use`／`403 chat-thread` |
| `sendChatMessage(viewer, assistantId, text, threadId?)` | `chat-conversation.component.ts:158` | `POST /api/v1/assistants/{id}/chat/messages` | `200` `AssistantChatView`（整份對話） | `422`（只有 `message`）／`403 assistant-use`／`403 chat-thread` |
| `reviewChatForm(viewer, assistantId, formId, answers)` | `chat-conversation.component.ts:204` | `POST /api/v1/assistants/{id}/chat/forms/{formId}:review` | `200` `ChatFormReviewView` | `422`（`DatabaseFieldError[]`）／`403 assistant-use` |
| `submitChatForm(viewer, assistantId, submission, threadId?)` | `chat-conversation.component.ts:226` | `POST /api/v1/assistants/{id}/chat/forms/{formId}/submissions` | `200` `AssistantChatView`（含收據訊息） | `422`（`DatabaseFieldError[]`）／`403 assistant-use`／`403 chat-thread` |

注意 `sendChatMessage` 與 `submitChatForm` 都回傳**整份對話**而不是單一訊息（`mock-demo-repository.ts:1534`、`:1617-1619`）。後端可以改回傳增量，但那需要同步改前端。

**`threadId` 是選填的**（契約 `demo-repository.ts:444`、`:453`、`:470`）：省略時 `getAssistantChat` 開啟「最後活動的那一段」、`sendChatMessage` 寫進同一段，沒有任何對話時就開一段新的（`resolveChatTarget()`，`mock-demo-repository.ts:1743-1768`）。`/use` 永遠不傳 `threadId`，所以它的行為與加上側欄之前完全一樣。

**四個對話方法的第一個參數是 `ChatViewerId`，不是 `AccountId`**（`demo-repository.ts:444`、`:453`、`:460`、`:470`）：`getAssistantChat` / `sendChatMessage` / `reviewChatForm` / `submitChatForm` 都接受未登入訪客。對話紀錄側欄那四個方法（`listChatThreads` / `createChatThread` / `renameChatThread` / `deleteChatThread`，`:411`–`:432`）仍然只收 `AccountId`——**訪客沒有對話清單**。規則見 5.7 節。

### 5.2 請求／回應型別

`apps/admin/src/app/core/domain/conversation.model.ts`：

```ts
type ChatThreadId = `chat-thread-${number}`;        // :173 — mock 以流水號產生，正式版請改成不可猜測的 id
type ChatHistoryMode = 'saved' | 'not-saved';      // :179 — not-saved 代表助理的規則關閉了「保存自己的對話」

interface ChatThreadSummaryView {                  // :182-188 — 側欄用，刻意不含任何訊息文字
  id: ChatThreadId; title: string;
  messageCount: number; updatedAt: string;
}

interface ChatThreadListView {                     // :191-199
  assistantId: AssistantId; assistantName: string;
  historyMode: ChatHistoryMode;
  threads: readonly ChatThreadSummaryView[];       // historyMode 為 not-saved 時固定為 []
  historyNotice: string;                           // 側欄要顯示的說明文字
}

interface AssistantChatView {                      // :202-214
  assistantId: AssistantId; assistantName: string; purpose: string;
  threadId: ChatThreadId | null;                   // 還沒建立任何對話、或助理不保存對話時為 null
  title: string;                                   // 這一段對話的標題
  historyMode: ChatHistoryMode;
  welcome: string;                                 // 助理的開場白
  privacyNotice: string;                           // 對話隱私說明
  suggestedPrompts: readonly { id: ChatResponseId; text: string }[];
  messages: readonly ChatMessageView[];
}

type ChatMessageView =                             // :153-165
  | { id: ChatMessageId; author: 'account';   text: string;          createdAt: string }
  | { id: ChatMessageId; author: 'assistant'; reply: ChatReplyView;  createdAt: string };

type ChatReplyView =                               // :123-149
  | { kind: 'company-data';       text: string; citations: readonly ChatCitationView[] }
  | { kind: 'general-knowledge';  text: string; notice: string }
  | { kind: 'no-result';          text: string; nextSteps: readonly string[] }
  | { kind: 'form-request';       text: string; form: ChatFormView }
  | { kind: 'submission-receipt'; text: string; recipient: string; entries: readonly DatabaseRecordEntryView[] };

interface ChatCitationView {                       // :98-105
  id: ChatCitationId; knowledgeBaseName: string; documentName: string;
  excerpt: string; updatedLabel: string;           // YYYY-MM-DD
}

interface ChatFormView {                           // :116-121
  id: DatabaseId; title: string;
  fields: readonly DatabaseFieldView[];
  consent: ChatConsentView;
}

interface ChatConsentView {                        // :107-114 — 送出前必須讓使用者看到的全部內容
  recipient: string;                               // 接收者
  purpose: string;                                 // 收集目的
  viewers: readonly string[];                      // 可查看者
  sensitiveNotice: string;                         // 敏感資料提示
  withdrawalNotice: string;                        // 撤回說明
}

interface ChatFormSubmission {                     // :223-227
  formId: DatabaseId;
  answers: DatabaseTrialAnswers;
  consent: boolean;                                // 必須為 true，否則拒絕
}

interface ChatFormReviewView { formId: DatabaseId; saved: false; entries: readonly DatabaseRecordEntryView[] }  // :217-221
```

新的結果型別在 `demo-repository.ts`：`RenameChatThreadResult = RepositoryView<ChatThreadSummaryView> | ChatValidationFailedView`（`:178-180`），也就是改名的 validation-failed **只有 `message`**，與 `sendChatMessage` 同形（`:161-168`）。

`ChatConsentView` 的五個欄位不是裝飾：設計文件 `...-design.md:201` 與計畫 `...-demo.md:437` 都要求送出前必須顯示接收單位、收集目的、可查看者、敏感資料提示，並提供撤回入口。目前 `recipient` 由資料庫擁有者名稱與資料庫名稱組成，`viewers` 由 `dataManagerAccountIds` 對映帳號名稱（`mock-demo-repository.ts:1957-1959`）。

### 5.3 回答種類（五種）與其他列舉

中文顯示名稱來自 `features/assistant-use/message/chat-message.component.ts:15-21` 的 `REPLY_LABELS`：

| union 值 | 中文顯示 | 必帶欄位 | 型別定義 |
| --- | --- | --- | --- |
| `company-data` | 根據你的資料 | `citations[]`（可展開的引用來源） | `conversation.model.ts:124-128`；設計 `...-design.md:188-192` |
| `general-knowledge` | 一般知識補充 | `notice`（必須以獨立區塊標示，不得混進公司資料） | `:129-133` |
| `no-result` | 查無資料 | `nextSteps[]`（下一步建議） | `:134-138` |
| `form-request` | 需要填寫資料 | `form`（含 consent） | `:139-143` |
| `submission-receipt` | 資料已送出 | `recipient`、`entries[]` | `:144-149` |

**注意兩套「沒有答案」的 union 並不相同**：精靈試問用的是 `TrialAnswerView['kind']` 的 `no-answer`，顯示「資料中沒有答案」（`features/assistants/assistant-wizard/steps/test-step/test-step.component.ts:6-10`）；終端對話用的是 `ChatReplyView['kind']` 的 `no-result`，顯示「查無資料」。後端若要統一，需要同時改兩處型別與兩份標籤表。

其他列舉：`ChatHistoryMode` = `saved` / `not-saved`（`conversation.model.ts:179`）；`ConversationStatus` = `active` / `resolved`（`:19`）；`MessageAuthor` = `account` / `assistant`（`:21`）；`SubmissionConsentStatus` = `consented` / `withdrawn` / `not-consented`（`:41-42`）；`SubmissionTrackingStatus` = `received` / `in-review` / `completed`（`:44`）；`AnalyticsPeriod` 目前只有 `last-7-days`（`:73`）。

`SubmissionConsentStatus` 已經有 `withdrawn` 這個值，但**沒有任何方法能把紀錄改成 withdrawn**——撤回是只存在於文案中的承諾（見 Open questions）。

### 5.4 權限規則

- 使用助理的條件（`canUseAssistant()`，`mock-demo-repository.ts:2299-2309`）：viewer 是擁有者，**或**在 `sharedWithAccountIds` 內，**或** viewer 是 `account-external-customer` 且助理的 audience 不是 `account-members`。最後這條是 Demo 的簡化捷徑，正式版必須改成真正的授權關係。
- 對話依 `(accountId, assistantId)` 隔離，key 內含 accountId（`mock-demo-repository.ts:1690-1692`），**每個 key 底下再分成多段 thread**。`listChatThreads` 只讀取 viewer 自己那把 key（`toThreadListView()`，`:1841-1859`），所以**助理擁有者不但讀不到別人的對話內容，連別人有幾段對話、叫什麼名字都看不到**——這是計畫 `...-demo.md:449` 與設計 `...-design.md:202-207` 的硬性要求。
- **threadId 一律當成未經驗證的輸入**：`getAssistantChat` / `renameChatThread` / `deleteChatThread` 都先確認助理可用（`assistant-use`），再在 viewer 自己的儲存裡找那段對話；找不到與屬於別人回傳**同一則** `chat-thread`（`mock-demo-repository.ts:1678-1680`），訊息不含標題也不透露是否存在。後端請照做，不要用 `404` 區分。
- 擁有者端只拿得到**不含對話文字的匿名統計**：`getAssistantAnalytics` 回傳 `conversationCount` / `resolvedCount` / `helpfulRatingPercent`（`conversation.model.ts:75-81`）。算法已改成「**所有帳號中有訊息的對話段數**」（原本是「有對話的帳號數」），並保留明確註解「不讀取任何對話文字」（`mock-demo-repository.ts:1805-1818`）。標題也不會進統計。
- 助理的規則關閉「保存自己的對話」（`AssistantConfigurationView.keepOwnConversations`，`assistant.model.ts:52-58`）時：`listChatThreads` 回傳 `historyMode: 'not-saved'` + 空清單 + 說明文字，而不是空清單而已；`renameChatThread` / `deleteChatThread` 一律 `chat-thread`（沒有東西可以改或刪）；對話本身變成**單一段暫時對話**，不寫入儲存（`keepsConversations()`，`:1620-1622`）。
- 對話表單只在助理**真的連了那個資料庫**時才提供（`chatForm()`，`mock-demo-repository.ts:1943-1946`），且必須是 fixture 中被設定為 `form-request` 的資料庫（`chatFormTarget()`，`:1966-1978`）。
- 送出的紀錄之後只有**指定資料管理者**看得到（透過 `getDatabaseTracking` 的 `database-records` 檢查，第 4.5 節）。
- 未登入訪客走的是另一條判斷（`chatAssistant()`，`mock-demo-repository.ts:1653-1661`），規則見 5.7 節。

### 5.5 驗證規則

| 方法 | 條件 | 回應 | 行號 |
| --- | --- | --- | --- |
| `sendChatMessage` | `text.trim()` 為空 | `{ status: 'validation-failed', message: '請先輸入問題。' }` | `mock-demo-repository.ts:1508-1510` |
| `sendChatMessage` | 長度 > 500（`MAX_QUESTION_LENGTH`，`:328`） | `{ status: 'validation-failed', message: '問題請在 500 個字以內。' }` | `mock-demo-repository.ts:1511-1516` |
| `renameChatThread` | 新名稱 trim 後為空 | `{ status: 'validation-failed', message: '請輸入對話名稱。' }` | `mock-demo-repository.ts:1425-1427` |
| `renameChatThread` | 長度 > 60（`MAX_THREAD_TITLE_LENGTH`，`:330`） | `{ status: 'validation-failed', message: '對話名稱請在 60 個字以內。' }` | `mock-demo-repository.ts:1428-1433` |
| `reviewChatForm` | 欄位驗證失敗（共用 `evaluateTrial`） | `{ status: 'validation-failed', errors, message: '還有欄位需要修正。' }` | `:1526-1532` |
| `submitChatForm` | 欄位驗證失敗 | 同上 | `mock-demo-repository.ts:1569-1575` |
| `submitChatForm` | **`consent !== true`** | `{ status: 'validation-failed', errors: [{ fieldId: null, message: '請先勾選同意，才能送出資料。' }], message: '尚未同意，資料沒有送出。' }` | `mock-demo-repository.ts:1576-1582` |

改名的權限檢查在驗證**之前**（順序見 `:1396-1402` 與 `:1404-1412`）：別人的對話一律 `chat-thread`，不會先回「名稱太長」而洩漏那段對話存在。

同意勾選是在**欄位驗證通過之後**才檢查的（順序見 `:1547` 與 `:1555`），所以使用者會先看到欄位錯誤、修正完才會被要求勾選同意。後端請保留這個順序，否則畫面訊息會錯位。

`sendChatMessage` 與 `renameChatThread` 的 validation-failed 只有 `message`（`demo-repository.ts:161-168`、`:178-180`），而 `reviewChatForm` / `submitChatForm` 帶 `DatabaseFieldError[]`（`:170-176`）。這兩種形狀不同，不要統一。

### 5.6 這一區哪些是假的

1. **所有回覆都來自 fixture，完全沒有 LLM**。契約註解 `demo-repository.ts:450`；計畫 `...-demo.md:458`「未知問題回覆 Demo 拒答，不模擬真正 LLM」。
   - 對應方式是**關鍵字比對**：把問題去除所有空白後（`mock-demo-repository.ts:1887`），逐一檢查 fixture 的 `matchers`；任一組關鍵字全部出現即算命中（`:1889`）。fixture 定義在 `apps/admin/src/app/core/repositories/demo-seed-chat.ts:58` 起，結構註解在 `:27-34`。
   - 命中後還要通過 `fixtureReply()` 的來源檢查（`mock-demo-repository.ts:1909-1940`）：`company-data` 必須至少有一則引用來自助理**真的連了的知識庫**，否則視為未命中（`:1927`）；`general-knowledge` 必須該助理的 profile 允許（`:1929-1934`，profile 在 `demo-seed-chat.ts:42-56`）；`form-request` 必須助理連了該資料庫。
   - 都沒命中就回 `no-result`，`nextSteps` 是三條寫死的建議（`mock-demo-repository.ts:1898-1906`）。
2. **引用來源是 fixture 寫死的文件名與摘錄**（`demo-seed-chat.ts:66-76` 等），`updatedLabel` 也是固定字串。沒有真正的檢索、切塊或相關性排序。citation id 由 fixture id 與序號組成（`mock-demo-repository.ts:1919`）。
3. **建議問題是「能成功回答的 fixture」的清單**（`mock-demo-repository.ts:1878-1880`）——實際上是把答案反推成問題。
4. **訊息 id 用陣列長度遞增**（`chat-message-<n>`，`mock-demo-repository.ts:1523-1532`），使用者訊息與助理回覆共用同一個 `createdAt`（`:1525`、`:1530`）——沒有真正的時間差，也沒有串流。訊息 id **只在該段對話內唯一**，不同 thread 會出現相同的 `chat-message-1`。
5. **對話 id 是流水號**（`chat-thread-<n>`，`nextThreadId()`，`mock-demo-repository.ts:1731-1737`），序號取「目前最大值 + 1」，刪掉再開會重用被刪掉的號碼。正式版必須改成不可猜測、不重用的 id，否則使用者可以直接試 id。
6. **標題是把第一則提問截斷**（`deriveThreadTitle()`，`mock-demo-repository.ts:422-431`，上限 24 字元見 `demo-seed-chat.ts:147`），沒有摘要模型。改過名字之後 `titleSource` 變成 `manual`，之後就不再自動跟著訊息變（`writeChatMessages()`，`:1793`）。
7. **排序只看 `updatedAt`，同一毫秒時用序號遞減決定**（`byRecentActivity()`，`mock-demo-repository.ts:447-450`）。Demo 常常在同一個 tick 內連續操作，所以這個 tiebreak 是必要的；正式版有真實時間戳就不需要。
8. **沒有分頁、沒有搜尋、沒有釘選或封存**。側欄一次把所有對話畫出來。
9. **刪除是真的刪掉**（`deleteChatThread()`，`mock-demo-repository.ts:1445-1466`），沒有軟刪除、沒有垃圾桶、沒有稽核紀錄。確認對話框只在前端（`conversation-rail.component.html:76-95`）。
10. **「不保存對話」只是不寫 localStorage**：訊息仍在記憶體裡完整存在（`mock-demo-repository.ts:493-497`），也仍然會產生結構化紀錄（同意送出的表單一樣會寫進 `sme-demo:chat-records`）。正式版要決定「不保存對話」是否也代表不保留伺服端日誌。
11. **沒有打字中狀態、沒有重試、沒有訊息編輯或刪除**（可以刪整段對話，不能刪單一訊息）。
12. **送出收據的文字寫死承諾「你可以隨時申請撤回或刪除」**（`mock-demo-repository.ts:1610`），但系統裡沒有任何撤回機制。後端接手時這是法遵風險點。
13. 紀錄 id 用既有紀錄數遞增（`record-chat-<n>`，`mock-demo-repository.ts:1591`）。

---

### 5.7 未登入訪客（`/use/:assistantId`）

設計依據：`...-design.md:199`「未登入的官網訪客以獨立瀏覽工作階段保存對話，其他訪客與建立者無法查看。」

**身分。** 每個瀏覽器分頁一個 `visitor-<亂數>`（`core/domain/account.model.ts:10`），由 `AnonymousVisitorService` 發放並存在 sessionStorage 的 `demo-visitor`（`core/session/anonymous-visitor.service.ts:10`、`:63-78`）。它**不是帳號**：沒有憑證、沒有 `AccountPermission`、不出現在 `listAccounts()`。`isVisitorId()`（`account.model.ts:16-18`）是帳號與訪客唯一的區分方式，兩邊的 id 命名空間不重疊。發放時機在 `embeddedChatGuard`（`core/session/embedded-chat.guard.ts:14-19`）：有 Demo 身分就不發，沒有（或剛逾時）才發，而且**永遠不轉址**。

**誰開得了。** `anonymouslyOpenAssistant()`（`mock-demo-repository.ts:1637-1651`）只放行 `isExternallyPublished()` 為真的助理——也就是**官網嵌入或 LINE 的管道狀態是 `published`**（`publishing-channels.ts:230-245`）。平台內分享**不算對外**，它仍然需要一個已登入的帳號。尚未設定、測試中、需要處理與已暫停都關著，所以新建立的助理預設開不了（`defaultPublishingRecord()` 的網域是空的，`publishing-channels.ts:48`）。Demo 的種子資料中只有 `assistant-customer-service` 開得了；`assistant-internal-onboarding` 的官網管道是「測試中」、LINE 是「尚未設定」，所以開不了。

**拒絕不得洩漏。** 「沒有對外發布」與「助理不存在」回**同一個** `assistant-use` 與**同一則訊息**，訊息不含助理名稱（`anonymousUsePermissionDenied()`，`mock-demo-repository.ts:1669-1675`）。畫面上訪客也拿不到任何 `/app` 連結或復原按鈕（`chat-conversation.component.ts:253-262`）。後端請照做。

**對話的歸屬與儲存。** key 的組法與帳號完全一樣（`sme-demo:chat:<viewerId>:<assistantId>`），但 `chatStorage()`（`mock-demo-repository.ts:1694-1697`）把訪客的那份寫進**另一個 storage**：DI 注入時是 **sessionStorage**（`tokens.ts:15-17`），帳號用的仍是 localStorage。結果是三層隔離——不同訪客 key 不同、訪客與帳號 key 不同、訪客的資料連 storage 都不同，關閉分頁就整個消失。`countChatConversations()` 只掃 `seed.accounts`（`mock-demo-repository.ts:1804-1815`），所以**訪客的對話不會出現在擁有者的匿名統計裡**。

**同意與結構化紀錄。** 訪客的表單流程與帳號完全相同：同意畫面照樣顯示接收單位、收集目的、可查看者、敏感資料提示與撤回說明（`ChatConsentView`，5.2 節），未勾同意一樣回 `validation-failed`。差別只在追蹤對象：紀錄寫的是 `subject-<visitorId>`（`mock-demo-repository.ts:1593`），顯示名稱由 `anonymousSubjectName()` 產生為「未登入訪客（id 末四碼）」（`mock-demo-repository.ts:433-439`、`demo-seed-chat.ts:131`）——**不冒認任何帳號、不含任何個人資料**，末四碼只是為了讓兩位訪客在下拉選單裡分得開。紀錄本身寫進共用的 `sme-demo:chat-records`（所以資料管理者看得到），之後仍只有**指定資料管理者**能在收集紀錄中查看。

**訪客看到的文案不同。** `privacyNotice` 換成 `CHAT_VISITOR_PRIVACY_NOTICE`（`demo-seed-chat.ts:127`、套用於 `mock-demo-repository.ts:1877`），說的是「只存在這個瀏覽器分頁、關閉分頁就會結束」，而不是「你的帳號」。畫面另外加一段 Demo 聲明（`chat-conversation.component.html:37-41`）。

**這一段哪些是假的。** ① 訪客 id 是 `crypto.randomUUID()`，沒有簽章也沒有伺服端紀錄，改 sessionStorage 就能換一位訪客——**這不是身分驗證**。② `isExternallyPublished()` 在前端判斷，後端必須自己擋。③ `?embed=1` 只是視覺開關，不做 origin／referrer／`frame-ancestors` 檢查。④ 匿名同意紀錄的法遵主體是一個不可追溯的隨機 id，撤回承諾（5.6 節第 12 點）在匿名情境下更難兌現。

---

## 6. 發布管道（Task 10）

前端位置：`apps/admin/src/app/features/publishing/`。純函式集中在 `apps/admin/src/app/core/repositories/publishing-channels.ts`（檔頭註解 `:28`）。

### 6.1 前端呼叫的方法 → 建議 endpoint

| repository 方法 | 前端呼叫位置 | 建議 REST endpoint | 成功回應 | 失敗回應 |
| --- | --- | --- | --- | --- |
| `listChannelOverview(viewer)` | `channel-overview/channel-overview-page.component.ts:26` | `GET /api/v1/publishing/overview` | `200` `AssistantChannelsView[]` | — |
| `listPublishingChannels(viewer)` | `assistants/assistant-list/assistant-list-page.component.ts:31` | `GET /api/v1/publishing/channels` | `200` `PublishingChannelView[]` | — |
| `getAssistantPublishing(viewer, assistantId)` | `assistant-publishing/assistant-publishing.component.ts:49` | `GET /api/v1/assistants/{id}/publishing` | `200` `AssistantPublishingView` | `403 publishing` |
| `updatePlatformSharing(viewer, assistantId, accountIds)` | `platform-sharing/platform-sharing.component.ts:40` | `PUT /api/v1/assistants/{id}/publishing/platform` | `200` `PlatformSharingView` | `422`（`PublishingFieldError[]`）／`403 publishing` |
| `updateWebsiteEmbed(viewer, assistantId, settings)` | `website-embed/website-embed.component.ts:127` | `PUT /api/v1/assistants/{id}/publishing/website` | `200` `WebsiteEmbedView` | `422`／`403 publishing` |
| `checkWebsiteInstallation(viewer, assistantId)` | `website-embed.component.ts:147` | `POST /api/v1/assistants/{id}/publishing/website:check-installation` | `200` `WebsiteEmbedView` | `403 publishing` |
| `saveLineSettings(viewer, assistantId, input)` | `line-setup/line-setup.component.ts:81` | `PUT /api/v1/assistants/{id}/publishing/line` | `200` `LineSetupView` | `403 publishing` |
| `sendLineTestMessage(viewer, assistantId)` | `line-setup.component.ts:95` | `POST /api/v1/assistants/{id}/publishing/line:test` | `200` `LineSetupView` | `403 publishing` |
| `activateLineChannel(viewer, assistantId)` | `line-setup.component.ts:102` | `POST /api/v1/assistants/{id}/publishing/line:activate` | `200` `LineSetupView` | `422`（`PublishingFieldError[]`）／`403 publishing` |
| `setPublishingChannelPaused(viewer, assistantId, type, paused)` | `assistant-publishing.component.ts:74` | `PUT /api/v1/assistants/{id}/publishing/{type}/paused` | `200` `PublishingChannelView` | `403 publishing` |

每個助理**固定就是三個管道**，不能新增或刪除（契約註解 `demo-repository.ts:247`、`:251`；組裝位置 `publishing-channels.ts:266-311`）。

### 6.2 五種管道狀態

union 與清單：`apps/admin/src/app/core/domain/publishing.model.ts:16`、`:18-24`。中文顯示、符號與色調：`:35-41`。

| union 值 | 中文顯示 | 符號 | tone |
| --- | --- | --- | --- |
| `not-configured` | 尚未設定 | ○ | `neutral` |
| `testing` | 測試中 | ◐ | `info` |
| `published` | 已發布 | ✓ | `success` |
| `needs-attention` | 需要處理 | ! | `warning` |
| `paused` | 已暫停 | ‖ | `neutral` |

**狀態是推導出來的，不是儲存的。** 三個管道各有自己的推導函式，優先序如下：

- 平台內分享（`publishing-channels.ts:77-82`）：`paused` → 帳號數為 0 則 `not-configured` → 否則 `published`。
- 官網嵌入（`:103-115`）：`paused` → 無允許網域則 `not-configured` → `installCheck === 'detected'` 且處於斷線情境則 `needs-attention` → `installCheck === 'not-detected'` 則 `needs-attention` → `not-checked` 則 `testing` → 否則 `published`。
- LINE（`:187-197`）：`paused` → 未檢查且四個欄位全空則 `not-configured` → 有欄位未通過則 `needs-attention` → 未檢查則 `testing` → `enabled` 則 `published` → 否則 `testing`。

每個狀態都配一段 `statusDetail`（說明現況、影響範圍與下一步），文案就在上述函式內。**單一管道故障不得影響其他管道**（計畫 `...-demo.md:478`、設計 `...-design.md:237`）——這是三個管道各自獨立推導的原因。

管道類型與名稱：`publishing.model.ts:5`、`:7`、`:9-13` → `platform`（平台內分享）／`website`（官網嵌入）／`line`（LINE）。管道 id 格式 `channel-<type>:<assistantId>`（`:45`，組裝於 `publishing-channels.ts:255`）。

### 6.3 請求／回應型別

```ts
interface PublishingChannelView {          // publishing.model.ts:47-57
  id: PublishingChannelId; assistantId: AssistantId; ownerAccountId: AccountId;
  name: string; type: PublishingChannelType; status: PublishingChannelStatus;
  statusDetail: string;                    // 現況＋影響範圍＋下一步
  updatedAt: string;
}

interface PlatformSharingView {            // :78-84
  channel: PublishingChannelView;
  usagePath: string;                       // 站內路徑，目前為 `/use/<assistantId>`（publishing-channels.ts:274）
  allowedAccountIds: readonly AccountId[];
  candidates: readonly { id: AccountId; displayName: string; audienceLabel: string }[];
}

interface WebsiteEmbedSettings {           // :110-116
  displayName: string;                     // 必填，≤ 30 字（MAX_WEBSITE_NAME_LENGTH，:127）
  welcomeMessage: string;                  // 必填，≤ 120 字（MAX_WELCOME_LENGTH，:128）
  brandColor: WebsiteBrandColor;           // 必填，四選一
  position: WebsiteLauncherPosition;       // 必填，兩選一
  allowedDomains: readonly string[];       // 最多 5 個（MAX_ALLOWED_DOMAINS，:126）
}

interface WebsiteEmbedView extends WebsiteEmbedSettings {  // :118-124
  channel: PublishingChannelView;
  embedCode: string;                       // 示範用，指向保留網域
  installCheck: WebsiteInstallCheck;       // 'not-checked' | 'detected' | 'not-detected'（:108）
  installCheckedAt: string | null;
}

type LineSettingsInput = Record<LineField, string>;  // :168，四個欄位皆為必填字串

interface LineSetupView extends LineSettingsInput {   // :185-193
  channel: PublishingChannelView;
  webhookUrl: string;                      // 示範用，指向保留網域
  checks: readonly LineFieldCheckView[];   // 逐欄檢查結果（:172-177）
  lastTest: LineTestResultView | null;     // { outcome: 'delivered' | 'failed'; message; testedAt }（:179-183）
  canSendTest: boolean;
  canActivate: boolean;
}

interface AssistantPublishingView {        // :197-203
  assistantId: AssistantId; assistantName: string;
  platform: PlatformSharingView; website: WebsiteEmbedView; line: LineSetupView;
}
```

其他列舉：`WebsiteLauncherPosition` = `bottom-right`（右下角）／`bottom-left`（左下角）（`publishing.model.ts:88-93`）；`WebsiteBrandColor` = `forest`（森林綠 `#1f6f5c`）／`ocean`（海洋藍 `#1d5fa8`）／`amber`（琥珀橘 `#a3520c`）／`plum`（梅子紫 `#6b3fa0`）（`:95-106`）；`LineCheckState` = `pending`（尚未檢查 ○）／`passed`（通過 ✓）／`failed`（未通過 ✕）（型別 `:170`，標籤與符號 `features/publishing/line-setup/line-setup.component.ts:13-17`）。畫面固定顯示的標語 `PUBLISHING_DEMO_LABEL = 'Demo，不會連接外部服務'`（`:43`）。

### 6.4 驗證規則

**平台內分享**（`publishing-channels.ts:91-99`）：只接受候選名單內的 accountId（候選 = 除了擁有者以外的所有帳號，`:84-89`）。有任何一個不在名單內就整批拒絕：

```ts
{ status: 'validation-failed', errors: [{ field: 'accounts', message: '只能選擇清單中的帳號。' }],
  message: '可使用的帳號有誤，請重新選擇。' }   // mock-demo-repository.ts:759-765
```

空清單是合法的，代表「尚未設定」（`publishing-channels.ts:80`）。

**官網設定**（`publishing-channels.ts:130-162`）：

| 欄位 | 條件 | 錯誤訊息 | 行號 |
| --- | --- | --- | --- |
| `displayName` | trim 後為空 | 請填寫顯示名稱。 | `:137` |
| `displayName` | > 30 字 | 顯示名稱請在 30 個字以內。 | `:138-140` |
| `welcomeMessage` | trim 後為空 | 請填寫歡迎語。 | `:141` |
| `welcomeMessage` | > 120 字 | 歡迎語請在 120 個字以內。 | `:142-144` |
| `brandColor` | 不在四色之內 | 請選擇品牌色。 | `:145-147` |
| `position` | 不在兩個位置之內 | 請選擇顯示位置。 | `:148-150` |
| `allowedDomains` | 逐一驗證，遇到第一個錯就停 | 見下表 | `:151-159` |

**允許網域格式**（`publishing.model.ts:137-147`，正規化 `:132-134` 為 trim + 轉小寫）。這是整個 Demo 中唯一在前端也跑一次的格式驗證（`features/publishing/website-embed/website-embed.component.ts:106`），但後端仍必須自行驗證：

| 條件 | 錯誤訊息 |
| --- | --- |
| 空字串 | 請輸入網域，例如 shop.example.com。 |
| 含 scheme（`/^[a-z]+:\/\//`）或含 `/` | 只需要填網域，不要包含 https:// 或路徑，例如 shop.example.com。 |
| 不符 `DOMAIN_PATTERN` | 「{domain}」不是有效的網域，例如 shop.example.com。 |
| 重複 | 「{domain}」已在允許清單中。 |
| 已達 5 個 | 最多可設定 5 個網域。 |

`DOMAIN_PATTERN`（`publishing.model.ts:130`）：

```
/^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$/
```

不接受 IP、不接受 port、不接受萬用字元、不接受 punycode 之外的非 ASCII。正式版若要支援子網域萬用字元或 IDN，必須先改這條規則並同步前端。

**網域變更會重置安裝檢查**：只要 `allowedDomains` 有變動，`installCheck` 就回到 `not-checked`、`installCheckedAt` 設為 null（`mock-demo-repository.ts:788-797`）。契約註解 `demo-repository.ts:269`。

**LINE 逐項檢查**（`publishing-channels.ts:166-185`）：

| 欄位 | 標籤 | 正規式 | 失敗訊息 |
| --- | --- | --- | --- |
| `officialAccountId` | 官方帳號 ID | `/^@[a-z0-9._-]{3,20}$/i` | 需以 @ 開頭，接 3–20 個英數字，例如 @anxin-demo。 |
| `channelId` | Channel ID | `/^\d{10}$/` | 應為 10 位數字。 |
| `channelSecret` | Channel secret | `/^[a-f0-9]{32}$/i` | 應為 32 個英數字（0–9、a–f）。 |
| `accessToken` | Channel access token | `/^\S{40,}$/` | 至少 40 個字元且不含空白。 |

欄位定義與提示文案在 `publishing.model.ts:161-166`，其中 `channelSecret` 與 `accessToken` 標記為 `sensitive: true`（畫面預設遮蔽）。

檢查狀態的三種結果（`publishing-channels.ts:174-184`）：未按下儲存前一律 `pending`／「尚未檢查。」；空值 → `failed`／「請填寫 {label}。」；不符正規式 → `failed`／「{label}{規則訊息}」；全部通過 → `passed`／「通過檢查（模擬）。」。

**啟用的前置條件鏈**（不可跳過）：

1. `canSendLineTest`：已按過儲存（`checked`）＋ 未暫停 ＋ 四項檢查全部 `passed`（`publishing-channels.ts:199-201`）。
2. 傳送測試訊息，`lastTest.outcome` 必須是 `delivered`（`:203-213`）。
3. `canActivateLine`：滿足 1、尚未 `enabled`、且 `lastTest.outcome === 'delivered'`（`:215-217`）。

不滿足時 `activateLineChannel` 回：

```ts
{ status: 'validation-failed',
  errors: [{ field: 'line', message: '請先讓所有欄位通過檢查並確認測試訊息送達，再啟用。' }],
  message: 'LINE 管道尚未完成測試。' }        // mock-demo-repository.ts:874-880
```

**儲存設定會重置測試狀態**：`saveLineSettings` 每次都把 `checked` 設為 true、`enabled` 設為 false、`lastTest` 清為 null（`mock-demo-repository.ts:837-845`）。也就是說改過設定就必須重新測試才能再啟用。契約註解 `demo-repository.ts:280`。

錯誤格式（`publishing.model.ts:65-68`）：`{ field: string; message: string }`，`field` 是自由字串（目前用過 `accounts`、`displayName`、`welcomeMessage`、`brandColor`、`position`、`allowedDomains`、`line`）。

### 6.5 權限規則

- **所有發布方法都要求 viewer 是助理擁有者**（`ownedAssistant()`，`mock-demo-repository.ts:2312-2319`，註解在 `:2311`）。不存在與非擁有者回同一個訊息（`:2322`）。
- `setPublishingChannelPaused` 額外檢查 `channelType` 必須是三種之一，不是就同樣回 permission-denied（`mock-demo-repository.ts:896-898`）。
- 暫停／恢復只影響指定的那一個管道，其他管道的設定與狀態不變（`:771-777`；契約註解 `demo-repository.ts:296`）。
- 總覽只列出 viewer 自己擁有的助理（`channelOverview()`，`mock-demo-repository.ts:2325-2336`）。
- 帳號的 `manage-publishing` 權限雖然定義在 `account.model.ts:26`，但目前的發布方法**只檢查擁有者身分、沒有檢查這個權限**。正式版需要決定兩者關係（列入 Open questions）。

### 6.6 這一區哪些是假的

1. **嵌入碼是寫死的樣板，指向保留網域**：`demoEmbedCode()` 產生指向 `https://widget.demo.invalid/assistant.js` 的 script（`publishing-channels.ts:117-127`），第一行就是「Demo 嵌入碼：僅供展示，不可用於正式環境，也不會連接外部服務」。計畫 `...-demo.md:506` 明講不可用於正式環境。
2. **webhook 網址同樣是保留網域**：`https://webhook.demo.invalid/line/<assistantId>`（`publishing-channels.ts:303`）。
3. **安裝檢查不連線到任何網站**：`checkWebsiteInstallation` 只要有允許網域就把 `installCheck` 設成 `detected`（或在斷線情境下設成 `not-detected`），完全不發出請求（`mock-demo-repository.ts:804-826`）。契約註解 `demo-repository.ts:275`。
4. **LINE 測試訊息不連接 LINE**：`lineTestResult()` 依本地狀態直接產生結果，訊息裡就寫著「模擬結果，未實際連接 LINE」（`publishing-channels.ts:203-213`）。契約註解 `demo-repository.ts:286`。
5. **有一個寫死的「過期權杖」**：`EXPIRED_DEMO_LINE_TOKEN`（`demo-seed-publishing.ts`）被檢查函式特判成失敗，訊息「此權杖已失效（模擬結果）」（`publishing-channels.ts:180-182`）——這是為了展示 `needs-attention` 狀態而做的假資料。
6. **LINE 憑證以明文存在 localStorage**（`sme-demo:publishing:<assistantId>`，`mock-demo-repository.ts:2349`）。正式版必須加密保存於後端，且 `LineSetupView` **不應該把 `channelSecret` 與 `accessToken` 原文回傳給前端**（目前會，見 `publishing-channels.ts:297-301`）。這是需要修改契約的一項。
7. **`disconnected-channel` 情境只影響官網管道**，用來展示「單一管道故障不影響其他管道」（`publishing-channels.ts:107-109`、`mock-demo-repository.ts:818`）。這是 Demo 開關，不是真實偵測。
8. 沒有任何管道的流量統計、錯誤率或送達率。

---

## 7. 契約已定義但 Task 6–10 畫面尚未呼叫的方法

以下方法定義在 `demo-repository.ts` 且在 mock 有實作，但目前沒有任何 Task 6–10 的功能元件呼叫它們（屬於 Task 3–5 的基礎建設，或僅供測試與情境切換使用）。後端仍需知道它們的語意，因為它們定義了跨帳號隔離的基準。

| 方法 | 契約位置 | mock 實作 | 語意 |
| --- | --- | --- | --- |
| `listAccounts()` | `demo-repository.ts:209` | `mock-demo-repository.ts:520` | 回傳三個 Demo 帳號，供切換器使用。正式版不應存在這個 endpoint。 |
| `getAssistantSources(viewer, assistantId)` | `:209-212` | `:415` | 助理的知識庫＋資料庫連接清單；非擁有者回 `assistant-configuration` 拒絕（`:424-427`）。 |
| `listKnowledgeBases(viewer)` | `:213-215` | `:444` | 原始知識庫清單（非摘要），只回 viewer 擁有的。 |
| `listDatabases(viewer)` | `:216-218` | `:454` | 原始資料庫清單，只回 viewer 擁有的。 |
| `listPrivateConversations(viewer)` | `:219-221` | `:464` | 只回 viewer 自己的對話。 |
| `getConversation(viewer, conversationId)` | `:222-225` | `:474` | 非本人一律回 `private-conversation` 拒絕（`:483-486`）。 |
| `listManagedSubmissions(viewer)` | `:226-228` | `:492` | 只回「viewer 是 dataManager」**且** `consentStatus === 'consented'` 的紀錄（`:495-500`）。 |
| `listOwnSubmissions(viewer)` | `:229-231` | `:504` | 只回 viewer 自己提交的紀錄。 |
| `submitAuthorizedForm(viewer, input)` | `:232-235` | `:514` | 條件：viewer 必須是 `account-external-customer`、`consent === true`、助理存在且可用（`:520-531`）；成立時建立 `consentStatus: 'consented'` / `trackingStatus: 'received'` 的紀錄（`:541-543`）。 |
| `getAssistantAnalytics(viewer, assistantId)` | `:236-239` | `:563` | 匿名統計，非擁有者回 `assistant-configuration` 拒絕（`:574-577`）。 |
| `setScenario` / `getScenario` / `resetScenario` | `:195-199` | `:379`／`:383`／`:387` | **純 Demo 情境切換器**，正式 API 不得提供。 |

這一組另有三個要特別提醒的地方：

1. **`listAccounts` 不做任何過濾**（`mock-demo-repository.ts:520-522`）——任何帳號都能列出全部帳號。正式版的「可分享對象」清單必須另外設計授權，帳號目錄本身就是敏感資料。
2. **`submitAuthorizedForm` 把「沒有勾選同意」當成 permission-denied 而不是 validation-failed**（條件 `mock-demo-repository.ts:651-656`，回應 `:657-660`），與 `submitChatForm` 的處理方式（validation-failed，`:1576-1582`）不一致。而且它**不會保存任何東西**：回傳的 id 與 `submittedAt` 直接沿用 seed 的那一筆（`:672`、`:687`）。正式版要挑一種一致的作法。
3. **`discardAssistantDraft` 是唯一沒有權限檢查、也沒有回傳信封的方法**（`mock-demo-repository.ts:1020-1022`）。因為 key 含 accountId，目前只會刪到自己的草稿；正式版必須加上授權檢查。

---

## 8. 後端必須自行決定的缺口（Open questions）

| # | 議題 | 目前 Demo 的狀態（含位置） | 需要後端決定的事 |
| --- | --- | --- | --- |
| 1 | 認證與 session | `activeAccountId` 只是記憶體 signal（`core/session/demo-session.service.ts:25`），**重新整理即失效**；守衛只檢查有沒有值（`demo-session.guard.ts:5-8`）；另有未使用的舊 `sa.auth.session` key（`core/auth/auth.service.ts:4`） | 登入機制、token 型式與存放位置、逾期與更新、登出要清掉哪些本機資料。設計文件要求「登入逾時前保留非敏感草稿；敏感內容依安全規則清除」（`...-design.md:247`），但沒有定義「敏感」的界線。 |
| 2 | viewer 來源 | 每個方法都把 `viewerAccountId` 當第一個參數（例 `demo-repository.ts:210`） | 正式 API 必須從 session 推導 viewer，並移除所有請求中的 accountId 參數。這會改動每一個 endpoint 的簽章。 |
| 3 | id 型別 | `AccountId`、`KnowledgeBaseId`、`ConversationId`、`TrialQuestionId`、`ChatResponseId` 等都是**字面值 union**（`account.model.ts:1-4`、`knowledge-base.model.ts:4-8`、`conversation.model.ts:10-11`、`:92-96`） | 決定 id 格式（UUID／ULID／數字），並讓前端把這些 union 放寬成一般字串。目前的字面值型別讓後端無法回傳任何新 id。 |
| 4 | 檔案上傳與文件處理 | 完全沒有上傳；`addDemoKnowledgeDocument` 只建一筆假紀錄（`mock-demo-repository.ts:1112-1141`），進度靠畫面按鈕手動推進（`knowledge-detail-page.component.ts:159`） | 上傳協定（直傳／預簽名 URL）、大小與格式限制、防毒掃描、解析失敗的錯誤分類（要能填進 `issue`）、處理進度如何通知前端（輪詢／SSE／WebSocket）、可否刪除與重新命名文件、`allowOriginalDownload` 為 true 時的下載授權。 |
| 5 | 真正的 LLM 與引用來源 | 關鍵字比對 fixture（`mock-demo-repository.ts:1886-1907`）；引用是寫死的文件名與摘錄（`demo-seed-chat.ts`） | 模型選擇與供應商、檢索策略與切塊、引用來源如何定位到文件位置、串流回應的協定、逾時與重試、成本與速率限制、`no-result` 的判定門檻、`general-knowledge` 與 `company-data` 如何在同一次回答中分區。 |
| 6 | 同意紀錄的稽核與撤回 | `SubmissionConsentStatus` 已有 `withdrawn`（`conversation.model.ts:41-42`），但**沒有任何方法能撤回**；收據文字卻已承諾「可隨時申請撤回或刪除」（`mock-demo-repository.ts:1610`） | 撤回的 endpoint 與流程、撤回後既有紀錄如何處理（軟刪除／匿名化／實刪）、撤回是否回溯影響趨勢計算、同意的稽核軌跡（誰在什麼時候看到什麼版本的同意條款）、同意條款版本管理。 |
| 7 | 資料保存期限 | 沒有任何 TTL 或清除機制；localStorage 永久保留。使用者可以刪掉單一段對話（`deleteChatThread`），但那是**硬刪除**，沒有軟刪除或垃圾桶（`mock-demo-repository.ts:1445-1466`） | 對話、結構化紀錄、草稿、稽核紀錄各自的保存期限與刪除方式；刪除一段對話是否連帶刪掉它產生的結構化紀錄（目前**不會**，紀錄留在 `sme-demo:chat-records`）；帳號刪除時的連動清除。 |
| 8 | 多租戶邊界 | 只有三個固定帳號，沒有組織／團隊層級；`shareTargets` 是「除了自己以外的所有帳號」（`mock-demo-repository.ts:1104-1106`） | 租戶（公司）、團隊、使用者的三層關係；跨租戶分享是否允許；帳號目錄本身的可見性（列出所有帳號本身就是資訊洩漏）。 |
| 9 | 權限模型的落地 | `AccountPermission` 有七個值（`account.model.ts:23-30`），但發布相關方法只檢查擁有者、沒有檢查 `manage-publishing`（`mock-demo-repository.ts:2312-2319`）；`canUseAssistant` 用「外部客戶＋audience 不是 account-members」這條捷徑（`:2299-2309`） | 權限與擁有權的關係（擁有者是否自動具備全部權限）、是否引入角色或 ACL、`use-shared-assistants` 等權限的實際執行點、外部客戶的授權關係如何建立。 |
| 10 | 敏感憑證的處理 | LINE 的 `channelSecret` / `accessToken` 明文存 localStorage 並**原文回傳給前端**（`publishing-channels.ts:297-301`、`mock-demo-repository.ts:2349`） | 憑證加密保存、回傳時是否遮蔽（建議只回末四碼與是否已設定）、輪替流程、稽核。這會改動 `LineSetupView` 的契約。 |
| 11 | 真正的官網嵌入與 LINE 串接 | 嵌入碼與 webhook 都指向 `.invalid` 保留網域（`publishing-channels.ts:117-127`、`:303`）；安裝檢查與測試訊息都是本地模擬（`mock-demo-repository.ts:804-826`、`publishing-channels.ts:203-213`） | 真實 widget 的託管與版本管理、CORS 與允許網域的執行點、安裝偵測的技術手段、LINE Messaging API 的 webhook 驗簽與重送、未登入訪客的 session 隔離（設計 `...-design.md:194-199` 已有要求）。 |
| 12 | 趨勢計算的歸屬 | 全部在 repository 層預先算好（`database-tracking.ts:63-128`），前端只顯示字串 | 後端是否照樣預算（建議照做，前端已依此設計）；大量紀錄時的分頁與聚合策略；`en-US` 數字格式與「分」「持平」等文案該放在哪一層（`database-tracking.ts:22`、`:30`、`:93`）。 |
| 13 | 追蹤對象（subject）的實體 | 硬對映成 `subject-<accountId>`（`mock-demo-repository.ts:1593`、`:1827-1833`），但設計文件明確說「登入帳號」與「被追蹤對象」是不同概念（`...-design.md:191`） | subject 的資料模型、一個帳號可否管理多個 subject、授權關係如何建立與撤銷。 |
| 14 | 併發與版本控制 | 所有寫入都是整份覆寫，沒有版本或樂觀鎖（例 `mock-demo-repository.ts:1012`、`:1312`、`:2349`） | ETag／version 欄位、衝突時的回應（`409`）、草稿多裝置編輯的策略。 |
| 15 | 分頁、排序與搜尋 | 所有列表都一次回全部，沒有任何分頁參數（例 `:945`、`:1080`、`:1880`） | 分頁協定（cursor／offset）、預設排序、搜尋與篩選需求。目前對話紀錄與收集紀錄成長無上限。 |
| 16 | 週期性報告 | `PeriodicReportSchedule`（`off` / `weekly` / `monthly`，`assistant-draft.model.ts:32`）只被保存進草稿，**沒有任何排程或發送邏輯** | 排程器、送達管道（email／LINE／站內）、時區、失敗重試。 |
| 17 | 助理狀態機 | `AssistantStatus` 有四種（`assistant.model.ts:14`），但只有「建立後固定為 ready」這一條轉移（`mock-demo-repository.ts:1054`）；沒有任何方法能改狀態 | `draft → ready → published → paused` 的完整轉移規則、誰能觸發、與發布管道狀態的關係。 |
| 18 | `loading` 與 `partial-failure` 的真實來源 | 只由 Demo 情境切換器產生（`mock-demo-repository.ts:2253-2275`），`RepositoryUnavailableResource` 只有 `'knowledge-sync'`（`demo-repository.ts:76`） | 哪些下游資源失敗時應降級為 `partial-failure`、需要擴充哪些 `unavailable` 值、逾時與熔斷策略。 |
| 19 | 對話紀錄的 id、標題與規模 | `ChatThreadId` 是流水號且刪除後會重用（`mock-demo-repository.ts:1731-1737`）；標題是第一則提問截斷 24 字（`:422-431`、`demo-seed-chat.ts:147`），改名後就固定；側欄一次載入全部對話，沒有分頁、搜尋、釘選或封存 | 不可猜測且不重用的對話 id；標題要不要用摘要模型產生、是否隨對話演進更新；對話清單的分頁與搜尋；跨裝置同步與「最後活動時間」的權威來源（目前完全靠寫入當下的 `now()`，同毫秒時用序號 tiebreak，`:446-450`）。 |

---

## 9. 前端替換位置

### 9.1 單一替換點

整個 Demo 只有**一個**注入點需要換：

```
apps/admin/src/app/core/repositories/tokens.ts:6-19
```

```ts
export const DEMO_REPOSITORY = new InjectionToken<DemoRepository>('DEMO_REPOSITORY', {
  providedIn: 'root',
  factory: () => new MockDemoRepository(DEMO_SEED, {
    storage: typeof localStorage === 'undefined' ? undefined : localStorage,
  }),
});
```

所有功能元件都只注入 `DEMO_REPOSITORY`，沒有任何元件直接 import seed 或 mock（計畫 `...-demo.md:176` 就是這條要求）。因此替換步驟是：

1. **新增** `apps/admin/src/app/core/repositories/http-demo-repository.ts`，實作 `DemoRepository`（`demo-repository.ts:208-471`）。
2. 把 `tokens.ts` 的 factory 改成回傳新的 adapter。
3. **不要動** `demo-repository.ts` 的介面，除非確實要改契約（要改的項目見第 8 節第 2、3、10 點）。
4. `MockDemoRepository` 與 seed 檔保留，作為測試替身——現有的六個 spec 檔（`mock-demo-repository*.spec.ts`）就是契約的可執行規格，新 adapter 應該能通過同一組行為測試。

`apps/admin/src/app/core/repositories/local-storage-repository.ts` 與 `repository.ts` 是**沒有任何人使用的舊程式**，語意也與 Demo 這條路不同（找不到時會 `throw`，`local-storage-repository.ts:35`）。不要拿它當作後端模型的參考。

### 9.2 需要處理的介面落差

| 落差 | 說明 | 建議作法 |
| --- | --- | --- |
| 同步 vs 非同步 | 目前所有方法都是**同步**回傳（例 `demo-repository.ts:209`），HTTP 必然非同步 | 把回傳型別改為 `Observable<RepositoryView<T>>` 或 `Signal`，並在各元件把直接取值改成訂閱。這是替換工作量的主體。呼叫點共 41 處，清單見 9.3。 |
| `loading` 狀態 | 目前只由情境切換器產生 | 改由請求生命週期產生；`RepositoryView` 的 union 本身不需要改。 |
| `discardAssistantDraft` 無回傳值 | `demo-repository.ts:322` 宣告為 `void` | 改為回傳 `RepositoryView<void>` 或保持 fire-and-forget，需與前端確認。 |
| `DemoScenarioController` | `demo-repository.ts:202-206`，三個情境切換方法 | HTTP adapter 可實作為 no-op，或在正式環境把情境切換 UI 整個移除。 |
| `DemoKeyValueStorage` | `demo-repository.ts:196-200`，只在 mock 使用 | HTTP adapter 不需要；但草稿若要保留離線編輯，仍可沿用同樣的 key 格式。 |

### 9.3 每個方法對應要改的檔案

介面宣告一律在 `apps/admin/src/app/core/repositories/demo-repository.ts`；mock 實作一律在 `apps/admin/src/app/core/repositories/mock-demo-repository.ts`（純函式另在 `database-tracking.ts` 與 `publishing-channels.ts`）。下表列出**呼叫端**，也就是換成非同步時需要跟著改的檔案：

| 功能區 | 方法 | 呼叫端檔案:行號 |
| --- | --- | --- |
| 精靈 | `listAssistantTemplates` | `features/assistants/assistant-wizard/assistant-draft.store.ts:98` |
| 精靈 | `listConnectableSources` | `assistant-draft.store.ts:106` |
| 精靈 | `listTrialQuestions` | `assistant-draft.store.ts:115` |
| 精靈 | `previewTrialAnswer` | `assistant-draft.store.ts:245` |
| 精靈 | `createAssistantFromDraft` | `assistant-draft.store.ts:278` |
| 精靈 | `saveAssistantDraft` | `assistant-draft.store.ts:305` |
| 精靈 | `getAssistantDraft` | `assistant-draft.store.ts:318`、`features/home/home-page.component.ts:37` |
| 精靈 | `discardAssistantDraft` | 目前僅由 mock 內部呼叫（`mock-demo-repository.ts:1070`） |
| 知識庫 | `listKnowledgeBaseSummaries` | `features/knowledge/knowledge-list/knowledge-list-page.component.ts:28` |
| 知識庫 | `getKnowledgeBaseDetail` | `features/knowledge/knowledge-detail/knowledge-detail-page.component.ts:84` |
| 知識庫 | `addDemoKnowledgeDocument` | `knowledge-detail-page.component.ts:117` |
| 知識庫 | `retryKnowledgeDocument` | `knowledge-detail-page.component.ts:128` |
| 知識庫 | `updateKnowledgeSharing` | `knowledge-detail-page.component.ts:139` |
| 知識庫 | `advanceKnowledgeDocument` | `knowledge-detail-page.component.ts:159`（正式版應整段移除） |
| 資料庫 | `listDatabaseSummaries` | `features/databases/database-list/database-list-page.component.ts:24` |
| 資料庫 | `listDatabaseTemplates` | `database-list-page.component.ts:29` |
| 資料庫 | `createDatabaseFromTemplate` | `database-list-page.component.ts:55` |
| 資料庫 | `getDatabaseDetail` | `features/databases/database-detail/database-detail-page.component.ts:78` |
| 資料庫 | `getDatabaseTracking` | `database-detail-page.component.ts:86` |
| 資料庫 | `updateDatabaseFields` | `database-detail-page.component.ts:111` |
| 資料庫 | `previewDatabaseEntry` | `database-detail-page.component.ts:130` |
| 對話 | `getAssistantChat` | `features/assistant-use/conversation/chat-conversation.component.ts:121` |
| 對話 | `sendChatMessage` | `chat-conversation.component.ts:158` |
| 對話 | `reviewChatForm` | `chat-conversation.component.ts:204` |
| 對話 | `submitChatForm` | `chat-conversation.component.ts:226` |
| 對話紀錄 | `listChatThreads` | `features/assistant-use/workspace-chat/workspace-chat-page.component.ts:64` |
| 對話紀錄 | `createChatThread` | `workspace-chat-page.component.ts:112` |
| 對話紀錄 | `renameChatThread` | `workspace-chat-page.component.ts:124` |
| 對話紀錄 | `deleteChatThread` | `workspace-chat-page.component.ts:134` |
| 發布 | `listChannelOverview` | `features/publishing/channel-overview/channel-overview-page.component.ts:26` |
| 發布 | `listPublishingChannels` | `features/assistants/assistant-list/assistant-list-page.component.ts:31` |
| 發布 | `getAssistantPublishing` | `features/publishing/assistant-publishing/assistant-publishing.component.ts:49` |
| 發布 | `setPublishingChannelPaused` | `assistant-publishing.component.ts:74` |
| 發布 | `updatePlatformSharing` | `features/publishing/platform-sharing/platform-sharing.component.ts:40` |
| 發布 | `updateWebsiteEmbed` | `features/publishing/website-embed/website-embed.component.ts:127` |
| 發布 | `checkWebsiteInstallation` | `website-embed.component.ts:147` |
| 發布 | `saveLineSettings` | `features/publishing/line-setup/line-setup.component.ts:81` |
| 發布 | `sendLineTestMessage` | `line-setup.component.ts:95` |
| 發布 | `activateLineChannel` | `line-setup.component.ts:102` |
| 助理 | `listAssistantConfigurations` | `features/assistants/assistant-list/assistant-list-page.component.ts:30`、`features/assistants/assistant-detail/assistant-detail-page.component.ts:74` |
| 助理 | `getAssistantAnalytics` | `assistant-detail-page.component.ts:85` |
| 助理 | `listUsableAssistants` | `features/home/home-page.component.ts:21`、`:29` |
| 基礎 | `listAccounts`、`getAssistantSources`、`listKnowledgeBases`、`listDatabases`、`listPrivateConversations`、`getConversation`、`listManagedSubmissions`、`listOwnSubmissions`、`submitAuthorizedForm` | Task 6–10 畫面尚未呼叫（見第 7 節） |
| 基礎 | `setScenario`、`getScenario`、`resetScenario` | 情境切換器專用，正式版移除 |

### 9.4 替換時必須保留的行為

即使換成真後端，下列行為是畫面正確性的前提，不可省略：

1. 「不存在」與「無權限」回傳完全相同的內容，且不含資源名稱（第 1.6 節）。
2. 非資料管理者的 `recordCount` / `subjectCount` 必須是 `null`，不是 0（第 4.5 節）。
3. 趨勢比較的差異值、標籤與摘要由伺服端算好（第 4.6 節第 1 點）。
4. 表單欄位的正規化規則（非選擇類型清空 options、非量尺 scale 為 null 等，第 4.4 節）。
5. 同意勾選在欄位驗證**之後**才檢查（第 5.5 節）。
6. LINE 儲存設定會重置測試與啟用狀態；網域變更會重置安裝檢查（第 6.4 節）。
7. 三個發布管道各自獨立推導狀態，單一管道故障不影響其他管道（第 6.2 節）。
8. 各區的 validation-failed 形狀不同（有些只有 `message`，有些帶 `errors`），詳見 `demo-repository.ts:119-198`。
9. 對話紀錄只回傳 viewer 自己的對話；`threadId` 不存在與屬於別人必須回傳**同一則** `chat-thread`，訊息不得包含對話標題（第 5.4 節）。
10. 助理關閉「保存自己的對話」時，`listChatThreads` 要回 `historyMode: 'not-saved'` 與說明文字，而不是空清單——側欄要能分辨「還沒有對話」與「這個助理不留紀錄」（第 5.4 節）。
