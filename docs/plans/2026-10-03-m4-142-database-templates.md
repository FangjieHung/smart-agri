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
- **#147 趨勢與固定統計查詢**：已實作，見下方第 11 節（固定查詢清單、參數與時區規則、給 #149／#150 的接點）。
- **#148 連接助理**：已實作，見下方第 10 節。
- **#149 對話中查詢授權紀錄**：已實作，見下方第 12 節（工具定義、授權順序、回答格式、失敗分類）。
- **#150 站內定期報表**：已實作，見下方第 13 節（資料模型、排程、摘要保護、保留規則）。

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
- `API_UPCOMING_DATABASE_FEATURES`：`records` 移除，改為 `trends`（#147）。與 #148 合併後（#148 移除了 `assistant-connections`）清單與 `DatabaseUpcomingFeature` 都只剩 `trends`。API 模式的 `comparison` 是「趨勢比較將於後續版本開放」的佔位、`periodicReports` 為空陣列，畫面在趨勢頁籤顯示將於後續版本開放，不用顯示文字自己算差異。
- 提交前的撤回說明（後端 `DatabaseSubmissionRules.WithdrawalNotice` 與 mock 逐字相同）改成說明到哪裡撤回、撤回的效果，以及既有定期報表不追溯修改。
- mock 只列出表單連結的提交（回執存在 `sme-demo:database-submissions`）；對話中送出的資料在 mock 仍從對話收據撤回（既有行為）。API 模式的清單包含所有來源（含 #148 的對話來源），每列以「來源：助理對話／表單連結」標示。

### 9.7 給 #147 的接點

- 有效紀錄＝`DatabaseActiveRecords.ReadableAsync(...)`（或已檢查 `CanReadAsync` 後的 `Of`），型別化值用 `EntriesOf`；**不要**自己寫 `WithdrawnAt` 條件或繞過 `DatabaseRecordReaders`。撤回後重新查詢即排除，已產生的定期報表不追溯改寫（ADR）。
- 追蹤對象＝`SubmittedByAccountId`；欄位以穩定的 `FieldId` 跨版本比較；`NumberValue`（數字、量尺）是趨勢的來源。
- 前端：`TrackedSubjectView.comparison` 與 `DatabaseTrackingView.periodicReports` 由 #147 從伺服器填入（可擴充 `GET .../tracking` 或另開固定查詢端點），完成後從 `API_UPCOMING_DATABASE_FEATURES` 移除 `trends`。
- 數據庫清單摘要的 `recordCount`／`subjectCount` 在 API 模式仍為 `null`：若 #147 要顯示，用 `DatabaseActiveRecords` 計數並只對可讀者回傳。（#177 已做，見第 16 節。）

## 10. #148 助理連接數據庫並請求表單（2026-10-03）

### 10.1 連接模型（migration `AddAssistantDatabases`）

| 資料表／欄位 | 內容 | 規則 |
| --- | --- | --- |
| `AssistantDatabases` | `AssistantId`、`DatabaseId`、`ConnectedAt`、`CollectsForms`、`CollectionPurpose`（≤ 500 字） | 主鍵 `(AssistantId, DatabaseId)`；兩條含 `OrganizationId` 的複合外鍵（→ `Assistants`、`Databases`，皆 `Cascade`）；部分唯一索引 `(AssistantId) WHERE CollectsForms`＝每個助理最多一個寫入對象；check：寫入對象一定有非空白目的、非寫入對象目的為空字串 |
| `ChatMessages.FormDatabaseId`、`ChatMessages.SubmissionId` | 表單請求／收據訊息只存資料庫 id 與提交 id | 無外鍵（資料庫刪除後訊息仍在，顯示為已無法使用）；**不存任何填寫值**，收據訊息文字只有接收單位與回執編號 |
| `ChatReplyKind` | 新增 `FormRequest`、`SubmissionReceipt`（整數，加在尾端） | 只由表單流程寫入，不經回答流程 |

- **可連接＝擁有者可使用**（`AssistantDatabaseAccess.ConnectableBy` ＝ `DatabaseAccess.ListedFor`，以**助理擁有者**判斷）：自己擁有的，或被指定為資料管理者且具備 `read-consented-submissions` 的。資料管理者指定就是數據庫的「分享」；撤銷指定或權限＝分享撤回。
- 連接本身不讓助理請求表單；`rules.dataWriteDatabaseId`／`dataWritePurpose`（前端 mock 既有欄位）選定其中一個已連接、可使用的數據庫作為寫入對象並說明目的。解除連接（或刪除數據庫）連帶清掉寫入對象。多個數據庫可同時連接（#149 的查詢工具使用）。
- 解除最後一個來源（知識庫＋數據庫合計）一律 `422 last-source`。

### 10.2 API 契約

| 端點 | 權限 | 成功 | 錯誤 |
| --- | --- | --- | --- |
| `PUT /api/v1/assistants/{id}/sources/database/{databaseId}` | 登入＋`manage-assistants`＋助理擁有者 | `200 AssistantSettingsView`（冪等） | `403 assistant-configuration`；`422 source-not-connectable`（不存在、別組織、擁有者無權使用、id 格式錯誤，逐位元組相同） |
| `DELETE …/sources/database/{databaseId}` | 同上 | `200`（未連接或 id 格式錯誤＝no-op） | `403`；`422 last-source` |
| `PATCH …/settings` `rules.dataWriteDatabaseId`（`""` 清除、省略不變）、`rules.dataWritePurpose` | 同上 | `200`，`rules` 多兩欄、頂層多 `databaseIds` | `422`：`dataWritePurpose`（有對象卻無目的／過長）、`sources`（不是已連接且可使用的數據庫） |
| `GET /api/v1/connectable-sources` | `manage-assistants` | 知識庫之後多 `type: "database"`（`summary`「N 個欄位」、`permission` owner／read-only） | — |
| `POST /api/v1/assistants/{id}/chat/forms/{databaseId}/review` `{formVersionNumber, answers}` | 登入＋可使用助理 | `200 DatabaseTrialPreviewView`（不寫入） | `403 assistant-use`／`403 assistant-form`；`409 form-version-changed`；`422` |
| `POST …/chat/forms/{databaseId}/submissions` `{submissionId, formVersionNumber, consent, answers, threadId?}` | 同上 | `201 ChatFormSubmissionView{receipt, message}`；同鍵同內容 `200` | `403 assistant-use`／`chat-thread`／`assistant-form`；`409 form-version-changed`／`submission-key-reused`／`chat-run-in-progress`；`422`（含 `consent-required`） |
| `GET /api/v1/databases`、`GET /api/v1/databases/{id}` | 不變 | 摘要多 `connectedAssistantNames`、詳情多 `connectedAssistants`（只列呼叫者自己的助理） | 不變 |

`ChatReplyView` 多 `form`（`ChatFormRequestView`：`id`、`title`、`formVersion`、`fields`、`consent{recipient, purpose, viewers, sensitiveNotice, withdrawalNotice}`）與 `receipt`（`DatabaseSubmissionReceiptView`），其他 kind 皆為 `null`。AG-UI 錄製檔（`tools/agui-contract/fixtures`）已重錄。

### 10.3 表單工具與觸發

- 伺服器只定義一個工具 `request_database_form`（`AssistantFormRequestRules.ToolName`），唯一的參數是「哪一份表單」，只能是伺服器判定此刻可用的寫入對象；欄位、版本、目的、接收單位、可查看者全部來自伺服器（目前表單版本＋`DatabaseSubmissionService.GetFormAsync(databaseId, purpose)`）。模型不產生欄位、查詢或 SQL。
- 本票的呼叫由**編排層**（`ChatRunEndpoints`）決定，不經模型：問題含填寫意圖詞（`AsksForForm`：填寫／填表／表單／回報／登記／報名…）且 `AssistantFormRequests.FormRequestAsync` 找到可用的寫入對象，就以 `form-request` 回覆（同樣的 AG-UI 事件、同樣的對話保存規則；沒有模型呼叫，所以不寫 `ModelInvocations`）。否則照常走回答流程。Fake 與真實模型行為相同；沒有引入 Agent Framework 或新的 NuGet 套件。

### 10.4 授權檢查順序

