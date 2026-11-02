# 店務統籌助理｜設計與拆票

**日期：** 2026-11-02
**狀態：** 已確認（2026-11-02）。設計問答 Q1–Q26 由負責人逐輪回覆「照建議」定案；Q2 補充點餐系統是 Xurf 與 foodpanda；Q26 補充「先不接雲悅 API」。可以拆票，issue 標題前綴用「店務助理｜」。
**依據：** 2026-11-02 設計問答（本文件第 3 節逐條記錄）；smart-agri `master`（cfdc7b7）現況盤點；Google、Meta、LINE、foodpanda、Xurf 公開文件（2026-11-02 查證，見第 2.2 節）。
**拆票方式：** 與 M5a／M5b 相同。每個 Slice 都是可單獨合併的垂直切片，附測試與驗收條件；「依賴」只列硬依賴；會改 migration 或 `openapi/v1.json` 的 Slice 用接續分支依序做。

---

## 1. 目標與非目標

**情境：** 店家老闆在 LINE 對「統籌助理」說「今天臨時店休」，助理回一張預覽卡，列出這家店設定好的所有動作；老闆按一次「確認」，助理逐項執行並回報結果：

> 老闆：「今天臨時店休」
> 助理：〔預覽卡〕王記麵店｜11/2（日）臨時店休
> ・FB 粉專貼文（自動）・LINE 群發 160 則，本月剩 200 則（自動）・Google 特殊營業時間（自動）
> ・Xurf 關閉接單（提醒）・foodpanda 暫停接單（提醒）
> ・已接的訂單仍需處理　〔確認〕〔取消〕
> 老闆按〔確認〕→ 助理：✅ FB 已發布　✅ LINE 已群發　✅ Google 已標示休息　⏳ Xurf：請到 iPad 側邊欄關閉接單〔已完成〕

**目標（第一版交付）：**

1. 多租戶：一家店是 smart-agri 的一個組織。
2. 平台共用一個「統籌助理」LINE 官方帳號；老闆、店長用綁定碼綁到自己的店。
3. 固定四種情境：臨時店休、恢復營業、提早打烊、延後開店。
4. 先預覽、確認後才執行，逐項回報，部分失敗不回滾。
5. 每家店自訂每個情境要做哪些動作；每個動作是「自動」或「提醒」。
6. 自動動作：LINE 群發（先查額度）、Google 特殊營業時間、FB 粉專圖文貼文，各自可以還原。
7. 點餐系統（Xurf、foodpanda）只做提醒；依每週營業時間在開店前提醒關閉、在下一個營業日開店前提醒重新開啟。

**非目標：** 見第 6 節。

---

## 2. 現況事實

### 2.1 smart-agri（2026-11-02，`master` cfdc7b7）

