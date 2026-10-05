# M4 進度報告與交接（暫停點）

- **日期：** 2026-10-05
- **狀態：** M4 九張工單已合併八張（#142–#149）；#150 已實作、驗證完畢但尚未推送與開 PR。依負責人指示在此暫停。
- **基準：** `master` 的 `b384f14`（合併 PR #167）。
- **需求來源：** [M3.5 結案與 M4 交接](2026-10-03-m3.5-closeout-m4-handoff.md)、[M4 設計文件](2026-10-03-m4-142-database-templates.md)（資料模型、API 契約與各票接點，§6–§13）、[Mock → API 對照](../handoff/mock-to-api-mapping.md)。

## 給下一個 session 的交接 prompt

繼續 smart-agri M4。#142–#149 已合併到 `master`（`b384f14`）。剩下 #150「站內定期報表」：程式已完成並整合了 #149，在本機分支 `feature/m4-150-periodic-reports`（`5436297`），**尚未 push**。先讀本文件第 3 節，照步驟同步 `master`、重新驗證、push、開 PR、等 CI；合併仍需負責人明確同意。之後再處理第 4 節的待辦。

## 1. 已完成（已合併到 master）

| 工單 | 交付內容 | PR | 實作模型 |
| --- | --- | --- | --- |
| #142 從模板建立數據庫 | `Databases`＋不可變的 `DatabaseFormVersions`、五種伺服器端模板、清單／建立／詳情 API；他組織、他人、不存在一律相同的 `403 database` | #159 | Opus |
| #143 編輯表單與試填 | 以 `baseVersionNumber` 新增表單版本（同時儲存 409、欄位錯誤可定位）；試填與正式提交共用 `DatabaseAnswerRules`，不寫入 | #160 | Sonnet |
| #144 指定資料管理者 | `DatabaseDataManagers`＋變更軌跡；可讀紀錄＝被指定 **且** 帳號具 `read-consented-submissions`，每次請求重查；共用規則 `DatabaseRecordAccess`／`DatabaseRecordReaders` | #161 | Sonnet |
| #145 同意提交與回執 | 表單連結 `/app/forms/{id}`；軌跡表 `DatabaseSubmissions` 與內容表 `DatabaseSubmissionEntries` 分開；明確同意、伺服器重驗、單一交易、冪等鍵（並行重送只一筆）、提交當時的欄位快照 | #162 | Opus |
| #146 查看紀錄與撤回 | 撤回在同一交易內刪除內容、只留不含內容的軌跡（冪等）；提交者「我送出的資料」；資料管理者時間軸；`DatabaseActiveRecords` | #163 | Opus |
| #148 助理連接數據庫並請求表單 | `AssistantDatabases`；對話表單請求與提交重用 #145；對話訊息只存提交 id；處理人看不到私人對話；對話回執可撤回 | #165 | Opus |
| #147 趨勢與固定統計查詢 | 四個固定查詢（期間筆數、欄位加總、期間摘要、對象比較），參數只接受定義內的值；權限先於參數驗證；撤回後重算；「紀錄不足」；`Statistics:TimeZone`（預設 `Asia/Taipei`） | #166 | Sonnet |
| #149 對話中查詢授權紀錄 | 模型只在伺服器列出的四個固定查詢工具與參數中選擇；以提問成員權限執行；數字由伺服器從結果組成（模型看不到結果）；無權限、不足、失敗各有不洩漏的結果 | #167 | Opus |

每張 PR 的 CI 三個 job（`build`、`e2e`、`e2e-api`）皆通過，以 merge commit 合併。合併前在本機重跑 `dotnet build`→`dotnet test`、spec `tsc`、`nx test admin`，並在臨時資料庫上跑受影響的 mock Cypress 與整套 API 模式 E2E。`master` 最後一次本機驗證（#149 分支同步後）為後端 1424/1424、前端 931/931。

### 已確認的決策（記錄在 M4 設計文件）

- **數據庫「分享」**＝被指定為資料管理者且帳號具讀取權限；撤銷任一條件即無法連接與使用（§10.7）。
- **對話表單請求與紀錄查詢的觸發**目前是伺服器端的關鍵字門檻；表單請求不經模型，紀錄查詢在門檻後由模型選工具。改由模型完整決定留待 #164 以真實模型評估。
- **統計日期**以 `Statistics:TimeZone` 的曆日計算（預設 `Asia/Taipei`），部署映像 `aspnet:10.0` 已確認含此時區資料（§11）。
- **撤回保留**提交當下的告知條款 `ConsentTerms`（組織告知的內容，不是填寫內容）；同鍵重送已撤回的提交回已撤回的回執（§9）。
- **未引入新套件或 Agent Framework**；`Microsoft.Extensions.AI` 的 function calling 已足夠（backend-stack ADR 補充、§12）。

## 2. 未完成

### #150 站內定期報表：已實作、未開 PR