1. 登入（`401`）。
2. 可使用助理（`ChatEndpoints.FindUsableAsync`：擁有者，或分享＋`use-shared-assistants`＋未暫停）→ `403 assistant-use`。
3. 指定的對話是自己的（只在保存對話時）→ `403 chat-thread`。
4. `AssistantFormRequests.FormTargetAsync`：連接列存在、是寫入對象、數據庫在組織內、**助理擁有者此刻仍可使用**（擁有者的權限經 `RequestAccountPermissions` 每次請求重讀，指定每次查表）→ `403 assistant-form`（不連接、撤回、刪除、他組織、亂造 id 逐位元組相同）。
5. 訊息保存需要時取得對話鎖（`409 chat-run-in-progress`），在寫入任何紀錄之前。
6. `DatabaseSubmissionService.SubmitAsync`（`AssistantConversation` 來源、助理的收集目的寫進同意條款）：提交編號／版本 → 重送比對 → 目前版本 → 欄位 → 同意 → 單一交易寫入。
7. 成功後才寫收據訊息（只存提交 id）；重送時沿用已存在的收據訊息，前一次中斷在兩步之間也會補上。

表單請求（chat run）與重新讀取（`GET chat`）走同一個第 4 步，所以撤回後**下一個請求**就不再出現表單、舊訊息的 `form` 變成 `null`。

### 10.5 隱私

- 提交只帶表單答案；對話內容從不是輸入。處理人（資料管理者）經 `GET …/records` 只讀到欄位快照與提交者，讀不到對話（`chat-thread` 仍只屬於本人）。
- 表單請求與收據訊息不能轉人工（`AssistantHandoffEndpoints` 對這兩種回 `403 chat-thread`），填寫內容只經同意的紀錄到達可查看者。
- 不保存對話時：不寫任何對話表，紀錄照常寫入資料庫，收據訊息只在回應中出現一次。

### 10.6 給 #146／#149 的接點

- **#146（已接上，合併 #146 時完成）**：對話中的收據以 `ChatMessages.SubmissionId` 指向提交，顯示時呼叫 `DatabaseSubmissionService.GetReceiptAsync`（提交者本人），所以撤回後對話讀回的收據就是已撤回、不含內容，對話表不需要改。提交者本人在對話收據上撤回走 #146 的 `POST /api/v1/submissions/{id}/withdrawal`（與「我送出的資料」同一條，資料管理者一律 `403 submission-withdrawal`）；同一個 `submissionId` 在撤回後重送，回已撤回的回執與原本的收據訊息，不會再寫入。
  - 前端：Hybrid 的收據 `recordId` 是 `record-<提交 id>`（與時間軸相同的前綴），`withdrawal` 依回執狀態決定（`available`／`withdrawn`；讀不到回執才是 `unavailable`）。`withdrawChatSubmission` 改為 `Observable`，結果是收據撤回後的 `SubmissionWithdrawalView`；Hybrid 去掉前綴後呼叫 `withdrawDatabaseSubmission`。畫面在保存的對話重新讀取；不保存對話時就地更新本頁的收據（API 模式讀不回來）；5xx／連線中斷顯示「目前無法撤回，這筆資料沒有任何變更」並保留撤回鍵。mock 行為不變（再撤回一次是 `validation-failed`；API 是冪等的 `200`）。
  - 測試：`AssistantDatabaseFormEndpointsTests.The_submitter_withdraws_an_in_chat_submission_and_the_conversation_reads_it_back_withdrawn`（資料管理者代撤回被拒 → 本人撤回 → `DatabaseSubmissionEntries` 無該筆、`ChatMessages` 不含填寫值、對話讀回已撤回、同鍵重送不再寫入、「我送出的資料」與時間軸標示對話來源）；Hybrid（`hybrid-demo-repository-records.spec.ts`、`hybrid-demo-repository.spec.ts`）與元件（`chat-conversation.component.spec.ts`、`own-submissions.component.spec.ts`）。
- **#149**：可用的數據庫集合是 `AssistantFormRequests.UsableDatabaseIdsAsync(assistant)`（連接列 ∩ 擁有者此刻可使用），每次請求重算；固定查詢工具應比照 `request_database_form` 只接受伺服器列出的參數，並在執行時再套用 `DatabaseRecordReaders`（查詢者本人的讀取權）。若改由模型選擇工具，`AssistantFormRequestRules.ToolName/ToolDescription` 可直接成為工具定義，授權與執行不變。

### 10.7 已確認的決策

負責人於 2026-10-03 同意：

1. **數據庫「分享」的定義**：數據庫「分享」給某個帳號＝該帳號被指定為這個數據庫的資料管理者（#144），**且**帳號具備 `read-consented-submissions` 權限。助理擁有者要連接數據庫、讓助理在對話中請求它的表單，兩個條件都要成立；撤銷其中任何一個（移除指定，或收回權限），下一個請求起就無法再連接，已連接的助理也不再提供這份表單（`form` 讀回為 `null`、送出回 `403 assistant-form`）。
2. **對話中表單請求的觸發方式**：目前由伺服器的編排層依填寫意圖關鍵字（`AsksForForm`）觸發，不經模型。工具定義 `request_database_form`（`AssistantFormRequestRules.ToolName`／`ToolDescription`）保留給日後改由模型選擇工具；真實模型就緒後再評估，見 #164（對話表單請求改由模型選擇工具）。**#164 已加入可切換的模型選擇（預設仍為關鍵字），見第 14 節。**

## 11. #147 趨勢與固定統計查詢（2026-10-03）

依據：[助理存取知識庫與數據庫 ADR](../adr/2026-09-25-assistant-access-to-knowledge-and-databases.md)（讀取只能走服務端事先寫好的固定查詢：依期間計數、加總、比較；不接受 SQL）、[撤回與保存 ADR](../adr/2026-09-25-withdrawal-and-retention.md)、[定期報表 ADR](../adr/2026-09-25-periodic-reports.md)。

### 11.1 固定查詢清單

定義在 `DatabaseFixedQueries.Definitions`（Application；名稱、必填與選填參數），執行在 `DatabaseFixedQueryService`（Api；`RunAsync(kind, accountId, databaseId, parameters)` 或四個具名方法）。

| 查詢（wire name） | 參數 | 結果 | 說明 |
| --- | --- | --- | --- |
| `record-count` | 期間（`period` 或 `from`＋`to`）；選填 `subjectId` | `DatabaseRecordCountResult`：`count`、`previousCount`、`change`、`changeLabel`（`+3 筆`／`持平`） | 有效紀錄筆數與前一期 |
| `field-sum` | 期間；必填 `fieldId`（**數字**欄位）；選填 `subjectId` | `DatabaseFieldSumResult`：`field`＝`sum`、`display`（`1,200 元`）、`recordCount`、`previousSum`、`change`、`changeLabel` | 一個數字欄位的加總與前一期；量尺不可加總（`422`） |
| `period-summary` | 期間；選填 `subjectId` | `DatabasePeriodSummaryResult`：筆數＋每個數字欄位的 `sums[]`（同上）＋前一期 | 趨勢頁籤用的「期間統計」 |
| `subject-comparison` | 必填 `subjectId`；選填 `fieldId`（數字或量尺） | `DatabaseSubjectComparisonResult`：`comparison`＝`status`（`available`／`insufficient-records`）、`recordCount`、`message`／`summary`、`metrics[]`（首次／上次／本次、變化與標籤、`direction`、`points`、`axis`、`summary`） | 同一份 `comparison` 也放在 `GET .../tracking` 每位追蹤對象裡，兩處逐字相同（整合測試比對） |

端點：`GET /api/v1/databases/{id}/queries/{record-count|field-sum|period-summary|subject-comparison}`，參數放 query string；其他名稱是 `404`，沒有「任意查詢」的路由。

### 11.2 參數規則（`DatabaseFixedQueries.Validate`）

