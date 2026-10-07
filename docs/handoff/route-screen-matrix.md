# 路由 × 畫面 × 狀態 × e2e 對照表

**路由定義**：`apps/admin/src/app/app.routes.ts`（M5a 時共 23 筆設定：17 筆 `loadComponent` 路由、5 筆 redirect、1 筆 wildcard；2026-10-07 重數為 30 筆 `path` 設定，其中 23 筆 `loadComponent`、17 筆掛 `demoSessionGuard`。M5a 之後新增的 `/app/operations`、`/app/issues`、`/app/forms/:databaseId`、`/app/assistants/drafts/:draftId/:step`、`/auth/callback`、`/change-password` 與 M7 的 **`/app/cases`** 中，只有 `/app/cases` 收進第 3.2 節；其餘尚未補，列為待確認）。
**守衛**：工作區用 `apps/admin/src/app/core/session/demo-session.guard.ts:9-12`；`/use/:assistantId` 用
`apps/admin/src/app/core/session/embedded-chat.guard.ts`（mock 模式的示範用守衛；API 模式一律轉址，見第 1 節）。

> **M5a 之後的 `/use`（2026-10-06）**：真正給官網訪客的對話視窗是獨立的 Nx app `apps/widget`，由 **API** 在
> `GET /use/{assistantId}` 提供（`/widget/*` 是它的檔案、`/embed.js` 是客戶貼的載入器），不在 admin 的路由表裡，
> 也不是這份文件的 23 筆路由之一；它送出依助理允許網域動態產生的 `frame-ancestors`。admin 的
> `/use/:assistantId` 只剩 **mock 模式的 Demo 畫面**（API 模式轉址，不再是訪客入口）。詳見第 5.2 節與
> `docs/plans/2026-10-06-backend-milestone-5a-website-embed.md`。

這份文件回答「**哪個網址會長出哪個畫面、它會呼叫誰、它可能變成什麼樣子、誰在測它**」。每個方法的 endpoint 與錯誤分類在 `docs/handoff/mock-to-api-mapping.md`；每個功能區的型別與規則在 `docs/handoff/tasks-6-10-backend-handoff.md`。

---

## 1. 守衛與權限

有**兩種**守衛。`demoSessionGuard` 掛在 17 筆 `loadComponent` 路由中的 14 筆（全部的 `/app/**`），判斷邏輯只有一行：

```ts
return session.refreshActivity() ? true : inject(Router).createUrlTree(['/login']);
```

`embeddedChatGuard`（`core/session/embedded-chat.guard.ts`）只掛在 `/use/:assistantId` 一筆上，行為依模式而異：

- **mock 模式（Demo）**：**永遠回 `true`、永遠不轉址**：

  ```ts
  if (!inject(DemoSessionService).refreshActivity()) inject(AnonymousVisitorService).ensureVisitor();
  return true;
  ```

  有 Demo 身分時照舊延長工作階段（逾時仍在這裡結束，之後就當成訪客）；沒有身分時發給這個瀏覽器分頁一個匿名訪客 id。Demo 用它示範「嵌入客戶官網的訪客」，能不能看到這個助理由 mock repository 判斷。
- **API 模式（#79）**：**一律轉址**，不再是訪客入口——已登入（`canEnterWorkspace()`）轉到 `/app/chat/:assistantId`；未登入轉到 `/login`，須先改密碼時轉到 `/change-password`。真正的訪客對話視窗由 API 提供的 `apps/widget`（`/use/{id}`，與 admin 不同的來源）負責，它的存取控制在伺服器端（`frame-ancestors`、訪客憑證，見第 5.2 節），不在 admin 的路由層。

`refreshActivity()`（`core/session/demo-session.service.ts:118-130`）做三件事：沒有身分就回 `false`；讀不到紀錄或已逾時就結束工作階段並回 `false`；否則延長有效時間並回 `true`。逾時上限是 30 分鐘（`demo-session.service.ts:29`），逾時文案在 `:31-34`。

**重要限制**：這個守衛**不檢查任何權限**，只檢查「有沒有選過 Demo 身分」。下表「權限」欄寫的是**畫面實際會呈現什麼**——權限判斷全部發生在 repository 層，由 `permission-denied` 的 `reason` 驅動，而不是路由層。正式版若要在路由層擋，必須另外設計 guard，並且**不能因此洩漏資源存在性**（見 `tasks-6-10-backend-handoff.md` 第 1.6 節）。

`/` 與 `/login` 是**唯二完全沒有 `canActivate`** 的路由。`/use/:assistantId` 有 `embeddedChatGuard`：mock 模式下它不會把任何人擋在外面（實務上是公開的）；API 模式下它把所有人轉址走——見第 5.2 節。

---

## 2. 九種畫面狀態

`app-state-panel` 只提供四種視覺狀態（`shared/ui/state-panel/state-panel.component.ts:8`）：`loading` / `empty` / `error` / `permission-denied`。下表用的九種狀態對應到程式中不同的來源：

