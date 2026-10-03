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
- 清單、詳情：只有擁有者（`DatabaseAccess.ListedFor`／`ManageableBy`，目前相同）。**擁有不代表能讀提交內容**：讀紀錄要同時有資料管理者指定與 `read-consented-submissions`（前端 `canReadConsentedRecords` 的規則，#144 移到服務端）。#144 讓資料管理者看得到所管理的數據庫時，放寬的是 `ListedFor`，`ManageableBy` 維持只有擁有者。

## 4. 前端

- `listDatabaseTemplates`、`listDatabaseSummaries`、`createDatabaseFromTemplate`、`getDatabaseDetail` 改為 `Observable` 契約（不再傳 viewer），mock 與 Hybrid 一致；Hybrid **讀取與建立都走 API**。
- 詳情的 `upcomingFeatures` 列出 API 模式尚未提供的功能（`form-editing`、`data-managers`、`records`、`assistant-connections`），畫面改為「將於後續版本開放」並唯讀列出初始表單；mock 為空陣列。各工單完成時從 `API_UPCOMING_DATABASE_FEATURES` 移除自己那一項。
- 可恢復狀態：清單／模板讀取失敗與「沒有資料」分開顯示並可重試；建立失敗保留輸入、可再送出；送出中不能重複送出；伺服器的 `422` 訊息顯示在名稱欄位旁。

## 5. 留給後續工單的介面

- **#143 編輯表單與試填**：已實作，見下方第 7 節（端點、錯誤鍵與並發規則）。
- **#144 指定資料管理者**：新表 `DatabaseDataManagers(DatabaseId, AccountId, OrganizationId, AssignedByAccountId, AssignedAt)`，複合外鍵到 `Databases` 與同組織帳號；讀取判斷 = 指定 **且** 帳號有 `read-consented-submissions`，每次查詢重新計算。
- **#145 提交與回執**：提交列指向 `(DatabaseFormVersionId, OrganizationId)`，另存欄位快照（名稱、型別、單位）與值；版本不是目前版本時拒絕。
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

