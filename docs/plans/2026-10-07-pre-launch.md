# 上線前準備：實作計畫

- **日期：** 2026-10-07
- **起因：** #235 在負責人真實 LINE 官方帳號（大西洋鳳林店）實機驗收時發現的問題（`docs/evals/2026-10-07-235-line-acceptance.md`），以及正式部署前的缺口。
- **負責人決定（2026-10-07）：**
  - #292 用「表格每列切一段＋兩段門檻＋放寬推論」；
  - admin 由 api 容器一起提供；
  - 隱私權政策由我們起草範本，負責人確認後自行發布。
- **不在範圍：** 正式主機與網域的建置（等負責人決定主機與 Cloudflare 後另寫）、#291（非文字訊息回覆可設定，已是獨立工單）、轉接真人。

## 1. 目標

1. 顧客最常問的店家資訊（地址、電話、營業時間、公休日）與條件判斷題（「收到 10 天了可以退貨嗎」），在資料有寫的情況下能正確回答；資料沒寫的仍然拒答。
2. 正式環境只需要**一個網址、一個容器**，就同時提供 admin、API、LINE webhook 與官網嵌入。
3. 部署文件附一份隱私權政策範本，內容和系統實際收集、保存的資料一致。

## 2. 現況事實（2026-10-07，master `88df5f0`）

### 2.1 檢索與回答
- **切段**：只有一個切段器 `KnowledgeChunker`（目標 600 字、上限 1000、重疊 100）。
  - Markdown 依 `#`–`###` 分節，**表格沒有特別處理**：整節不超過 1000 字就整節一段。
  - docx 的表格每列轉成一行（儲存格以 ` | ` 連接），併入所在章節的內文。
  - xlsx 以列為單位切段，每段重複表頭。
  - 標題路徑存成 `LocationLabel`，嵌入時放在第一行一起嵌入。
- **重新處理**：沒有「切段版本」欄位，也沒有重新切段的途徑。`reindex` 只重新嵌入向量，不重新切段；重試只接受 `Failed` 的版本。
- **門檻**：`Retrieval:MinScore`（正式 0.406、Development 0.3）在檢索後篩選，最多 5 段交給模型（Top 5）。
  - 沒有任何段落達到門檻時，不呼叫模型，直接回 `below-threshold`。
  - 模型輸出 `[[SMARTAGRI_CANNOT_ANSWER]]` 時回 `cannot-answer`。
  - `Assistants.MinScore` 欄位存在，但沒有 API 可以設定。
- **回答規則**：`GroundedAnswerPrompt`（`grounded-answer/2026-09-27.1`）第 3 條是「參考段落不足以回答問題時，只輸出拒答標記」。員工對話、訪客、LINE、測試回合、試答、`eval-answers` 全部共用 `GroundedAnswerService`。
- **評估基準**：
  - `eval-retrieval`（34 題，其中 7 題應查無結果）：hit@5 27/27；應查無結果題的最高分 0.458 高於正確命中的最低分 0.379，所以沒有任何單一門檻能把兩者完全分開；0.406 時正確判斷 32/34（`docs/evals/2026-10-06-retrieval-text-embedding-3-small.md`）。
  - `eval-answers`（12 題）：回覆類型 11/12（`docs/evals/2026-10-06-answers-gpt-6-luna.md`）。
- **實機量到的分數**（`text-embedding-3-small`，見 #292）：
  - 店家資料表格對「週二有營業嗎」「地址在哪裡」「電話幾號」只有 0.30–0.39。
  - 改成「每列一段（標題＋欄位：值）」後升到 0.375–0.551，地址與電話仍略低於 0.406。