| 狀態 | 來源 | 程式判斷 |
| --- | --- | --- |
| 空白 | `data` 為空陣列 | 各頁自訂的空狀態區塊（例 `knowledge-list-page.component.html:47`） |
| 載入中 | `RepositoryView.status === 'loading'` | `demo-repository.ts:110-112`；Demo 中僅由 `?demoScenario=loading` 產生 |
| 成功 | `status === 'ready'` | `demo-repository.ts:105-108` |
| 部分成功 | `status === 'partial-failure'` | `demo-repository.ts:114-119`；橫幅 + 仍顯示主資料 |
| 無結果 | `ChatReplyView.kind === 'no-result'` | `core/domain/conversation.model.ts:135-138`，只出現在對話畫面 |
| 權限不足 | `status === 'permission-denied'` | `demo-repository.ts:121-125`，11 種 `reason`（`:90-103`） |
| 處理失敗 | 文件狀態 `failed` / `partially-readable` | 知識庫專屬，見 `tasks-6-10-backend-handoff.md` 第 3.3 節 |
| 連線失敗 | 管道狀態 `needs-attention` | 發布專屬，由 `?demoScenario=disconnected-channel` 觸發（`mock-demo-repository.ts:1080`、`:3087`） |
| 登入逾時 | 守衛回傳 `UrlTree('/login')` | `demo-session.guard.ts:11`；`/login` 顯示逾時說明（`demo-login-page.component.ts:26-31`）。**mock 模式的 `/use/:assistantId` 沒有這個狀態**：逾時後會直接變成未登入訪客（`embedded-chat.guard.ts`）；API 模式的 `/use` 本來就轉址，API 提供的訪客視窗則用 12 小時的訪客憑證，過期時自動重建工作階段 |

**登入逾時對每一條有守衛的路由都成立**，下表不再逐列重複。

---

## 3. 對照表

路徑一律省略 `apps/admin/src/app/`。「repository 方法」欄的行號是呼叫點。

### 3.1 公開路由（無守衛）

| 路由（`app.routes.ts` 行號） | 畫面／元件 | 守衛 | repository 方法 | 可能的狀態 | e2e |
| --- | --- | --- | --- | --- | --- |
| `/` `:11` | `features/landing/landing-page.component.ts` | 無 | **無**（純靜態） | 成功 | `apps/admin-e2e/src/e2e/app.cy.ts`、`navigation.cy.ts`、`accessibility.cy.ts`、`responsive.cy.ts` |
| `/login` `:16` | `features/demo-login/demo-login-page.component.ts` | 無 | **無**；三個身分寫死在 `:18-22` | 成功、**登入逾時說明**（`sessionExpired` 為 true 時，`:26-31`） | 全部 14 個 spec 的進入點；逾時分支由 `error-states.cy.ts`、`accessibility.cy.ts` 覆蓋 |

`/login` 不呼叫 `listAccounts()`——三個 persona 是前端常數。正式版的身分選擇畫面會整個被真實登入取代。

### 3.2 工作區路由（`demoSessionGuard`）

