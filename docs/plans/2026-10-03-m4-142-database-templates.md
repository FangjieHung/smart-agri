# M4 #142｜從模板建立數據庫：資料模型與 API 契約

- **日期：** 2026-10-03
- **工單：** #142（M4 第一張）；後續 #143–#150 建在本文件的資料邊界上。
- **需求來源：** [M4 交接](2026-10-03-m3.5-closeout-m4-handoff.md) 第 2–4 節、[助理使用數據庫 ADR](../adr/2026-09-25-assistant-access-to-knowledge-and-databases.md)、[撤回與保存 ADR](../adr/2026-09-25-withdrawal-and-retention.md)。

## 1. 資料模型

| 資料表 | 內容 | 規則 |
| --- | --- | --- |
| `Databases` | 名稱（≤ 40 字）、用途（≤ 500 字，預設為模板說明）、`TemplateId`（wire name，例如 `template-satisfaction`）、擁有者、建立／更新時間 | 屬於一個組織（`IOrganizationScoped`，查詢過濾＋寫入防護）；擁有者以 `(OwnerAccountId, OrganizationId)` 複合外鍵指向同組織帳號，`Restrict` |
| `DatabaseFormVersions` | 一個數據庫的一版表單：`VersionNumber`、`Fields`（`jsonb`）、`CreatedByAccountId`、`CreatedAt` | **不可修改**，改表單就新增下一版；`(DatabaseId, VersionNumber)` 唯一；`VersionNumber >= 1`；`Fields` 必須是 JSON 陣列；複合外鍵到 `Databases`，刪數據庫連帶刪版本 |

- **目前的表單 = 版本號最大的那一版。** 由模板建立時寫入第 1 版，模板之後改內容也不影響已建立的數據庫（各自持有一份複本）。
- **欄位 id 是跨版本的穩定鍵**（`field-…`）：改名稱、選項或順序都沿用同一個 id；刪除的欄位 id 不再拿來表示別的問題。#147 用它跨版本比較同一個欄位，#145 的提交另存當時的欄位名稱與設定作為快照。
- **欄位形狀**（與前端 `DatabaseFieldView` 相同）：`id`、`label`、`type`（`text`／`number`／`date`／`single-choice`／`multiple-choice`／`scale`）、`required`、`options`、`scale`（`{min,max,minLabel,maxLabel}`，非量尺為 `null`）、`unit`（只有數字欄位可以有）。順序就是陣列順序。
- **不變條件**（`DatabaseFormVersion.EnsureValid`，與前端 `validateFields` 同規則）：1–50 個欄位；id 與名稱各自不重複；單選／多選 2–30 個不重複選項；量尺為整數、`min < max`、最多 11 個刻度；用不到的屬性必須是空的。這是寫入前的最後防線，**給使用者看的逐欄錯誤**屬於 #143 的 Application 規則。
- 沒有紀錄數量、資料管理者、提交或連接的欄位：它們是 #144–#148 各自的新資料表，以複合外鍵指回 `Databases`（已為此建立 `(Id, OrganizationId)` 替代鍵；`DatabaseFormVersions` 也有，供 #145 的提交指向「提交當時的版本」）。

## 2. API 契約（`apps/api/openapi/v1.json`）

| 端點 | 權限 | 成功 | 錯誤 |
| --- | --- | --- | --- |
| `GET /api/v1/database-templates` | 登入＋`manage-data-sources` | `200 DatabaseTemplateView[]`（五個模板，固定順序） | `401`；`403 database`「只有可管理資料來源的帳號可以建立資料庫。」 |
| `GET /api/v1/databases` | 登入 | `200 DatabaseSummaryView[]`，只有自己擁有的，舊到新 | `401` |
| `POST /api/v1/databases` `{templateId, name, purpose?}` | 登入＋`manage-data-sources` | `201 DatabaseSummaryView`＋`Location` | `401`；`403 database`（同上）；`422`：`message`＋`errors.templateId`／`name`／`purpose`，什麼都不寫入 |
| `GET /api/v1/databases/{id}` | 登入＋擁有者 | `200 {summary, form:{id, versionNumber, createdAt, fields}}` | `401`；`403 database`「你沒有這個資料庫的存取權限，或它已不存在。」 |

- `DatabaseSummaryView`：`id`、`name`、`purpose`、`templateId`、`templateName`、`fieldCount`、`formVersion`、`owner {id, displayName}`、`createdAt`、`updatedAt`（資料列與目前表單版本較晚者）、`viewerCanManage`。
- **不洩漏：** 不存在、別人的、別的組織的 id 回傳逐位元組相同的 `403 database`（整合測試比對狀態、內容類型、本文與 cookie），本文不含名稱、欄位或數量。任何回應都**沒有紀錄或對象數量**，直到 #144／#146 有了「資料管理者指定＋帳號讀取權限」的判斷點。
- 與派工描述的差異：本 repo 既有資源一律以 `403`（同 `ApiErrors.NotFound`）代替 `404`、以 `422` 代替 `400`，數據庫照同一慣例，不另開 `404`／`400`。只有 id 不是 GUID 時路由不符而得到框架的 `404`，前端與 `403` 同樣處理。

## 3. 權限與可見範圍

- 建立與取得模板：帳號權限 `manage-data-sources`（與知識庫相同）。
- 清單、詳情：#142 時只有擁有者（`DatabaseAccess.ListedFor`／`ManageableBy` 相同）；**#144 起 `ListedFor` 放寬為「擁有者，或指定且有權限的資料管理者」**。**擁有不代表能讀提交內容**：讀紀錄要同時有資料管理者指定與 `read-consented-submissions`（前端 `canReadConsentedRecords` 的規則，#144 移到服務端）。#144 讓資料管理者看得到所管理的數據庫時，放寬的是 `ListedFor`，`ManageableBy` 維持只有擁有者。

