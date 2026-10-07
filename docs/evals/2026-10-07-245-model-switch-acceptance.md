# #245 兩個真實對話模型的切換驗收（M6-8）

- **日期**：2026-10-07
- **分支**：`feature/245-model-deploy-acceptance`（起點 `master` 2d00e35，含全部 M6／M7）
- **結論**：通過。組織在部署預設 `gpt-6-luna` 與額外的 `gpt-5.6-terra` 之間切換（預設 → terra → 切回預設），每一段的試問、一般回答、題組重跑、表單請求、數據庫查詢、報表摘要都記到當時生效的模型；兩個模型都以工具呼叫完成了表單請求與數據庫查詢。驗收頁「上次測試使用模型 X，現在是 Y」的條件在切換後成立、重跑後消失。

## 1. 環境

| 項目 | 值 |
| --- | --- |
| API | `dotnet run --project apps/api/src/SmartAgri.Api --no-build --urls http://localhost:5265`，`ASPNETCORE_ENVIRONMENT=Development`（預設 launch profile） |
| 建置 | 在沒有任何 `Ai__Chat__*` 變數的乾淨 shell 裡 `dotnet build apps/api/SmartAgri.slnx`（結束碼 0），之後只用 `--no-build` 啟動 |
| 資料庫 | 共用的開發 PostgreSQL 容器裡另建的臨時資料庫 `m6_245`（`create extension vector` 後 `migrate`），驗收後已 `drop database` |
| 部署預設（`Ai:Chat`） | Provider `OpenAI`、Model `gpt-6-luna`、DisplayName `gpt-6-luna`、ReasoningEffort `None` |
| 額外的模型（`Ai:Chat:Models:0`） | Provider `OpenAI`、Model `gpt-5.6-terra`、Id `gpt-5.6-terra`、DisplayName `gpt-5.6-terra（進階）`、ReasoningEffort `None` |
| 金鑰 | 負責人的本機金鑰檔，只在啟動 API 的同一條指令裡載入；本文、log 與指令輸出都沒有金鑰原文（第 6 節） |
| 其他設定 | `Chat__FormRequests__Trigger=Model`（與客戶 compose 的預設相同）、`Jobs__WorkerEnabled=true`（題組與報表是背景工作） |
| 嵌入模型 | `Fake`（`fake-dev`），見第 5 節問題 1 |
| 資料 | `migrate` 時加 `SEED_DEMO_PASSWORD` 與 `SEED_DEMO_KNOWLEDGE=true`：安心商行（`anxin`）的 `admin`／`internal`／`customer` 三個帳號，以及三個示範知識庫（6 個版本全部核准） |

啟動 log 列出了兩個模型（不含金鑰）：

```text
Chat: model gpt-6-luna (deployment default): provider openai, model gpt-6-luna.
Chat: model gpt-5.6-terra: provider openai, model gpt-5.6-terra.
```

## 2. 準備（全部用 API，只有日期用 SQL）

以 `admin`（管理者）登入（`/api/v1/auth/login` → `/connect/authorize` PKCE → `/connect/token`），依序：

1. `GET /api/v1/connectable-sources` 取得 3 個知識庫。
2. `POST /api/v1/assistant-drafts` 建兩份草稿（三個知識庫、`knowledgeScope: allow-general-knowledge`）：一份給精靈試問，一份 `POST /api/v1/assistants` 建成「#245 驗收助理」。
3. 助理加兩題題組（`POST .../test-cases`：「退貨要幾天內？」「配送大約要幾天？」）。
4. `POST /api/v1/databases`（範本 `template-periodic-report`，名稱「每週回報」）→ `PUT .../sources/database/{id}` 連接 → `PATCH .../settings` 設為寫入對象（`dataWritePurpose`「每週回報完成數量。」）→ `PUT /api/v1/databases/{id}/access` 把 `admin` 設為資料管理者（查詢與報表需要）。
5. 以 `customer` 送出 8 筆紀錄（`POST /api/v1/databases/{id}/submissions`）。
6. **SQL（只改測試資料的日期）**：把 8 筆的 `DatabaseSubmissions.SubmittedAt` 分到 2026-09-28、10-05、10-12、10-19 四週各 2 筆，讓三次報表（10-05、10-12、10-19 週）都有「本期與前一期」的紀錄，才會產生 AI 摘要。
7. `PATCH .../settings` 開啟每週報表（`periodicReport: weekly`），排入 10-05 週的 `reports.generate-period` 工作。

助理沒有連接任何案件類型，所以表單的選擇呼叫是 `form-request`（不是 `proposal-selection`）。

## 3. 步驟

驗收腳本依序跑三段，每段開頭先讀 `GET /api/v1/organization/chat-model`，需要時以 `PUT` 切換，再觸發下列呼叫：