| 路由（行號） | 畫面／元件 | repository 方法（呼叫點） | 可能的狀態 | e2e |
| --- | --- | --- | --- | --- |
| `/app/home` `:21` | `features/home/home-page.component.ts` | `listUsableAssistants` `:21`、`:29`；`getAssistantDraft` `:37`；**案件卡片**（M7，內部帳號才顯示）：`CasesRepository.attention()` `home-page.component.ts:62`（`GET /api/v1/cases/attention`，見 `mock-to-api-mapping.md` 第 2.9 節），卡片上「我負責的逾期案件 N 件」「承辦組待受理的逾期案件 N 件」兩個連結帶 `?scope=owned&overdue=true`、`?scope=my-groups&status=pending&overdue=true` 進 `/app/cases`（`home-page.component.html:92`、`:95`） | 空白（沒有可用助理）、成功、部分成功、登入逾時；案件卡片另有載入中（`home-page.component.html:84`）、錯誤（`:86`）、外部客戶沒有這張卡片 | `app.cy.ts`、`navigation.cy.ts`、`create-assistant.cy.ts`、`accessibility.cy.ts`、`responsive.cy.ts` |
| `/app/assistants` `:27` | `features/assistants/assistant-list/assistant-list-page.component.ts` | `listAssistantConfigurations` `:30`；`listPublishingChannels` `:31` | 空白（`assistant-list-page.component.html:23`）、成功、部分成功、權限不足、登入逾時 | `assistant-list.cy.ts`、`error-states.cy.ts`、`accessibility.cy.ts`、`responsive.cy.ts` |
| `/app/assistants/new` `:34` | **redirect** → `/app/assistants/new/purpose` | — | — | `assistant-list.cy.ts`（由「建立助理」按鈕導向） |
| `/app/assistants/new/:step` `:36` | `features/assistants/assistant-wizard/assistant-wizard-page.component.ts`，狀態集中在 `assistant-draft.store.ts` | `listAssistantTemplates` `:98`；`listConnectableSources` `:106`；`listTrialQuestions` `:115`；`previewTrialAnswer` `:245`；`createAssistantFromDraft` `:278`；`saveAssistantDraft` `:305`；`getAssistantDraft` `:318` | 載入中（`steps/sources-step/sources-step.component.html:7`）、成功、部分成功、權限不足（`sources-step.component.html:9`、`assistant-wizard-page.component.html:67`）、**逐欄驗證失敗**、登入逾時 | `create-assistant.cy.ts`、`accessibility.cy.ts`、`responsive.cy.ts` |
| `/app/assistants/:id/:tab` `:44` | `features/assistants/assistant-detail/assistant-detail-page.component.ts`；`overview` / `data-sources` / `rules` 三個編輯頁籤在 `assistant-detail/tabs/`，狀態集中在 `assistant-detail/assistant-settings.store.ts` | `listAssistantConfigurations` `:106`；`getAssistantAnalytics` `:117`；`getAssistantSettings` `assistant-settings.store.ts:51`；`listConnectableSources` `assistant-settings.store.ts:95`；`updateAssistantSettings` `assistant-settings.store.ts:133`、`:139`；`setAssistantSourceConnection` `assistant-settings.store.ts:152`；**可提議的案件類型**（M7，`data-sources` 頁籤，`tabs/sources-tab/assistant-sources-tab.component.html:20` 的 `app-proposable-case-types`）：`CaseSettingsRepository.listCaseTypes()` `proposable-case-types.component.ts:33`、`setAssistantCaseType` `assistant-settings.store.ts:279`；`publishing` 分頁內嵌 `assistant-publishing.component.ts`（見下） | 空白（使用紀錄，`assistant-detail-page.component.html:73`）、成功、部分成功、權限不足（`assistant-detail-page.component.html:88`）、**逐欄驗證失敗**（三個編輯頁籤，變更不寫入）、登入逾時 | `assistant-editing.cy.ts`、`assistant-list.cy.ts`、`private-conversations.cy.ts`、`publishing.cy.ts`、`error-states.cy.ts`、`accessibility.cy.ts`、`responsive.cy.ts` |
| `/app/knowledge` `:52` | `features/knowledge/knowledge-list/knowledge-list-page.component.ts` | `listKnowledgeBaseSummaries` `:28` | 空白（`.html:47`）、載入中（`.html:9`）、成功、**部分成功**（`.html:17`）、權限不足（`.html:11`）、登入逾時 | `knowledge.cy.ts`、`error-states.cy.ts`、`accessibility.cy.ts`、`responsive.cy.ts` |
| `/app/knowledge/:id` `:59` | **redirect** → `/app/knowledge/:id/content` | — | — | `knowledge.cy.ts` |
| `/app/knowledge/:id/:tab` `:61` | `features/knowledge/knowledge-detail/knowledge-detail-page.component.ts` | `getKnowledgeBaseDetail` `:84`；`addDemoKnowledgeDocument` `:117`；`retryKnowledgeDocument` `:128`；`updateKnowledgeSharing` `:139`；`advanceKnowledgeDocument` `:159`（Demo 專用） | 空白（`.html:64`、`:82`）、載入中（`.html:5`）、成功、部分成功（`.html:22`）、權限不足（`.html:8`）、**處理失敗**（文件 `failed` / `partially-readable`）、登入逾時 | `knowledge.cy.ts`、`error-states.cy.ts`、`accessibility.cy.ts`、`responsive.cy.ts` |
| `/app/databases` `:69` | `features/databases/database-list/database-list-page.component.ts` | `listDatabaseSummaries` `:24`；`listDatabaseTemplates` `:29`；`createDatabaseFromTemplate` `:55` | 空白（`.html:82`）、載入中（`.html:55`）、成功、部分成功（`.html:59`）、權限不足（`.html:57`）、登入逾時 | `tracking.cy.ts`、`accessibility.cy.ts`、`responsive.cy.ts` |
| `/app/databases/:id` `:76` | **redirect** → `/app/databases/:id/form` | — | — | `tracking.cy.ts` |
| `/app/databases/:id/:tab` `:78` | `features/databases/database-detail/database-detail-page.component.ts`（5 個分頁，`:29-35`；**權限**頁籤可由擁有者編輯資料管理者名單） | `getDatabaseDetail` `:82`；`getDatabaseTracking` `:91`；`updateDatabaseFields` `:129`；`updateDatabaseAccess` `database-access.component.ts:58`；`previewDatabaseEntry` `:148`；**送出後自動開案**（M7，`access` 頁籤內的 `app-database-auto-case`，`database-detail-page.component.html:135`，只有管理者）：`CaseSettingsRepository.getDatabaseAutoCase` `database-auto-case.component.ts:52`、`setDatabaseAutoCase` `:97` | 空白（`.html:62`、`:115`）、載入中（`.html:5`、`:92`）、成功、部分成功（`.html:21`）、**兩種權限不足**（`.html:8` 整頁／`:94` 只擋收集紀錄）、登入逾時 | `tracking.cy.ts`、`consented-submission.cy.ts`、`error-states.cy.ts`、`accessibility.cy.ts`、`responsive.cy.ts`、`team-and-access.cy.ts` |
| `/app/activity` `:86` | `features/dashboard/pages/dashboard-page.component.ts`（**placeholder**） | **無** | 成功（靜態）、登入逾時 | 無專屬 spec |
| `/app/cases` `:136`（M7，lazy；清單＋以 `?case=<id>` 選取的詳情；管理者另有 `?view=statistics`） | `features/cases/cases-page.component.ts`（清單、建立案件表單、詳情）；動作表 `features/cases/case-actions.component.ts`；統計 `features/cases/case-statistics.component.ts` | `CasesRepository`（**不是 `DemoRepository`**）：`list` `cases-page.component.ts:142`；`get` `:160`；`create` `:286`；`act` `case-actions.component.ts:163`；`statistics` `case-statistics.component.ts:64`；`CaseSettingsRepository.listCaseTypes` `:167`、`listCaseGroups` `:171`。endpoint 與錯誤見 `mock-to-api-mapping.md` 第 2.9 節 | 空白（`cases-page.component.html:96` 沒有符合條件的案件；`:227` 尚未選取案件）、載入中（`:90` 清單／`:231` 詳情）、成功、錯誤（`:92`／`:233`，可重試）、**權限不足**（`:94` 清單／`:235` 詳情，`403 case`——看不到與不存在同一畫面；外部客戶整頁是這個狀態）、**逐欄驗證失敗**（建立表單的 `422`，含 `due-in-past`、`case-type-inactive`、`link-not-available`）、**動作被擋**（`403 case-action`）、**版本過期**（`409 case-changed`，請重新整理）、統計分頁的期間錯誤（`case-statistics.component.html:40`，`422 invalid-date-range`）與空白（`:51`）、登入逾時；沒有 `partial-failure`。非管理者帶 `?view=statistics` 只看到清單（`cases-page.component.ts:101`，`showStatistics` 要求 `isManager()`），統計的真正授權是後端的 `403 organization-settings` | `cases.cy.ts`（建立、可見性、受理／待補件／轉組、完成並另開新案、逾期數字與首頁卡片、外部客戶看不到、管理者的瓶頸統計、資料庫自動開案）、`accessibility.cy.ts`（`:19`、`:20`、`:74`）、`responsive.cy.ts`（`:25`、`:26`、`:133`）。API 模式（`apps/admin-e2e/src/e2e-api/`）**沒有案件的 spec** |
| `/app/channels` `:91` | `features/publishing/channel-overview/channel-overview-page.component.ts` | `listChannelOverview` `:26` | 空白、載入中（`.html:14`）、成功、部分成功（`.html:23`）、權限不足（`.html:16`）、**連線失敗**（`?demoScenario=disconnected-channel`）、登入逾時 | `publishing.cy.ts`、`error-states.cy.ts`、`accessibility.cy.ts`、`responsive.cy.ts` |
| `/app/settings` `:131` | `features/settings/pages/settings-page.component.ts`（團隊與權限＋外觀設定） | `getTeam`（僅具備 `manage-assistants` 才顯示，其餘得到 `team` permission-denied）`team-panel.component.ts:36`；`updateMemberPermissions` `team-panel.component.ts:86`；**承辦組**與**案件類型**兩個區塊（M7，只有管理者看得到；`settings-page.component.html:11`、`:12`，見下方 3.2.1） | 成功、權限不足（`team` reason）、登入逾時；承辦組與案件類型區塊對非管理者整塊不顯示（`canManage` 為 false） | `accessibility.cy.ts`、`responsive.cy.ts`（迴圈路由）、`team-and-access.cy.ts`；承辦組與案件類型區塊另由 `accessibility.cy.ts`（`:269`）、`responsive.cy.ts`（`:115`、`:124`）涵蓋展開狀態 |