- **只收定義內的**：不在該查詢定義內的參數名稱（例如 `sql`、`select`、`expression`、對 `record-count` 傳 `fieldId`）、重複的鍵（`?period=a&period=b`）都是 `422`，鍵就是那個參數名；必填缺漏、值不在定義內同樣是 `422`（每個失敗各一個 `errors.<參數>`）。
- **期間**：`period` 是 `this-week`／`last-week`／`this-month`／`last-month`／`last-7-days`／`last-30-days`，或 `from`＋`to` 兩個真實存在的 `yyyy-MM-dd`（`from` 不晚於 `to`、最長 366 天、2000-01-01 至 2100-12-31）；兩種擇一，不可並用。
- **欄位**：必須存在於這個數據庫**目前或任何歷史表單版本**（以該 id 最新的那一版定義為準，已移除的欄位仍可查）；`field-sum` 要數字，`subject-comparison` 要數字或量尺。
- **對象**：`subjectId` 是提交帳號的 id，且必須**提交過這個數據庫**（含已全部撤回的，與時間軸列出的一致）。不是 GUID、不存在、別的數據庫的對象、別組織的帳號、從未提交過的管理者，**一律同一則** `422 subjectId`「找不到這位追蹤對象。」（整合測試比對逐位元組相同）。
- **順序**：先判斷能不能讀這個數據庫的紀錄（`DatabaseActiveRecords.ReadableAsync`，每次呼叫重查指定與帳號權限），再驗參數。所以無權者不論參數對錯都得到同一個 `403`（`database` 或 `database-records`，與時間軸相同），看不到任何欄位或對象的資訊。

### 11.3 時區規則

- **設定**：`Statistics:TimeZone`（`StatisticsOptions`，IANA id，預設 `Asia/Taipei`；環境變數 `Statistics__TimeZone`）。啟動時以 `ValidateOnStart` 驗證，id 不存在就拒絕啟動並指名這個設定（容器需有 tzdata）。
- **曆日以這個時區為準**：具名期間、`from`／`to`（解讀為該時區的曆日，`to` 含當天）、週首（週一）、「前一期」、`today` 都以該時區的曆日計算；紀錄屬於 `SubmittedAt` 落在的**該時區**曆日。所以台北 10/03 07:30（UTC 10/02 23:30）算 10/03，台北 10/04 00:30（UTC 10/03 16:30）算 10/04；週日 23:59:59（+08）仍在該週，週一 00:00（+08）是下一週；11/01 00:30（+08，UTC 仍是 10/31）屬於 11 月。
- **SQL**：每個期間換算成 UTC 的半開區間 `[該時區 from 00:00, to+1 日 00:00)`（`DatabaseQueryPeriod.Start/EndExclusive(timeZone)`；遇到夏令時間造成當日午夜不存在的時區，該日從 01:00 起算）。
- **顯示**：回應裡的日期（`period.label`、`period.from/to`、首次／上次／本次與 `points` 的 `date`、比較摘要的起訖日）同一時區，與前端回執、時間軸顯示的日期一致。前端以常數 `STATISTICS_TIME_ZONE = 'Asia/Taipei'`（`database-tracking.ts`）的 `statisticsDay()` 產生時間軸日期標籤與 mock 的期間統計，Hybrid 的時間軸標籤也用它，不再切 ISO 字串（原本是 UTC 日，會與統計差一天）；後端設定若改了，前端常數要一起改（之後可由伺服器告知）。**#177 起前端不再有這個常數**：時區由 `GET /api/v1/me` 的 `statisticsTimeZone` 提供，見第 16 節。回執編號中的日期仍是 UTC 日（`R-yyyyMMdd-…`，不是統計日期）。
- 週首固定週一；`this-week`／`this-month` 涵蓋整週／整月（含尚未發生的日子），`last-7-days`／`last-30-days` 到今天為止。**前一期**是完整的前一期：上週、上月（月份天數不同照曆月）；滾動與自訂期間則是緊接在前、同樣天數。
- **#150 排程必須讀同一個設定**：注入 `IOptions<StatisticsOptions>` 解析時區，或直接呼叫 `DatabaseFixedQueryService`（已內建）；排程以「現在」在該時區的曆日解析具名期間，報表記錄的資料期間是該時區的曆日。
- 測試：`DatabaseFixedQueriesTests`（半開區間、日界、DST）、`DatabaseFixedQueryEndpointsTests.Days_weeks_and_months_are_Taipei_calendar_days_not_UTC_days`（UTC 23:30Z／16:30Z 邊界、週界、月界、前一期、比較日期）、`StatisticsOptionsStartupTests`（無效 id 拒絕啟動）、前端 `database-tracking.spec.ts`。

### 11.4 計算與資料不足

- **比較**（`DatabaseQueryResults.Compare`）：依穩定的 `FieldId` 跨版本比較；指標取自**最新一筆**紀錄的數字／量尺欄位，每個指標的點是有這個 id 且**型別與單位和最新一筆相同**的紀錄（單位改過的不混進同一條趨勢），至少 2 個點才成立。量尺的圖表縱軸取量尺範圍（與值取聯集），數字取值的最小最大。文字：`本次 X，較上次 +N，較首次 +M`、無變化 `持平`、量尺單位 `分`，數字格式與回執相同（千分位、最多 3 位小數）。
- **加總**（`DatabaseQueryResults.Sums`）：只加總型別是數字、且單位等於該欄位最新單位的值；每個欄位是 `SELECT SUM … GROUP BY FieldId, Unit`（一次查詢，成本不隨筆數成長；`ToQueryString()` 確認過沒有逐欄的純量子查詢）。目前表單的數字欄位即使 0 也列出；已移除的欄位只在這兩個期間有值時才列出。
- **資料不足**：沒有紀錄或只有一筆 → `insufficient-records`＋`message`（`目前只有 N 筆紀錄，累積 2 筆以上才會顯示比較與趨勢。`）、沒有指標；有 2 筆以上但沒有任何欄位累積 2 個數值 → 同狀態，訊息改為 `目前有 N 筆紀錄，但沒有任何數字或量尺欄位累積 2 筆以上的數值，無法比較。`（不回「available 但沒有指標」的空趨勢）。筆數與加總沒有「不足」：0 就是 0，`recordCount` 讓呼叫端分辨「沒資料」。
- **撤回**：撤回刪除 entries 並設 `WithdrawnAt`，所有查詢經 `DatabaseActiveRecords`，下一次查詢就排除（整合測試：逐筆撤回後筆數、加總、比較、時間軸同步變）。已產生的定期報表不追溯改寫是 #150 的規則（ADR）。

### 11.5 前端

- `TrackedSubjectView.comparison` 由伺服器填入（Hybrid 只轉換型別）；新增 `getDatabasePeriodSummary(databaseId, {period, subjectId})`（`Observable` 契約；mock 以 `now()` 在統計時區（台北）的曆日為今天計算，`summarizePeriod`／`resolvePeriod`／`previousPeriod` 在 `database-tracking.ts`，與後端同一套規則）。
- 趨勢頁籤：變化摘要＋比較表＋趨勢圖（紀錄不足時只顯示說明，不畫圖）；新增「期間統計」（`features/databases/period-summary/`，預設近 30 天，可切換期間；讀取失敗與「沒有紀錄」分開、可重試）；每位追蹤對象底下有「查看…的原始紀錄」連到收集紀錄頁籤（`?subject=`）。
- `DatabaseUpcomingFeature`：`'trends'` 移除，改為 `'periodic-reports'`（#150；**#150 已移除它，清單與型別成為空，見第 13 節**）；與 #148 合併後（#148 移除了 `assistant-connections`）清單與 `DatabaseUpcomingFeature` 都只剩 `periodic-reports`。API 模式的趨勢頁籤只在有追蹤對象時顯示「定期回報摘要：這項功能將於後續版本開放」，`periodicReports` 仍是空陣列。
- 摘要清單的 `recordCount`／`subjectCount` 仍為 `null`（本票沒有需要顯示它的畫面；要顯示時用 `DatabaseActiveRecords` 計數且只對可讀者回傳）。（#177 已做，見第 16 節。）

### 11.6 測試