| 呼叫 | 觸發方法 | 用途（`ModelInvocations.Purpose`） |
| --- | --- | --- |
| 試問 | `POST /api/v1/assistant-drafts/{id}/trial-answers`「退貨要幾天內？」 | `trial-answer` |
| 一般回答（附帶） | `POST /api/v1/assistants/{id}/chat/runs`「商品保固多久？」 | `form-request`（模型判斷不需要表單）＋ `generate-answer` |
| 題組重跑 | `POST /api/v1/assistants/{id}/test-runs`，背景工作 `assistants.run-test-set`，輪詢到 `completed` | `assistant-test`（每題一次） |
| 表單請求 | chat run「我要回報本週的完成數量」 | `form-request` |
| 數據庫查詢 | chat run「每週回報這個資料庫總共有幾筆紀錄？」 | `database-query` |
| 報表摘要 | SQL 把排隊中的 `reports.generate-period` 的 `RunAfter` 改成現在（與 `PeriodicReportEndpointsTests` 的作法相同），背景工作產生報表後排入 `reports.summarize` | `generate-report-summary` |

三段：

1. **預設**：沒有選擇（`selectedId: null`、`source: deployment-default`、`effective.model: gpt-6-luna`）。
2. **切到 terra**：`PUT /api/v1/organization/chat-model {"modelId":"gpt-5.6-terra","revision":…}` → `200`，`source: selected`、`effective.model: gpt-5.6-terra`。
3. **切回預設**：`PUT … {"modelId":null,…}` → `200`，`source: deployment-default`、`effective.model: gpt-6-luna`。

`GET` 的 `options` 每次都是 `gpt-6-luna`、`gpt-5.6-terra` 兩項。兩次切換各寫了一筆 `OrganizationActivities`（`chat-model-changed`，detail 記前後的 id 與顯示名稱，例如 `from {id: null, displayName: gpt-6-luna}` → `to {id: gpt-5.6-terra, displayName: gpt-5.6-terra（進階）}`）。

## 4. 結果

### 4.1 用途 × 模型：是否記到正確的模型

依 `ModelInvocations` 的 `Purpose`／`Model` 統計。分段的界線是兩筆 `chat-model-changed` 的時間（07:14:03.198、07:14:17.002 UTC；由 API 寫入，與 `ModelInvocations.At` 同一個時鐘）。每一列都 `Succeeded = true`。

| 用途 | 1. 預設 `gpt-6-luna` | 2. 切到 `gpt-5.6-terra` | 3. 切回 `gpt-6-luna` | 正確？ |
| --- | --- | --- | --- | --- |
| 試問（`trial-answer`） | gpt-6-luna × 1 | gpt-5.6-terra × 1 | gpt-6-luna × 1 | 是 |
| 題組重跑（`assistant-test`） | gpt-6-luna × 2 | gpt-5.6-terra × 2 | gpt-6-luna × 2 | 是 |
| 表單請求（`form-request`） | gpt-6-luna × 2 | gpt-5.6-terra × 2 | gpt-6-luna × 2 | 是 |
| 數據庫查詢（`database-query`） | gpt-6-luna × 1 | gpt-5.6-terra × 1 | gpt-6-luna × 1 | 是 |
| 報表摘要（`generate-report-summary`） | gpt-6-luna × 1 | gpt-5.6-terra × 1 | gpt-6-luna × 1 | 是 |
| 一般回答（`generate-answer`，附帶） | gpt-6-luna × 1 | gpt-5.6-terra × 1 | gpt-6-luna × 1 | 是 |

`form-request` 每段 2 次：一次是一般回答那題（模型沒有選表單，照常回答），一次是表單請求那題（模型選了表單）。

其他紀錄：

| 紀錄 | 1. 預設 | 2. terra | 3. 切回預設 |
| --- | --- | --- | --- |
| `AssistantTestRuns.Model` | gpt-6-luna（completed，1 過 1 不過） | gpt-5.6-terra（同） | gpt-6-luna（同） |
| `DatabaseReports.SummaryModel`（期間） | gpt-6-luna（10-05 週，`ready`） | gpt-5.6-terra（10-12 週，`ready`） | gpt-6-luna（10-19 週，`ready`） |
| 回覆的 `reply.kind`：試問／一般回答／表單／查詢 | company-data／general-knowledge／form-request／database-query | 同 | 同 |

題組每次「1 過 1 不過」是測試資料的問題，不是模型的問題：「退貨要幾天內？」我設的預期是 `general-knowledge`，實際兩個模型都從知識庫回答（`company-data`，`kind-mismatch`）。

### 4.2 工具呼叫

兩個模型、每一段，表單請求與數據庫查詢都是**模型回傳的工具呼叫**，不是退回關鍵字：

- **表單請求**：回覆 `kind: form-request`、表單「每週回報」。這條路徑只有在模型呼叫失敗（例外）時才退回關鍵字，而且會記 `Succeeded = false` 並寫一筆 warning「…the keyword gate decides instead」；三段的 `form-request` 紀錄全部成功，API log 裡這則 warning 是 0 筆。模型在一般回答那題也正確地**沒有**選表單。
- **數據庫查詢**：回覆 `kind: database-query`，`databaseQuery.status: answered`、`query: record-count`、`period: last-30-days`，結果「近 30 天（2026-09-08 至 2026-10-07）共有 4 筆有效紀錄」。查詢沒有關鍵字退路：模型沒有回傳工具呼叫就不會有 `database-query` 回覆，模型呼叫失敗則是 `RUN_ERROR chat-unavailable`。
- API log 裡沒有任何 `fail:` 或 HTTP 400。