#### 3.2.1 `/app/settings` 的承辦組與案件類型（M7，管理者）

兩個區塊都由 `settings-page.component.html` 直接放在團隊與權限、對話模型、對話保存之後；`GET` 清單給所有內部帳號，但區塊只在回應的 `canManage` 為 true（角色 `smb-admin`）時顯示（`case-groups-panel.component.ts:77`、`case-types-panel.component.ts:106`），所以非管理者看不到，也不會看到權限不足畫面。

| 區塊 | 元件 | repository 方法（呼叫點） | 畫面狀態與錯誤 | e2e |
| --- | --- | --- | --- | --- |
| 承辦組（`data-case-groups-panel`） | `features/settings/components/case-groups-panel/case-groups-panel.component.ts` | `listCaseGroups({ includeArchived: true })` `:69`；`createCaseGroup` `:117`；`renameCaseGroup` `:147`；`setCaseGroupArchived` `:165`；`updateCaseGroupMembers` `:204`；`listCaseGroupMemberChanges` `:220` | 成功；建立與改名的逐欄錯誤（空白、超過 40 字、`case-group-name-taken`）；封存被擋（`422 case-group-in-use`，說明哪些類型與案件佔用）；成員儲存被擋（`member-not-eligible`、`409 case-group-members-conflict`）；異動紀錄展開（`data-case-group-history`，只有管理者拿得到，非管理者是 `403 organization-settings`）；已封存的組標示並可還原；成員編輯器展開（`data-case-group-members-editor`） | `accessibility.cy.ts:269`、`responsive.cy.ts:115`（展開成員編輯器與異動紀錄）；改名、封存只有元件 spec（`case-groups-panel.component.spec.ts`），e2e 沒有直接測 |
| 案件類型（`data-case-types-panel`） | `features/settings/components/case-types-panel/case-types-panel.component.ts` | `listCaseTypes({ includeInactive: true })` `:91`；`listCaseGroups()` `:98`（預設承辦組選項）；`createCaseType` / `updateCaseType` `:224`；停用／啟用 `updateCaseType` `:234` | 成功；表單逐欄錯誤（名稱 1–40 字、說明 0–500 字、時限 1–2,160 小時、`case-type-name-taken`、`case-group-not-found`、`case-group-archived`）；停用被擋（`422 case-type-in-use`，說明哪些資料庫在用）；停用中的類型標示、預設承辦組已封存標示（`（已封存）`）；新增／編輯表單（`data-case-type-form`） | `responsive.cy.ts:124`（展開的類型表單）；`apps/admin-e2e/src/e2e-api/org-settings-api.cy.ts` 與案件無關 |