- 後端單元：`DatabaseFixedQueriesTests`（期間解析、前一期、參數規則）、`DatabaseQueryResultsTests`（比較、不足、單位不混、加總、標籤）。
- 後端整合（`DatabaseFixedQueryEndpointsTests`，真實 PostgreSQL，11 個）：無權限／未指定／他組織／權限撤銷／指定移除皆拒絕且不洩漏（錯誤參數同一個 403）；對象不在這個數據庫一律同一則 422；不在定義內的參數與值 422、無「任意查詢」路由；空資料與單筆＝紀錄不足；跨期邊界與跨對象計數、加總；具名期間；撤回後各查詢與時間軸同步排除；表單改版（改名、新增、移除欄位）仍以 `FieldId` 比較；量尺的軸與單位。
- 前端：`database-tracking.spec.ts`、`mock-demo-repository-trends.spec.ts`、`period-summary.component.spec.ts`、`hybrid-demo-repository-trends.spec.ts`（**真實 API JSON**）、詳情頁與趨勢元件既有 spec 更新。
- Cypress（未在本機執行，由 CI 跑）：`tracking.cy.ts`、`consented-submission.cy.ts`（mock）、`e2e-api/database-api.cy.ts`（API）。

### 11.7 給 #149／#150 的接點

- **#149（對話工具）**：模型只選 `DatabaseFixedQueries.Definitions` 之一與參數（字串字典），服務端呼叫 `DatabaseFixedQueryService.RunAsync(kind, accountId, databaseId, parameters, ct)`：`Readable = false`＝無權限（不洩漏）、`Failures`＝參數不被接受（可轉成給模型的錯誤）、`Value`＝上列結果記錄。呼叫前要先確認助理已連接該數據庫且帳號當下有權使用（#148）；本服務只管「這個帳號能不能讀這個數據庫的紀錄」。結果的 `period.label`、`display`、`changeLabel` 可直接當作回答的統計期間與可核對的數字；結果沒有任何自由文字來自使用者填寫的內容，除了欄位名稱（`label`）。
- **#150（排程）**：排程以擁有者帳號呼叫同一個服務（擁有者仍須是被指定且具權限的資料管理者，否則 `Readable = false`＝本期不產生報表）；每期存 `period`（`from`／`to`）與結果 JSON，AI 摘要只拿已算好的數字；`this-week`／`last-month` 等具名期間以排程當下在統計時區（`Statistics:TimeZone`）的曆日解析（服務用注入的 `TimeProvider`）。紀錄不足時沿用 `insufficient-records`，不產生假趨勢。

## 12. #149 對話中查詢授權紀錄（2026-10-03）

依據：[助理存取知識庫與數據庫 ADR](../adr/2026-09-25-assistant-access-to-knowledge-and-databases.md)（模型只選固定查詢與參數，不產生／執行 SQL）、[後端技術棧 ADR](../adr/2026-09-25-backend-stack.md)、[可觀測性 ADR](../adr/2026-09-25-observability.md)、M3.5 收尾交接 §3。

### 12.1 工具定義（`DatabaseQueryTools`，Application，純函式）

- 每個固定查詢（§11.1）一個工具：`database_record_count`、`database_field_sum`、`database_period_summary`、`database_subject_comparison`（`ToolName(kind)`），沒有其他工具。
- 參數 schema（JSON Schema，`additionalProperties: false`）＝`databaseId`（**enum**：這次提問者經這個助理可查的數據庫 id）＋該查詢定義的參數，且只有這些：`period`（enum：六個具名期間）、`from`／`to`（`yyyy-MM-dd`）、`fieldId`（**enum**：`field-sum` 只列數字欄位，`subject-comparison` 列數字與量尺欄位；來源是各數據庫目前表單；沒有可選欄位時 `field-sum` 不提供）、`subjectId`（字串；追蹤對象的姓名屬個資，不列給模型，只有對話中已提供 id 時才用得到）。工具說明列出數據庫 id 與名稱、欄位 id 與標籤（不含任何紀錄內容）。
- 以 `Microsoft.Extensions.AI` 的 `AIFunctionFactory.CreateDeclaration` 宣告、`ChatOptions.Tools` 交給模型（`ToolMode = Auto`、`AllowMultipleToolCalls = false`）；**不**掛 `FunctionInvokingChatClient`，模型的 `FunctionCallContent` 由編排層自己比對與執行。**沒有引入 Agent Framework 或任何新 NuGet 套件**：本票只有「一次選擇呼叫 → 伺服器執行一個固定查詢 → 伺服器組回答」，沒有多步驟代理迴圈；`IChatClient` 的 function calling 已足夠，且沿用既有的錄製中介層（用量）與 Fake 模型。日後真的需要多步驟工具編排時再依後端技術棧 ADR 引入，同樣只放在編排層。
- Fake 模型（`FakeChatClient`）在呼叫帶工具時改為選工具：`#query:{"name":…,"arguments":{…}}` 原樣呼叫（測試可送定義外的名稱與參數）、`#query-none` 不呼叫，否則依問題確定性地選（含「加總／合計」且有數字欄位 → `field-sum` 第一個欄位，否則 `record-count`；第一個數據庫；問題中的「本週／上週／本月／上個月／近 7 天／近 30 天」，否則 `last-30-days`）；`#fail-midway` 照舊讓呼叫失敗。

### 12.2 編排流程與授權順序（`ChatDatabaseQueries`，`ChatRunEndpoints` 呼叫）

1. 既有的 chat run 檢查（登入 → `403 assistant-use` → `403 chat-thread` → `422` → `503` → `409`），問題照保存規則先存。
2. **觸發**：問題含統計詞（`AsksForStatistics`：幾筆、幾次、多少筆、筆數、次數、總共、一共、共有、加總、合計、總計、總和、統計、趨勢…）**且** `AssistantFormRequests.UsableDatabaseIdsAsync`（連接列 ∩ 擁有者此刻可使用）非空。助理沒有可用數據庫時完全不走查詢（照常回答，行為與 #148 前相同）。
3. **提問者可讀**：上一步的數據庫 ∩ `DatabaseRecordReaders.ReadableDatabaseIdsAsync(提問者)`（被指定為資料管理者＋此刻有 `read-consented-submissions`，每次請求重查）。交集為空 → 直接回 `not-available`，**不呼叫模型**。
4. **模型選擇**：一次 `GetResponseAsync`（用途 `database-query`），只提供第 3 步的數據庫與其欄位。模型沒有呼叫工具 → 回到 #148 的表單請求（若問題也有填寫意圖且有寫入對象）或一般回答流程。
5. **比對**：工具名稱不是固定查詢 → `rejected`（不提任何數據庫）；`databaseId` 缺漏、不是 GUID、不在提供清單 → `not-available`（與無權限相同）。
6. **執行**：`DatabaseFixedQueryService.RunAsync(kind, 提問者帳號, databaseId, 參數)`——服務內再查一次提問者能否讀（`Readable = false` → `not-available`），再以 `DatabaseFixedQueries.Validate` 驗參數（定義外的鍵、值、欄位、對象 → `rejected`，不執行）。查詢丟例外 → `failed`（記 log）。
7. **回答**：`DatabaseQueryTools.Compose` 由結果記錄組文字與結構化欄位。

優先序（與 #148 共存）：**查詢 → 表單請求 → 一般回答**。「本月回報了幾筆？」（同時含「回報」與「幾筆」）是查詢；「我要回報這週的完成數量」只有填寫意圖，是表單；模型決定不查詢時，有填寫意圖仍得到表單。mock 的固定回覆順序相同（`chat-order-count` 排在 `chat-order-issue` 前）。

### 12.3 回答格式與數字防改寫

- 新的回覆種類 `ChatReplyKind.DatabaseQuery`（wire `database-query`，整數接在尾端）。`ChatReplyView.databaseQuery`（`ChatDatabaseQueryView`，其他種類為 `null`）：`status`、`databaseId`／`databaseName`（資料來源）、`query`／`queryLabel`（查詢種類）、`period`／`previousPeriod`（統計期間，§11 的 `DatabaseQueryPeriodView`）、`subjectOnly`、`figures[]`（`metric`、`value`、`display`、`previousDisplay`、`changeLabel`）、`message`（比較摘要或資料不足說明）。
- **模型只負責選擇，看不到結果**；回答文字由伺服器範本組成，例如「根據「回報資料庫」的紀錄筆數查詢：2026-02-01 至 2026-02-28共有 3 筆有效紀錄；前一期（2026-01-04 至 2026-01-31）為 1 筆，變化 +2 筆。」。文字中的每個數字都是結果的 `display`／`changeLabel`／`period.label` 原字串，`figures` 也是同一批字串，所以畫面上的數字與固定查詢端點逐字相同（整合測試直接比對 `GET .../queries/*`）。沒有第二次模型呼叫，也就沒有模型改寫數值的機會。
- 前端：`ChatReplyView` 多 `{ kind: 'database-query', text, query }`；訊息顯示「數據庫查詢」標籤、資料來源（數據庫名稱＋查詢種類）、統計期間、數字表（指標／數值／前一期或上次／變化）與「數字由系統依你目前的查詢權限計算，不由 AI 產生」。不顯示轉人工按鈕；不保存對話時也不把查詢回答當成前文送回。

