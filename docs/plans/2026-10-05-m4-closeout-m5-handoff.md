# M4 結案與 M5 交接

- **日期：** 2026-10-05
- **狀態：** M4 與其後續工單（#150、#164、#171、#174–#180）已全部合併並關閉。M5 尚未寫計畫，也還沒有建立工單。
- **基準：** `master` 的 `9993ee8`（合併 PR #187）。
- **取代：** [M4 進度報告與交接](2026-10-05-m4-progress-handoff.md)（暫停點版本），它第 4 節的待辦已在本文件第 2 節逐項結清。

## 給下一個 session 的交接 prompt

繼續 smart-agri。M4「數據庫、表單紀錄與固定查詢」與所有後續工單都已合併到 `master`（`9993ee8`）。負責人已決定下一個里程碑是 **M5a 網站嵌入**，見本文件第 3 節。M5 還沒有計畫文件，所以**第一步是寫 M5a 計畫並拆成工單，不是直接實作**：

1. 先讀本文件、[里程碑 ADR](../adr/2026-09-25-milestone-order.md)、[對外管道防濫用 ADR](../adr/2026-09-25-public-channel-protection.md)、[機敏設定 ADR](../adr/2026-09-25-secrets-storage.md)、[模型供應商與資料落地 ADR](../adr/2026-09-25-llm-providers-and-data-residency.md)、[M3.5 計畫](2026-09-29-assistant-acceptance-milestone.md)（發布閘門所需的驗收狀態）。
2. 以第 3 節的範圍寫 `docs/plans/<日期>-backend-milestone-5a-website-embed.md`，比照 M2–M4 計畫的格式；有待負責人決定的事，開工前一次問完。
3. 計畫確認後才建 GitHub 工單（標籤 `ready-for-agent`，用原生 blocked-by 標出先後）。第一批先做第 4 節的前置工作。

## 1. M4 交付總覽

| 工單 | 內容 | PR |
| --- | --- | --- |
| #142–#149 | 數據庫、表單版本、資料管理者、同意提交、撤回、固定統計查詢、助理連接與對話表單、對話中查詢 | #159–#163、#165–#167 |
| #150 | 站內定期報表（AI 摘要只用算好的數字，並檢查摘要中的數字） | #169 |
| #164 | 對話表單請求可改由模型選擇工具（`Chat:FormRequests:Trigger`）；新增 `Ai:Chat:ReasoningEffort`；真實模型評估 | #170 |
| #171 | 表單請求體驗：判斷中的等待狀態、跳錯時的退路（「不用了」、撤回連結）、「回報資料」入口；E1–E3 API | #172（設計交接）、#173 |
| #174 | 串流中建議問題列可用鍵盤捲動（axe `scrollable-region-focusable`） | #181 |
| #175 | 客戶部署 compose 接上對話模型、表單判斷（預設 `Model`）與統計時區 | #182 |
| #176 | mock repository 改懶載入，初始 bundle 735 kB → 589 kB | #183 |
| #177 | 統計時區由 `/me` 提供；數據庫摘要筆數只給可讀者 | #184 |
| #178 | 對話查詢回答納入回覆統計，另分一類 `database-query` | #185 |
| #179 | 定期報表連續略過 3 期自動停用，可重新啟用 | #186 |
| #180 | 數據庫封存（軟刪除），封存期間報表暫停 | #187 |

契約都記錄在 [M4 設計文件](2026-10-03-m4-142-database-templates.md) §6–§19。合併時的本機驗證：後端 1549/1549、前端 1015/1015，初始 bundle 589.34 kB。