- **分支：** `feature/m4-150-periodic-reports`，HEAD `5436297`，只在本機（ref 存在 repo 的 `.git`，worktree 在 session 暫存目錄，刪除 worktree 不會遺失 commit）。內容＝#150 實作（`2025532`）＋合併 #149（`e230f30`）＋兩個修正（`8f2ffd1` 報表表格捲動區的鍵盤可達性與 `tracking.cy.ts` 頁籤清單；`5436297` `assistant-forms-api.cy.ts` 等待正確的設定請求）。
- **內容摘要（詳見設計文件 §13）：** `AssistantReportSchedules`、`DatabaseReports`（統計 `jsonb` 快照、每期唯一索引）；以既有 PostgreSQL 背景工作自我接續排程，同期只產生一份；AI 摘要只拿算好的數字，`ReportSummaryGuard` 檢查摘要中每個數字，不符就整段捨棄；模型失敗時統計與圖表仍可看並可重試；撤回不追溯既有報表；只有目前可讀紀錄者看得到。
- **已驗證（本機）：** `dotnet build` exit 0、`dotnet test` 1479/1479、spec `tsc` exit 0、`nx test admin` 957/957、`has-pending-model-changes` 無變更、臨時資料庫從零 migrate 成功。Cypress：mock 10 支全過（修正後只重跑了 `tracking`、`accessibility`）；API 模式 6 支全過（修正後只重跑了 `assistant-forms-api`）。
- **與 master 的關係：** `master` 已含 #149，`git merge-tree` 確認 #150 與 `b384f14` 可乾淨合併。

## 3. 恢復工作的步驟（#150）

1. 在 `feature/m4-150-periodic-reports` 合併 `origin/master`（merge commit）。
2. 重跑 `dotnet build apps/api/SmartAgri.slnx`（exit 0 才往下）→ `dotnet test apps/api/SmartAgri.slnx`；`npx tsc -p apps/admin/tsconfig.spec.json --noEmit`；`npx nx test admin`（Node 24：`export PATH=~/.nvm/versions/node/v24.18.0/bin:$PATH NX_DAEMON=false`）。
3. push、開 PR（`Closes #150`），等 CI 三個 job；CI 會補上本機修正後未整套重跑的 Cypress。
4. 負責人同意後以 merge commit 合併，關閉 #150。

## 4. 待辦與已知事項

| 項目 | 說明 | 建議 |
| --- | --- | --- |
| #164 對話表單請求改由模型選擇工具 | 表單請求與紀錄查詢都靠關鍵字門檻，可能誤觸或漏觸；`subject-comparison` 因對象 id 不給模型而難以使用 | 真實模型就緒後評估誤觸／漏觸率 |
| 負責人待辦（M2 遺留） | [待辦清單](2026-09-26-m2-owner-action-items.md)的 OpenAI 金鑰、去識別化真實文件、多語嵌入模型、GitGuardian 事件仍未勾選；本輪未重新確認 | M4 全程以 Fake 模型驗收，真實模型結果待補 |
| 前端初始 bundle 超過預算 | 708 kB（M4 開工前）→ 約 728 kB（#150），上限 600 kB；只是警告，CI 不失敗 | 另開票拆分 lazy chunk 或調整預算 |
| 前端統計時區寫死 | `STATISTICS_TIME_ZONE = 'Asia/Taipei'`，後端設定改了前端要一起改 | 之後由 API 回傳時區 |
| 數據庫摘要筆數 | API 模式清單的 `recordCount`／`subjectCount` 仍為 `null` | 需要時以 `DatabaseActiveRecords` 計數，只回給可讀者 |
| 查詢回答不進回覆統計 | #149 的回答不寫 `AnswerOutcomes` | 決定是否納入營運彙總 |
| 定期報表略過紀錄 | 擁有者失權或解除連接後排程不刪，每期留一筆「已略過」；報表沒有到期刪除 | 視使用情況決定自動停用與保留期限 |
| 數據庫無刪除 API | API 模式 E2E 每次執行會多建一個數據庫 | 若需要再開票 |
| 後端測試容器偶發啟動失敗 | 兩次在多個 agent 同時跑整套 `dotnet test` 時，Testcontainers 的 Postgres 啟動失敗，重跑即過；CI 未出現 | 觀察；本機避免同時跑多套 |
| 共用開發資料庫 | #142 期間曾對共用 `smartagri` 跑 migrate，已退回 #142 的 migration，但 master 當時較早的 7 個 migration 仍保留 | 之後一律用臨時資料庫跑 API 模式 E2E |
| PR #141 | M3.5 舊交接文件，內容已過期 | 負責人決定關閉 |
| 本機 worktree | `feature/m4-143`～`feature/m4-150` 的 worktree 位於 session 暫存目錄；#143–#149 已合併 | #150 開 PR 後可用 `git worktree remove` 清理 |