### 12.4 失敗分類

| `status` | 何時 | 文字 | 透露什麼 |
| --- | --- | --- | --- |
| `answered` | 查詢成功且期間內有紀錄 | 範本＋數字 | 數據庫名稱、查詢、期間、數字 |
| `no-data` | 期間內沒有紀錄（`record-count`／`period-summary` 筆數 0、`field-sum` 該欄位無值） | 「…沒有任何有效紀錄，共 0 筆…」 | 同上（0 也是可核對的數字） |
| `insufficient-data` | `subject-comparison` 紀錄不足（§11.4） | 「資料不足，無法比較。」＋伺服器的說明 | 數據庫名稱、查詢；無數字 |
| `not-available` | 提問者沒有可查的數據庫、模型給的數據庫不在清單（不存在、他組織、未連接）、執行時已不可讀（撤銷指定或權限） | `DatabaseQueryTools.NotAvailableText`（逐位元組相同） | 無（`databaseId`／名稱皆 `null`） |
| `rejected` | 工具名稱不是固定查詢，或參數不在定義內 | `RejectedText` | 只有提問者可讀的數據庫名稱與查詢種類（未知工具連這些都沒有）；不執行 |
| `failed` | 固定查詢執行時丟例外 | `FailedText` | 無 |
| （`RUN_ERROR chat-unavailable`） | 選擇工具的模型呼叫失敗 | 與一般回答相同的串流錯誤 | 只保存問題（保存對話時） |

### 12.5 保存、轉人工與用量

- **保存**：保存對話時，回答存成 `ChatMessages`（`ReplyKind = DatabaseQuery`、`Text`、新的 `jsonb` 欄位 `DatabaseQuery`＝結構化快照；migration `AddChatDatabaseQueries`）。快照只在**提問者本人的私人對話**裡，且每次 `GET chat` 重新檢查：該數據庫仍是助理可用的、提問者仍可讀（`ChatDatabaseQueries.VisibleDatabaseIdsAsync`），否則文字與結構都換成 `not-available`（不透露名稱與數字）。快照是回答當時的數字，與定期報表相同不追溯改寫；新的提問一律重新查詢。不保存對話時不寫任何對話表。
- **模型看不到結果**：保存的查詢回答在之後的模型呼叫前文中換成 `DatabaseQueryTools.HistoryPlaceholder`；前端不保存對話時也不把查詢回答送回當前文。
- **轉人工**：查詢回答不能轉人工（`AssistantHandoffEndpoints` 回 `403 chat-thread`，前端不顯示按鈕）——數字來自只有提問者能讀的紀錄，處理人不因轉人工看到他人紀錄。
- **用量**：選擇工具的呼叫經既有錄製中介層寫一筆 `ModelInvocations`（新用途 `ModelInvocationPurpose.DatabaseQuery`，wire `database-query`，歸屬提問者與助理，不含內容；失敗也記 `Succeeded = false`）；`not-available`（第 3 步）不呼叫模型、不記。工具執行本身是一個 `smartagri.chat.database_query` span（`smartagri.database_query.query`／`.status`，不含參數與結果）。查詢回答不寫 `AnswerOutcomes`（那是知識庫回答品質的分析：組織資料／一般知識／查無資料）。

### 12.6 測試

- 單元：`DatabaseQueryToolsTests`（只有四個工具、每個 schema 只有定義內參數與 enum、沒有 SQL；比對未知工具／未提供的數據庫；定義外參數交給 `Validate` 拒絕；回答文字與數字來自結果字串；無資料、資料不足；拒絕不含名稱）、`FakeChatClientTests`（指令、確定性選擇、不呼叫）、`ModelInvocationTests`（用途清單）。
- 整合（`ChatDatabaseQueryEndpointsTests`，真實 PostgreSQL，7 個）：授權成員的筆數與加總等於固定查詢端點、標明期間／指標／來源、保存後讀回相同、用量正確、不能轉人工；不能讀的成員（不呼叫模型）、他組織與不存在的數據庫、撤銷指定（含讀回舊回答）、撤銷帳號權限皆為同一個 `not-available`，解除連接則照常回答；定義外參數與未知工具 `rejected`；資料不足；模型失敗 `RUN_ERROR`（只存問題、記失敗用量）與工具失敗 `failed`；不保存對話不寫對話表；與表單請求的優先序。
- 前端：`hybrid-demo-repository.spec.ts`（**真實 API JSON**，答覆與拒絕）、`chat-message.component.spec.ts`、`mock-demo-repository-chat.spec.ts`（mock 以 `summarizePeriod` 算出與期間統計相同的筆數；無權者同一個拒絕、不被建議這個問題；填寫意圖仍得到表單）。
- Cypress（未在本機執行）：`e2e-api/chat-database-query-api.cy.ts`（授權成功、資料不足、撤銷指定後拒絕並讀回拒絕）。AG-UI 錄製檔因 `smartagri.reply` 多了 `databaseQuery: null` 重錄。

## 13. #150 站內定期報表（2026-10-03）

依據：[定期報表 ADR](../adr/2026-09-25-periodic-reports.md)（統計為主、AI 摘要為輔並標示，第一版只在站內查看）、[PostgreSQL 背景工作 ADR](../adr/2026-09-25-background-jobs-on-postgresql.md)（不引入排程套件）、[撤回與保存 ADR](../adr/2026-09-25-withdrawal-and-retention.md)（已產生的報表不追溯修改）、[LLM 與資料落地 ADR](../adr/2026-09-25-llm-providers-and-data-residency.md)、[可觀測性 ADR](../adr/2026-09-25-observability.md)。

### 13.1 資料模型

- **`AssistantReportSchedules`**（`ReportSchedule`）：一位助理最多一列（`AssistantId` 唯一索引）。`Id`、`DatabaseId`、`Frequency`（`weekly`／`monthly`）、`NextPeriodFrom`（`date`，下一個要報告的期間的第一天，統計時區的曆日）、`CreatedAt`。外鍵都含 `OrganizationId`、都 `Cascade`（刪助理或資料庫，排程跟著結束）；**刻意沒有指向 `AssistantDatabases`**，所以解除連接後排程還在，下一期才能記錄「為什麼沒產生」。
- **`DatabaseReports`**（`DatabaseReport`）：每一期一份**快照**。`DatabaseId`（外鍵＋`Cascade`）、`AssistantId`（無外鍵，報表比助理活得久）＋`AssistantName`（複本）、`Frequency`、`PeriodFrom`／`PeriodTo`（`date`）、`Status`（`generated`／`skipped`）、`SkipReason`（`not-connected`／`owner-cannot-read`）、`DataState`（`sufficient`／`insufficient-records`）、`StatisticsJson`（`jsonb`，固定查詢 `period-summary` 的結果原樣）、`GeneratedAt`；**摘要與統計分開保存**：`SummaryStatus`（`not-requested`／`pending`／`ready`／`failed`／`discarded`）、`SummaryText`（只有 `ready`）、`SummaryNote`、`SummaryModel`、`SummaryUpdatedAt`。唯一索引 `IX_DatabaseReports_OnePerPeriod (AssistantId, DatabaseId, Frequency, PeriodFrom)`：同一設定同一期間只有一份。check constraint：`generated` 一定有統計與資料狀態、沒有略過原因，`skipped` 相反；只有 `ready` 的摘要有文字與模型名稱。
- 列舉值全部用 wire name 儲存，`ModelInvocationPurpose` 多 `generate-report-summary`。
- 兩張表都是 `IOrganizationScoped`（查詢過濾與寫入守衛照常；`OrganizationModelTests` 白名單加入）。migration：`AddPeriodicReports`。

