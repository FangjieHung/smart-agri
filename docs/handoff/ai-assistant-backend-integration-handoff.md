# AI 助理 Demo：正式後端與外部服務介接 hand-off（總表）

**對象**：要把這個前端 Demo 接上真實後端、真實 AI 與真實外部管道的團隊。
**前端版本**：`apps/admin`，分支 `master`。
**核心警告**：這個 Demo **沒有任何真實安全邊界**。模擬登入、模擬權限、模擬憑證保存都只是為了讓畫面長出正確的樣子；任何一項都不可以當成正式實作的依據。

---

## 0. 這套文件怎麼讀

| 文件 | 回答什麼 | 什麼時候讀 |
| --- | --- | --- |
| **本文件** | 全域驗收清單、外部服務接點、前端已經假設的非功能需求 | 規劃階段、驗收階段 |
| `docs/handoff/tasks-6-10-backend-handoff.md` | 每個功能區的**請求／回應型別、列舉值、狀態機、驗證規則、權限規則、哪些是假的** | 實作每個功能區時 |
| `docs/handoff/mock-to-api-mapping.md` | **全部 57 個 repository 方法**的 endpoint、授權、成功狀態、可恢復／不可恢復錯誤、前端替換檔案、同步→非同步的具體影響 | 設計 API 與排替換工時時 |
| `docs/handoff/route-screen-matrix.md` | 每條路由 × 元件 × 守衛 × repository 方法 × 可能狀態 × e2e spec | 排測試與確認畫面涵蓋範圍時 |
| `docs/plans/2026-09-18-sme-ai-assistant-ux-demo-design.md` | 產品與 UX 決策的權威來源 | 有爭議時 |

**不要把 fixture 的形狀當成已定案 API。** 型別是前端目前依賴的最小集合，欄位命名與 id 格式都可以談；能不能改，逐項寫在 `tasks-6-10-backend-handoff.md` 第 8 節。

---

## 1. 驗收清單

這是 hand-off 的**完成定義**。每一項都要能指到一份文件的具體章節，或一段可執行的驗證。

| # | 項目 | 涵蓋位置 | 完成判準 |
| --- | --- | --- | --- |
| 1 | **路由** | `route-screen-matrix.md` 第 3 節 | `app.routes.ts` 的 23 筆設定全部出現在對照表，含 `/use/:assistantId`、`?embed=1`、三條 `/app/chat[...]`、5 筆 redirect 與 wildcard |
| 2 | **元件狀態** | `route-screen-matrix.md` 第 2、3 節 | 九種狀態（空白／載入中／成功／部分成功／無結果／權限不足／處理失敗／連線失敗／登入逾時）在每條路由上都有明確的「會或不會出現」 |
| 3 | **domain 關係** | 本文件第 2 節；型別在 `tasks-6-10-backend-handoff.md` 第 2.2、3.2、4.2、5.2、6.3 節 | 帳號／助理／知識庫／資料庫／對話／提交／管道七個實體的關係與基數都已寫明 |
| 4 | **權限** | `tasks-6-10-backend-handoff.md` 第 1.6、2.5、3.4、4.5、5.4、6.5 節；`mock-to-api-mapping.md` 第 1.1 節 | 每個方法都標了授權層級；11 種 `permission-denied` reason 都有出現時機與文案；「不存在＝無權限」的硬規則有明文 |
| 5 | **分頁** | 本文件第 5.1 節 | 前端目前假設的排序已寫明；後端要提供的分頁協定被列為必須決定項 |
| 6 | **上傳處理** | `mock-to-api-mapping.md` 第 3.2 節；`tasks-6-10-backend-handoff.md` 第 3 節與第 8 節第 4 點 | 已明說「Demo 完全沒有上傳」，以及契約必須怎麼改 |
| 7 | **回答與引用** | `tasks-6-10-backend-handoff.md` 第 5.2、5.3 節 | 五種 `ChatReplyView.kind` 與 `ChatCitationView` 的欄位都已列出 |
| 8 | **拒答** | `tasks-6-10-backend-handoff.md` 第 5.3 節；本文件第 3.3 節 | `no-result` 的語意、`nextSteps` 的來源、判定門檻是誰的責任 |
| 9 | **一般知識標記** | `tasks-6-10-backend-handoff.md` 第 5.3 節；本文件第 3.3 節 | `general-knowledge` 與 `notice` 的呈現義務、與 `company-data-only` 設定的關係 |
| 10 | **表單同意** | `tasks-6-10-backend-handoff.md` 第 5.5 節與第 8 節第 6 點 | 同意內容的四個必揭露欄位、驗證順序、撤回缺口 |
| 11 | **追蹤比較** | `tasks-6-10-backend-handoff.md` 第 4.6 節與第 8 節第 12、13 點 | 差異值與文案由伺服端算好、subject 與帳號是不同概念 |
| 12 | **登入** | 本文件第 4.1 節；`tasks-6-10-backend-handoff.md` 第 8 節第 1、2 點 | sessionStorage persona + 30 分鐘逾時的現況、以及正式登入要取代哪些東西 |
| 13 | **LINE** | 本文件第 3.1 節；`tasks-6-10-backend-handoff.md` 第 6 節 | Messaging API 的接點、憑證處理缺口、測試與啟用的順序約束 |
| 14 | **網站嵌入** | 本文件第 3.2 節；`route-screen-matrix.md` 第 5.2 節 | 嵌入腳本託管、允許網域的執行點、`?embed=1` 沒有安全意義 |
| 15 | **錯誤訊息** | 本文件第 5.3 節；`mock-to-api-mapping.md` 第 1.2 節 | 可恢復與不可恢復的分界、錯誤 body 的形狀、不得洩漏資源名稱 |
| 16 | **mock replacement** | `mock-to-api-mapping.md` 第 4 節 | 單一 DI 注入點、15 個注入檔、45 個呼叫點、同步→非同步的六項具體影響 |

