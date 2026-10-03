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
- **#146 撤回**：刪除值與快照內容，只留提交／撤回時間與來源（撤回 ADR）。
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