兩個模型的推理強度都設成 `None`。負責人事前另做過最小的工具呼叫：沒設推理強度時，`gpt-5.6-terra` 回 HTTP 400「Function tools with reasoning_effort are not supported … in /v1/chat/completions」（`gpt-6-luna` 同樣的限制見 M4 #164）。所以部署文件要求兩個都設 `None`（`deploy/README.md` 第 11.2 節）。

### 4.3 驗收頁提示

驗收頁的條件（`chatModelChangedSinceRun`）：最近一次有記模型的題組重跑 `model` ≠ `GET /api/v1/organization/chat-model` 的 `effective.model`，且沒有排隊中或進行中的重跑。

| 時間點 | 最近一次重跑的模型 | `effective.model` | 提示 |
| --- | --- | --- | --- |
| 第 1 段開始（還沒有重跑） | — | gpt-6-luna | 不顯示 |
| 切到 terra 後、重跑前 | gpt-6-luna | gpt-5.6-terra | **顯示**「上次測試使用模型 gpt-6-luna，現在是 gpt-5.6-terra。」 |
| 切回預設後、重跑前 | gpt-5.6-terra | gpt-6-luna | **顯示**「上次測試使用模型 gpt-5.6-terra，現在是 gpt-6-luna。」 |
| 第 3 段重跑完成後 | gpt-6-luna | gpt-6-luna | 不顯示 |

（依 API 回應判斷條件，沒有截圖；畫面的文字與條件已由 `assistant-acceptance-tab.component.spec.ts` 測試。）

### 4.4 token 用量

只計對話模型（嵌入模型是 `Fake`）。

| 分段 | 呼叫數 | 輸入 tokens | 輸出 tokens |
| --- | --- | --- | --- |
| 1. 預設 `gpt-6-luna` | 8 | 4,025 | 295 |
| 2. `gpt-5.6-terra` | 8 | 4,176 | 304 |
| 3. 切回 `gpt-6-luna` | 8 | 4,174 | 282 |
| **合計** | **24** | **12,375** | **881** |

依模型：`gpt-6-luna` 16 次、8,776 tokens；`gpt-5.6-terra` 8 次、4,480 tokens。同一組呼叫的輸入 tokens 兩個模型相同（例如數據庫查詢都是 1,499、兩次表單選擇合計都是 798、題組兩題合計都是 711），輸出長度相近（報表摘要 terra 53、luna 78／80）。

## 5. 發現的問題與偏離

1. **金鑰檔裡的金鑰不能用 `text-embedding-3-small`**：第一次 `migrate`（嵌入模型照計畫用真實的 `text-embedding-3-small`）6 個版本的嵌入呼叫全部失敗，OpenAI 回 HTTP 403 `model_not_found`；直接以 curl 對 `/v1/embeddings` 試三個金鑰變數，也都是 403 `model_not_found`（只印狀態碼與錯誤代碼）。看起來是金鑰所屬的專案沒有開放這個嵌入模型。因此改用 `Fake` 嵌入模型重建臨時資料庫，知識庫照樣處理、核准、可檢索。**對話模型的切換與工具呼叫不受影響**；但這次沒有驗到「真實嵌入＋真實對話」的檢索品質（檢索品質的驗收見 `2026-10-06-41-openai-embedding-acceptance.md`）。負責人若要在正式部署用這把金鑰做嵌入，要先在 OpenAI 專案設定開放 `text-embedding-3-small`。
2. **報表期間用 SQL 提早到期**：每週報表的工作原本在期間結束（10-11 16:00 UTC）後才執行；驗收時把 `RunAfter` 改成現在，並把測試紀錄的日期分到四週（第 2 節第 6 步），其中兩週在未來。這只是讓三段各產生一份有摘要的報表，報表與摘要都由正常的背景工作產生。
3. **紀錄時間與資料庫時鐘差約 0.5 秒**：資料庫容器的時鐘比主機快約 0.54 秒，第一次用資料庫的 `now()` 當分段界線時，有一筆試問被算進前一段。改以 API 寫的 `chat-model-changed` 時間當界線後全部正確。之後要依時間分段比對 `ModelInvocations`，請用 API 寫入的時間，不要用資料庫的 `now()`。
4. **沒有發現產品缺陷。**

## 6. 金鑰檢查

- 金鑰只在啟動 API 與 `migrate` 的同一條指令裡載入；建置在沒有這些變數的 shell 裡進行。
- 只印過 Provider、Model、Id、DisplayName、ReasoningEffort 的值與金鑰的長度。
- 收尾時對所有改動的檔案、本文、API／migrate log 與驗收腳本的原始回應用 `grep -rn "sk-"` 檢查：除了 `ChatModelCatalogStartupTests.cs` 原本就有的兩個假金鑰常數（`sk-default-never-logged`、`sk-second-never-logged`，新增的測試也用它們），沒有任何命中。

## 7. 收尾

- 停掉驗收用的 API（port 5265），`drop database m6_245`。
- 原始回應與 log 留在本機 scratch 目錄，不進 repo。