### 1.1 可執行的自查

1. **文件中不得有未解決標記**——用 `rg` 在 `docs/handoff` 搜尋三個常見的待辦標記（`TO`+`DO`、`TB`+`D`、`FIX`+`ME`）必須無輸出。這裡刻意不把完整字串寫進文件，否則自查指令會掃到自己。

2. **每個 repository 方法都要能在 mapping 找到**：

   ```bash
   grep -nE "^  [a-zA-Z]+(\(|<)" apps/admin/src/app/core/repositories/demo-repository.ts
   ```

   輸出 57 個方法名稱，每一個都必須能在 `mock-to-api-mapping.md` 中搜尋到。

3. **每條路由都要能在 matrix 找到**：

   ```bash
   grep -n "path:" apps/admin/src/app/app.routes.ts
   ```

   輸出 23 筆路徑，每一筆都必須能在 `route-screen-matrix.md` 第 3 節中搜尋到。

---

## 2. Domain 關係總覽

```
Account ──擁有──> Assistant ──連接(0..n)──> KnowledgeBase
   │                  │      ──連接(0..n)──> Database
   │                  │
   │                  ├──固定 3 個──> PublishingChannel (platform | website | line)
   │                  │
   │                  └──1..n──> ChatThread ──屬於──> Account (發起者，非助理擁有者)
   │                                  │
   │                                  └──同意後產生──> StructuredSubmission ──寫入──> Database
   │
   ├──擁有──> KnowledgeBase ──分享──> Account[]
   └──擁有──> Database ──指定──> dataManager: Account
```

七條**不可違反**的關係約束（詳細規則見 `tasks-6-10-backend-handoff.md` 對應章節）：

1. **助理的資料來源是混合的**：`AssistantSourceReference` 是 `knowledge-base | database` 的 union（`core/domain/assistant.model.ts:25-33`），不是兩個分開的清單。
2. **每個助理固定三個發布管道**，不能新增或刪除（契約註解 `core/repositories/demo-repository.ts:251`、`:255`）。
3. **對話只屬於發起的帳號**，助理擁有者也看不到（契約 `demo-repository.ts:437`）。擁有者只能看不含對話文字的匿名統計（`getAssistantAnalytics`）。
4. **結構化紀錄只有指定資料管理者看得到**，而且只包含 `consentStatus === 'consented'` 的紀錄（契約 `demo-repository.ts:399-401`）。
5. **被追蹤對象（subject）不等於登入帳號**。Demo 把它硬對映成 `subject-<accountId>`，但設計文件明確說這是兩個概念（`docs/plans/2026-09-18-sme-ai-assistant-ux-demo-design.md:183`）。
6. **知識庫與資料庫各自有獨立的擁有者與分享範圍**，不繼承助理的可見度。助理被分享出去，不代表它連的知識庫被分享出去。
7. **刪除一段對話不會刪掉它產生的結構化紀錄**（現況，見 `tasks-6-10-backend-handoff.md` 第 8 節第 7 點）。這是刻意還是疏漏，需要後端與法遵確認。