## 4. 前端

- `listDatabaseTemplates`、`listDatabaseSummaries`、`createDatabaseFromTemplate`、`getDatabaseDetail` 改為 `Observable` 契約（不再傳 viewer），mock 與 Hybrid 一致；Hybrid **讀取與建立都走 API**。
- 詳情的 `upcomingFeatures` 列出 API 模式尚未提供的功能（`form-editing`、`data-managers`、`records`、`assistant-connections`），畫面改為「將於後續版本開放」並唯讀列出初始表單；mock 為空陣列。各工單完成時從 `API_UPCOMING_DATABASE_FEATURES` 移除自己那一項。
- 可恢復狀態：清單／模板讀取失敗與「沒有資料」分開顯示並可重試；建立失敗保留輸入、可再送出；送出中不能重複送出；伺服器的 `422` 訊息顯示在名稱欄位旁。

## 5. 留給後續工單的介面

- **#143 編輯表單與試填**：已實作，見下方第 7 節（端點、錯誤鍵與並發規則）。
- **#144 指定資料管理者**：新表 `DatabaseDataManagers(DatabaseId, AccountId, OrganizationId, AssignedByAccountId, AssignedAt)`，複合外鍵到 `Databases` 與同組織帳號；讀取判斷 = 指定 **且** 帳號有 `read-consented-submissions`，每次查詢重新計算。
  - **已完成（#144，2026-10-03）**：資料表如上（主鍵 `(DatabaseId, AccountId)`、兩條含 `OrganizationId` 的複合外鍵、`Cascade`），另加 append-only 的 `DatabaseDataManagerChanges`（每次指定／移除一列：帳號、動作、操作人、時間；無外鍵）。`PUT /api/v1/databases/{id}/access` 以完整清單取代指定（只有擁有者；`422` 不認得或別組織的帳號；非擁有者含資料管理者一律同一則 `403 database`）；`GET /api/v1/databases/{id}` 多回 `access`。
  - 放寬 `ListedFor` 的做法：`DatabaseAccess.ListedFor(viewer, 帳號有無讀取權限, 指定查詢)` = 擁有者，或（有權限且被指定）。`ManageableBy`／`viewerCanManage` 維持只有擁有者，所以資料管理者開詳情是唯讀。
  - **「誰可讀紀錄」的判斷點**：`DatabaseRecordAccess`（`CanRead`、`ReadableBy`，可在記憶體測試）與 Api 的 `DatabaseRecordReaders`（`CanReadAsync`、`ReadableDatabaseIdsAsync`、`EffectiveReaderIdsAsync`）；帳號權限來自每次請求重讀的 `RequestAccountPermissions`，所以撤銷指定或權限在下一個請求立即失效。#146／#147 在回傳任何紀錄、數量、趨勢前呼叫它（紀錄表本票尚未建立）。
  - 建立資料庫時建立者自動成為唯一的資料管理者（mock 同）；既有資料庫在 migration 內補同一列。
  - 與 #142 文件第 2 節的差異：詳情 `{summary, form}` 多了 `access`；`GET /databases`、`GET /databases/{id}` 不再只回擁有者的資料庫。
- **#145 提交與回執**：已實作，見下方第 8 節（資料模型、API 契約、給 #146／#147／#148 的接點）。
- **#146 查看紀錄與撤回**：已實作，見下方第 9 節（資料保留規則、撤回語意、API 契約、給 #147 的接點）。
- **#148 連接助理**：目前 `AssistantEndpoints` 對資料庫來源回「將於後續版本開放」；連接表以複合外鍵指向 `Databases`。

## 6. 驗收紀錄

見 PR 說明；後端整合測試在 `apps/api/tests/SmartAgri.Api.Tests/Databases/DatabaseEndpointsTests.cs`，API 模式 E2E 在 `apps/admin-e2e/src/e2e-api/database-api.cy.ts`。

## 7. #143 編輯表單與試填（2026-10-03）

| 端點 | 權限 | 成功 | 錯誤 |
| --- | --- | --- | --- |
| `PUT /api/v1/databases/{id}/form` `{baseVersionNumber, fields:[{id?, label, type, required, options, scale?, unit}]}` | 登入＋擁有者 | `200 DatabaseFormView`（`id`、`versionNumber`、`createdAt`、`fields`） | `401`；`403 database`（不存在、別人的、別的組織的逐位元組相同）；`409 form-version-changed`；`422`（`message`＋`errors`） |
| `POST /api/v1/databases/{id}/form/preview` `{answers:{<欄位 id>: 字串 \| 字串陣列}}` | 登入＋擁有者 | `200 {saved:false, formVersion, entries:[{fieldId,label,display}]}` | `401`；`403 database`；`422`（`errors` 鍵 `answers.<欄位 id>`） |