### 13.2 設定

`rules.periodicReport`（`off`／`weekly`／`monthly`，`PATCH .../settings`，見 `ReportScheduleRules`）：

- 報告的是助理的**寫入對象**（`dataWriteDatabaseId`）。設週期但沒有寫入對象 `422 periodicReport`；新設定的寫入對象必須在 `AssistantFormRequests.UsableDatabaseIdsAsync`（已連接、且擁有者目前可使用——自己擁有，或被指定且具讀取權限）內，否則 `422`。未知的值 `422`。
- 週期或資料庫變了，就**換掉**排程（新的 `Id`，舊的已排入佇列的工作找不到排程，什麼都不做）；寫入對象改了，排程跟著移；清掉寫入對象，報表關閉；`off` 刪除排程（已產生的報表保留）。沒有帶 `periodicReport`、也沒動寫入對象的 `PATCH` 不會碰排程（它可能正在等著記錄略過的期間）。
- 設定回應的 `rules.periodicReport` 讀回目前排程的週期（沒有就是 `off`）。

### 13.3 排程（PostgreSQL 背景工作，沒有新的排程套件）

- 排程是一串**自我接續的工作**，不是時鐘：設定時排入「包含今天的那一期」的工作（`reports.generate-period`，`RunAfter` ＝ 該期結束後隔天 00:00，統計時區換成 UTC）；工作做完，在同一個交易內把 `NextPeriodFrom` 以 compare-and-set（`ExecuteUpdate … WHERE NextPeriodFrom = 這一期`）推到下一期，**只有推成功才排下一個工作**。工作被送兩次、或 worker 停機後補跑，每一期仍只產生一次、依序補齊；補跑的工作報告的是它名下的那一期，不是「現在」所在的那一期（`DatabaseFixedQueryService.PeriodSummaryForAsync` 接收明確的期間與前一期）。
- 期間：統計時區（`Statistics:TimeZone`，預設 `Asia/Taipei`）的曆日週（週一到週日）或曆月；前一期是完整的前一個曆週／曆月（`ReportPeriods`，單元測試涵蓋大小月與閏年）。
- 每期做的事（`GenerateDatabaseReportHandler`，一個交易）：排程不在或 `NextPeriodFrom` 不是這個工作的期間 → 什麼都不做；期間的報表已存在 → 不再產生，仍推進接續；助理已不再連接 → 存 `skipped / not-connected`；**以助理擁有者的帳號**呼叫同一個固定查詢服務，`Readable = false`（擁有者已不是指定且具權限的資料管理者）→ 存 `skipped / owner-cannot-read`；否則存統計。略過的期間不含任何統計，仍每期記一筆（擁有者的設定頁仍顯示原設定）。
- **資料不足**：`DataState = insufficient-records` ＝ 這一期或前一期有效紀錄為 0（`ReportDataRules`）。統計照存（0 就是 0），但畫面不顯示變化、圖表趨勢，也**不排摘要工作**、不呼叫模型。
- 狀態表：只有 `Sufficient` 的報表同一個交易內排一個 `reports.summarize`（`MaxAttempts = 3`）。

### 13.4 AI 摘要與保護

- 模型**只拿已算好的數字**（`ReportSummaryPrompt.Facts`：期間、筆數、各數字欄位的加總與變化文字，欄位名稱壓成一行）；沒有紀錄、答案、提交者、資料庫或助理名稱。用量照既有規則：`ModelInvocations` 一筆，purpose `generate-report-summary`，帶組織、擁有者、助理、模型、token、成功與否，沒有內容。
- **數字保護（`ReportSummaryGuard`）**：摘要文字裡寫出的每一個數字（任何文字的阿拉伯數字，千分位與小數正規化，`1,200` ＝ `1200`、全形數字視為同一個數字）都必須也出現在給模型的那份數字裡；日期以整個 `yyyy-MM-dd` 比對（不讓日期的 `09`、`01` 讓筆數 `1`、`9` 看起來有驗證過）。不符（改過的加總、自己算的百分比或倍數、編出來的筆數）→ 整段**捨棄**：不存文字，狀態 `discarded`、說明「含有統計結果裡沒有的數字」，統計欄位不動；中文數字緊接單位（`三筆`、`五成`、`百分之十`）無法比對，同樣捨棄。過長（> 2000 字）也捨棄。這個檢查不判斷文字描述得好不好（上升或下降、哪個欄位）——所以摘要永遠標示「AI 摘要」、放在統計下面，並附「一切數字以統計為準」。
- **模型失敗**（任何例外、未設定模型也算）：記在報表上 `failed`、說明「統計與圖表不受影響」，工作**成功結束**（不由佇列重試）；報表仍可查看統計與圖表。只有資料庫之類的基礎錯誤由佇列重試。
- **重試**：`POST .../reports/{reportId}/summary`；只有 `failed`／`discarded` 變成 `pending` 並在同一次儲存排一個摘要工作，其他狀態原樣回傳（連按兩次只一次模型呼叫）。處理工作只處理 `pending` 的報表，所以重複送達不會再呼叫模型。
- 測試用的 Fake 聊天模型對報表摘要回「整體來看，」加上數字檔的第一行，所以只含統計裡有的數字；整合測試以 `ChatClientProvider` 替身驗證竄改與失敗。

### 13.5 查看與權限

- `GET /api/v1/databases/{id}/reports`（排程與清單，最多 60 份）、`GET .../reports/{reportId}`、`POST .../reports/{reportId}/summary`。只有**目前能讀這個資料庫紀錄的帳號**（指定的資料管理者 **且** 具備 `read-consented-submissions`，`DatabaseRecordReaders.CanReadAsync`，每次請求重查，帳號權限不在 token 內）；助理擁有者沒有額外權利。
- 其他人：看不到資料庫是 `403 database`（與不存在逐位元組相同），看得到但不能讀是 `403 database-records`，**對真的與假的報表 id 一樣**，所以報表是否存在、助理名稱都不洩漏；能讀但 id 不是這個資料庫的報表是 `403 database-report`。別的組織與不存在的資料庫相同。
- 回應的 `statistics` 是 `StatisticsJson` 反序列化成 `DatabasePeriodSummaryResult`，欄位與固定查詢的 `period-summary` 逐位元組相同。

### 13.6 保留規則

- **已產生的報表不追溯修改**：統計是快照，沒有任何重算的程式路徑；撤回只影響之後產生的報表（新報表走 `DatabaseActiveRecords`，已排除撤回；下一期的「前一期」數字也不含已撤回的紀錄）。整合測試：產生 → 撤回 → 舊報表 `StatisticsJson` 逐位元組相同、下一期的前一期數字少了那一筆。
- 報表只含彙總數字（筆數、加總），沒有紀錄內容或提交者。刪除資料庫連帶刪除它的報表（`Cascade`）；刪除助理不刪報表（名稱是複本）。報表本身沒有到期刪除（沿用撤回與保存 ADR 的規則，之後若要加保存期限，在這裡處理）。
- 摘要的文字是模型輸出、存在報表列上，不進 `ModelInvocations`；失敗與捨棄的摘要不留文字。

### 13.7 前端

- 助理設定「回答與記錄」的 `#periodic-report`（標籤「是否定期產生報表？」）API 模式啟用；`periodicReport` 欄位的 `422` 顯示在它下面。
- 資料庫詳情新增「定期報表」頁籤（`features/databases/database-reports/`）：排程（下一份在哪天產生）、報表清單（紀錄不足／未產生／含 AI 摘要等標示）、選中的一份——統計表、依比例畫的長條圖（只有兩期都有紀錄才畫，數字以表為準）與獨立的「AI 摘要」區（失敗或捨棄時說明並可重試；產生中可重新整理）。讀取失敗與「沒有報表」分開、可重試；無權限只顯示拒絕訊息。
- mock 同形：最近三個完成的月份（週報是四週）依 mock 的紀錄產生快照並存起來（所以撤回後舊報表不變），摘要是「整體來看，」加上筆數那一行。
- 移除：`PeriodicReportView`、`DatabaseTrackingView.periodicReports`、`buildPeriodicReport`、`features/databases/periodic-report/`；`DatabaseUpcomingFeature` 為 `never`、`API_UPCOMING_DATABASE_FEATURES` 為空。