`AssistantStatus` 有四種（`assistant.model.ts:14`）但 Demo 只有「建立後固定為 ready」這一條轉移，**沒有任何方法能改狀態**。完整狀態機是後端要補的（第 8 節第 17 點）。

---

## 3. 外部服務接點

Demo **沒有任何一次真的連出去**。三個外部接點都是本地模擬，但畫面已經依照「會有外部服務」的形狀設計好了。

### 3.1 LINE Messaging API

| 接點 | Demo 現況 | 正式版要做的事 |
| --- | --- | --- |
| 憑證保存 | `channelSecret` / `accessToken` **明文存 localStorage，而且原文回傳給前端**（`core/repositories/publishing-channels.ts:297-301`） | 伺服端加密保存；回傳時只給末四碼與「是否已設定」。**這會改動 `LineSetupView` 契約**，`features/publishing/line-setup/line-setup.component.ts` 的遮罩顯示要跟著調整 |
| Webhook | URL 指向 `.invalid` 保留網域（`publishing-channels.ts:303`） | 真實 webhook endpoint、**簽章驗證**、重送與冪等處理 |
| 測試訊息 | `sendLineTestMessage` 是本地模擬，不會連 LINE（契約 `demo-repository.ts:290`） | 真的推一則訊息。**LINE 端失敗必須回 `200` + `lastTest` 失敗紀錄，不是 `5xx`**，否則畫面會把「測過了，失敗」顯示成「檢查失敗」 |
| 啟用順序 | 必須先測試成功才能啟用，否則回 `validation-failed`（契約 `demo-repository.ts:295`） | 保留這個約束；`saveLineSettings` 會重置測試與啟用狀態（第 6.4 節），這個重置也要保留 |
| 訪客身分 | 每個瀏覽器分頁一個匿名 `visitor-<亂數>`（`core/session/anonymous-visitor.service.ts:50-96`），**與 LINE 的使用者完全無關** | 正式版要把 LINE 的 `userId` 對應成訪客身分，並決定它與官網訪客是不是同一種主體。見第 4.2 節第 5 點 |

驗證規則（逐欄檢查、哪些欄位必填、錯誤摘要怎麼對焦）寫在 `tasks-6-10-backend-handoff.md` 第 6.4 節，`accessibility.cy.ts` 有對應的鍵盤流測試。

### 3.2 官網嵌入

| 接點 | Demo 現況 | 正式版要做的事 |
| --- | --- | --- |
| 嵌入腳本 | 嵌入碼字串由 `demoEmbedCode()` 產生（`publishing-channels.ts:291`），指向 `.invalid` 網域（`:117-127`） | 真實 widget 的**託管、版本管理與快取策略**；嵌入碼要能安全地嵌在客戶站上 |
| 允許網域 | 只是一個字串清單，沒有任何執行力 | **後端執行**：CORS 白名單 + CSP `frame-ancestors`。前端的 `?embed=1` 只是視覺開關（`features/assistant-use/chat-shell/chat-shell-page.component.ts:42`），**不做 origin 檢查、不驗 referrer、不限制 iframe 來源** |
| 安裝檢查 | `checkWebsiteInstallation` 本地模擬；`?demoScenario=disconnected-channel` 可強制成 `not-detected`（`core/repositories/mock-demo-repository.ts:818`） | 真的去偵測。**外部網站無回應也要回 `200` + `installCheck: 'not-detected'`**，理由同 LINE 測試 |
| 網域變更 | 變更允許網域會**重置安裝檢查**（契約 `demo-repository.ts:273`） | 保留這個行為，畫面依賴它 |
| 訪客 session | 已可用。`/use/:assistantId` 改掛 `embeddedChatGuard`（`app.routes.ts:126`），未登入訪客直接開得了；身分是分頁內的匿名 `VisitorId`，且只有**官網嵌入或 LINE 已發布**的助理開得起來（`publishing-channels.ts:230-245`） | 把 Demo 的 sessionStorage 換成真正的匿名 session，並在後端強制「未發布的助理不得被匿名開啟」。見第 4.2 節第 5 點 |

