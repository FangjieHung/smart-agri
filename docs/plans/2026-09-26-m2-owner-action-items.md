# M2｜需要專案負責人處理的事項

**日期：** 2026-09-26
**用途：** 集中列出 M2 期間需要專案負責人親自處理或同意的事項。依負責人指示，這些事項**全部留到 M2 最後補做**，實作票不必等待；各票在補做前的替代做法寫在下表。
**相關文件：** [M2 計畫](2026-09-26-backend-milestone-2-knowledge-base.md) 第 7 節、PR #34、issue #35–#54。

## 總覽

| # | 事項 | 影響的票 | 補做前的做法 | 完成的判斷 |
| --- | --- | --- | --- | --- |
| 1 | 合併 PR #34（M2 計畫與 ADR） | 所有 M2 票的「來源」連結 | 實作票照常從 `master` 開分支；計畫內容以 `origin/docs/m2-plan` 為準 | PR #34 狀態為 merged |
| 2 | 提供去識別化的真實文件樣本 | #40、#50 | 以自製的測試檔完成 #40 的驗收，#50 先只用示範題庫 | 樣本已放到指定位置，並完成第 2 節的補做檢查 |
| 3 | 提供 OpenAI API 金鑰（只放本機） | #41、#50 | 一律用 `Fake` 嵌入器；需要真實模型的驗收項目在 PR 說明中標為「待補做」 | 本機能以 `Provider=OpenAI` 處理一份文件 |
| 4 | 同意在本機執行評測用的多語嵌入模型 | #50 | 評測只跑 OpenAI 那一組 | 兩組評測結果都已寫入 `docs/evals/` |
| 5 | 把 GitGuardian 的誤報標記為 false positive | 無（M1 遺留） | 不影響開發 | GitGuardian 上該事件已關閉 |

---

## 1. 合併 PR #34

- **內容：** M2 計畫、原檔存放 ADR、里程碑 ADR 的補充，以及本文件。只有文件變更，CI（build、e2e）已通過。
- **做法：** 在對話中回覆「合併」由 Claude 執行，或自行在 GitHub 以 merge commit 合併（本 repo 沿用 merge commit，不用 squash）。
- **尚未合併時：** issue 中的計畫路徑在 `master` 上還不存在，實作 agent 要改用 `git show origin/docs/m2-plan:docs/plans/2026-09-26-backend-milestone-2-knowledge-base.md` 讀取計畫。

## 2. 真實文件樣本（去識別化）

- **需要什麼：**
  - **PDF 2–3 份：** 最好涵蓋「由 Word 匯出」「含表格」「部分頁面是掃描圖片」三種情況。
  - **Excel 2–3 份：** 最好涵蓋「多個工作表」「合併儲存格」「有單位欄位（公斤、元、天）」。
  - 內容類型接近實際要導入的資料即可，例如商品規格、退換貨或配送規則、內部 SOP。
- **去識別化：** 替換人名、電話、地址、統一編號、客戶或供應商名稱；敏感的金額與數量可以等比例改動。文件的版面、字型、表格結構**不要改**，要測的正是這些。
- **放置位置：** 放在 `~/Desktop/smart-agri-samples/`，每份檔案註明是否可以公開。
  - **本 repo 是公開的（PUBLIC）。** 只有明確標示「可公開」的樣本，才會放進 `apps/api/tests/fixtures/knowledge/` 或評測題庫並 commit。
  - 其餘樣本只在本機使用：放在 #50 新增的 gitignore 目錄，用於本機解析檢查與評測，不進 repo、不進 CI。
- **補做時 Claude 會做的事：**
  1. 逐份上傳並檢查處理狀態與抽取預覽：文字是否正確、頁碼與章節是否對應、表格欄位與單位是否保留。
  2. 以這些樣本補充評測題（每份約 3–5 題），重跑 `eval-retrieval`。
  3. 發現的解析問題各自開新票，不直接修改已合併的程式碼。
  4. 把結果寫進 `docs/evals/<日期>-real-samples.md`；非公開的樣本只記錄檔名代號與統計，不記錄內容。

