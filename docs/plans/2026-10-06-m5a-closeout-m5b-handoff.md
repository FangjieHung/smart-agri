# M5a 結案與 M5b 交接

- **日期：** 2026-10-06
- **狀態：** M5a「網站嵌入」的 16 張工單（#188、#191–#205）與驗收修正（#212、#222）全部合併並關閉。M5b「LINE」尚未寫計畫，也還沒有建立工單。
- **基準：** `master` 的 `9b5af28`（合併 PR #223）。
- **計畫：** [M5a 計畫](2026-10-06-backend-milestone-5a-website-embed.md)；上一份交接是 [M4 結案與 M5 交接](2026-10-05-m4-closeout-m5-handoff.md)。

## 給下一個 session 的交接 prompt

繼續 smart-agri。M5a「網站嵌入」已全部合併：客戶可以把通過驗收的助理放上自己的官網，未登入的訪客在 iframe 裡問答，伺服器以 `frame-ancestors`、同源限制、頻率限制與每月 token 上限把關。下一個里程碑是 **M5b LINE**，見本文件第 4 節。M5b 還沒有計畫文件，所以**第一步是寫 M5b 計畫並拆成工單，不是直接實作**：

1. 先讀本文件、[M5a 計畫](2026-10-06-backend-milestone-5a-website-embed.md)（第 3 節的設計大多可以沿用到 LINE）、[對外管道防濫用 ADR](../adr/2026-09-25-public-channel-protection.md)、[機敏設定 ADR](../adr/2026-09-25-secrets-storage.md)、`docs/handoff/ai-assistant-backend-integration-handoff.md` §3.1（LINE）、`docs/handoff/tasks-6-10-backend-handoff.md` §6（LINE 的 mock 介面與型別）。
2. 以第 4 節的範圍寫 `docs/plans/<日期>-backend-milestone-5b-line.md`，比照 M5a 計畫的格式；第 4.2 節的待決事項開工前一次問完。
3. 計畫確認後才建 GitHub 工單（標籤 `ready-for-agent`，用原生 blocked-by 標出先後）。

## 1. M5a 交付總覽

| 工單 | 內容 | PR |
| --- | --- | --- |
| #191、#192 | #41／#50 真實模型補做；`Retrieval:MinScore` 校準為 0.406（Development 的 Fake 模型維持 0.3） | #206、#209 |
| #188 | API 模式數據庫清單顯示連接的助理 | #207 |
| #193 | Data Protection 金鑰環（檔案系統＋憑證加密）、只寫的機敏欄位 `ProtectedSecret`／`SecretStatusView` | #211 |
| #194 | 網站頻道資料模型、設定與發布 API；發布閘門與「實際服務狀態」 | #213 |
| #195 | 組織每月 token 用量與上限、`set-token-limit` 指令、用量 API | #215 |
| #196 | `Visitor` 驗證 scheme、訪客工作階段與訪客對話端點、同源限制 | #218 |
| #197 | 訪客 API 的頻率限制與 `TrustedProxies` | #220 |
| #198 | 抽出共用的 `libs/chat` | #210 |
| #199 | 官網訪客對話視窗 `apps/widget` | #214 |
| #200 | 載入器 `embed.js`（`apps/embed-loader`） | #208 |
| #201 | API 提供 `/use/{id}`（依助理送 `frame-ancestors` 與樣式 nonce）、`/widget/*`、`/embed.js`；Dockerfile 加 Node stage | #217 |
| #202 | admin 網站頻道設定與發布改用 API | #219 |
| #203 | admin 每月用量提示 | #216 |
| #204 | API 模式 E2E：從發布到訪客對話（CI `e2e-api`） | #221 |
| #205 | 部署文件 `deploy/README.md`、真實網域驗收 | #223 |
| — | Tailwind 只掃描 admin 與 libs（後端文字讓 bundle 多 1.7 kB） | #212 |
| — | 真實網域驗收發現的問題：widget 跨網域無法啟動、暫停後無法再試、嵌入程式碼沒有帶位置 | #222 |