### 3.3 真實 LLM 與檢索

目前是關鍵字比對 fixture（`mock-demo-repository.ts:1886-1907`），引用是寫死的文件名與摘錄（`core/repositories/demo-seed-chat.ts`）。

**前端已經依照五種回答種類設計**（`core/domain/conversation.model.ts:123-147`）：

| kind | 畫面義務 |
| --- | --- |
| `company-data` | **必須**附引用（`citations`），引用抽屜可開啟並有 focus trap |
| `general-knowledge` | **必須**顯示 `notice`，明確標示這段不是公司資料 |
| `no-result` | **必須**顯示 `nextSteps`——拒答不能是死路 |
| `form-request` | 帶出對話中表單與同意內容 |
| `submission-receipt` | 帶出收據與接收方 |

這代表後端必須把「這一段是公司資料／這一段是一般知識」**在回應中分區標示**，不能只回一整段文字讓前端猜。同一次回答要不要能混合兩種，目前契約**不支援**（union 是互斥的），這是要確認的設計決定。

其餘必須決定的：模型與供應商、檢索切塊策略、引用如何定位到文件位置、串流協定、逾時與重試、成本與速率限制、`no-result` 的判定門檻、助理的 `company-data-only` 設定如何在檢索層強制執行。完整清單見 `tasks-6-10-backend-handoff.md` 第 8 節第 5 點。

---

## 4. 登入、session 與身分

### 4.1 現況（2026-09 已更新）

Demo 的身分**不再只是記憶體 signal**。目前實作（`core/session/demo-session.service.ts`）：

| 面向 | 現況 | 位置 |
| --- | --- | --- |
| 保存位置 | **`sessionStorage`**，key 為 `demo-session` | `:26`、`:71` |
| 保存內容 | `{ accountId, lastActiveAt }`，**沒有任何憑證** | `:43-46`、`:167-170` |
| 重新整理 | **會保留** | `:97`（建構時 `restore()`） |
| 關閉分頁 | 結束（sessionStorage 語意）；兩個分頁可同時示範不同帳號 | `:25` 註解 |
| 閒置逾時 | **30 分鐘**，逾時後回到 `/login` 並顯示說明 | `:29`、`:31-34` |
| 逾時判定時機 | 每次進入工作區路由時（guard 呼叫 `refreshActivity()`） | `:118-130`、`core/session/demo-session.guard.ts:9-12` |
| 降級處理 | 無痕模式或 sessionStorage 被封鎖時退回記憶體儲存 | `:55-63` |

另有一個**完全沒被使用的舊 `auth.service.ts`**（`core/auth/auth.service.ts:4` 的 `sa.auth.session` key）。正式化時應一併移除，避免誤導。

### 4.2 正式登入要取代什麼