**真實模型評估**（[#164 評估](../evals/2026-10-05-164-form-request-trigger.md)）：判斷是否跳出表單，關鍵字門檻漏觸 10/18、誤觸 8/18；`gpt-6-luna`（`ReasoningEffort=None`）0/18、0/18；`gpt-4o-mini` 1/18、0/18。推理型模型在 Chat Completions 上必須設 `ReasoningEffort=None` 才能使用工具，否則表單工具與 #149 的查詢工具都會回 HTTP 400。

## 2. M4 進度報告第 4 節待辦的結清

| 原待辦 | 結果 |
| --- | --- |
| #164 改由模型選擇工具 | 已完成（PR #170）；程式預設 `Keyword`，客戶部署預設 `Model` |
| 前端 bundle 超過預算 | 已完成（#176）；**只剩約 11 kB 空間**，見第 3 節的架構決定 |
| 前端統計時區寫死 | 已完成（#177） |
| 數據庫摘要筆數 | 已完成（#177） |
| 查詢回答不進回覆統計 | 已完成（#178） |
| 定期報表略過紀錄 | 已完成（#179）；報表保留期限依負責人決定，先不設 |
| 數據庫無刪除 API | 改做封存（#180） |
| 後端測試容器偶發啟動失敗 | 繼續觀察，不另開工單；本機避免同時跑多套 `dotnet test` |
| 共用開發資料庫 | 維持規則：API 模式 E2E 一律用臨時資料庫 |
| PR #141 | 已關閉，內容由 M3.5 結案與 M4 進度文件取代 |
| 本機 worktree | 本輪的 worktree 已清除；`butterfish/.worktrees/` 下 6 個 M3.5 worktree 由負責人自行清理 |

另發現並修正：mock 的資料管理者原本讀不到紀錄頁籤（#144 起 API 已允許），隨 #180 修正（PR #187）。

## 3. 負責人已決定（2026-10-05）

### 3.1 下一個里程碑：M5 對外發布，先做網站嵌入

- **先做 M5，不先做「待補功能 6：跨部門案件、交接與追蹤」**（[業務審查](../reviews/2026-09-26-project-review-and-backlog.md)第 6 項）。理由：M3.5 本來就是 M5 的閘門，已完成；M4 也已完成，發布沒有其他前置條件；案件是全新領域，ADR 本身就需要多輪討論。
- 待補功能 6 的 ADR 可以另外撰寫，**不擋 M5**。該 ADR 仍要決定 `AssistantIssue` 是否併入通用的工作項目，以及之後的里程碑順序。

### 3.2 M5 拆成兩段

| 段落 | 範圍 |
| --- | --- |
| **M5a 網站嵌入** | 發布閘門（M3.5 驗收「通過且未過期」才可發布）；`/use/:assistantId` 匿名訪客；每個助理的網域白名單，由伺服器以 CORS 與 `frame-ancestors` 限制，白名單為空時不可用；依訪客、IP、助理的速率限制；組織每月 token 上限，接近時通知、超過時暫停對外回覆（組織內部不受影響）；機敏設定加密（Data Protection，只寫不讀，回傳「已設定」與末四碼） |
| **M5b LINE** | Webhook 簽章驗證；LINE Channel Secret／Access Token 以 M5a 的加密保存 |
| 之後另排 | 依組織選擇模型的設定畫面、對話保存期限與自動刪除（屬於組織設定，不是發布本身的條件） |

### 3.3 架構：嵌入入口獨立於 admin

客戶網站上的對話視窗做成**獨立的輕量入口**，不載入整個 admin 應用（初始 589 kB，離 600 kB 預算只剩約 11 kB）。實際形式（獨立的 Nx app、Web Component 或 iframe 頁）在 M5a 計畫中決定，計畫要列出它自己的 bundle 預算。

### 3.4 M2 遺留的負責人待辦

依 [M2 負責人待辦](2026-09-26-m2-owner-action-items.md)：

| 項目 | 決定 |
| --- | --- |
| 1. PR #34 | 已合併，已在該文件勾選 |
| 3. OpenAI 金鑰 | 已在本機提供（放在 repo 之外，不得讀出或印出內容）。**#41 本機驗收與 #50 OpenAI 檢索評測列為 M5a 第一批**，對外前完成，並依結果調整 `Retrieval:MinScore` |
| 2. 去識別化的真實文件 | 不擋 M5a 開發，但**對外上線前**由負責人提供 3–5 份 |
| 4. 本機多語嵌入模型 | **延後**，等有完全地端部署的客戶再做 |
| 5. GitGuardian 誤報 | 由負責人在儀表板標記成 false positive |

## 4. M5a 第一批（前置工作）

1. **#41、#50 的真實模型補做**：以 OpenAI 處理一份文件並在 Aspire 儀表板看到嵌入呼叫；跑 OpenAI 檢索評測，結果寫入 `docs/evals/`，`Retrieval:MinScore` 的調整另開 PR。
2. **#188**：API 模式數據庫清單顯示連接的助理（已建票，`ready-for-agent`）。
3. 寫 M5a 計畫時，同時列出負責人要準備的外部事項（第 6 節）。

## 5. 開工與驗收基準

沿用 M4 的做法：

- 每張工單一個 worktree，從最新 `master` 開分支；需要時用 `cp -cR` 複製 `node_modules`。subagent 實作後由主 session 自行重跑驗證；push、開 PR、合併都要負責人明確同意，以 merge commit 合併。
- 互相依賴、會改 migration 或 `openapi/v1.json` 的工單，用接續分支依序做；合併時逐層把下一張 PR 的目標改成 `master`、同步並重新驗證。
- 後端：`dotnet build` 的結束碼為 0 才跑 `dotnet test`，不要用 `--no-build` 測試合併前的舊二進位檔。前端：`tsc -p apps/admin/tsconfig.spec.json`、`nx test admin`，並以 `--skip-nx-cache` 的 production build 量 bundle。
- Node 24（`~/.nvm/versions/node/v24.18.0/bin`），`NX_DAEMON=false`；Testcontainers 需要 colima。
- 本機 port 4200 可能被其他專案佔用：改用 production build 加靜態伺服器（其他 port），再直接執行 `cypress run --config baseUrl=…`。
- API 模式 E2E 一律用臨時資料庫，不 migrate 共用的 `smartagri`。
- 真實金鑰只放在 repo 外的本機檔案；檢查時只輸出變數名稱與值的長度，絕不印出原文。

## 6. 負責人在 M5 開工前要準備的事

- 一個可以測試網站嵌入的網域（放進白名單）。
- M5b 才需要：LINE 官方帳號與 Messaging API channel。
- 上線前：3–5 份去識別化的真實文件。
- 正式部署：在 `deploy/.env` 設定 `CHAT_PROVIDER`、`CHAT_MODEL`、`CHAT_API_KEY`；使用 `gpt-6-luna` 時加 `CHAT_REASONING_EFFORT=None`。