### 13.8 測試

- 後端單元：`ReportRulesTests`（期間、資料是否足夠、設定規則、給模型的內容、數字保護，含全形數字與中文數字）、`DatabaseReportTests`（狀態轉移）、`ModelInvocationTests`（purpose wire name）。
- 後端整合（`PeriodicReportEndpointsTests`，真實 PostgreSQL，10 個）：只能為已連接且可使用的資料庫設定／換掉或移除排程；排程產生正確期間與數字（含 +08 日界：週日 23:30 屬上週、週一 00:30 屬本週）、只排一個接續工作；同期不重複（重複送達、重試）；資料不足不產生趨勢與摘要、不呼叫模型；AI 摘要標示、竄改數字被捨棄且統計不動、重試與連按；模型失敗統計仍可查看、重試恢復；撤回後舊報表不變、下一期排除；擁有者失權或解除連接記錄略過原因；無權限者看不到且不洩漏（含權限撤銷、別組織、擁有者未指定）。
- 前端：`hybrid-demo-repository-reports.spec.ts`（**真實 API JSON**）、`mock-demo-repository-databases.spec.ts`／`-records.spec.ts`（mock 快照與撤回）、`database-reports.component.spec.ts`、`database-report.component.spec.ts`、詳情頁既有 spec 更新。
- Cypress（未在本機執行，由 CI 跑）：`tracking.cy.ts`、`assistant-editing.cy.ts`、`accessibility.cy.ts`、`responsive.cy.ts`（mock）、`e2e-api/assistant-forms-api.cy.ts`、`e2e-api/database-api.cy.ts`（API）。

## 14. #164 對話表單請求改由模型選擇工具（2026-10-05）

### 14.1 設定與預設

- 新設定 `Chat:FormRequests:Trigger`（`ChatFormRequestOptions`）：`Keyword`（預設，未設定也是）或 `Model`，不分大小寫；其他值啟動失敗。
- `Keyword` 與 #148 完全相同：編排層以 `AsksForForm` 決定、不呼叫模型。`Fake` 模型的 E2E 與既有測試因此不變。

### 14.2 `Model` 模式的流程（`ChatFormRequestTool`，比照 #149 的設計）

1. 進入串流前，照 #148 的順序檢查：登入 → 可使用助理（`403 assistant-use`）→ 對話屬於本人 → `AssistantFormRequests.FormRequestAsync(assistant, null)`：助理此刻可用的寫入對象。沒有寫入對象就**不提供工具、不呼叫模型**，照常回答（與關鍵字模式沒有寫入對象時相同）。
2. 串流中，#149 的查詢仍先執行（查詢的關鍵字門檻與行為不變）；查詢沒有回答時，呼叫一次模型（用途 `form-request`），只提供 `request_database_form`，參數 `databaseId` 是只含這一個表單 id 的 `enum`、`additionalProperties: false`，模型只看到表單名稱與收集目的。
3. 伺服器比對回覆（`AssistantFormRequestRules.ParseCall`）：必須是 `request_database_form` 且 id 是列出的那一個；再以該 id 呼叫 `FormRequestAsync(assistant, id)` 重新授權、由伺服器組出表單。其他工具、他組織或不存在的 id、格式錯誤、此刻已失效的寫入對象，一律「沒有表單」→ 照常回答，回覆完全相同，不透露表單是否存在。
4. 模型呼叫失敗（逾時、供應商錯誤）時改用關鍵字門檻決定，不中斷回覆；失敗的呼叫照既有規則記一筆 `Succeeded = false`。

優先序不變：**查詢 → 表單請求 → 一般回答**。

### 14.3 保存與用量

- 表單請求的回覆、保存（`ChatMessage.FormRequest`，只存資料庫 id，讀取時重新授權）、事件與不保存對話時的行為都沿用 #148，沒有 schema 變更、沒有 OpenAPI 變更、前端不需修改。
- 新用途 `ModelInvocationPurpose.FormRequest`（wire `form-request`，字串欄位、不需 migration）：選擇呼叫經既有錄製中介層記錄，歸屬提問者與助理、不含內容。關鍵字模式仍不呼叫模型、不記錄。`Model` 模式下，有寫入對象的助理每題多一次選擇呼叫。

### 14.4 `Fake` 模型與測試

- `FakeChatClient` 被提供表單工具時：題目含 `#form-request` 一定呼叫（模擬模型抓到關鍵字以外的意圖）、含 `#form-none` 一定不呼叫，其餘照 `AsksForForm` 決定；`#query:` 仍可指定任意工具與參數（用來送出未列出的 id）。
- 整合（`ChatFormRequestToolTests`，真實 PostgreSQL，7 個）：關鍵字模式不變且不呼叫模型；未知設定值啟動失敗；模型模式給出伺服器的表單、讀回相同、保存與用量正確；他組織、未連接、不存在、格式錯誤的 id 與其他工具一律相同的一般回答；未被分享與他組織帳號仍是 `403 assistant-use`，清除寫入對象後不再提供工具；模型失敗退回關鍵字；不保存對話不寫對話表。單元：`AssistantFormRequestRulesTests`（工具定義、比對、提示）、`FakeChatClientTests`、`ModelInvocationTests`。

### 14.5 評測

- `eval-form-requests`（`apps/api/eval/form-requests/` 48 題標記題庫）比較關鍵字與模型的漏觸／誤觸；結果記錄於 `docs/evals/2026-10-05-164-form-request-trigger.md`：關鍵字漏觸 10/18、誤觸 8/18；`gpt-6-luna` 0/18、0/18；`gpt-4o-mini` 1/18、0/18。程式預設維持 `Keyword`，正式環境是否切換為 `Model` 由負責人決定。
- `Ai:Chat:ReasoningEffort`（`None`／`Low`／`Medium`／`High`／`ExtraHigh`，未設定時用供應商預設）：推理型模型（例如 `gpt-6-luna`）在 Chat Completions 上必須設為 `None` 才能使用工具，否則本票的表單工具與 #149 的查詢工具都會回 HTTP 400。

## 15. #171 對話表單請求體驗：等待事件、可用表單與關閉紀錄（2026-10-05）

畫面規格見 `docs/plans/2026-10-05-171-form-request-ux-handoff.md`；本節只記 API 契約（交接文件的 E1–E3）。

### 15.1 E1 等待事件 `CUSTOM smartagri.form-check`

- `POST …/chat/runs` 的串流在 `Model` 模式（§14）實際進行表單判斷時，於選擇呼叫**之前**送一個 `CUSTOM`，`name` 為 `smartagri.form-check`、`value` 為空物件 `{}`（`ChatRunEndpoints.FormCheckEventName`）。位置：`TEXT_MESSAGE_START` 之後；#149 的查詢若已回答就不會走到這裡（優先序不變：查詢 → 表單 → 一般回答）。
- 只在 `Chat:FormRequests:Trigger = Model` **且** `AssistantFormRequests.FormRequestAsync(assistant, null)` 找到此刻可用的寫入對象時送出，每次執行最多一次。關鍵字模式、沒有寫入對象的助理一律不送（事件序列與 §10.2 完全相同，AG-UI 錄製檔不變）。
- 前端收到才顯示「判斷中」狀態，收到第一個 `TEXT_MESSAGE_CONTENT`（一般回答或表單請求的固定文字）或 `smartagri.reply` 就收起。模型呼叫失敗退回關鍵字時，事件已經送出，同樣在之後的第一段文字收起。`@ag-ui/client` 接受文字訊息進行中的 `CUSTOM` 事件（已用 `HttpAgent` 驗證）。

### 15.2 E2 可用表單 `GET /api/v1/assistants/{id}/chat/forms`

| 權限 | 成功 | 錯誤 |
| --- | --- | --- |
| 登入＋可使用助理（`ChatEndpoints.FindUsableAsync`，同 §10.4 第 1–2 步） | `200 ChatFormRequestView[]` | `401`；`403 assistant-use`（未分享、他組織、不存在，逐位元組相同） |