### 3.3 對話路由

`/app/chat` 三條路由指向**同一個元件** `features/assistant-use/workspace-chat/workspace-chat-page.component.ts`，差別只在 `paramMap` 有沒有 `assistantId` / `conversationId`。

| 路由（行號） | 畫面／元件 | repository 方法（呼叫點） | 可能的狀態 | e2e |
| --- | --- | --- | --- | --- |
| `/app/chat` `:99` | `workspace-chat-page.component.ts`（無 assistantId：顯示助理選擇） | `listUsableAssistants` `:86` | 空白（沒有可用助理）、成功、部分成功、登入逾時 | `chat-history.cy.ts` |
| `/app/chat/:assistantId` `:107` | 同上＋對話紀錄側欄 `conversation-rail.component.ts`，內容區為 `conversation/chat-conversation.component.ts` | `listChatThreads` `:64`；`listUsableAssistants` `:86`；`createChatThread` `:112`；`renameChatThread` `:124`；`deleteChatThread` `:134`；`getAssistantChat` `:100`；`sendChatMessage` `:137`；`reviewChatForm` `:183`；`submitChatForm` `:205`；**開案提議卡片**（M7，`chat-conversation.component.html:81`、`:107` 的 `app-case-proposal-card`）：`confirmChatCaseProposal` `chat-conversation.component.ts:869`、`dismissChatCaseProposal` `:879`（外部客戶與訪客永遠不會收到提議） | **空白**（`.html:21` 側欄空狀態／`conversation-rail.component.html:13`）、載入中（`.html:33`）、成功、部分成功、**無結果**、權限不足（`.html:20`＝`assistant-use`／`.html:37`＝`chat-thread`）、登入逾時 | `chat-history.cy.ts`；開案提議由 `case-proposal.cy.ts` 涵蓋 |
| `/app/chat/:assistantId/:conversationId` `:115` | 同上，直接開啟指定對話 | 同上（`getAssistantChat` 帶 `threadId`） | 同上，另含「對話不存在或屬於其他帳號」→ `403 chat-thread` | `chat-history.cy.ts` |
| `/use/:assistantId` `:125`（**mock 模式的 Demo**；API 模式轉址，見下方註） | `features/assistant-use/chat-shell/chat-shell-page.component.ts` → `chat-conversation.component.ts`（`chat-shell-page.component.ts:22` 把 `allowAnonymous` 固定為 `true`），**單欄、無側欄** | `chat-conversation.component.ts` 的 `getAssistantChat` `:121`（**永遠不帶 `threadId`**）；`sendChatMessage` `:158`；`reviewChatForm` `:204`；`submitChatForm` `:226` | 空白、載入中（`chat-conversation.component.html:4`）、成功、部分成功、**無結果**、權限不足（`.html:8`）。**沒有登入逾時**——逾時後變成未登入訪客 | `anonymous-visitor.cy.ts`、`private-conversations.cy.ts`、`consented-submission.cy.ts`、`chat-history.cy.ts`、`error-states.cy.ts`、`accessibility.cy.ts`、`responsive.cy.ts` |
| `/use/:assistantId?embed=1` | 同上，`header` 由 `full` 切成 `minimal`（`chat-shell-page.component.ts:21`、`:42`） | 同上 | 同上，但**沒有頁首、返回連結與品牌外框**；標題只留視覺隱藏版（`chat-conversation.component.html:32`） | `anonymous-visitor.cy.ts`、`chat-history.cy.ts` |
| `/use/:assistantId`（**未登入訪客**） | 同上。沒有 Demo 身分時改用這個分頁的匿名訪客 id（`chat-conversation.component.ts:103-111`）；`.chat-header` 的返回連結與拒絕畫面的復原按鈕都不顯示（`.html:21`、`chat-conversation.component.ts:254-262`），並加上一段 Demo 聲明（`.html:37-41`） | 同上，第一個參數是 `VisitorId` | 成功、**無結果**、**拒絕**（助理沒有對外發布或不存在，兩者同一則訊息：`mock-demo-repository.ts:2019-2024`） | `anonymous-visitor.cy.ts`、`accessibility.cy.ts` |