- **檢查順序（儲存）**：擁有者 → `baseVersionNumber` 缺漏（422）→ 是否為目前版本（409）→ 欄位驗證（422）→ 寫入。任何一步失敗都不寫入。與派工描述的差異：路徑用 `/form` 與 `/form/preview`（不用 `:preview`，路由沒有先例），一律 `403`／`422` 不開 `404`／`400`。
- **版本規則**：寫入列的 `VersionNumber` 是「目前 + 1」，列永遠不更新；兩個請求從同一版本同時儲存，輸家撞 `(DatabaseId, VersionNumber)` 唯一索引，與「版本過期」同一個 `409`（整合測試同時送兩個請求，結果一定是一個 200、一個 409、只多一列）。沒有任何變動（逐欄位逐設定相同）時回目前版本、不新增列。
- **欄位驗證**（`DatabaseFormRules`，訊息與前端 mock 的 `validateFields` 逐字相同，每個欄位只回報第一個錯誤）：鍵為 `fields`（整份表單：沒有欄位／超過 50 個）、`fields[i]`（元素是 null）、`fields[i].id｜type｜label｜options｜scale｜unit`。正規化：名稱、選項、單位去頭尾空白，空白選項丟棄，與類型無關的設定清空，量尺沒給範圍時用 1–5（同編輯器預設）。`DatabaseFormVersion.EnsureValid` 仍是最後防線；Application 規則把它檢查的每一項都先擋成訊息，所以正常請求不會觸發它的例外。
- **欄位 id**：留下的欄位沿用同一個 id；新欄位不帶 id 由伺服器產生；id 必須符合 `field-…` 格式、請求內不重複、**不得是先前版本用過、目前版本已移除的 id**（避免舊版本提交裡的同一個 id 指到另一個問題）。測試以 JSON 比對證明版本 1、2 在第 3 版寫入後內容逐字不變。
- **試填**：`DatabaseAnswerRules.Validate(fields, answers)` 回傳每個欄位的 `DatabaseAnswerEntry`（顯示文字，加上 #145 要存的型別化值：文字／數字／選項清單）；這就是日後正式提交要呼叫的同一個函式，所以「試填通過 = 提交通過」。永遠對目前最新版本驗證；測試以前後資料列數證明不寫入任何資料。答案中不在表單裡的欄位 id 忽略；非字串或字串陣列的值視為未填。
- **前端**：`updateDatabaseFields(id, fields, baseFormVersion)`、`previewDatabaseEntry(id, answers)` 改為 `Observable`；結果多了 `conflict`；`DatabaseDetailView.formVersion`、`DatabaseTrialPreviewView.formVersion` 新增。畫面儲存中停用按鈕避免重複送出；欄位錯誤標在對應欄位；`409` 顯示「重新載入最新表單」；5xx 與連線中斷保留草稿、顯示可再試的訊息；試填失敗保留答案。


## 8. #145 同意提交與回執（2026-10-03）

### 8.1 提交入口與 #148 的分界

- **本票的入口是「表單連結」**（`/app/forms/{databaseId}`，來源 `form-link`）：同組織、具備帳號權限 `submit-authorized-forms` 的帳號（示範資料的外部客戶）填寫數據庫目前的表單。數據庫不存在、別的組織的、沒有權限一律同一則 `403 authorized-form`，不透露名稱、欄位或可查看者。擁有者與資料管理者沒有這個權限就不能提交（他們用試填）。
- **提交是應用服務 `DatabaseSubmissionService`**（`apps/api/src/SmartAgri.Api/Databases/DatabaseSubmissionService.cs`），端點只是其中一個入口。服務負責入口以外的所有伺服器端檢查；**入口授權由呼叫端負責**：表單連結檢查 `submit-authorized-forms`，#148 的對話表單要先檢查助理可用、助理已連接這個數據庫、分享未撤回，再以 `DatabaseSubmissionSource.AssistantConversation` 呼叫同一個 `SubmitAsync`（同一套欄位驗證、同意、冪等與回執）。#148 只需新增入口與連接表，不另寫提交邏輯。
- 私人對話內容永遠不是輸入：提交只帶表單答案。

### 8.2 資料模型（migration `AddDatabaseSubmissions`）

| 資料表 | 內容 | 規則 |
| --- | --- | --- |
| `DatabaseSubmissions`（**軌跡**，不含填寫內容） | `Id`、`DatabaseId`、`FormVersionId`／`FormVersionNumber`、`SubmittedByAccountId`、`IdempotencyKey`、`Source`（`form-link`／`assistant-conversation`）、`ReceiptNumber`（`R-yyyyMMdd-` + id 末 10 碼）、`SubmittedAt`、`ConsentTerms`（jsonb：資料庫名稱、目的、接收單位、送出當下實際可查看者、敏感資料提示）、`WithdrawnAt`（#146 用，本票恆為 null） | 三個複合外鍵都含 `OrganizationId`（→ `Databases`、`DatabaseFormVersions (Id, OrganizationId)`、同組織帳號），全部 `Restrict`；`(SubmittedByAccountId, IdempotencyKey)` 唯一（冪等）；`(OrganizationId, ReceiptNumber)` 唯一；`(DatabaseId, SubmittedAt)` 索引 |
| `DatabaseSubmissionEntries`（**內容**，一欄一列） | `SubmissionId`、`Position`、`FieldId`、欄位快照 `Label`／`FieldType`／`Unit`／`Display`，型別化值 `TextValue`（文字、日期、單選）／`NumberValue`（數字、量尺）／`ChoiceValues`（`text[]`，依表單選項順序）；選填未填三者皆空、`Display` 為「未填寫」 | 主鍵 `(SubmissionId, FieldId)`；複合外鍵到 `DatabaseSubmissions (Id, OrganizationId)`，`Cascade` |

- 成功提交只有一次 `SaveChanges`（一個交易）同時寫入軌跡與每個欄位；任何檢查失敗都不寫入任何列（整合測試以兩張表的列數驗證）。
- **快照**：回執與紀錄只讀 `ConsentTerms` 與 entries，表單之後改版、改名或改指定都不影響（測試以 JSON 逐字比對）。
- **為撤回預留（#146）**：撤回 = 刪除該筆的 entries（真正刪除內容與數值）＋設定 `WithdrawnAt`；`DatabaseSubmissions` 留下「曾提交、何時撤回、來源、表單版本、提交者」且不含任何填寫內容。`ConsentTerms` 是當時告知的條款（非填寫內容），保留作為同意的證據；若 #146 認定也要清除，可改成刪除時一併改寫。重送比對讀的是 entries，所以撤回後再用同一個提交編號重送會得到 `409 submission-key-reused`（不會復活內容）；#146 可改成回傳已撤回的回執。