### 2.2 admin 的提供方式
- **目前的部署**：api 容器的 Dockerfile 有 Node 階段建置 widget，`/widget/*`、`/use/{id}`、`/embed.js` 由 `WidgetEndpoints` 提供。沒有 `UseStaticFiles`，也沒有 SPA fallback；`deploy/README.md` 寫明 admin 要另外部署。
- **API 模式的 admin**：用相對路徑 `/api/v1/...` 呼叫 API，OIDC 的 authority 就是頁面自己的來源。登入 cookie 是 `SameSite=Strict`，`LoginPath` 也假設 SPA 與 API 同源。所以同一個網址一起提供 admin 正好符合原本的設計。
- **路徑衝突**：admin 的「使用助理」頁是 `/use/:assistantId`，與 API 的訪客對話頁 `/use/{id}` 同路徑。合併後 API 端點會先比對，admin 這條路由就到不了。
- **建置設定**：admin 的 `api` 建置設定是 `optimization: false`、`sourceMap: true`，沒有 budgets，只適合開發。
- **允許的來源**：`ADMIN_SPA_ORIGIN` 只能設一個值，`migrate` 會據此登錄 `{origin}/auth/callback` 與 `{origin}/login` 兩個 redirect URI。

### 2.3 隱私
- **LINE 使用者**：資料庫不保存任何 LINE id 或訊息內容；`AnswerOutcome`、`ModelInvocation` 也不記錄 LINE 使用者。
  - 前文只放在記憶體（20 則、閒置 30 分鐘），封鎖、退出群組或收回訊息時清除。
  - webhook 去重與頻率限制的鍵也只在記憶體。
- **官網訪客**：不保存對話。訪客 token 有效 12 小時、不寫入資料庫；IP 只用在記憶體中的頻率限制。
- **送交模型供應商的內容**：助理設定、檢索到的段落、最多 6 則前文，以及問題本身；嵌入模型會收到前一題加本題。
- **待確認**：`LineQuestionHandler` 失敗時會記錄例外的 `Message`，尚未確認這段訊息是否可能夾帶使用者的文字。

## 3. 關鍵選擇

### A. 表格每列切一段（#292 ①）
- Markdown 表格與 docx 表格：每一列切成一段，內容為「欄名：值」逐行列出；標題路徑沿用 `LocationLabel`，並照現行做法放進嵌入文字的第一行。
- 表格以外的內文照舊。只有一兩列的小表格，同樣每列一段，不做合併。
- xlsx 已經以列為單位切段，這次不改（評估集若顯示也有同樣問題，再另開工單）。
- 新增**切段格式版本**（`KnowledgeDocumentVersion.ChunkFormat`），並提供一次性的 CLI `rechunk`：把格式較舊的 `ready` 版本重新抽取、切段、嵌入。原始檔已保存，用它重跑。過程中舊段落持續服務，新段落寫好後才在同一個交易裡替換。

### B. 兩段門檻（#292 ②）
- 新增 `Retrieval:CandidateMinScore`（預設值由評估決定，目標約 0.30，必須 ≤ `MinScore`）。
- 有段落 ≥ `MinScore`：照現行做法，只送這些段落。
- 沒有段落 ≥ `MinScore`、但有段落 ≥ `CandidateMinScore`：把這些候選段落（最多 Top 5）交給模型，由現有的拒答標記把關。模型拒答時記為 `cannot-answer`。
- 兩者都沒有：照舊回 `below-threshold`，不呼叫模型。
- `AnswerOutcome` 新增「這次是否用到候選段落」，方便觀察誤答。測試回合也要記錄 `CandidateMinScore`，比照現行記錄 `MinScore` 的方式。

### C. 放寬推論（#292 ③）
- 回答規則補一條：參考段落寫明規則或條件時（期限、營業日、價格），可以據此推論出明確的結論，包括否定的結論（「已超過七天，無法退貨」「週二公休，不營業」），並引用該段落。
- 第 3 條拒答規則保留：段落沒有涵蓋問題時，仍然只輸出拒答標記。
- `PromptVersion` 升版。

### D. 評估先行（#292 ④）
- **先加題目，再修改**：
  - `eval-retrieval` 加一份虛構的表格式店家資料（Markdown，包含地址、電話、營業時間、公休日、外送規則），以及對應的口語短問題。**不使用真實店家的文件。**
  - `eval-answers` 加上條件判斷題：退貨期限內與期限外、營業日與公休日、套餐有無。也加上應拒答的陷阱題，例如資料沒有提到的優惠活動。
- **先跑一次基準**：在 A–C 之前用真實模型跑一次，作為修改前的基準。
- **最後比較**：A–C 合併後再跑一次，並在報告中比較：hit@5、應查無結果題是否被誤答、回覆類型正確率、陷阱題拒答率，同時用這次的結果決定 `CandidateMinScore`。