**`/use/:assistantId` 在 API 模式**：`embeddedChatGuard` 轉到 `/app/chat/:assistantId`（已登入）或 `/login`、`/change-password`，上表三筆 `/use` 列描述的是 mock 模式。官網訪客真正看到的畫面是 `apps/widget`，由 API 在 `GET /use/{assistantId}` 提供，不屬於 admin 路由表；它的狀態是：對話中、查無資料（助理的 `refusalMessage`）、「目前暫停服務」（未發布以外的任何不服務狀態，不揭露原因）、「這個對話視窗目前無法使用」（助理不存在、未發布或沒有允許網域，`404`，三者同一頁）、「請從官網開啟這個對話視窗」（不在 iframe 裡直接開啟）、「問題太頻繁了，請稍後再試」（`429`）。e2e：`apps/admin-e2e/src/e2e-api/website-embed-api.cy.ts`；widget 本身的測試在 `apps/widget`。

`/app/chat/:assistantId/:conversationId` 的第三段參數名稱是 **`conversationId`**（`app.routes.ts:116`），但 repository 契約叫它 `threadId`（`demo-repository.ts:551`）。同一個東西兩個名字，正式版應該統一。

### 3.4 Redirect 與 fallback

| 路由（行號） | 目標 | 用途 |
| --- | --- | --- |
| `/settings` `:138` | `/app/settings` | 舊網址相容 |
| `/dashboard` `:139` | `/app/home` | 舊網址相容 |
| `**` `:140` | `/` | 未知網址一律回公開首頁，**不顯示 404**——這也避免了用網址探測資源是否存在 |

wildcard 導回 `/` 而不是 `/login`，所以未登入使用者看到的是產品首頁；已登入使用者也會被丟回首頁，需要自己再點進工作區。

---

## 4. e2e spec 清單

測試框架：Cypress（`apps/admin-e2e/cypress.config.ts`），baseUrl `http://localhost:4301`，spec 位於 `apps/admin-e2e/src/e2e/`，2026-10-07 實際共 **21 個** spec（M5a 之前是 14 個；`assistant-editing.cy.ts`、`assistant-acceptance.cy.ts`、`database-archive.cy.ts`、`form-request-ux.cy.ts` 是之後新增、尚未收進下表，列為待確認；M7 的兩個已收進）。API 模式的 spec 在 `apps/admin-e2e/src/e2e-api/`，共 9 個，其中沒有案件相關。無障礙掃描透過自訂 `a11yViolations` node task 回報 axe 結果。

| spec | 覆蓋重點 |
| --- | --- |
| `app.cy.ts` | 公開首頁進入工作台的冒煙測試、單一 `h1` 規則 |
| `navigation.cy.ts` | 訪客導覽：landing → 身分選擇 → 工作台側欄 |
| `assistant-list.cy.ts` | 助理清單只顯示有權限者、跨帳號隔離、建立動作導向精靈 |
| `create-assistant.cy.ts` | 精靈全流程、草稿自動儲存與 reload 續填、草稿不跨帳號外洩 |
| `knowledge.cy.ts` | 知識庫清單與分頁、模擬新增文件、失敗文件重試、分享範圍需明確儲存、權限不足不洩漏名稱 |
| `tracking.cy.ts` | 從範本建資料庫、表單試填、時間軸與趨勢比較、資料不足不下結論、權限不足 |
| `publishing.cy.ts` | 三種管道卡片與五種統一狀態、平台分享限定帳號、網站 widget 預覽／網域驗證／嵌入碼、LINE 逐欄驗證與遮罩、跨帳號設定隔離 |
| `chat-history.cy.ts` | 對話側欄多對話切換／改名／刪除、跨帳號不外洩、不儲存對話的助理說明、`/use` 單欄與 `?embed=1` 去 chrome、手機版 rail 收合 |
| `anonymous-visitor.cy.ts` | 未登入訪客開啟已對外發布的助理、對話對每個 Demo 身分與另一位訪客皆不可見、沒有對外管道的助理不揭露名稱、`?embed=1` 無工作區外框與 `/app` 連結、匿名同意送出進入資料管理者的收集紀錄、訪客可在同一分頁內撤回 |
| `private-conversations.cy.ts` | 回答分類（組織資料含引用／一般知識／無結果）、對話對其他帳號與助理擁有者皆私密、無權限助理不揭露 |
| `consented-submission.cy.ts` | 同意前不可送出、揭露接收方／目的／可見者／敏感資料、送出紀錄僅指定資料管理者可見、提交者可從收據撤回（紀錄離開收集紀錄與趨勢、只留不含內容的軌跡、撤不了第二次）、資料管理者沒有代為撤回的入口 |
| `error-states.cy.ts` | **全狀態矩陣**：成功／空白、載入中、部分成功、無結果、權限不足、處理失敗與連線失敗、登入逾時、帳號切換不殘留資料 |
| `accessibility.cy.ts` | axe critical/serious、skip-link 鍵盤流、LINE 錯誤摘要對焦欄位、引用抽屜與撤回確認對話框的 focus trap 與 Esc 還原、`prefers-reduced-motion`；掃 11 條工作區路由 |
| `responsive.cy.ts` | 360px／1280px 無水平捲動、手機 header + drawer 與桌機常駐側欄、寬表格自身捲動、手機聊天輸入列可達；掃 12 條工作區路由 |
| `cases.cy.ts` | `/app/cases`（M7）：清單只列看得到的案件、停用的對話連結顯示為打不開、建立案件並在送出前擋下過去的時限、看不到的案件不外洩、受理→待補件→轉組後仍看得到、完成並以「另開新案」連結舊案件、側欄「案件」旁的逾期數字等於首頁卡片連結的兩個逾期篩選、外部客戶沒有數字也沒有案件卡片、管理者的瓶頸統計每個數字開出等長的清單；另含資料庫「送出後自動開案」（只有管理者設定、承辦組有人讀不到紀錄時的警告、自動開出的案件由資料庫建立且紀錄撤回後承辦組仍可受理） |
| `case-proposal.cy.ts` | 對話中的開案提議（#254）：預設不提議；擁有者加入案件類型後，提問者確認並打開建立的案件；「不用了」不建立案件，移除類型後卡片讀為「無法建立」 |
| `team-and-access.cy.ts` | `/app/settings` 團隊與權限（成員清單、各角色能做什麼、非 `manage-assistants` 帳號的拒絕畫面不揭露成員名稱、管理者不能移除自己的 `manage-assistants`）；`/app/databases/:id/access` 資料庫的權限頁籤（收回 `read-consented-submissions` 後對應成員立刻看不到收集紀錄） |