### 8.3 API 契約

| 端點 | 權限 | 成功 | 錯誤 |
| --- | --- | --- | --- |
| `GET /api/v1/databases/{id}/submission-form` | 登入＋`submit-authorized-forms` | `200 DatabaseSubmissionFormView`（`databaseName`、`purpose`、`recipient`「組織（數據庫）」、`viewers` 目前實際可讀者、`sensitiveNotice`、`withdrawalNotice`、`form`） | `401`；`403 authorized-form`（不存在／別組織／無權限逐位元組相同） |
| `POST /api/v1/databases/{id}/submission-form/review` `{formVersionNumber, answers}` | 同上 | `200 DatabaseTrialPreviewView`（`saved:false`，**不寫入**） | `403`；`409 form-version-changed`；`422`（`answers.<欄位 id>`） |
| `POST /api/v1/databases/{id}/submissions` `{submissionId, formVersionNumber, consent, answers}` | 同上 | `201 DatabaseSubmissionReceiptView`＋`Location: /api/v1/submissions/{id}`；同一個 `submissionId`、同內容重送 `200` 同一張回執 | `401`；`403 authorized-form`；`409 form-version-changed`；`409 submission-key-reused`；`422`（缺 `submissionId`／`formVersionNumber`；欄位；未同意 `reason: consent-required`、鍵 `consent`） |
| `GET /api/v1/submissions/{id}` | 登入＋提交者本人 | `200 DatabaseSubmissionReceiptView` | `403 authorized-form`「找不到這張回執，或你沒有查看它的權限。」（不存在、別人的、別組織的相同；資料管理者也拿不到別人的回執） |
| `GET /api/v1/databases/{id}/records` | 登入＋`DatabaseRecordReaders.CanReadAsync`（指定且具權限，每次請求重查） | `200 DatabaseRecordListView`（未撤回的紀錄，新到舊，含提交者與欄位快照） | `403 database`（看不到數據庫，同不存在）；`403 database-records`（看得到但不能讀紀錄，例如擁有者把自己移除） |

- **檢查順序（提交）**：入口權限（403）→ 數據庫在組織內（403）→ `submissionId`／`formVersionNumber`（422）→ 同一提交者用過此編號：同數據庫、同版本、同來源、同意、型別化值相同 → 原回執 `200`，否則 `409 submission-key-reused` → 版本不是目前版本（409）→ 欄位 `DatabaseAnswerRules`（與試填同一套，422）→ 同意（422，欄位錯誤優先，同 mock）→ 寫入。
- **同鍵不同內容選 409**（不是 422）：請求本身格式正確，衝突的是伺服器上已存在的狀態，與 `form-version-changed` 同類；前端以 `reason` 區分並換新編號。
- **並行重送**：兩個請求同時用同一編號，輸家撞 `(SubmittedByAccountId, IdempotencyKey)` 唯一索引（以索引名稱辨識），清掉追蹤後走重送比對，回同一張回執。整合測試同時送 4 個請求：恰一筆軌跡、一組 entries、所有回應同一個 id。
- **已知取捨**：版本檢查與寫入之間若擁有者剛好存了新版，紀錄仍指向成員看到並同意的那一版（內容與版本一致），不另加鎖。可查看者在提交資訊與送出之間若被變更，回執記錄的是**送出當下**的可查看者。

### 8.4 前端

- `getDatabaseSubmissionForm`、`reviewDatabaseSubmission`、`submitDatabaseEntry`、`getDatabaseSubmissionReceipt`（`Observable` 契約）；mock 與 Hybrid 一致，Hybrid **讀寫兩端都走 API**（mock storage 不寫入，測試驗證）。mock 的表單連結紀錄寫進收集紀錄（來源「表單連結」），資料管理者在 mock 的時間軸看得到；回執存在 `sme-demo:database-submissions`。
- 填寫頁 `features/databases/database-submission/`：填寫 → 伺服器檢查 → 同意（`app-consent-confirmation`，送出中停用）→ 回執（網址帶 `?receipt=<id>`，重新整理仍可看）。提交編號由前端 `crypto.randomUUID()` 產生，失敗重試沿用、成功或衝突後換新；5xx／連線中斷保留答案並提示「再按一次不會重複建立紀錄」；`409` 顯示「重新載入最新表單」。詳情頁的摘要多了「表單連結」。
- `API_UPCOMING_DATABASE_FEATURES` **保留 `records`**：收集紀錄頁籤的時間軸、撤回軌跡與趨勢是 #146／#147；本票只提供 `GET .../records` 原始清單給 #146 接。

### 8.5 給後續工單的接點

- **#146**：提交者自己的清單可用 `DatabaseSubmissions.SubmittedByAccountId`；撤回刪 `DatabaseSubmissionEntries`、設 `WithdrawnAt`（軌跡表已預留）；資料管理者時間軸以 `ListRecordsAsync` 為起點（已排除 `WithdrawnAt` 非 null），追蹤對象＝提交者。
- **#147**：型別化值在 `DatabaseSubmissionEntries`（`NumberValue`／`TextValue`／`ChoiceValues`），以穩定的 `FieldId` 跨版本比較；查詢前呼叫 `DatabaseRecordReaders`，只看 `WithdrawnAt IS NULL`。
- **#148**：見 8.1；回執 `DatabaseSubmissionReceiptView` 可直接放進對話的 `submission-receipt` 訊息，`Source = assistant-conversation`。


