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

- **#143 編輯表單與試填**：新增 `PUT /api/v1/databases/{id}/form`（或 `POST .../form-versions`），請求帶 `baseVersionNumber`，伺服器以 `目前版本 + 1` 寫入新列；並發儲存撞到唯一索引時回 `409`。逐欄錯誤以 `errors["fields[2].options"]` 這類鍵回傳，對應前端 `DatabaseFieldError.fieldId`。新欄位 id 由前端或伺服器產生 `field-<隨機>`，伺服器驗證不與任何舊版本的其他欄位重複。試填與正式提交共用同一套答案驗證（建議放 `SmartAgri.Application.Databases`）。
- **#144 指定資料管理者**：新表 `DatabaseDataManagers(DatabaseId, AccountId, OrganizationId, AssignedByAccountId, AssignedAt)`，複合外鍵到 `Databases` 與同組織帳號；讀取判斷 = 指定 **且** 帳號有 `read-consented-submissions`，每次查詢重新計算。
  - **已完成（#144，2026-10-03）**：資料表如上（主鍵 `(DatabaseId, AccountId)`、兩條含 `OrganizationId` 的複合外鍵、`Cascade`），另加 append-only 的 `DatabaseDataManagerChanges`（每次指定／移除一列：帳號、動作、操作人、時間；無外鍵）。`PUT /api/v1/databases/{id}/access` 以完整清單取代指定（只有擁有者；`422` 不認得或別組織的帳號；非擁有者含資料管理者一律同一則 `403 database`）；`GET /api/v1/databases/{id}` 多回 `access`。
  - 放寬 `ListedFor` 的做法：`DatabaseAccess.ListedFor(viewer, 帳號有無讀取權限, 指定查詢)` = 擁有者，或（有權限且被指定）。`ManageableBy`／`viewerCanManage` 維持只有擁有者，所以資料管理者開詳情是唯讀。
  - **「誰可讀紀錄」的判斷點**：`DatabaseRecordAccess`（`CanRead`、`ReadableBy`，可在記憶體測試）與 Api 的 `DatabaseRecordReaders`（`CanReadAsync`、`ReadableDatabaseIdsAsync`、`EffectiveReaderIdsAsync`）；帳號權限來自每次請求重讀的 `RequestAccountPermissions`，所以撤銷指定或權限在下一個請求立即失效。#146／#147 在回傳任何紀錄、數量、趨勢前呼叫它（紀錄表本票尚未建立）。
  - 建立資料庫時建立者自動成為唯一的資料管理者（mock 同）；既有資料庫在 migration 內補同一列。
  - 與 #142 文件第 2 節的差異：詳情 `{summary, form}` 多了 `access`；`GET /databases`、`GET /databases/{id}` 不再只回擁有者的資料庫。
- **#145 提交與回執**：提交列指向 `(DatabaseFormVersionId, OrganizationId)`，另存欄位快照（名稱、型別、單位）與值；版本不是目前版本時拒絕。
- **#146 撤回**：刪除值與快照內容，只留提交／撤回時間與來源（撤回 ADR）。
- **#148 連接助理**：目前 `AssistantEndpoints` 對資料庫來源回「將於後續版本開放」；連接表以複合外鍵指向 `Databases`。

## 6. 驗收紀錄

見 PR 說明；後端整合測試在 `apps/api/tests/SmartAgri.Api.Tests/Databases/DatabaseEndpointsTests.cs`，API 模式 E2E 在 `apps/admin-e2e/src/e2e-api/database-api.cy.ts`。