1. **`viewerAccountId` 參數全部拿掉**。目前每個方法的第一個參數都是它（例 `demo-repository.ts:214-216`），正式 API 必須從 session 推導。這會改動**每一個 endpoint 的簽章**，是替換工作的固定成本。
2. **`AccountId` 是字面值 union**（`core/domain/account.model.ts:1-4`），只有三個值。後端無法回傳任何新帳號 id，必須先放寬成 `string`。
3. **逾時策略要重新定義**。30 分鐘閒置是 Demo 的數字，不是需求。設計文件要求「登入逾時前保留非敏感草稿；敏感內容依安全規則清除」（`docs/plans/2026-09-18-sme-ai-assistant-ux-demo-design.md:247`），但**沒有定義「敏感」的界線**——這是後端與法遵要先畫清楚的。
4. **登出要清掉哪些本機資料**。目前 `clearSession()` 只移除 sessionStorage 的 `demo-session`（`:132-135`、`:156-160`），**`localStorage` 中的草稿、對話與紀錄完全不動**。
5. **未登入訪客的路徑已經打通（2026-09 更新），但形式是 Demo 級的**。`/use/:assistantId` 改掛 `embeddedChatGuard`（`app.routes.ts:126`、`core/session/embedded-chat.guard.ts:14-19`），這條路由**永遠不轉址**：沒有 Demo 身分的人就是未登入的官網訪客。目前的答案是：

   | 原本的問題 | Demo 的答案 | 位置 |
   | --- | --- | --- |
   | 匿名 session 的形式 | 每個瀏覽器分頁一個 `visitor-<亂數>`，存在 sessionStorage 的 `demo-visitor`，關閉分頁就結束；沒有憑證、沒有權限、不對應任何帳號 | `core/session/anonymous-visitor.service.ts:10`、`:50-96` |
   | 誰開得了 | 只有**官網嵌入或 LINE 已發布**的助理（平台內分享不算對外）；其餘與「助理不存在」回同一則不含名稱的訊息 | `core/repositories/publishing-channels.ts:230-245`、`mock-demo-repository.ts:1637-1651`、`:1670-1675` |
   | 匿名對話的歸屬 | 屬於那個 `VisitorId`，存在 `sme-demo:chat:<visitorId>:<assistantId>`，寫進**訪客專屬的 sessionStorage**；帳號、其他訪客與助理擁有者都讀不到，也不計入匿名統計 | `mock-demo-repository.ts:1690-1697`、`:1806-1809`、`tokens.ts:15-17` |
   | 能不能提交表單 | 可以。同意畫面照樣顯示接收單位／目的／可查看者／敏感資料提示；紀錄的追蹤對象是 `subject-<visitorId>`，顯示成「未登入訪客（末四碼）」，只有指定資料管理者看得到 | `mock-demo-repository.ts:1593`、`:433-439`、`demo-seed-chat.ts:131` |

   **正式版仍要自己決定的**：匿名 session 的真實形式（簽章 cookie／一次性 token）與保存期限、匿名對話要不要落伺服端與保存多久、匿名同意紀錄的法遵主體是誰（目前只有一個不可追溯的隨機 id）以及撤回入口怎麼對應到它、LINE 使用者與官網訪客是不是同一種主體。`isExternallyPublished()` 是前端的一道門，**不是授權**——後端必須自己擋。

---

## 5. 前端已經假設的非功能需求

這一節是**前端已經寫死、後端不照做畫面就會壞**的期待。它們不在 `tasks-6-10-backend-handoff.md` 的功能章節裡。

### 5.1 分頁與排序

**目前沒有任何分頁。** 所有 list 類方法一次回全部，簽章裡沒有 `limit` / `cursor` / `offset`。前端也沒有任何捲動載入或分頁列 UI。

前端**已經依賴**的排序：

| 資料 | 排序 | 位置 |
| --- | --- | --- |
| 對話清單 | 最後活動時間**由新到舊**，同毫秒時用序號 tiebreak | `mock-demo-repository.ts:447-450` |
| 追蹤紀錄 | `recordedAt` **由舊到新**（時間軸與差異計算依賴這個順序） | `mock-demo-repository.ts:2027` |
| 其他清單 | 無明確排序，等於 seed 的順序 | — |

後端要決定的：

- **分頁協定**（建議 cursor，對話與紀錄都會無上限成長）。
- 分頁後**排序必須由伺服端保證穩定**，否則翻頁會重複或漏掉。
- 追蹤紀錄若分頁，**差異計算不能只用當頁資料**——本次／上次／首次的比較是跨全部紀錄的。
- 對話側欄一次載入全部，也沒有搜尋、釘選或封存（第 8 節第 19 點）。

### 5.2 樂觀 vs 悲觀更新、延遲與載入狀態

**目前全部是悲觀更新，而且因為契約同步所以零延遲。**

| 前端現況 | 後端接上後的影響 |
| --- | --- |
| 寫入方法多半回傳**完整的新 view**（`sendChatMessage` 回整份對話、`deleteChatThread` 回剩下的清單） | 可以直接拿回應當新狀態，**免掉一次重讀**。建議保留這個設計 |
| 五個元件用 `revision` signal 遞增來刷新（清單見 `mock-to-api-mapping.md` 第 4.3 節 (3)） | 每次遞增會變成一次網路請求；要決定重讀期間顯示 `loading`（會閃爍）還是保留舊資料（需要新的「更新中」視覺，**目前沒有**） |
| **送出按鈕沒有 in-flight disabled 狀態** | 同步時不可能連點兩次，非同步時可以。每個寫入點都要補進行中旗標，或後端自行冪等 |
| `RepositoryView` 已經有 `loading` 成員（`demo-repository.ts:96-98`），所有模板都有對應分支 | **`loading` 從情境切換器換成請求生命週期時，模板不用動**。這是替換時少數不痛的地方 |
| 沒有任何 rollback 路徑 | 若要改樂觀更新，每個寫入都要補失敗還原 |