## 9. #146 查看紀錄與撤回（2026-10-03）

依據：[撤回與保存 ADR](../adr/2026-09-25-withdrawal-and-retention.md)（撤回＝真正刪除；只留不含內容的軌跡；既有定期報表不追溯改寫）。

### 9.1 資料保留規則（撤回後留下什麼）

| 資料 | 撤回後 | 理由 |
| --- | --- | --- |
| `DatabaseSubmissionEntries`（每欄的顯示值、型別化值、欄位名稱／型別／單位快照） | **整列刪除** | 這就是填寫內容與數值 |
| `DatabaseSubmissions`：提交者、數據庫、表單版本（id 與號碼）、來源、回執編號、`SubmittedAt`、冪等鍵 | 保留，設 `WithdrawnAt` | ADR 要求的「曾提交、何時撤回、來源」軌跡；回執編號讓提交者對得上自己的回執 |
| `DatabaseSubmissions.ConsentTerms`（當時的數據庫名稱、目的、接收單位、可查看者名單、敏感資料提示） | **保留** | 不是提交者填的內容，而是組織當時告知並取得同意的條款，是「曾經同意過什麼」的證據；撤回後的回執仍需顯示交給了誰。名單是資料管理者的名稱，不是提交者的資料 |

- 其他可能殘留內容的地方（已逐一檢查）：API 沒有記錄請求本文的中介軟體，EF Core 沒有開 `EnableSensitiveDataLogging`，OpenTelemetry 的 Npgsql span 只記 SQL 文字不記參數值；回執與清單每次都從資料表讀，伺服器沒有回執快取。mock 的回執快取（`sme-demo:database-submissions`）撤回時一併清空 `entries`，收集紀錄的 `values` 清空。
- **對話訊息**：API 模式目前沒有對話中的表單（#148）。#148 實作 `submission-receipt` 訊息時，訊息本身**只存提交 id**、顯示時讀回執（撤回後自然沒有內容），不要把 `entries` 複製進對話訊息；否則撤回後對話裡仍留有內容。mock 的對話收據（既有行為）在撤回後會把撤回狀態疊上去，但訊息裡的 `entries` 仍在提交者本人的私人對話中，這是 mock 的已知差異，#148 時一併處理。
- 資料表沒有「撤回不可復原」以外的狀態：`WithdrawnAt` 一旦設定不會清除，撤回後不能再提交同一份（見 9.3 同鍵重送）。

### 9.2 撤回語意

- **只有提交者本人**可以撤回，與帳號權限無關（`read-own-tracking` 刻意不接行為：管理者不應能關掉一個人看或撤回自己資料的能力，見 `tasks-6-10-backend-handoff.md` 第 8 節第 9 點）；資料管理者、擁有者都不能代為撤回。
- 同一個交易：`UPDATE "DatabaseSubmissions" SET "WithdrawnAt" = now WHERE "Id" = @id AND "SubmittedByAccountId" = @me AND "WithdrawnAt" IS NULL`，影響 1 列才 `DELETE` 該筆的 entries，然後 commit（`DatabaseSubmissionService.WithdrawAsync`）。任何一步失敗就整個 rollback，內容與 `WithdrawnAt` 一起維持原狀，畫面可以再按一次。
- **冪等（擇一：200）**：再撤回一次回 `200` 與第一次逐字相同的回執（同一個 `withdrawnAt`），不是錯誤碼——使用者要的狀態已經達成，重試（例如回應在路上遺失）不該看到失敗。
- **並行**：多個撤回同時進來時，條件式 `UPDATE` 在同一列上排隊，只有一個符合 `WithdrawnAt IS NULL`；其餘在它 commit 後符合 0 列、不刪任何東西，全部讀回同一份軌跡（整合測試同時送 4 個：全部 200、內容相同、entries 0 列）。
- `ExecuteUpdate` 不經過 `TimestampPrecisionInterceptor`，所以撤回時間先截到微秒再寫入，回傳值與之後讀到的一致。

### 9.3 撤回後

- **回執**（`GET /api/v1/submissions/{id}`）：`entries: []`、`withdrawnAt` 有值，其他欄位（回執編號、送出時間、接收單位、可查看者、表單版本、來源）不變。有效時 `withdrawnAt` 是 `null`（一律送出，不省略）。
- **同鍵重送**（`POST .../submissions` 用已撤回那份填寫的 `submissionId`）：同數據庫、同表單版本、同來源 → `200` 已撤回的回執（不含內容、不寫入任何列）；內容已刪除無從比對，所以不論答案為何都一樣。不同數據庫／版本／來源 → 照舊 `409 submission-key-reused`。選擇回回執而不是 `409` 的理由：重送的是同一份填寫，它的最終狀態就是「已撤回」，告訴用戶端這個事實比「編號已用在別的內容」更正確，而且絕不會讓內容復活。
- **紀錄清單、時間軸與任何計數都排除已撤回**：唯一的定義是 `DatabaseActiveRecords`（`apps/api/src/SmartAgri.Api/Databases/DatabaseActiveRecords.cs`）——`Of(db, databaseId)` = `WithdrawnAt IS NULL`；`EntriesOf(db, submissions)` 取型別化值；`ReadableAsync(db, permissions, accountId, databaseId)` 先問 `DatabaseRecordReaders.CanReadAsync`，不能讀回 `null`。`ListRecordsAsync`、時間軸都用它。

### 9.4 追蹤對象（subject）的邊界