### 4.1 尚未被 e2e 直接覆蓋的路由

| 路由 | 原因 |
| --- | --- |
| `/app/activity` | 目前是 placeholder 元件（`dashboard-page.component.ts`），沒有實際內容可斷言 |
| `/app/chat/:assistantId/:conversationId` | `chat-history.cy.ts` 是**點側欄項目**進到這條路由，沒有直接 `cy.visit()` 帶 `conversationId` 的案例 |
| `/settings`、`/dashboard` redirect | 沒有 spec 斷言轉址結果 |
| `**` wildcard | 沒有 spec 造訪未知網址 |

此外，`/app/cases` 只有 Demo（mock）模式的 e2e；API 模式（`e2e-api/`）沒有案件 spec，後端接上後建議補「建立→受理→完成」與「看不到的案件回 `403 case`」兩條。

後端接上後這四條建議補測，尤其 wildcard：它是「不因網址探測洩漏資源存在性」的最後一道。

---

## 5. 給後端與正式化的重點

### 5.1 `?demoScenario=` 必須在正式版消失

`core/repositories/demo-scenario-param.ts:9-17` 讀網址參數，`core/repositories/tokens.ts:19-22` 在 DI factory 套用。合法值五種：`ready`、`loading`、`partial-failure`、`permission-denied`、`disconnected-channel`（`demo-repository.ts:78-84`）。

**任何人都能用網址把畫面切成「權限不足」或「部分失敗」**，這只是預覽機制，不代表後端行為。移除方式見 `mock-to-api-mapping.md` 第 2.8 節。移除後 `error-states.cy.ts` 與 `accessibility.cy.ts` 中所有帶 `?demoScenario=` 的案例都要改成用 `cy.intercept` 偽造回應。

### 5.2 官網訪客的對話視窗（M5a 已實作；admin 的 `/use/:assistantId` 只剩 mock 模式的 Demo）

**現況（M5a，2026-10-06）**：訪客入口不再是 admin 的路由，而是 API 提供的 `apps/widget`（`GET /use/{assistantId}`、`/widget/*`、`/embed.js`）；admin 的 `/use/:assistantId` 在 API 模式轉址（第 1 節），只在 mock 模式保留 Demo。下表是 **Demo 的做法**（mock 模式仍然如此），保留作為 mock 的行為紀錄；正式版的對應決定見表後的「M5a 的決定」。

這條路由原本的用途是**嵌入客戶官網的 iframe** 與**從 LINE 開啟**（元件註解 `chat-shell-page.component.ts:6-14`；LINE 留到 M5b），所以它掛 `embeddedChatGuard`（`app.routes.ts:126`），mock 模式下未登入訪客可以直接開啟。Demo 的做法：