| 事實 | 出處 |
| --- | --- |
| LINE webhook 以助理為單位：`POST /api/v1/line/webhook/{assistantId}`，驗簽章、檢查 `destination`，事件交給背景的 `LineWebhookProcessor` | `apps/api/src/SmartAgri.Api/Line/LineWebhookEndpoints.cs:54`、`apps/api/src/SmartAgri.Api/Line/LineWebhookProcessor.cs:31` |
| `ILineMessagingClient` 有 reply、push（含 `X-Line-Retry-Key`）、輸入中動畫、bot info、設定 webhook；**沒有** broadcast、narrowcast、額度查詢 | `apps/api/src/SmartAgri.Application/Line/ILineMessagingClient.cs:19-42` |
| LINE 頻道設定一個助理一筆（`AssistantLineChannel`），憑證用 `ProtectedSecret` 加密 | `apps/api/src/SmartAgri.Domain/Assistants/AssistantLineChannel.cs:42-48` |
| LINE 使用者的訊息目前一律走知識庫問答 | `apps/api/src/SmartAgri.Api/Line/LineQuestionHandler.cs:159` |
| 模型層是 Microsoft.Extensions.AI `IChatClient`；工具以 `AIFunctionFactory.CreateDeclaration` 宣告、由伺服器自己解析 `FunctionCallContent`，沒有工具會呼叫外部 API | `apps/api/src/SmartAgri.Application/Databases/DatabaseQueryTools.cs:207`、`apps/api/src/SmartAgri.Application/Chat/ProposalSelectionRules.cs:123` |
| 新增 `ModelInvocationPurpose` 必須歸類為計入用量或嵌入，由測試強制 | `apps/api/src/SmartAgri.Domain/Ai/ModelInvocationPurpose.cs:8` |
| 背景工作佇列在 PostgreSQL，支援 `RunAfter` 延後執行與重試；沒有 cron | `apps/api/src/SmartAgri.Domain/Jobs/BackgroundJob.cs:69`、`docs/adr/2026-09-25-background-jobs-on-postgresql.md` |
| 機敏資料以 `ProtectedSecret`／`ISecretProtector` 加密保存，API 永遠不回傳原文 | `apps/api/src/SmartAgri.Domain/Secrets/ProtectedSecret.cs:22`、`apps/api/src/SmartAgri.Application/Secrets/ISecretProtector.cs:15` |
| 允許 `IgnoreQueryFilters` 的檔案由守門測試寫死（目前三處查詢＋工作執行） | `apps/api/tests/SmartAgri.Api.Tests/Tenancy/IgnoreQueryFiltersSourceTests.cs:34-71` |
| `OrganizationActivity` 只記「組織設定的變更」，`Detail` 不放任何人寫的內容 | `apps/api/src/SmartAgri.Domain/Organizations/OrganizationActivity.cs:5-19` |
| 平台營運者的操作目前都是 CLI 指令（`migrate`、`list-organizations` 等），沒有跨組織的營運後台 | `apps/api/src/SmartAgri.Api/Program.cs:314-317` |
| LINE 使用者沒有對應到內部帳號，只讀 `Source.UserId` 拿來推播 | `apps/api/src/SmartAgri.Api/Line/LineQuestionHandler.cs:221` |
| 沒有任何 Meta、Google 商家、POS 的串接 | grep 無結果 |

### 2.2 外部服務（2026-11-02 查證）