- mock 的追蹤對象有兩種來源：示範種子裡手寫的人物（王小姐等，`demo-seed-databases.ts` 的 `trackedSubjects`），以及對話／表單連結提交時的 `subject-<提交帳號 id>`。工單與 ADR 都以「提交者」為撤回與查看的主體，所以 **API 模式的追蹤對象＝提交的帳號**（同一數據庫內以 `SubmittedByAccountId` 分組），不從表單欄位（例如「客戶姓名」）推導：欄位值是提交者填的內容，撤回後就刪了，不能拿來當身分；也無法驗證兩筆填同一個名字的是不是同一人。
- 邊界：時間軸只包含提交到**這個**數據庫的帳號，所以資料管理者不可能經由 A 數據庫看到只在 B 提交過的人；組織以查詢過濾器與複合外鍵隔離；讀取一律先過 `DatabaseRecordReaders`（指定＋帳號權限，每次請求重查）。只剩撤回軌跡的對象仍列出（不能無聲消失，與 mock 相同）。
- 前端 id：`subject-<帳號 GUID>`（與 mock 的寫法相同）、紀錄 id `record-<提交 GUID>`，只為對上樣板字面型別。

### 9.5 API 契約

| 端點 | 權限 | 成功 | 錯誤 |
| --- | --- | --- | --- |
| `GET /api/v1/submissions` | 登入（只依提交者本人） | `200 DatabaseOwnSubmissionListView`：`submissions[]`＝`id`、`receiptNumber`、`submittedAt`、`databaseId`、`databaseName`（同意當下的快照）、`formVersionNumber`、`source`、`withdrawnAt`（`null`＝有效）；新到舊，含已撤回，**不含內容** | `401` |
| `GET /api/v1/submissions/{id}` | 登入＋提交者本人 | `200 DatabaseSubmissionReceiptView`（多了 `withdrawnAt`；已撤回時 `entries: []`） | `401`／`403 authorized-form`（不存在、別人的、別組織的同一則） |
| `POST /api/v1/submissions/{id}/withdrawal` | 登入＋提交者本人 | `200 DatabaseSubmissionReceiptView`（已撤回；再撤回一次逐字相同） | `401`／`403 submission-withdrawal`「找不到這筆紀錄，或你沒有撤回它的權限。」（不存在、別人的——含資料管理者——別組織的逐位元組相同）／`404`（id 不是 GUID） |
| `GET /api/v1/databases/{id}/tracking` | 登入＋`DatabaseRecordReaders.CanReadAsync` | `200 DatabaseTrackingView`：`subjects[]`＝`subject {id, displayName}`、`records`（有效紀錄，含內容，同 `/records` 的形狀）、`withdrawals`（`id`、`submittedAt`、`withdrawnAt`、`source`、`formVersionNumber`，**不含內容**）；對象依最近一次提交新到舊 | `401`／`403 database`（看不到數據庫，同不存在）／`403 database-records`（看得到但不能讀） |

整合測試：`apps/api/tests/SmartAgri.Api.Tests/Databases/DatabaseRecordWithdrawalEndpointsTests.cs`（7 個：自己的清單、他人／他組織不可讀不可撤且不洩漏、撤回後直接查表確認無內容且軌跡保留、再撤回與並行撤回、同鍵重送、時間軸分開有效與撤回且清單與計數排除、資料管理者需指定＋權限且不跨數據庫／組織）。

### 9.6 前端

- 契約：`listOwnDatabaseSubmissions()`、`withdrawDatabaseSubmission(id)`（新增）；`getDatabaseTracking(databaseId)` 改為 `Observable`、不再傳 viewer。mock 原本的同步本體改名 `readDatabaseTracking(viewer, id)`，只給 mock 內部與單元測試，不在 `DemoRepository` 契約內。Hybrid 三個方法都走 API（測試以真實 API JSON 驗證，且 mock storage 不被寫入）。回執多 `withdrawnAt`。
- 畫面：「對話與回報紀錄」（`/app/activity`）新增「我送出的資料」（`features/activity/own-submissions/`）：載入、錯誤可重試、無權限、空白分開顯示；撤回要先確認，送出中停用按鈕，失敗（5xx／連線中斷）保留確認區塊並說明「沒有任何變更」可再按一次；伺服器拒絕時顯示其訊息並重新讀取清單。回執頁顯示已撤回狀態且不顯示內容，並連到「我送出的資料」。數據庫詳情的「收集紀錄」頁籤在 API 模式開放（讀取失敗與「沒有紀錄」分開、可重試）。
- `API_UPCOMING_DATABASE_FEATURES`：`records` 移除，改為 `trends`（#147）。`DatabaseUpcomingFeature` 從 `'records' | …` 改成 `'trends' | …`。API 模式的 `comparison` 是「趨勢比較將於後續版本開放」的佔位、`periodicReports` 為空陣列，畫面在趨勢頁籤顯示將於後續版本開放，不用顯示文字自己算差異。
- 提交前的撤回說明（後端 `DatabaseSubmissionRules.WithdrawalNotice` 與 mock 逐字相同）改成說明到哪裡撤回、撤回的效果，以及既有定期報表不追溯修改。
- mock 只列出表單連結的提交（回執存在 `sme-demo:database-submissions`）；對話中送出的資料在 mock 仍從對話收據撤回（既有行為）。API 模式的清單會包含所有來源，#148 加上對話來源後自然出現。

### 9.7 給 #147 的接點