最後一次完整的後端驗證（#197）：Domain 208、Application 590、Api 1,034 個測試全過；CI 每張 PR 都跑完整的 build、mock E2E 與 API 模式 E2E。admin 初始 bundle 589.23 kB（預算 600 kB），widget 初始 196.35 kB（預算 240 kB）。

**真實網域驗收**（[紀錄](../evals/2026-10-06-205-website-embed-acceptance.md)）：GitHub Pages 測試頁（`FangjieHung/smart-agri-embed-test`）以 Cloudflare 通道連到本機 API，使用真實模型；Chrome 全項通過，Safari 由負責人確認通過；不在清單的網域被瀏覽器以 `frame-ancestors` 擋下。

## 2. M5a 期間的決定與發現

| 項目 | 結果 |
| --- | --- |
| 嵌入形式 | iframe＋獨立的 Angular app；客戶只貼原生 JS 載入器（負責人 2026-10-06 決定） |
| 訪客範圍 | 只做知識庫問答，不保存任何對話內容；不開放表單、數據庫查詢、轉人工 |
| 發布閘門 | 發布時驗收必須「通過」；之後「過期」照常服務，「未通過」自動暫停；連接別人擁有的知識庫也會暫停（`suspended-knowledge`） |
| 用量上限 | 部署預設 2,000,000 tokens／月＋營運 CLI；用量計入組織所有對話模型呼叫；80% 站內提示，超過時只暫停對外回覆 |
| widget 預算 | 原訂 120／150 kB 達不到（Angular 空 app 就有 96 kB），負責人放寬為 210／240 kB |
| CSP | Angular 執行時會插入 `<style>`，`/use/{id}` 以每次回應的 nonce（`ngCspNonce`）允許樣式，不開 `unsafe-inline` |
| 組織篩選的例外 | 允許關掉組織篩選的地方從一處變成三處：`AccountLookup`、`PublicAssistantLookup`（只回組織 id）、`PublicWebsiteChannelLookup`（只回狀態與網域），由原始碼掃描測試鎖定 |
| 訪客的「查無資料」 | 不顯示給內部同仁的「請聯絡這個助理的管理者」，聯絡方式由擁有者寫在拒答訊息 |
| 跨網域才會出現的 bug | widget 曾把 `window.parent` 交給 Angular DI，實際嵌入時 `SecurityError` 而無法啟動；同源的單元測試與 E2E 測不到，只有真實網域驗收抓到（#222） |

## 3. 待辦與後續工單

**已建立的後續工單**（不擋 M5b）：

| 工單 | 內容 |
| --- | --- |
| #224 | 建立助理時允許選擇對外的服務對象（目前仍回 M3 的「對外發布將於後續版本開放」） |
| #225 | 官網啟動按鈕使用擁有者選的品牌色 |
| #226 | 訪客遇到預期中的 403／429 時不要在主控台顯示錯誤 |

**其他：**

- `docs/handoff/tasks-6-10-backend-handoff.md`、`docs/handoff/ai-assistant-backend-integration-handoff.md` 仍描述舊的 `/use` 行為，寫 M5b 計畫時順手更新 LINE 以外的部分。
- 測試頁 repo `FangjieHung/smart-agri-embed-test` 保留；重測時更新它的 `config.json`（通道網址與助理 id）。不再需要時由負責人封存或刪除。
- 本機已安裝 `cloudflared`（Homebrew）。

## 4. M5b LINE

### 4.1 範圍（[M4 結案與 M5 交接](2026-10-05-m4-closeout-m5-handoff.md) §3.2）

- **Webhook**：`POST` 端點接收 LINE 的事件，以 Channel Secret 驗證 `X-Line-Signature`（HMAC-SHA256），失敗直接拒絕（對外管道 ADR）。
- **憑證**：Channel Secret、Channel Access Token 以 M5a 的 `ProtectedSecret` 加密保存，API 只回「已設定」與末四碼（機敏設定 ADR）；admin 的 LINE 設定頁（mock 已有 `line-setup` 元件與 `LineSetupView`）改用 API。
- **回答**：LINE 使用者傳來的文字訊息走同一條 `GroundedAnswerService`，沿用 M5a 的發布閘門、實際服務狀態、知識庫擁有權、頻率限制與每月用量上限。
- **啟用流程**：mock 已有「填憑證 → 送測試訊息 → 啟用」的流程與逐欄檢查（`tasks-6-10-backend-handoff.md` §6）。