## 3. OpenAI API 金鑰

- **用途：** 本機開發與評測時，用真實嵌入模型處理文件（#41 的本機手動驗收，#50 的評測）。CI 不需要金鑰，CI 一律使用 `Fake`。
- **做法：**
  1. 在 OpenAI 平台建立一個專用的 project 與 API key，權限只開 embeddings，並設定每月用量上限。評測題庫規模很小，預估費用不到 1 美元，實際以 OpenAI 價目表為準。
  2. **不要把金鑰貼進對話或任何 repo 檔案。** 請自行寫入本機檔案，例如 `~/.config/smart-agri/embedding.env`，並設定權限 `chmod 600`：
     ```
     Ai__Embedding__Provider=OpenAI
     Ai__Embedding__Model=text-embedding-3-small
     Ai__Embedding__ApiKey=<你的金鑰>
     ```
     實際的設定鍵名與載入方式，以 #41 完成後 `apps/api/README.md` 的說明為準。
  3. 完成後告知 Claude 檔案位置即可；Claude 只確認變數是否存在，不讀取或顯示金鑰內容。
- **補做時的指令：** 見 `apps/api/eval/retrieval/README.md` 的「Evaluating retrieval」（PR #68）。
- **補做時 Claude 會做的事：** 完成 #41 的「本機以真實端點處理一份 PDF，並在 Aspire 儀表板看到嵌入呼叫」，以及 #50 的 OpenAI 評測，並依結果決定 `Retrieval:MinScore` 的預設值（這個調整需另開 PR）。

## 4. 本機評測用的多語嵌入模型

- **用途：** 依計畫第 7 節決定 3，和 OpenAI 的結果比較繁體中文檢索效果，作為之後全地端部署時選擇模型的依據。
- **需要同意的事項：**
  - 下載一個非中國團隊、寬鬆授權的多語嵌入模型（預定 Microsoft 的 `multilingual-e5-large`，MIT，約 2 GB）。
  - 以 Docker 啟動一個 OpenAI 相容的嵌入服務（選項為 Hugging Face 的 text-embeddings-inference 或 vLLM，實際採用時再查授權與版本）。
  - 執行期間約佔用 3–4 GB 記憶體。啟動前 Claude 會先檢查 swap 用量；記憶體不足時會停止並回報，不會硬起服務。
- **做法：** 在對話中回覆同意即可。啟動與評測指令見 `apps/api/eval/retrieval/README.md`（PR #68）；FAQ 一則可能長達約 4500 字，超過 e5 的 512 token 上限，評測時要一併檢查（PR #67）。評測結束後，Claude 會停掉服務並說明模型快取的位置，由你決定是否刪除。

## 5. GitGuardian 誤報（M1 遺留）

- **事件：** 「Line Messaging OAuth2 Keys」，2026-09-24 推送時觸發。
- **為什麼是誤報：** 那是前端 Demo 用的假憑證，M1 期間已查過所有歷史紀錄確認；原始碼現在改成組合字串，不會再比對到。
- **做法：** 登入 GitGuardian 儀表板 → 找到該事件 → Resolve → 選擇 false positive（或 test credential），備註可寫「Demo 用的假憑證，非真實金鑰」。

---

## M2 收尾時的補做清單

依上面各節處理完後，逐項勾選：

- [ ] PR #34 已合併
- [ ] 真實樣本：解析檢查完成，問題已開票，結果記錄在 `docs/evals/`
- [ ] #41：以 OpenAI 端點完成本機手動驗收
- [ ] #50：OpenAI 評測完成，`Retrieval:MinScore` 預設值已依結果調整
- [ ] #50：本機多語模型評測完成，並與 OpenAI 的結果比較
- [ ] GitGuardian 事件已關閉
