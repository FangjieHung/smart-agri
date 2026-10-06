# #41 真實嵌入模型的本機驗收（2026-10-06）

- **工單：** #191（補做 #41 最後一項與 #50 的 OpenAI 評測）。
- **基準：** `master` 的 `994331a`。
- **模型：** OpenAI `text-embedding-3-small`。金鑰放在 repo 之外的本機檔案，以 `set -a; . <檔案>; set +a` 載入；執行紀錄、報告與 commit 中都檢查過，沒有出現金鑰內容。

## 環境

- `deploy/docker-compose.dev.yml` 的 PostgreSQL 與 Aspire 儀表板（`mcr.microsoft.com/dotnet/aspire-dashboard:13.5.2`）。
- 臨時資料庫 `eval_191`（`create database` 加上 `create extension vector`），沒有使用共用的 `smartagri`。
- API 以 Development 的預設 launch profile 啟動（`--urls http://localhost:5163`），加上 `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` 與 `Ai__Embedding__*`。

## 步驟與結果

1. 以示範帳號 `anxin/admin` 登入（授權碼＋PKCE），建立一個知識庫，上傳 `apps/api/eval/retrieval/files/return-policy-v2.pdf`。
2. API 的背景工作處理這個版本，狀態變成 `ready`。
3. `ModelInvocations` 多了一筆：組織 `anxin`、`Purpose = embed-document`、`Provider = openai`、`Model = text-embedding-3-small`、`Succeeded = true`、`InputTokens = 388`，`AccountId` 是上傳者；沒有任何內容欄位。
4. Aspire 儀表板（以它的 `/api/telemetry/traces` 讀取）收到 `smart-agri-api` 的 span `embeddings text-embedding-3-small`，屬性包含：`gen_ai.operation.name = embeddings`、`gen_ai.request.model = text-embedding-3-small`、`gen_ai.provider.name = openai`、`gen_ai.usage.input_tokens = 388`、`smartagri.organization_id`。它是 `smartagri.job` span 的子項，數字與第 3 步的紀錄一致。

## 發現

**一次性指令不會匯出遙測資料。** `migrate`、`eval-retrieval`、`eval-answers`、`reindex` 等指令只使用 `app.Services`，不會啟動 host；OpenTelemetry 的 provider 要在 host 啟動時才會建立，所以即使設定了 `OTEL_EXPORTER_OTLP_ENDPOINT`，這些指令也不會送出任何 span（實測 `eval-retrieval` 執行後，儀表板的 trace 數量是 0）。模型呼叫紀錄（`ModelInvocations`）照常寫入，不受影響。要在儀表板觀察真實模型呼叫，就像上面的步驟，經由執行中的 API 操作。

## 檢索評測

報告：[2026-10-06-retrieval-text-embedding-3-small.md](2026-10-06-retrieval-text-embedding-3-small.md)。

- hit@5 27/27（#50 要求 ≥ 90%，達成）、hit@1 24/27。
- 「應查無結果」題的最高分 0.458（none-07「安心商行的統一編號是多少？」），正確命中的最低分 0.379（delivery-01「寄到澎湖要幾天？」）；兩組分數**無法**以單一門檻完全分開。
- 建議門檻 0.406 判對 32/34 題，判錯的兩題是 delivery-01（正確段落 0.379，會變成查無資料）與 none-07（這道題本來就是為了抓「門檻太低」而設計，0.458 會取到段落）。目前的 0.300 判對 28/34 題，7 題應查無結果的題目有 6 題會取到段落（只有 none-03 低於 0.300）。
- `Retrieval:MinScore` 的調整在 #192 另開 PR，並以新門檻重跑 `eval-answers`。