### 4.2 寫計畫時要請負責人決定的事

1. **LINE 使用者的身分與對話保存**：比照網站訪客「不保存任何內容」，還是依 LINE userId 保存（LINE 本身就有對話紀錄）？若保存，保存期限與刪除方式為何（這會碰到「之後另排」的對話保存期限設定）。
2. **回覆方式**：只用 reply token 回覆（免費、需在時限內回覆），還是需要 push 訊息（例如回答超時後補送；會消耗 LINE 的訊息額度）？
3. **群組與聊天室**：只支援一對一，還是也支援把官方帳號加進群組？
4. **非文字訊息**：圖片、貼圖、位置等的固定回覆內容。
5. **引用的呈現**：LINE 沒有引用抽屜；改為在回答後附上文件名稱，或附一個連到網頁版的連結。
6. **加入好友時的歡迎訊息**：沿用網站頻道的歡迎訊息，或另外設定。

### 4.3 負責人要準備的事

- 一個 LINE 官方帳號與 Messaging API channel（開發測試用）。
- 一個能從網際網路連到的 Webhook 網址：開發時可沿用 M5a 驗收的 Cloudflare 通道（`cloudflared tunnel --url http://localhost:5153`），正式環境就是 API 的 `PUBLIC_BASE_URL`。

## 5. 上線前（M5a 已可對外，正式啟用前）

- 3–5 份去識別化的真實文件（不擋開發）。
- 正式環境依 [`deploy/README.md`](../../deploy/README.md) 設定：API 的 HTTPS 對外網址（`PUBLIC_BASE_URL`）、三個憑證（`signing`、`encryption`、`dataprotection`）與金鑰環備份、`TRUSTED_PROXIES`、對話與嵌入模型（`CHAT_*`、`EMBEDDING_*`；`gpt-6-luna` 要加 `CHAT_REASONING_EFFORT=None`）、`DEFAULT_MONTHLY_TOKEN_LIMIT`。
- 換嵌入模型時，`Retrieval:MinScore` 要重新以 `eval-retrieval` 校準（0.406 只適用 `text-embedding-3-small`）。

## 6. 開工與驗收基準（沿用 M5a）

- 每張工單一個 worktree，從最新 `master` 開分支；`cp -cR` 複製 `node_modules`。subagent 實作後由主 session 重跑驗證；負責人已同意「驗證完就推上去開 PR，CI 過了就合併」，以 merge commit 合併。
- git 走 SSH 目前被拒，推送用 `git -c credential.helper= -c credential.helper='!gh auth git-credential' push https://github.com/FangjieHung/smart-agri.git HEAD:refs/heads/<branch>`。
- 後端：`dotnet build` 結束碼為 0 才跑 `dotnet test`；平行的 agent 只跑相關的測試類別，完整套件由主 session 依序跑（Testcontainers 共用 colima 的 4 GB VM）。前端：`tsc -p apps/admin/tsconfig.spec.json`、`nx test admin`，以 `--skip-nx-cache` 的 production build 量 bundle。**前端改動 `core/domain` 或 seed 時也要跑後端 Domain 測試**（後端測試會讀這些前端檔案）。
- 改 widget 或 admin 的標記時，先 `grep` `apps/admin-e2e/src` 的選擇器（#222 就因此讓 `e2e-api` 紅過一次）。
- iframe／嵌入相關的改動，要在真正不同網域的頁面上實機驗收一次。
- Node 24（`~/.nvm/versions/node/v24.18.0/bin`）、`NX_DAEMON=false`；本機 port 4200 可能被其他專案佔用，改用 production build 加靜態伺服器（例如 4310）。
- API 模式 E2E 與實機驗收一律用臨時資料庫；真實金鑰只放在 repo 外的本機檔案，只輸出變數名稱與值的長度。