**延遲期待**：對話送出（`sendChatMessage`）是唯一會明顯等待的操作。目前沒有串流、沒有打字指示器、沒有逾時文案。要不要串流，會決定 `sendChatMessage` 是回整份對話還是改成 SSE，**這是契約層級的決定，不是實作細節**。

### 5.3 錯誤形狀與文案歸屬

| 約定 | 內容 |
| --- | --- |
| 成功 | `200` / `201`，body 即 `data` |
| 部分降級 | `200`，body 加 `unavailable[]` 與 `message`，**主資料仍必須回傳** |
| 驗證失敗 | `422`，body `{ errors?, message }`。**各區的 `errors` 形狀不同**（有些只有 `message`），見 `demo-repository.ts:119-198` |
| 無權限或不存在 | `403`，body `{ reason, message }`，**兩者內容完全相同**，訊息不得含資源名稱 |
| Session 逾時 | `401`，前端導回 `/login` |
| 外部服務失敗 | **不是 HTTP 錯誤**——回 `200` + 結果欄位（LINE `lastTest`、官網 `installCheck`） |

**文案歸屬**：目前所有使用者看得到的中文字串都在**後端會回傳的欄位裡**（`message`、`notice`、`nextSteps`、趨勢摘要文字）。前端只負責顯示，沒有 i18n 層。後端要決定這是不是長期作法；若要前端做在地化，`tasks-6-10-backend-handoff.md` 各節列出的列舉值就必須成為穩定的機器可讀 key，而不是只靠文案。

`RepositoryUnavailableResource` 目前只有 `'knowledge-sync'` 一個值（`demo-repository.ts:76`）。哪些下游資源失敗要降級成 `partial-failure`、要擴充哪些值，由後端決定（第 8 節第 18 點）。

### 5.4 併發與冪等

所有寫入都是**整份覆寫，沒有版本欄位也沒有樂觀鎖**。`saveAssistantDraft` 在多分頁編輯時會靜默互相覆蓋。後端要決定 ETag / version 欄位與 `409` 的回應形狀；若加了，前端的寫入分支要補 `409` 處理（目前一處都沒有）。

### 5.5 資料保存與刪除

目前沒有任何 TTL。localStorage 永久保留，`deleteChatThread` 是**硬刪除**，沒有垃圾桶，也不連帶刪除已產生的結構化紀錄。`SubmissionConsentStatus` 已有 `withdrawn` 這個值，但**沒有任何方法能撤回**——而收據文字已經對使用者承諾「可隨時申請撤回或刪除」（`mock-demo-repository.ts:1610`）。**這是目前最明確的法遵缺口**，詳見第 8 節第 6、7 點。

---

## 6. 替換順序建議

1. **先定 session 與 viewer**（第 4 節）。它改動每一個簽章，越晚做代價越大。
2. **同時放寬 id 型別**（`AccountId` 等字面值 union → `string`）。
3. **把契約改成非同步**，一次改完 15 個注入點。詳細影響見 `mock-to-api-mapping.md` 第 4.3 節。這一步做完，`MockDemoRepository` 包一層 `of()` 仍然可用，**e2e 應該保持全綠**——這是驗證這一步沒做壞的方法。
4. **逐功能區換成真 endpoint**，順序建議：知識庫 → 資料庫 → 發布管道 → 對話（對話依賴前三者，而且牽涉 LLM）。
5. **最後拆掉 Demo 專用物**：`?demoScenario=`、`DemoScenarioController`、`advanceKnowledgeDocument`、`/login` 的三個寫死 persona、`core/auth/auth.service.ts`。

每一步做完都跑：

```bash
npx nx test admin
npx nx e2e admin-e2e
npx nx build admin
```

---

## 7. 交付時必須一起說清楚的事

這個 Demo 會被拿去給非技術的決策者看。交付時必須明說：

- 模擬登入、模擬權限與模擬憑證保存**不是安全功能**（`DEMO_SECURITY_NOTICE`，`core/repositories/demo-repository.ts:478-479`）。
- 回答不是真的 AI，是固定 fixture 的關鍵字比對。
- 三個發布管道都沒有真的連出去，嵌入碼與 webhook 指向保留網域。
- **不可以在 Demo 中輸入任何真實敏感資料**——所有輸入都會以明文留在瀏覽器的 localStorage 裡。
