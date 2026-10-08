# 上線前準備結案與自架部署交接（2026-10-08）

- **基準：** `master` `99f1c74`
- **給誰：** 下一個接手的 session。先讀這份，再讀 `deploy/README.md`。

## 1. 已完成

### M5b（LINE）
#229–#235 全部合併。實機驗收紀錄在 `docs/evals/2026-10-07-235-line-acceptance.md`。

### 上線前準備（計畫 `docs/plans/2026-10-07-pre-launch.md`）
全部合併：

| 工單 | 內容 |
| --- | --- |
| #300 | 評估集：表格式店家資料、條件判斷題、陷阱題，以及修改前的基準 |
| #301 | 表格每列切一段、`ChunkFormat`、`rechunk` CLI |
| #302、#304 | `Retrieval:CandidateMinScore`，最終定為 **0.35**。Development 設為 `""` 關閉，因為 Fake 模型的相符分數只有約 0.32 |
| #303 | 回答規則第 4 條：依資料推論否定結論 |
| #324 | 命中表格列時，同表其他列一起交給模型（`TableIndex`，`ChunkFormat` 3） |
| #305 | admin 的「使用助理」頁改成 `/chat/:id`；新增 `production-api` 建置 |
| #306 | api 容器一起提供 admin（`Admin:RootPath`、SPA fallback、`frame-ancestors 'self'`）；`ADMIN_SPA_ORIGIN` 預設為 `PUBLIC_BASE_URL` |
| #307 | `deploy/privacy-policy-template.md`；LINE、訪客、員工路徑的例外 log 改用 `ExceptionSummary`，不含使用者文字 |
| #308 | API 模式 admin 的 initial bundle 降到 587.91 kB |
| #291 | LINE 非文字訊息的回覆可由擁有者設定（`nonTextReply`） |
| #331 | `deploy/docker-compose.yml` 加上 `restart: unless-stopped` |

#292（回答品質）已關閉。最終評估在 `docs/evals/2026-10-07-304-final-evaluation.md`：
- #292 的實例都 3/3 答對：10 天退貨是否定結論，店家地址、電話、公休日、「週二有營業嗎」也都正確。
- 陷阱題與應拒答題 30/30 拒答。
- 原有題目沒有退步。

## 2. 還開著的工單
- **#333 多租戶 `add-organization`（下一個 session 的第一件事）**：資料層已經是多租戶，但 `setup` 在已有組織時會拒絕，所以第二家店沒有入口。負責人決定系統放在自己的主機上，給多家店測試使用。
- **#327 上線後實驗**：有段落超過 `MinScore` 時，也補上候選段落。「星期三可以去門市買東西嗎？」目前拒答，沒有答錯。

## 3. 部署決定（負責人 2026-10-07～08）
- **主機**：負責人的閒置 PC（原本是 Windows＋黑蘋果雙系統），**整顆硬碟重灌成 Ubuntu Server 24.04 LTS**。電腦放在負責人那裡，代管多家店（多租戶）。現階段只給免費測試用戶，不考慮雲端；之後若要收費再評估 AWS Lightsail 東京 4 GB。
- **對外 HTTPS**：**Tailscale Funnel** 提供免費的固定網址（`xxx.ts.net`），不買網域，也不開路由器的埠。
- **第一家店**：大西洋鳳林店。店家的真實文件放在 `/Users/fangjiemini/Projects/大西洋2026/文件資料/`（3 份 md），**不要放進 repo**。上線後由負責人在正式 admin 上傳。驗收時的觀察：文件裡的店名是「大西洋冰城」，公開電話已寫在文件裡。

## 4. 負責人正在做／下一個 session 要確認的
負責人在重灌電腦，完成後會回報：
1. 規格（處理器、記憶體、硬碟）；
2. 內網 IP；
3. Tailscale 是否已註冊。

安裝時如果看到兩顆以上的硬碟，負責人會先拍照回報，不要讓他直接選。

接著的步驟（每一步執行前先向負責人說明）：
1. 讓這台 Mac 可以用 SSH 金鑰登入主機（`ssh-copy-id`）。主機密碼由負責人自己輸入，不經過對話。
2. 用 Docker 官方 apt repo 安裝 Docker Engine 與 compose plugin（不要用 snap），並設定開機自動啟動。
3. 安裝 Tailscale，`tailscale funnel` 轉到 api 的對外埠。**要實測的事**：
   - Funnel 是否帶 `X-Forwarded-Proto: https` 與 `X-Forwarded-For`。Production 的 `/connect/*` 強制 HTTPS，`TRUSTED_PROXIES` 要設成 Funnel 進到容器時的來源，例如 docker bridge 網段。
   - LINE webhook 與官網嵌入都要能從外部連到。
4. 照 `deploy/README.md` 部署：
   - 在主機上產生三個 `.pfx`，密碼放在主機上的檔案，不印出；
   - `deploy/.env` 裡 `PUBLIC_BASE_URL` 設為 Funnel 網址；
   - 建置映像檔、`migrate`、`setup`（建立大西洋鳳林店與管理者，一次性密碼只顯示給負責人）、`up -d`；
   - 模型用 OpenAI（`text-embedding-3-small`、`gpt-6-luna`，`ReasoningEffort=None`），金鑰由負責人放在主機上。
5. 備份：資料庫 dump 加上 `dataprotection.pfx`，排程備份到主機以外的地方。
6. 用負責人的 LINE 官方帳號接上正式網址，重按「測試連線」讓 webhook 改指向正式網址。
7. #333 完成後，用 `add-organization` 加第二家店；每家店用 `set-token-limit` 設每月 token 上限。

## 5. 工作慣例（重要）
- **OpenAI 金鑰檔**：`/Users/fangjiemini/Projects/smart-agri/openai.env`，只能 `set -a; source …; set +a` 載入，**絕不印出**，只能看變數名稱與長度。評估前先探測嵌入端點、只印狀態碼：2026-10-07 曾因 OpenAI 專案的模型權限被改，回 403 `model_not_found`。
- **資料庫**：API 模式 E2E、評估、驗收一律用臨時資料庫，不碰共用的 `smartagri`。
- **Worktree**：一張工單一個 worktree，放在 `/Users/fangjiemini/orca/workspaces/smart-agri/porcupinefish/.worktrees/`。node_modules 從 `/Users/fangjiemini/orca/workspaces/smart-agri/emperor/node_modules` 用 `cp -cR` 複製。
- **Node 與 Nx**：Node 24（`~/.nvm/versions/node/v24.18.0/bin`），加上 `NX_DAEMON=false`。
- **Testcontainers**：Ryuk 起不來時，設 `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock`。
- **推送**：git 走 HTTPS，`-c credential.helper='!gh auth git-credential'`。負責人已同意「驗證 → push → PR → CI 全綠就合併」；合併時用 `--match-head-commit` 鎖定 commit。
- **記憶體**：記憶體很緊，一次只跑一個重的工作。subagent 只跑指定的測試，完整套件交給 CI。
- **平行開發**：另一個 session 在 `/Users/fangjiemini/orca/workspaces/smart-agri/orca/.worktrees/` 做 M6／M7。push 前先合併 master，同時有 migration 時注意 snapshot 是否衝突。
- **停服務**：`lsof -ti:<port> | xargs kill` 會連 cloudflared 這類客戶端一起殺掉，只想停伺服器時用 `-sTCP:LISTEN`。