### E. admin 由 api 容器提供
- **改 admin 路徑**：「使用助理」頁從 `/use/:assistantId` 改成 `/chat/:assistantId`。舊路徑在 admin 內轉址；正式環境的 `/use/*` 一律屬於訪客對話頁。
- **新增建置設定**：admin 新增 `production-api`（`optimization` 開啟、套用與 production 相同的 budgets、檔名加 hash）。
- **打包**：Dockerfile 的 Node 階段一起建置 admin，複製到 `wwwroot/admin`；`.dockerignore` 放行 `apps/admin`、`libs`。
- **API 提供靜態檔**：API 新增 `Admin:RootPath`。有設定時：
  - 以靜態檔提供 admin；
  - 不屬於 API 的路徑一律回 admin 的 `index.html`（SPA fallback）。API 的路徑包括 `/api`、`/connect`、`/.well-known`、`/health`、`/use`、`/widget`、`/embed.js`、`/openapi`；
  - `index.html` 不快取，帶 hash 的檔案長期快取。
- **預設來源**：admin 由 api 容器提供時，`ADMIN_SPA_ORIGIN` 沒設定就預設等於 `PUBLIC_BASE_URL`。
- **部署文件**：`deploy/README.md` 改成「一個網址同時提供 admin、API、LINE 與官網嵌入」，另外部署 admin 變成選項。

### F. 隱私權政策範本
- 新增 `deploy/privacy-policy-template.md`（繁中）：依 §2.3 的事實列出收集的資料、用途、保存期間、送交模型供應商（名稱由客戶填入）、使用者的權利與聯絡方式。範本開頭註明「需由客戶依自身狀況調整，並自行發布」。
- `deploy/README.md` 第 10.6 節連到這份範本。
- 一併確認 `LineQuestionHandler` 的例外訊息不會夾帶使用者文字；如果會，改成只記錄例外類型。

## 4. 工單（vertical slices）

| # | 內容 | 依賴 |
| --- | --- | --- |
| P1 | 評估集：表格式店家資料、條件判斷題、陷阱題；用真實模型跑基準並寫入 `docs/evals/` | — |
| P2 | 表格每列切一段＋`ChunkFormat`＋`rechunk` CLI | P1 |
| P3 | 兩段門檻 `Retrieval:CandidateMinScore` | P1 |
| P4 | 回答規則放寬推論＋`PromptVersion` 升版 | P1 |
| P5 | 修改後重跑評估、決定 `CandidateMinScore` 預設值，關閉 #292 | P2、P3、P4 |
| P6 | admin「使用助理」改路徑為 `/chat/:assistantId`，並新增 `production-api` 建置設定 | — |
| P7 | api 容器提供 admin（Dockerfile、`Admin:RootPath`、SPA fallback、預設來源、部署文件），以 `docker compose up` 實測 | P6 |
| P8 | 隱私權政策範本＋例外日誌檢查 | — |

P2 與 P3 都會動到檢索的程式碼，所以依序實作，不並行。

## 5. 驗收

- **#292 的實例**：「10 天能不能退貨」回答否定結論；表格式店家資料的地址、電話、公休日、「週二有營業嗎」都能正確回答；資料沒寫的問題仍然拒答。
- **評估不退步**：hit@5 不低於基準；應查無結果題的誤答數不增加；陷阱題全數拒答。
- **一個容器提供全部**：`docker compose up` 後，同一個網址可以登入 admin、通過 LINE 的測試連線、使用官網嵌入。
- **隱私權政策範本**：負責人確認內容。

## 6. 風險

- **兩段門檻可能讓陷阱題被誤答**：由評估集把關。如果陷阱題被誤答，就調高 `CandidateMinScore`，或在這一段只採用 Top 1–2 的段落。
- **`rechunk` 會重新呼叫嵌入模型**：會產生費用，所以只處理格式較舊的版本，並先顯示預計處理的段落數。
- **admin 打包進 api 映像檔**：建置時間和映像檔都會變大；widget 的預算不受影響。