- 有效紀錄＝`DatabaseActiveRecords.ReadableAsync(...)`（或已檢查 `CanReadAsync` 後的 `Of`），型別化值用 `EntriesOf`；**不要**自己寫 `WithdrawnAt` 條件或繞過 `DatabaseRecordReaders`。撤回後重新查詢即排除，已產生的定期報表不追溯改寫（ADR）。
- 追蹤對象＝`SubmittedByAccountId`；欄位以穩定的 `FieldId` 跨版本比較；`NumberValue`（數字、量尺）是趨勢的來源。
- 前端：`TrackedSubjectView.comparison` 與 `DatabaseTrackingView.periodicReports` 由 #147 從伺服器填入（可擴充 `GET .../tracking` 或另開固定查詢端點），完成後從 `API_UPCOMING_DATABASE_FEATURES` 移除 `trends`。
- 數據庫清單摘要的 `recordCount`／`subjectCount` 在 API 模式仍為 `null`：若 #147 要顯示，用 `DatabaseActiveRecords` 計數並只對可讀者回傳。


## 10. #147 趨勢與固定統計查詢（2026-10-03）

依據：[助理存取知識庫與數據庫 ADR](../adr/2026-09-25-assistant-access-to-knowledge-and-databases.md)（讀取只能走服務端事先寫好的固定查詢：依期間計數、加總、比較；不接受 SQL）、[撤回與保存 ADR](../adr/2026-09-25-withdrawal-and-retention.md)、[定期報表 ADR](../adr/2026-09-25-periodic-reports.md)。

### 10.1 固定查詢清單

定義在 `DatabaseFixedQueries.Definitions`（Application；名稱、必填與選填參數），執行在 `DatabaseFixedQueryService`（Api；`RunAsync(kind, accountId, databaseId, parameters)` 或四個具名方法）。

| 查詢（wire name） | 參數 | 結果 | 說明 |
| --- | --- | --- | --- |
| `record-count` | 期間（`period` 或 `from`＋`to`）；選填 `subjectId` | `DatabaseRecordCountResult`：`count`、`previousCount`、`change`、`changeLabel`（`+3 筆`／`持平`） | 有效紀錄筆數與前一期 |
| `field-sum` | 期間；必填 `fieldId`（**數字**欄位）；選填 `subjectId` | `DatabaseFieldSumResult`：`field`＝`sum`、`display`（`1,200 元`）、`recordCount`、`previousSum`、`change`、`changeLabel` | 一個數字欄位的加總與前一期；量尺不可加總（`422`） |
| `period-summary` | 期間；選填 `subjectId` | `DatabasePeriodSummaryResult`：筆數＋每個數字欄位的 `sums[]`（同上）＋前一期 | 趨勢頁籤用的「期間統計」 |
| `subject-comparison` | 必填 `subjectId`；選填 `fieldId`（數字或量尺） | `DatabaseSubjectComparisonResult`：`comparison`＝`status`（`available`／`insufficient-records`）、`recordCount`、`message`／`summary`、`metrics[]`（首次／上次／本次、變化與標籤、`direction`、`points`、`axis`、`summary`） | 同一份 `comparison` 也放在 `GET .../tracking` 每位追蹤對象裡，兩處逐字相同（整合測試比對） |

端點：`GET /api/v1/databases/{id}/queries/{record-count|field-sum|period-summary|subject-comparison}`，參數放 query string；其他名稱是 `404`，沒有「任意查詢」的路由。

### 10.2 參數規則（`DatabaseFixedQueries.Validate`）

- **只收定義內的**：不在該查詢定義內的參數名稱（例如 `sql`、`select`、`expression`、對 `record-count` 傳 `fieldId`）、重複的鍵（`?period=a&period=b`）都是 `422`，鍵就是那個參數名；必填缺漏、值不在定義內同樣是 `422`（每個失敗各一個 `errors.<參數>`）。
- **期間**：`period` 是 `this-week`／`last-week`／`this-month`／`last-month`／`last-7-days`／`last-30-days`，或 `from`＋`to` 兩個真實存在的 `yyyy-MM-dd`（`from` 不晚於 `to`、最長 366 天、2000-01-01 至 2100-12-31）；兩種擇一，不可並用。
- **欄位**：必須存在於這個數據庫**目前或任何歷史表單版本**（以該 id 最新的那一版定義為準，已移除的欄位仍可查）；`field-sum` 要數字，`subject-comparison` 要數字或量尺。
- **對象**：`subjectId` 是提交帳號的 id，且必須**提交過這個數據庫**（含已全部撤回的，與時間軸列出的一致）。不是 GUID、不存在、別的數據庫的對象、別組織的帳號、從未提交過的管理者，**一律同一則** `422 subjectId`「找不到這位追蹤對象。」（整合測試比對逐位元組相同）。
- **順序**：先判斷能不能讀這個數據庫的紀錄（`DatabaseActiveRecords.ReadableAsync`，每次呼叫重查指定與帳號權限），再驗參數。所以無權者不論參數對錯都得到同一個 `403`（`database` 或 `database-records`，與時間軸相同），看不到任何欄位或對象的資訊。

### 10.3 日期與時區邊界

- **一律 UTC 曆日**，與時間軸、紀錄的 `dateLabel`（ISO 時間的日期部分）同一天；紀錄屬於 `SubmittedAt` 落在的 UTC 日。期間含起訖兩日（`[from 00:00Z, to+1日 00:00Z)`），所以 `23:59:59.999Z` 屬當天、`00:00:00Z` 屬隔天。台灣使用者在 08:00 前送出的資料，統計上算前一天；組織時區是之後的課題（要加組織設定，兩邊一起換）。
- 週從週一到週日；`this-week`／`this-month` 涵蓋整週／整月（含尚未發生的日子），`last-7-days`／`last-30-days` 到今天為止。
- **前一期**是完整的前一期：上週、上月（月份天數不同照曆月）；滾動與自訂期間則是緊接在前、同樣天數。單元測試 `DatabaseFixedQueriesTests`（後端）與 `database-tracking.spec.ts`（前端）用同一組案例。

### 10.4 計算與資料不足