| 事實 | 影響 |
| --- | --- |
| Google 商家：Business Information API v1 的 `specialHours` 可把某一天設成 `closed: true`，前提是已設一般營業時間；單日休息應該用特殊營業時間，「暫停營業」是給一週以上用的（[locations](https://developers.google.com/my-business/reference/businessinformation/rest/v1/accounts.locations)） | Google 動作用 `specialHours`，讀出來、合併、寫回，不能蓋掉店家其他的特殊營業時間 |
| Google 商家 API 要申請「Basic API Access」；申請者的商家檔案要已驗證、建立超過 60 天、有網站；配額 0 QPM＝未通過、300 QPM＝通過（[prereqs](https://developers.google.com/my-business/content/prereqs)）。OAuth scope `business.manage` 是否需要另外通過 OAuth 驗證**未確認** | 審核通過前，Google 動作只能是提醒 |
| Google 商家貼文 API（v4 `localPosts`）仍在文件上，但未來是否保留未確認 | 第一版不做 Google 貼文 |
| Meta：粉專發文要 `pages_manage_posts` 等權限；替客戶的粉專發文要過 App Review 的進階權限，而進階權限需要商家驗證（[posts](https://developers.facebook.com/docs/pages-api/posts)、[business verification](https://developers.facebook.com/blog/post/2023/02/01/developer-platform-requiring-business-verification-for-advanced-access/)）。`POST /{page_id}/photos`（`url`＋`message`）即可發圖文。由長效使用者 token 換得的粉專 token 不會過期，但改密碼或撤銷授權就失效 | 審核通過前 FB 動作只能是提醒；token 失效時改回提醒並請老闆重新授權 |
| LINE：broadcast、narrowcast、push、multicast 都依收到的人數扣月額度，reply 免費。台灣方案：輕用量 0 元／200 則、中用量 800 元／3,000 則、高用量 1,200 元／6,000 則（第三方資料稱 2026-11-01 起中、高用量漲為 1,000／1,400 元，未經官方確認）；額度不足時 API 回 `429`、不送出。LINE VOOM 沒有公開的發文 API | 群發前先查額度並在預覽卡顯示；不做 VOOM |
| foodpanda：Delivery Hero POS 整合有「Update Vendor Availability」，但只開放給簽約的 POS 業者；商家可在 GO App 暫停接單，有時限的暫停會自動恢復 | foodpanda 只做提醒 |
| Xurf（雲悅科技 iPad POS）：官網連到的公開 API 規格是 404；iPad 側邊欄可「一鍵開關各通路接單」，是否包含 foodpanda 未知。負責人決定（2026-11-02）**先不接雲悅 API** | Xurf 只做提醒；請業主實測 Xurf 關閉接單是否連帶關閉 foodpanda（Q16），決定提醒清單列一項還是兩項 |

---

## 3. 決定（2026-11-02 設計問答）

| # | 決定 |
| --- | --- |
| Q1 | 多租戶。第二家店想要「盤點進貨」，另開一輪設計（見第 6 節）。 |
| Q2 | 點餐系統是 Xurf 與 foodpanda。 |
| Q3 | 第一版做 FB 粉專圖文貼文、LINE 群發、Google 特殊營業時間；不做 shorts／Reels、不做 LINE VOOM。 |
| Q4 | 先回預覽卡，按一次「確認」才執行。 |
| Q5／Q19 | 只有綁定過、角色是「老闆」或「店長」的 LINE 使用者能下指令。一個 LINE 使用者可以綁多家店；綁了兩家以上時先問「哪一家？」（含「全部」），預覽卡一定寫店名。只有老闆能產生店長的綁定碼。 |
| Q6 | 固定四種情境：臨時店休、恢復營業、提早打烊、延後開店。模型只負責判斷情境、日期、時段、原因，每個情境要做的動作事先設定。 |
| Q7 | 依每週營業時間，在下一個營業日開店前恢復點餐並通知老闆（點餐系統是提醒項目時，就是開店前提醒重新開啟）。 |
| Q8 | 逐項回報結果；失敗的項目附「重試」或手動步驟；不因單項失敗回滾其他動作。 |
| Q9 | 做在 smart-agri 上，一家店＝一個組織。 |
| Q10 | 平台共用一個「統籌助理」官方帳號；各店自己的官方帳號只用來群發。 |
| Q11 | 盤點進貨是同平台、按店開關的另一種能力，店休做完後另開一輪設計。 |
| Q12 | 前幾家店由平台營運者設定；FB、Google 由店家老闆點授權連結完成。 |
| Q13 | 公告用每家店的樣板（文字＋一張固定圖片）帶入日期、恢復時間、選填原因；老闆可以回訊息修改文字。 |
| Q14 | 每家店的每個情境綁一份動作清單。LINE 群發預設開啟，但預覽卡顯示「將用掉 N 則、本月剩 M 則」，額度不足時標成無法群發。 |
| Q15 | 點餐系統第一版用提醒清單；Q26 補充：先不接雲悅 API；不做瀏覽器自動化。 |
| Q16 | 請業主實測：在 Xurf 關閉接單後，foodpanda 上的店是否跟著顯示休息。 |
| Q17 | 只擋新訂單；預覽卡提醒「已接的訂單仍需處理」。 |
| Q18 | 每個動作標成「自動」或「提醒」；做不到自動的（沒資格、審核未過、沒有 API、授權失效）就顯示手動步驟＋「已完成」按鈕。接上 API 後只改設定。 |
| Q20 | 支援未來日期。公告與 Google 在確認後馬上設定；點餐系統的動作排在當天開店前。日期不明確時追問。 |
| Q21 | 恢復營業時，Google 刪除那天的特殊營業時間、FB 刪除店休貼文；LINE 更正群發是選項、預設不勾；點餐系統提醒重新開啟。 |
| Q22 | 每週營業時間由營運者設定、存在 smart-agri；不以 Google 的資料為準。 |
| Q23 | 預覽卡 30 分鐘內沒確認就失效；重複按「確認」只執行一次。 |
| Q24 | 不是指令的訊息，回覆「我目前可以幫你：臨時店休／恢復營業／提早打烊／延後開店」；統籌帳號不接知識庫問答。圖文選單放這四個按鈕。 |
| Q25 | 每次指令記下誰、何時、哪家店、做了哪些動作、每項結果。 |
| Q26 | 營運者現在就申請 Meta 商家驗證與 Google 商家 API（步驟見附錄 A）；先不聯絡雲悅。 |

### 3.1 拆票時補上的實作決定（可以推翻）

- **A｜營運者設定先用 CLI。** smart-agri 沒有跨組織的營運後台，第一版沿用既有慣例，以 CLI 匯入一份 JSON 店家設定（營業時間、情境、動作清單、樣板、圖片網址）。店家自助或後台介面之後再做。
- **B｜公告圖片用營運者提供的 HTTPS 網址。** FB 的 `url` 與 LINE 的圖片訊息都要公開網址；第一版不做上傳。
- **C｜綁定資料是平台層級的表。** 統籌帳號的 webhook 收到訊息時還不知道是哪一個組織，要用 LINE userId 跨組織查綁定，所以新增一個專用查詢類別並加進 `IgnoreQueryFiltersSourceTests` 的允許清單（與 `PublicAssistantLookup` 同一個模式）。
- **D｜先選店、再呼叫模型。** 綁了多家店的人先用快速回覆選店，再做意圖判斷，模型用量記在選定的店。
- **E｜統籌帳號的憑證放在平台設定**（環境變數／secrets，不是某個組織的資料）；它的推播額度由平台負擔。
- **F｜執行紀錄另開資料表**（每次指令一筆、每個動作一列結果），不塞進 `OrganizationActivity`；店家設定的變更才記 `OrganizationActivity`。第一版用 CLI 查詢執行紀錄。

---

## 4. 名詞

| 名詞 | 意思 |
| --- | --- |
| 統籌助理（coordinator） | 平台共用的 LINE 官方帳號與其背後的處理流程 |
| 成員（coordinator member） | 綁到某家店的 LINE 使用者，角色為老闆或店長 |
| 情境（scenario） | 臨時店休、恢復營業、提早打烊、延後開店 |
| 動作（action） | 情境要做的一件事，例如 LINE 群發、Google 特殊營業時間、FB 貼文、Xurf 關閉接單 |
| 自動／提醒（mode） | 動作由系統執行，或只給老闆手動步驟並等待「已完成」 |
| 指令（command） | 一次「預覽 → 確認 → 執行」的完整紀錄 |

---

## 5. Slice

### Slice 1（#372）｜統籌帳號 webhook 與綁定

- 平台設定統籌帳號的 Channel Secret／Access Token（決定 E）；新端點 `POST /api/v1/coordinator/line/webhook`，沿用現有 LINE 的簽章驗證、2 秒內回 200、背景處理、`webhookEventId` 去重。
- 成員資料表（組織、LINE userId、角色、綁定時間）與綁定碼（一次性、有效期限、指定角色）；跨組織查詢依決定 C。
- CLI：營運者為某組織產生「老闆綁定碼」。LINE：老闆說「產生店長綁定碼」取得店長用的碼。
- 未綁定的人：回「請輸入綁定碼」；輸入正確的碼即綁定並回店名。已綁定但不是指令：回能力選單（Q24）。
- 圖文選單由營運者在 LINE 官方帳號管理後台手動建立（四個按鈕送出對應文字），不寫程式。
- **驗收：** 簽章錯誤拒絕；重送事件不重複處理；綁定碼過期、用過、輸錯都不能綁；店長不能產生綁定碼；一個 LINE 使用者可綁兩家店；守門測試 `IgnoreQueryFiltersSourceTests` 更新且仍通過。
- **依賴：** 無。

### Slice 2（#373）｜店家設定與 CLI 匯入

- 每個組織一份店家設定：店名、時區（預設 Asia/Taipei）、每週營業時間、啟用的情境、每個情境的動作清單（動作種類、自動或提醒、提醒的手動步驟文字）、每個情境的公告樣板與圖片網址。
- CLI：匯入 JSON（先驗證再寫入，錯誤逐條列出）、顯示目前設定。變更寫 `OrganizationActivity`。
- 「下一個營業時段」的計算（跨日、公休日、時區）做成純函式並完整單元測試。
- **驗收：** 不合法的 JSON（未知動作、營業時間重疊、樣板缺少必要變數）被拒絕且不寫入；營業時段計算涵蓋公休日、跨午夜、未來日期。
- **依賴：** 無（與 Slice 1 都有 migration，用接續分支）。

### Slice 3（#374）｜指令理解與預覽卡

- 綁多家店時先選店（決定 D）。新 `ModelInvocationPurpose`（計入用量），以工具宣告讓模型輸出：情境、日期、時段、原因；相對日期以店家時區解析；日期或時段不明確時追問，不猜。
- 依店家設定組出預覽卡（Flex）：店名、日期、每個動作與模式、樣板帶入後的公告文字、「已接的訂單仍需處理」、〔確認〕〔取消〕。LINE 群發的額度那一行先留位置，由 Slice 5 補上。
- 老闆回「改成：……」可以修改公告文字並重出預覽卡。
- 預覽 30 分鐘失效；確認以指令 id 為鍵，重複按只執行一次（Q23）。
- **驗收：** 「今天臨時店休」「明天」「11/15」「下週一」「今天提早四點打烊」「下週休一天」（追問）各有測試（以假模型回應）；過期的卡按確認回「已失效，請重新下指令」；同一張卡連按兩次只建立一筆執行。
- **依賴：** Slice 1、Slice 2。

### Slice 4（#375）｜執行、回報、提醒項目與排程

- 確認後建立執行紀錄（決定 F），每個動作排一個背景工作；未來日期的點餐動作以 `RunAfter` 排在當天開店前；店休的「重新開啟點餐」排在下一個營業時段開店前。
- 提醒動作：在該時間推播手動步驟＋〔已完成〕；按下後記錄完成者與時間。
- 回報：reply token 還有效就用 reply，否則 push（記在統籌帳號的額度）；逐項顯示成功、失敗（原因＋〔重試〕）、等待手動。
- 「恢復營業」情境：取消尚未執行的排程、對已完成的自動動作呼叫各自的還原（由 Slice 5、7、8 提供）、提醒重新開啟點餐。
- CLI：查某組織最近的執行紀錄。
- **驗收：** 一項失敗不影響其他項目；重試只重做失敗那一項；未來日期的提醒在正確時間（用假時鐘）排入；恢復營業會取消未執行的排程；紀錄的 `Detail` 不含公告以外的使用者內容。
- **依賴：** Slice 3。

### Slice 5（#376）｜LINE 群發（自動）

- 每個組織設定自己官方帳號的 Access Token（`ProtectedSecret`，只寫不讀）。
- `ILineMessagingClient` 增加 broadcast、本月額度與已用量查詢、好友數估計（用哪個端點估算「這次會用掉幾則」，實作前查證官方文件）。
- 預覽卡顯示「將用掉 N 則、本月剩 M 則」；不夠時這一項標成無法群發。以 `X-Line-Retry-Key` 防止重送造成重複群發。
- 恢復營業的「更正群發」是選項，預設不勾（Q21）。
- **驗收：** 額度不足時不呼叫 broadcast；`429` 回報為失敗並附原因；重試不重複群發；token 不出現在任何 API 回應或 log。
- **依賴：** Slice 4。

### Slice 6（#377）｜外部授權連結

- CLI 為某組織產生某服務（FB、Google）的授權連結：一次性、有效期限、簽章的 `state`；老闆打開後導向該服務的同意畫面，callback 換得 token 以 `ProtectedSecret` 保存並回「授權完成」頁。
- 授權失效時（token 被撤銷、密碼變更）：該動作自動改為提醒，並推播老闆一條新的授權連結。
- **驗收：** 過期、重複使用、竄改 `state` 的 callback 被拒絕；以假的服務提供者跑完整流程。
- **依賴：** Slice 2。

### Slice 7（#378）｜Google 特殊營業時間（自動）

- 授權後選定營業地點（`accounts.locations.list`）。
- 臨時店休：讀出 `specialHours`、加入當天 `closed: true`、寫回；提早打烊／延後開店：當天改成調整後的時段；恢復營業：移除當天那一筆。不影響店家其他日期的特殊營業時間。
- **驗收：** 以假的 Google API 測試合併與移除不會動到其他日期；地點沒有一般營業時間時回報失敗並附原因。
- **依賴：** Slice 4、Slice 6。外部：Google 商家 API 申請通過（附錄 A.2）；未通過前這一項維持提醒。

### Slice 8（#379）｜FB 粉專圖文貼文（自動）

- 授權後選定粉專，保存粉專 token。
- 發文：`POST /{page_id}/photos`（`url`＋`message`），保存貼文 id；恢復營業時刪除該貼文。
- **驗收：** 以假的 Graph API 測試發文、刪文、token 失效改為提醒並送出重新授權連結。
- **依賴：** Slice 4、Slice 6。外部：Meta 商家驗證與 App Review 通過（附錄 A.1）；App Review 的操作錄影需要本 Slice 在開發模式下可運作。

### 依賴圖

```
Slice 1 ─┐
         ├─ Slice 3 ─ Slice 4 ─┬─ Slice 5
Slice 2 ─┤                     ├─ Slice 7 ─┐
         └─ Slice 6 ───────────┴─ Slice 8 ─┴─ （外部審核通過後改成自動）
```

Slice 1～4 完成後就能上線：所有動作先以提醒呈現；Slice 5、7、8 各自完成後，把那一項改成自動。

---

## 6. 非目標

- 盤點進貨（第二家店的需求）：另開一輪設計。
- 雲悅（Xurf）API、foodpanda POS 整合、任何瀏覽器自動化。
- shorts／Reels、LINE VOOM、Google 商家貼文、Instagram。
- 開放式的「什麼都能派工」；第一版只有四種情境。
- 店家自助設定介面與營運後台（第一版用 CLI）。
- 公告圖片上傳、模型自由產生公告文字。

---

## 7. 待辦（非開發）

| 項目 | 負責 |
| --- | --- |
| 準備公司網域與公開頁面：隱私權政策、服務條款、資料刪除說明 | 營運者 |
| Meta 商家驗證（現在送），App Review（Slice 8 可在開發模式運作後送） | 營運者 |
| Google 商家 API 申請；請第一家店把公司 email 加為商家管理員 | 營運者、業主 |
| 實測 Xurf 關閉接單是否連帶關閉 foodpanda（Q16） | 業主 |
| 建立統籌助理 LINE 官方帳號與圖文選單 | 營運者 |
| 收集第一家店的營業時間、公告樣板、公告圖片、Xurf 與 foodpanda 的手動步驟 | 營運者、業主 |

---

## 附錄 A｜Q26 申請步驟

**共同前置：** 公司網域，以及隱私權政策、服務條款、資料刪除說明三個公開頁面；申請用的 email 與網站同網域較順利。

### A.1 Meta（FB 粉專發文）

1. 在 business.facebook.com 以平台營運者公司的名義建立或確認企業管理平台。
2. 企業設定 →「安全中心」→ 開始商家驗證：公司登記或商業登記文件（含統一編號）、地址、電話、網站。
3. developers.facebook.com 建立 App（類型「商家」），綁定企業管理平台；加入「Facebook 登入（企業版）」；填隱私權政策、服務條款、資料刪除網址與 App 圖示。
4. 開發模式下以測試粉專跑完「授權連結 → 選粉專 → 同意 → LINE 確認店休 → 粉專出現貼文」。權限預計是 `pages_show_list`、`pages_manage_posts`、`pages_read_engagement`，送審前對照官方文件確認。
5. 送 App Review：每個權限寫用途說明並附上一步的操作錄影。
6. 通過後切到「上線」模式。

### A.2 Google 商家（特殊營業時間）

1. 確認第一家店的商家檔案已驗證、建立超過 60 天、有網站、已設一般營業時間；請老闆把公司 email 加為「管理員」。
2. 建立 Google Cloud 專案，記下專案編號。
3. 填「Basic API Access」申請表：專案編號、公司 email、公司網站。
4. 在 Cloud Console 的配額頁確認 My Business Business Information API：0 QPM＝未通過、300 QPM＝通過。
5. 通過後啟用 My Business Account Management API 與 Business Information API；設定 OAuth 同意畫面（外部）、scope `business.manage`；建立網頁版 OAuth 用戶端，重新導向網址指向 smart-agri 的授權 callback（Slice 6）。
6. 確認 `business.manage` 是否需要通過 OAuth 應用程式驗證（含 Search Console 網域驗證）後再對外使用。