- 清單＝`FormRequestAsync(assistant, null)`：與對話中跳出的表單**同一個物件**（`id`、`title`、`formVersion`、`fields`、`consent`），每次請求重新授權、不快取。交接文件只要求 id 與名稱，回傳完整表單是為了讓「回報資料」入口直接開啟同一張卡片、不必再多一個讀取端點。
- 每個助理最多一個寫入對象（§10.1 的部分唯一索引），所以目前清單只有 0 或 1 筆；空清單時前端不顯示入口。之後若允許多個寫入對象，契約不變。

### 15.3 E3 關閉紀錄 `POST /api/v1/assistants/{id}/chat/forms/{databaseId}/dismissals`

| 請求 | 權限與檢查順序 | 成功 | 錯誤 |
| --- | --- | --- | --- |
| `{ threadId? }`（整個 body 可省略） | `401` → 可使用助理（`403 assistant-use`）→ 有保存對話且指定 `threadId` 時必須是呼叫者自己的對話（`403 chat-thread`）→ `databaseId` 是助理此刻可用的寫入對象（`403 assistant-form`） | `204` | 拒絕時不寫入任何東西 |

- 資料表 `ChatFormDismissals`（migration `AddChatFormDismissals`）：`Id`、`OrganizationId`、`AssistantId`、`DatabaseId`、`At`。**不記帳號、對話、問題或任何填寫內容**（`ChatFormDismissal` 沒有任何字串屬性，網域測試鎖住欄位清單）；比照 `AnswerOutcomes` 是營運紀錄，沒有外鍵，助理或數據庫刪除後仍保留；索引 `(OrganizationId, AssistantId, At)` 供抽樣查詢。
- 只有對話中跳出的表單（`form-request` 訊息開啟的卡片）在使用者按「不用了」或 × 時記錄；從「回報資料」入口自己開啟的卡片關閉時不記錄。關鍵字模式下也會記錄（同樣是誤觸訊號）；抽樣時以當時的 `Chat:FormRequests:Trigger` 區分。
- 用途：上線後抽樣檢查誤觸（`docs/evals/2026-10-05-164-form-request-trigger.md` 第 5 節）。目前沒有讀取端點，以資料庫查詢抽樣。

### 15.4 測試

- 整合（`ChatFormRequestUxTests`，真實 PostgreSQL，7 個）：`Model` 模式有寫入對象時事件恰好一次、在 `TEXT_MESSAGE_START` 之後與第一段文字之前（給表單、不給表單、模型失敗退回關鍵字三種）；關鍵字模式與清除寫入對象的助理不送；清單與表單請求的 `form` 完全相同、清除寫入對象後是 `[]`；未分享、他組織、不存在的助理一律相同的 `403 assistant-use`；關閉紀錄只有助理、表單與時間，另一位成員指定別人的對話是 `403 chat-thread`、他組織 `403 assistant-use`、非寫入對象 `403 assistant-form`，拒絕時不寫入。網域：`ChatFormDismissalTests`。
- 前端與 Cypress 見交接文件的驗收對照與 PR 說明。

## 16. #177 統計時區由 API 提供、數據庫摘要筆數（2026-10-05）

### 16.1 統計時區：`GET /api/v1/me` 的 `statisticsTimeZone`

- `MeResponse` 新增必填字串 `statisticsTimeZone`：後端設定 `Statistics:TimeZone`（§11.3，預設 `Asia/Taipei`）的 **IANA** 名稱。設定若寫成執行環境也認得的 Windows 名稱（例如 `Taipei Standard Time`），回傳前轉成 IANA（`StatisticsOptions.TryResolveIanaId`），因為前端以 `Intl.DateTimeFormat` 使用它。
- 放在 `/me` 而不是新端點：前端登入與每次啟動都會先讀 `/me`，不多一次請求；這個值是部署設定、不屬於組織，所以放在頂層而不是 `organization` 裡。
- 前端：`database-tracking.ts` 刪除 `STATISTICS_TIME_ZONE`，`statisticsDay(iso, timeZone)`、`toRecordView`、`toWithdrawnRecordView`、`compareRecords`、`summarizePeriod`／`summarizeRange` 都改成接收時區。API 模式由 `toIdentity()` 存成 `ApiIdentity.statisticsTimeZone`，`HybridDemoRepository` 經 `MockDemoRepositoryOptions.statisticsTimeZone` 每次使用時讀取（時間軸標籤、仍在 mock 的期間統計與報表期間都用它）；mock 模式用 `MOCK_STATISTICS_TIME_ZONE`（扮演後端的預設值）。舊的分頁 session 沒有這個欄位時同樣退回該預設值，下一次讀 `/me` 就更新。
- 不變：對話收據的撤回日期（`toChatWithdrawal`）仍取 ISO 字串的 UTC 日期部分（既有行為，本票未改）；回執編號中的日期仍是 UTC 日。

### 16.2 數據庫摘要筆數：只回給可讀者

| 端點 | `recordCount`／`subjectCount` |
| --- | --- |
| `GET /api/v1/databases`（每一列） | 呼叫者此刻可讀該數據庫的紀錄（#144：被指定為資料管理者**且**目前具備 `read-consented-submissions`）時送出；否則兩個鍵都**省略** |
| `GET /api/v1/databases/{id}` 的 `summary` | 同上（`DatabaseRecordReaders.CanReadAsync`） |
| `POST /api/v1/databases`（`201`） | 建立者是唯一的資料管理者，具備權限時送 `0`／`0`，否則省略 |

- `recordCount`＝有效紀錄（`DatabaseActiveRecords`，`WithdrawnAt IS NULL`）筆數；`subjectCount`＝有效紀錄的不同提交者數（追蹤對象中至少還有一筆有效紀錄的人）。撤回後下一次請求即減少；只剩已撤回紀錄的對象不計入 `subjectCount`（時間軸上仍列出其軌跡，§9）。可讀但沒有紀錄時是 `0`，不是省略。
- **不可讀者的回應與 #177 之前逐位元組相同**：鍵是省略，不是 `null`（`JsonIgnore(WhenWritingNull)`），而 OpenAPI 文件把這兩個屬性標成選填（`recordCount?: number | null`），所以與 #106 的「必填鍵被省略」問題不同：`OpenApiContract` 檢查仍然成立。前端 adapter 把缺少的鍵轉成 `null`，畫面顯示「僅指定資料管理者可查看」，與 mock 一致；API 模式的清單不再顯示「將於後續版本開放」。
- 每次請求重新判斷（權限從資料庫讀、指定從 `DatabaseDataManagers` 讀），不快取；撤銷權限或取消指定後下一次請求就省略。
- 沒有 N+1：清單先以 `DatabaseRecordReaders.ReadableDatabaseIdsAsync` 取得可讀的 id（沒有權限時不查詢），再以 `DatabaseActiveRecords.CountsOf` **一個**分組查詢（`WHERE "DatabaseId" = ANY(@ids) AND "WithdrawnAt" IS NULL GROUP BY "DatabaseId"`，`COUNT(*)`、`COUNT(DISTINCT "SubmittedByAccountId")`）算出全部，查詢數與數據庫數量無關。

### 16.3 測試

- 整合（真實 PostgreSQL）：`DatabaseSummaryCountsTests`——可讀者在清單與詳情看到正確的筆數與對象數（沒有紀錄的數據庫是 0／0），撤回一筆對象仍在、撤回唯一一筆對象減少；擁有者取消自己指定後，清單與詳情沒有計數鍵，且新增、撤回紀錄前後的回應字串完全相同；授予權限的下一次請求出現計數、撤銷後消失；建立回應只對具備權限的建立者帶 0／0。`ToQueryString()` 確認計數是單一分組查詢。`SignInFlowTests`——`/me` 帶預設的 `Asia/Taipei`，設定 `Europe/Berlin` 或 `Taipei Standard Time` 時回 `Europe/Berlin`／`Asia/Taipei`。
- 前端：`statisticsDay` 與 `summarizePeriod` 換時區後曆日與期間歸屬跟著改變；Hybrid 時間軸在 `/me` 給 `America/Los_Angeles` 時標籤是前一天；`toIdentity()` 保存 `statisticsTimeZone`；清單 adapter 用 2026-10-05 實際取得的可讀者／不可讀者 JSON（後者沒有計數鍵）。