- **比較**（`DatabaseQueryResults.Compare`）：依穩定的 `FieldId` 跨版本比較；指標取自**最新一筆**紀錄的數字／量尺欄位，每個指標的點是有這個 id 且**型別與單位和最新一筆相同**的紀錄（單位改過的不混進同一條趨勢），至少 2 個點才成立。量尺的圖表縱軸取量尺範圍（與值取聯集），數字取值的最小最大。文字：`本次 X，較上次 +N，較首次 +M`、無變化 `持平`、量尺單位 `分`，數字格式與回執相同（千分位、最多 3 位小數）。
- **加總**（`DatabaseQueryResults.Sums`）：只加總型別是數字、且單位等於該欄位最新單位的值；每個欄位是 `SELECT SUM … GROUP BY FieldId, Unit`（一次查詢，成本不隨筆數成長；`ToQueryString()` 確認過沒有逐欄的純量子查詢）。目前表單的數字欄位即使 0 也列出；已移除的欄位只在這兩個期間有值時才列出。
- **資料不足**：沒有紀錄或只有一筆 → `insufficient-records`＋`message`（`目前只有 N 筆紀錄，累積 2 筆以上才會顯示比較與趨勢。`）、沒有指標；有 2 筆以上但沒有任何欄位累積 2 個數值 → 同狀態，訊息改為 `目前有 N 筆紀錄，但沒有任何數字或量尺欄位累積 2 筆以上的數值，無法比較。`（不回「available 但沒有指標」的空趨勢）。筆數與加總沒有「不足」：0 就是 0，`recordCount` 讓呼叫端分辨「沒資料」。
- **撤回**：撤回刪除 entries 並設 `WithdrawnAt`，所有查詢經 `DatabaseActiveRecords`，下一次查詢就排除（整合測試：逐筆撤回後筆數、加總、比較、時間軸同步變）。已產生的定期報表不追溯改寫是 #150 的規則（ADR）。

### 10.5 前端

- `TrackedSubjectView.comparison` 由伺服器填入（Hybrid 只轉換型別）；新增 `getDatabasePeriodSummary(databaseId, {period, subjectId})`（`Observable` 契約；mock 以 `now()` 的 UTC 日為今天計算，`summarizePeriod`／`resolvePeriod`／`previousPeriod` 在 `database-tracking.ts`，與後端同一套規則）。
- 趨勢頁籤：變化摘要＋比較表＋趨勢圖（紀錄不足時只顯示說明，不畫圖）；新增「期間統計」（`features/databases/period-summary/`，預設近 30 天，可切換期間；讀取失敗與「沒有紀錄」分開、可重試）；每位追蹤對象底下有「查看…的原始紀錄」連到收集紀錄頁籤（`?subject=`）。
- `DatabaseUpcomingFeature`：`'trends'` 移除，改為 `'periodic-reports'`（#150）；API 模式的趨勢頁籤只在有追蹤對象時顯示「定期回報摘要：這項功能將於後續版本開放」，`periodicReports` 仍是空陣列。
- 摘要清單的 `recordCount`／`subjectCount` 仍為 `null`（本票沒有需要顯示它的畫面；要顯示時用 `DatabaseActiveRecords` 計數且只對可讀者回傳）。

### 10.6 測試

- 後端單元：`DatabaseFixedQueriesTests`（期間解析、前一期、參數規則）、`DatabaseQueryResultsTests`（比較、不足、單位不混、加總、標籤）。
- 後端整合（`DatabaseFixedQueryEndpointsTests`，真實 PostgreSQL，10 個）：無權限／未指定／他組織／權限撤銷／指定移除皆拒絕且不洩漏（錯誤參數同一個 403）；對象不在這個數據庫一律同一則 422；不在定義內的參數與值 422、無「任意查詢」路由；空資料與單筆＝紀錄不足；跨期邊界與跨對象計數、加總；具名期間；撤回後各查詢與時間軸同步排除；表單改版（改名、新增、移除欄位）仍以 `FieldId` 比較；量尺的軸與單位。
- 前端：`database-tracking.spec.ts`、`mock-demo-repository-trends.spec.ts`、`period-summary.component.spec.ts`、`hybrid-demo-repository-trends.spec.ts`（**真實 API JSON**）、詳情頁與趨勢元件既有 spec 更新。
- Cypress（未在本機執行，由 CI 跑）：`tracking.cy.ts`、`consented-submission.cy.ts`（mock）、`e2e-api/database-api.cy.ts`（API）。

### 10.7 給 #149／#150 的接點

- **#149（對話工具）**：模型只選 `DatabaseFixedQueries.Definitions` 之一與參數（字串字典），服務端呼叫 `DatabaseFixedQueryService.RunAsync(kind, accountId, databaseId, parameters, ct)`：`Readable = false`＝無權限（不洩漏）、`Failures`＝參數不被接受（可轉成給模型的錯誤）、`Value`＝上列結果記錄。呼叫前要先確認助理已連接該數據庫且帳號當下有權使用（#148）；本服務只管「這個帳號能不能讀這個數據庫的紀錄」。結果的 `period.label`、`display`、`changeLabel` 可直接當作回答的統計期間與可核對的數字；結果沒有任何自由文字來自使用者填寫的內容，除了欄位名稱（`label`）。
- **#150（排程）**：排程以擁有者帳號呼叫同一個服務（擁有者仍須是被指定且具權限的資料管理者，否則 `Readable = false`＝本期不產生報表）；每期存 `period`（`from`／`to`）與結果 JSON，AI 摘要只拿已算好的數字；`this-week`／`last-month` 等具名期間以排程當下的 UTC 日解析（服務用注入的 `TimeProvider`）。紀錄不足時沿用 `insufficient-records`，不產生假趨勢。