| 決定 | Demo 實作 | 位置 |
| --- | --- | --- |
| 訪客身分 | 每個瀏覽器分頁一個 `visitor-<亂數>`，存在 sessionStorage 的 `demo-visitor`，關閉分頁就結束。**沒有憑證、沒有權限、不對應任何帳號** | `core/session/anonymous-visitor.service.ts:10`、`:50-96` |
| 型別 | `VisitorId` / `ChatViewerId` 與 `isVisitorId()`；四個對話方法的第一個參數改成 `ChatViewerId` | `core/domain/account.model.ts:10-18`、`demo-repository.ts:549`、`:558`、`:565`、`:575` |
| 誰開得了 | 只有**官網嵌入或 LINE 已發布**的助理；平台內分享不算對外 | `publishing-channels.ts:279-286`、`mock-demo-repository.ts:1991-2001` |
| 拒絕畫面 | 「沒有對外發布」與「不存在」回**同一則**訊息，不含助理名稱，也不提供任何 `/app` 復原動作 | `mock-demo-repository.ts:2019-2024`、`chat-conversation.component.ts:253-262` |
| 對話歸屬 | 存在 `sme-demo:chat:<visitorId>:<assistantId>`，寫進**訪客專屬的 sessionStorage**，與帳號的 localStorage 完全分開 | `mock-demo-repository.ts:2040-2047`、`tokens.ts:15-17` |
| 匿名統計 | 訪客的對話不會被計入擁有者的使用次數（擁有者的瀏覽器讀不到那份儲存） | `mock-demo-repository.ts:2155-2168` |
| 表單提交 | 可以提交，同意畫面內容不變；紀錄的追蹤對象是 `subject-<visitorId>`，顯示名稱為「未登入訪客（末四碼）」，不冒認任何帳號 | `mock-demo-repository.ts:1888`、`:580-586`、`demo-seed-chat.ts:131` |
| 撤回同意 | 可以，但**只在同一個瀏覽器分頁內**：收據上照樣有撤回鍵，分頁一關 `demo-visitor` 消失、對話讀不到、紀錄也再指認不到本人。同意畫面與收據的文案直接寫明這件事 | `mock-demo-repository.ts:1920-1969`、`demo-seed-chat.ts:166` |

**M5a 的決定**（原本「正式版仍必須自己決定」的四點，計畫 §3 B、D；實作 #194–#201；操作面見 `deploy/README.md`）：

| 原本的開放問題 | M5a 的答案 | 位置 |
| --- | --- | --- |
| 匿名 session 的形式與保存期限 | 伺服器簽發的**訪客憑證**：Data Protection 保護的 `{ visitorId, assistantId, organizationId, issuedAt, expiresAt }`，有效 **12 小時**，`Authorization: Visitor <token>`；`visitorId` 是隨機 GUID、不寫入資料庫。widget 把它放在 iframe 自己的 `sessionStorage`，關分頁就消失，過期時自動重建一次。遺失金鑰環時所有訪客憑證失效 | `apps/api/README.md`「Website visitors」 |
| 匿名對話要不要落到伺服端 | **不保存**。前文取自請求的 `messages`（最多 20 則），不寫任何對話串或訊息，不管助理的 `keepConversations`；模型呼叫紀錄與回覆統計照寫、不含內容 | 同上；M5a 計畫 §3 D |
| 匿名同意紀錄的法遵主體與分頁結束後的撤回 | **不適用**：訪客路徑上沒有表單請求、資料庫查詢、轉人工，所以沒有同意流程，也沒有可撤回的紀錄。Demo 的「表單提交／撤回同意」兩列只存在於 mock 模式 | M5a 計畫 §3 D |
| 來源限制 | 由**伺服器**執行：`GET /use/{id}` 依助理的允許網域送 `Content-Security-Policy: frame-ancestors https://<網域> …`，未發布、清單為空或助理不存在時一律 `frame-ancestors 'none'` 與同一頁（不洩漏助理是否存在）；訪客 API 不註冊 CORS，帶 `Origin` 且不是 API 本身來源的請求一律 `403 public-origin`；不在 iframe 裡直接開啟（`Sec-Fetch-Dest: document`）顯示「請從官網開啟這個對話視窗」。**這些只約束瀏覽器**，擋不住腳本；真正的防線是頻率限制與每月 token 上限（`deploy/README.md` 第 8 節） | `apps/api/README.md`「Serving the chat window and `embed.js`」 |

- `?embed=1` 只是 mock 模式 Demo 的視覺開關（`chat-shell-page.component.ts:42`），**沒有任何安全意義**。API 提供的 widget 一律是 iframe 用的精簡版面，不需要這個參數。
- 網域限制在 mock 的 `isExternallyPublished()`（`publishing-channels.ts:279-286`）只是前端的一道門，不是授權；正式版的授權在伺服器（上表）。
- LINE 管道的訪客（從 LINE 開啟）仍未實作，留到 M5b。

設計依據：`docs/plans/2026-09-18-sme-ai-assistant-ux-demo-design.md:199`「未登入的官網訪客以獨立瀏覽工作階段保存對話，其他訪客與建立者無法查看。」

### 5.3 所有 `:id` 參數都未經驗證就丟給 repository

`/app/knowledge/:id/:tab`、`/app/databases/:id/:tab`、`/app/assistants/:id/:tab`、`/use/:assistantId`、`/app/chat/:assistantId/:conversationId` 的 id 全部直接來自網址。契約上這些方法的 id 參數型別是 `string` 而非字面值 union（例 `demo-repository.ts:294`、`:433`、`:475`），**這是刻意的**——因為「不存在」與「無權限」要回同一個結果，前端不能先在本地判斷 id 是否合法。正式版必須維持這個性質。

### 5.4 沒有任何路由層的權限檢查

側欄會依身分隱藏項目，但**直接輸入網址仍然進得去**，只是內容區顯示權限不足。這是刻意的設計（避免路由層洩漏存在性），也代表：後端不能假設「前端沒顯示的東西就不會被請求」，每個 endpoint 都必須自行授權。
