# 線上點餐：設計與拆票

**日期：** 2026-11-06
**狀態：** 已確認（2026-11-06）。O1–O4 是負責人選定的範圍（第 3 節）；第 4 節 D1–D12 由負責人回覆「照建議繼續」確認，第 7 節非目標不變。issue 標題前綴「線上點餐｜」。
**依據：**
- 參考產品的功能盤點：一家台南早午餐店的公開線上點餐頁，2026-11-06 以瀏覽器實際操作到結帳頁、停在送出前。原始資料在 session scratchpad `ichef/report.md`，可能被系統清掉，重點已摘進第 2.1 節。只參考功能與流程，不沿用對方的品牌、圖片與文案。
- smart-agri `master`（99d1da7，含線上訂位 #389–#391）的現況，見第 2.2 節。
**拆票方式：** 同店務統籌助理。每個 Slice 都附測試與驗收條件；會改 migration 的 Slice 依序接續分支。

---

## 1. 目標

**情境：** 顧客從店家的 FB、Google 商家、LINE 圖文選單或店內 QR code 打開點餐網頁。顧客選取餐方式與時間、瀏覽菜單、選規格與加料、放進購物車、填姓名與電話後送出。店員在 smart-agri 後台的「點餐」頁看到新訂單（有提示音），按〔接單〕或〔婉拒〕；餐點好了按〔可取餐〕，顧客的訂單狀態頁即時更新，有留 Email 的同時收到信。

> 顧客：`https://<平台網址>/order/wangji` → 選「外帶・今天 12:30」→ 牛肉麵（大碗 +20、加蛋 +15）×1 → 送出 → 訂單狀態頁「#023 等待店家確認」
> 店員（後台「點餐」頁，響一聲）：#023 外帶 12:30　陳小姐 0912-345-678　NT$185　〔接單〕〔婉拒〕
> 店員按〔接單〕→ 顧客頁面變成「店家已接單，預計 12:30 可取餐」

**第一版要做到：**

1. 每家店一個公開、免登入的點餐網頁：分類菜單、商品選項（最多四層的規格與加料、必選與上限、加價）、購物車、結帳。
2. 四種取餐方式：外帶預約、外帶即時（儘快）、外送、內用掃碼，每家店可分別開關。
3. 只支援取餐（或用餐後）時付款。
4. 店員在後台接單、婉拒、標示可取餐與完成；可切換品項售完、暫停接單。
5. 顧客用訂單狀態頁追蹤；有留 Email 就收到接單、婉拒、逾時未處理的通知信。
6. 菜單與店家點餐設定用 CLI 匯入 JSON。

**非目標：** 見第 7 節。

---

## 2. 現況事實

### 2.1 參考產品的功能盤點（2026-11-06）

- **店家資訊：** 店名、橫幅圖、電話、地址、每週營業時間；系統算出「營業中／打烊中．明天 07:00 開始營業」；四種公告（提醒事項、結帳說明、優惠、點數）。打烊時先跳窗請顧客選預約時間，選完才看得到菜單。
- **時段：** 30 分鐘一格，未來 3 天；預約用的營業時間可以和一般營業時間分開設；準備時間分四檔（<15、15–30、30–45、45–60 分鐘），忙碌時提示顧客取餐時間延後。送出前伺服器再驗一次時段。
- **菜單：** 分類頁籤加單欄清單；商品卡有名稱、價格、一行描述、縮圖、已加入份數；售完分「暫停供應到某時」與「限量剩 N 份」；分類可依時段、星期、起訖日期顯示；菜單有版本，版本變了購物車會提示重新整理。
- **商品選項：** 選項群組有最少、最多、可否重複選；選項本身可以加價，也可以再帶下一層群組（實例：套餐選主餐沙拉 → 第二層選配料）。畫面顯示「必選 N 個／選填／最多 N 個／已選 N」，單選用圓鈕、多選用核取方塊；另有品項備註、數量、即時算出的總價。
- **購物車與結帳：** 改數量、修改選項、刪除、訂單備註、是否需要餐具；結帳欄位為姓名、電話、Email（線上付款才必填）、取餐時間、付款方式、隱私權聲明。
- **送出後：** 待店家確認 → 已接單 → 完成；顧客在接單前可取消；店家婉拒附理由（太忙、售完、廚房已休息等）；訂單頁有訂單號碼、預計時間、明細、「再點一次」。狀態即時更新。
- **未實際走過（只從字串推斷）：** 外送、內用掃碼、會員登入、線上付款、送單後的畫面。

### 2.2 smart-agri（`master` 99d1da7）

- **沒有任何金額、商品、菜單、購物車、訂單的概念**，全部是新的領域模型。
- **組織：** 一家店＝一個組織。`IOrganizationScoped` 的實體自動套用組織篩選。公開網頁用 slug 找組織時，在 `CoordinatorLookup` 裡繞過篩選，再開一個固定在該組織的 `AppDbContext`。
- **公開網頁的現有做法：** 訂位頁 `/reserve/{slug}` 是 API 自己產生的 HTML（`CoordinatorReservationPages`），嚴格 CSP、`no-store`、不需要 JS 也能用，每 IP 有次數限制（`PublicRateLimitedEndpoint`）。點餐頁的互動多很多，不適合沿用純伺服器產生的寫法，見 D1。
- **後台：** Angular（`apps/admin`）。登入走 OpenIddict。權限用 `AccountPermission` 列舉（目前 8 個），端點加上 `.RequirePermission(...)`。新功能區的寫法參考 `handoffs.repository.ts`，它同時支援示範資料與 API 兩種模式。
- **即時更新：** 後台沒有 SignalR 或 WebSocket；聊天有 SSE；其他頁面用 `pollWhile()` 輪詢。
- **背景工作、寄信、保存期限：** `BackgroundJob` 可設定延後執行時間（`runAfter`）、失敗會重試；寄信用 `IEmailSender`，沒設 SMTP 時整個寄信功能關閉；`RetentionCleanupService` 依組織刪除過期資料。這幾樣都能直接沿用。
- **營業時間計算：** `BusinessHours`／`WeeklyHours` 已處理跨午夜與日光節約，屬於統籌助理的 `ShopSettings`。
- **CLI：** `coordinator-shop import` 是範本：先驗證整份檔案、列出所有錯誤，再寫入，並記一筆組織活動。

---

## 3. 範圍決定（2026-11-06 負責人選定）

| # | 問題 | 決定 |
| --- | --- | --- |
| O1 | 做在哪裡、店家怎麼接單 | smart-agri 裡的**獨立模組**，不接統籌助理的 LINE。店員在後台網頁接單。 |
| O2 | 取餐方式 | 外帶預約、外帶即時、外送、內用掃碼，**四種都做**。 |
| O3 | 付款 | **只有取餐時（或用餐後）付款**，不接金流、不開發票（由店家自己的 POS 開）。 |
| O4 | 菜單管理 | 先用 **CLI 匯入 JSON**；售完與暫停接單由店員在後台切換（D7）。 |

---

## 4. 預設做法（D1–D12，2026-11-06 確認）

| # | 問題 | 建議 | 主要理由與代價 |
| --- | --- | --- | --- |
| D1 | 顧客端網頁用什麼做 | 新增一個小型 Angular app `apps/shop`，由 API 在 `/order/` 底下提供（和後台 SPA 同樣的提供方式），購物車存在瀏覽器。 | 選項樹、購物車、即時算價需要前端互動；放進後台 app 會讓顧客下載整個後台的程式。代價：多一個 build 與部署產物。 |
| D2 | 顧客身分 | 訪客。姓名、電話必填，Email 選填（同訂位 R3）；不做會員、簡訊驗證、LINE 登入。 | 最快上線；防亂下單靠每 IP 次數限制、同一支電話同時最多 N 筆待確認、店家可婉拒。 |
| D3 | 店員怎麼知道有新訂單 | 後台「點餐」頁每 10 秒輪詢，有新訂單就播提示音、分頁標題顯示數字，側邊選單顯示待處理數。頁面要開著才會響。 | 沿用現有輪詢，不必加 WebSocket。代價：頁面沒開就不會響。之後可加 LINE 或 Web Push 通知（第 7 節）。 |
| D4 | 顧客怎麼看狀態 | 送出後導到 `/order/{slug}/o/{token}`（token 是隨機長字串，不是流水號），每 10 秒輪詢；有留 Email 也寄信。 | 免登入也只有本人看得到。 |
| D5 | 訂單狀態 | 待確認 → 已接單（店員可改預計時間）→ 可取餐（外送為「已出發」）→ 完成；另有已婉拒（固定理由加自由文字）、顧客已取消（接單前才可取消）、逾時未處理（例如 15 分鐘沒人接，系統自動婉拒並寄信）。 | 同參考產品；逾時的做法沿用訂位 R7。 |
| D6 | 價格與計算 | 新台幣整數元。伺服器依菜單重算每一筆金額，不信任前端傳來的價格；訂單明細存下單當時的品名、選項、價格快照。菜單有版本，版本不同時請顧客確認新價格。 | 菜單之後改價，舊訂單仍顯示原價。 |
| D7 | 售完 | 後台可切換「今日售完」（隔天營業開始自動恢復）與「停售」；限量份數第一版不做。 | 「今日售完」最常用；限量要在接單時扣庫存，複雜度高。 |
| D8 | 時段與營業時間 | 點餐有自己的設定：每週可點餐時間（沿用 `WeeklyHours` 格式）、時段長度（預設 30 分）、可預約天數（預設 3）、最短準備時間、每時段接單上限（選填）。暫停接單由後台切換。 | 獨立模組不依賴統籌助理；之後若同一家店也用統籌助理，可加上「臨時店休自動暫停接單」。 |
| D9 | 外帶即時 | 店家開啟時，顯示「儘快（約 N–M 分鐘）」；N、M 由店員在後台從四檔準備時間選（<15、15–30、30–45、45–60）。 | 同參考產品。 |
| D10 | 外送 | 店家設定外送區域清單（例：「新營區・民權里」或「3 公里內」這類純文字），每區有運費與最低消費；顧客選區域再填地址。不做地圖定位與距離計算。 | 不需要地圖 API 與費用；代價是區域由店家自己定義。 |
| D11 | 內用掃碼 | 每張桌子一個 QR（`/order/{slug}?t=<桌號 token>`，CLI 可輸出桌號與網址清單，或直接印成 QR 紙）。同一桌可以多次送單（加點），後台依桌號把未結帳的訂單合併顯示，店員結帳後按〔已結帳〕清桌。 | 內用不選時間；結帳在櫃台，不需要金流。 |
| D12 | 個資保存 | 訂單的顧客姓名、電話、Email、地址在用餐或取餐時間後 90 天刪除，金額與品項統計保留（同訂位 R8）。 | 沿用 `RetentionCleanupService`。 |

**其他實作細節（拆票時再定，可推翻）：**
- 新權限 `manage-orders`（「處理線上點餐」），預設給老闆與內部員工；牽動 8 個權限勾選框寫死的測試（Domain、jest、Cypress `responsive`／`accessibility`／`team-and-access`）。
- 訂單號碼：每家店每天從 #001 起算，方便叫號；網址用另外的隨機 token。
- 菜單 JSON：分類（排序、可選的供應時段與星期）、品項（名稱、價格、描述、圖片網址、可選擇的取餐方式）、選項群組（最少、最多、可重複、選項加價、下一層群組，最多四層）。圖片第一版只放網址，由店家自己找地方放。
- 新的公開路徑 `/order/` 要加進 `AdminSpaHosting` 的保留清單與測試；新的每 IP 次數限制項目；新實體加進 `OrganizationModelTests`、slug 查詢加進 `IgnoreQueryFiltersSourceTests` 的允許清單；新的背景工作種類加進 `JobOptionsTests`。


### 4.1 O1（#397）實作時的決定（可以推翻）

1. **菜單整份存成一個版本**：`OrderingMenus` 每版一列，整棵樹存成 jsonb（正規化後的精簡 JSON）；內容和上一版完全相同時不產生新版。分類、品項、群組、選項的 `id` 由店家在 JSON 裡自己取（`[a-z0-9-]{1,40}`），售完狀態與之後的訂單都靠它對應。
2. **匯入檔**：`{ "shop": {…}, "menu": {…} }`，兩段都必填，不認得的鍵視為錯誤；範例 `deploy/ordering-shop.example.json`。點餐網址代號與訂位頁的代號分開，全平台唯一（資料庫唯一索引）。暫停接單、即時單準備時間檔位不在檔案裡：新店預設不暫停、`15-30`，由後台切換（O3），匯入不改動。
3. **沿用統籌助理的規則**：可點餐時間的格式與檢查（`ShopSettingsJson.ParseWeeklyHours`）、網址代號與電話的規則、時段長度的選項（10／15／20／30／60）。
4. **預設與上限**：最短準備 15 分鐘（0–1440）、可預約 3 天（1–14）、每時段上限不限（1–999）。品名 ≤ 40 字、描述 ≤ 300 字；價格 0–100,000 元；群組 `max` ≤ 99；最多 50 個分類、500 個品項、每層 20 個群組、每群組 50 個選項；四層。
5. **價格**：選項選 n 份＝n ×（選項加價＋它底下所選的加價）；同一選項不能出現兩次（多份用 count）；一行超過 100 萬元視為錯誤。錯誤是代碼加路徑（`set.salad.dressing`），每個錯誤都回報。
6. **時段**：從每段可點餐時間的開始，每隔時段長度一格，含結束時間；早於「現在＋最短準備」的不列。跨午夜的時段算在開始的那一天；前一天的跨午夜時段還有剩時也會列出（不佔 `daysAhead`）。達到每時段上限的時段仍列出、標成已滿、不能選。即時單：正在可點餐時間內、而且「現在＋準備時間上限」不超過這段的結束時間才開放。
7. **分類供應時間**：`{ days?, from?, to? }`，含 `from`、不含 `to`；`to` 早於 `from` 表示跨午夜。
8. **今日售完**：到「下一個營業日的第一個開始時間」自動恢復；營業中按的，「今天」是這段營業時間所屬的那天，所以中午按售完，晚上那段也不賣。
9. **組織紀錄**：`ordering-shop-updated` 只記改了哪些欄位與菜單版本，不記店名、公告或菜單內容。

### 4.2 O2（#398）API 與實作決定

後端在 #398 的第一個 commit 完成；`apps/shop` 目前只是佔位頁（建置、部署、`/order/` 路徑都已接好），點餐流程由前端票補上。

**路徑與共通規則**

- 公開 API：`/api/v1/public/ordering/{slug}/…`（`OrderingPublicEndpoints`）。放在 `/api/v1/public` 底下，所以帶著別的網站 `Origin` 的請求一律 `403`（`PublicOriginGuard`，與官網訪客 API 相同），沒有 CORS。免登入，帶著後台登入 cookie 也一樣當成匿名。
- 型別：這些端點在 `apps/api/openapi/v1.json` 裡（tag `Ordering (public)`），不另外做只含公開端點的文件。`apps/shop` 用同一份檔案以 openapi-typescript 產生型別即可（admin 的 `api-schema.ts` 也會含這些路徑，無害）。所有欄位一定會送出，沒有值時是 `null`（不會省略鍵）。
- 每個回應都是 `Cache-Control: no-store`、`Referrer-Policy: no-referrer`。
- slug 不分大小寫（`/WangJi` 等於 `wangji`）。未知的 slug、有點餐設定但沒有菜單的店、格式不對的 slug，回應完全相同：`404 shop-not-found`。
- 頻率限制（每個用戶端 IP、不分店家）：`POST …/orders` 每分鐘 10 次（`OrderingSubmissionsPerIpPerMinute`），其他（店家、菜單、時段、查詢、取消）合計每分鐘 120 次（`OrderingRequestsPerIpPerMinute`）。超過時 `429`，`Retry-After`（秒），body 是共用的 `{ type, title, status, reason: "rate-limited", message }`——**只有這五個欄位**，不是 `OrderingProblem`，前端要把缺的欄位當成 `undefined`。
- 錯誤一律是 `application/problem+json` 的 `OrderingProblem`：`{ type, title, status, reason, message, errors, lineErrors, menuVersion, orderNumber, orderStatus }`。`reason` 是給程式判斷的固定代碼，`message` 是可以直接顯示的中文；其餘欄位只在下表註明的情況有值，否則是 `null`。
- 時間：`DateTimeOffset` 以 ISO 8601（含時差）送出；日期 `yyyy-MM-dd`、時刻 `HH:mm` 都是店家時區（`timeZone`）的牆上時間。

**端點**

| 方法與路徑 | 成功 | 內容 |
| --- | --- | --- |
| `GET {slug}` | 200 `OrderingShopInfoView` | `slug`、`name`、`phone`、`address`、`notices{reminder,checkout}`、`services{takeoutScheduled,takeoutAsap}`、`paused`、`timeZone`、`now`（伺服器時間）、`open{isOpen,currentPeriodOpens,currentPeriodCloses,nextOpening}`、`asap{minMinutes,maxMinutes}`（現在可以點「儘快」時才有，否則 `null`）、`slotRules{slotMinutes,daysAhead,minLeadMinutes}`、`showOrderComment`、`showTableware`、`menuVersion`、`orderingHours[{day:"mon"…,periods[{open,close}]}]`、`limits{maxLines:30,maxQuantity:99,nameMaxLength:40,lineCommentMaxLength:100,orderCommentMaxLength:200,maxTotal:1000000}` |
| `GET {slug}/menu` | 200 `OrderingMenuView` | `version`、`categories[{id,name,description,availability,items}]`；`availability` 是 `{days:["tue",…]|null, from:"11:00"|null, to:"14:00"|null}` 或 `null`（隨時供應），含 `from`、不含 `to`，`to` 早於 `from` 表示跨午夜、算開始那天；品項 `{id,name,price,description,imageUrl,optionGroups,state}`；群組 `{id,name,min,max,repeatable,options}`，選項 `{id,name,price,optionGroups}`（最多四層）；`state` 是 `null` 或 `{kind:"sold-out-today"|"unavailable", resetsAt}`：今日售完的品項在取餐時間 ≥ `resetsAt` 時可以點，停售（`unavailable`，`resetsAt` 為 `null`）一律不能點 |
| `GET {slug}/slots` | 200 `OrderingSlotsView` | `now`、`dates[{date:"2026-11-17", slots[{value:"2026-11-17T12:30", at, time:"12:30", isNextDay, isFull}]}]`。只列至少有一個未滿時段的營業日；`isFull` 的時段照樣列出但不能選。暫停接單或沒開外帶預約時 `dates` 是空陣列 |
| `POST {slug}/orders` | 201 `OrderingSubmittedView` `{token, order}` | 見下方「送出訂單」 |
| `GET {slug}/orders/{token}` | 200 `OrderingOrderView` | `404 order-not-found`：這家店沒有這個 token 的訂單（包括別家店的 token、格式不對的 token） |
| `POST {slug}/orders/{token}/cancel` | 200 `OrderingOrderView`（已取消） | 只有 `pending` 可以取消；其他狀態（含已取消）`409 not-cancellable`，`orderStatus` 是目前狀態 |

`OrderingOrderView`：`number`（整數）、`displayNumber`（「#001」）、`status`（`pending|accepted|ready|completed|declined|cancelled|expired`；O2 只會出現 `pending`、`cancelled`）、`serviceType`（`takeout-scheduled|takeout-asap`）、`businessDate`、`pickupAt`（預約：選的時段；儘快：送出時間＋估計上限）、`estimate`（儘快才有 `{minMinutes,maxMinutes}`）、`createdAt`、`cancelledAt`、`lines`（明細快照，見下）、`subtotal`、`total`、`menuVersion`、`customerName`、`phoneLastDigits`（電話末三碼）、`orderComment`、`tableware`、`canCancel`。**不含 Email**。

**送出訂單**（`OrderingOrderRequest`，JSON）

```json
{
  "serviceType": "takeout-scheduled",
  "pickupDate": "2026-11-17", "pickupSlot": "2026-11-17T12:30",
  "menuVersion": 3,
  "lines": [
    { "itemId": "beef-noodle", "quantity": 1, "comment": "麵硬一點",
      "selections": [
        { "groupId": "size", "optionId": "large", "count": 1, "selections": null },
        { "groupId": "combo", "optionId": "set", "count": 1,
          "selections": [ { "groupId": "side", "optionId": "cucumber", "count": 1, "selections": null } ] } ] }
  ],
  "customer": { "name": "陳小姐", "phone": "0912-345-678", "email": null },
  "orderComment": null, "tableware": true,
  "submissionKey": "3f2504e0-4f89-11d3-9a0c-0305e82c3301"
}
```

- 不送任何價格；就算送了也會被忽略，伺服器依目前菜單重算（`MenuPricing`）。`pickupDate`、`pickupSlot` 只有外帶預約要填（取自 `GET slots` 的 `date` 與 `value`）；儘快不填。
- `submissionKey`：前端在**每次進入結帳**時產生一個 UUID，重試同一次送出時沿用。`orderComment`／`tableware` 只有店家顯示這兩欄時才存（`tableware` 沒填當成「不需要」）。
- 成功 `201`：`token` **只在這裡給一次**（資料庫只存它的 SHA-256），前端立刻存起來（例如 `localStorage` 以 `submissionKey` 為鍵）再導到 `/order/{slug}/o/{token}`。
- **同一個送出鍵再送一次**（例如回應遺失後重試）：`409 duplicate-submission`，`orderNumber` 是第一次的訂單號碼（「#003」），不會再建立訂單，也拿不回 token。這是刻意的最簡單做法：前端在送出前就把 `submissionKey` 存好、收到 201 立刻存 token；萬一回應真的遺失，畫面顯示「這筆訂單已經送出過了（訂單編號 #003）」並提供店家電話。
- 伺服器的檢查順序（先失敗的先回）：

| 狀態 | `reason` | 何時 | 額外欄位 |
| --- | --- | --- | --- |
| 400 | （框架預設的回應，不是 `OrderingProblem`） | body 不是這個形狀的 JSON（例如未知的 `serviceType`） | |
| 404 | `shop-not-found` | slug 找不到店或店沒有菜單 | |
| 422 | `invalid-order` | 欄位錯誤：`submissionKey`、`serviceType`、`pickupSlot`、`menuVersion`、`lines`（空的或超過 30 項）、`lines[i].comment`（> 100 字）、`customer.name`（必填、≤ 40 字）、`customer.phone`（台灣手機或市話，規則同訂位）、`customer.email`（選填，格式）、`orderComment`（≤ 200 字）。全部一次回報 | `errors`（欄位 → 訊息陣列） |
| 409 | `duplicate-submission` | 同一個送出鍵已經送出過（這家店） | `orderNumber` |
| 409 | `shop-paused` | 店家暫停接單 | |
| 409 | `service-unavailable` | 沒開外帶預約；或現在不能點儘快（打烊、太接近打烊、沒開） | |
| 409 | `slot-unavailable` | 時段不在目前可選的清單（已過、太近、不存在） | |
| 409 | `slot-full` | 時段已滿 | |
| 409 | `menu-changed` | `menuVersion` 不是目前版本：重新取菜單、請顧客確認新價格後再送 | `menuVersion`（目前版本） |
| 422 | `invalid-order` | 某一行選項不符菜單（代碼同 `MenuPricing`：`unknown-item`、`invalid-quantity`、`unknown-group`、`unknown-option`、`duplicate-option`、`invalid-count`、`not-repeatable`、`too-few`、`too-many`、`amount-too-large`）；或總額 > 1,000,000（`errors.lines`） | `lineErrors[{line, code, path}]`，`line` 從 0 起，`path` 如 `combo.set.side` |
| 409 | `items-unavailable` | 品項在取餐時間不能供應：`sold-out-today`、`unavailable`、`category-unavailable`（分類不在供應時段） | `lineErrors` |
| 429 | `too-many-pending` | 這支電話在這家店已有 3 筆待確認、取餐時間未到的訂單 | |
| 429 | `rate-limited` | 頻率限制（見上） | 只有五個基本欄位 |

**輪詢**：訂單狀態頁每 10 秒 `GET {slug}/orders/{token}`，頁面不在前景時可以停；狀態變成 `completed`、`declined`、`cancelled`、`expired` 之後停止。一個分頁每分鐘 6 次，在每 IP 120 次的限制內。

**實作決定（可以推翻）**

1. **訂單表 `OrderingOrders`**：取餐方式、狀態（七種都先定義）、`Version`（之後狀態變更的 compare-and-set）、營業日 `BusinessDate` 與當日號碼 `Number`（唯一 `(OrganizationId, BusinessDate, Number)`）、`PickupAt`（UTC；儘快＝送出時間＋估計上限，另存 `AsapMinMinutes`／`AsapMaxMinutes`）、顧客姓名／電話（只存數字）／Email、訂單備註、餐具、明細快照（jsonb：`PricedLine`＋品項備註）、小計與總額（O5 外送會加運費）、使用的菜單版本、狀態 token 的雜湊（唯一）、送出鍵（每店唯一）、`CreatedAt`、`CancelledAt`。
2. **號碼算在取餐的營業日**：預約單算在時段所屬的營業日（跨午夜時段算前一天），儘快單算在送出時所在營業時段的那天；所以先收到的明天預約單會是明天的 #001。
3. **防止競爭**：送出時在同一個交易裡先對這家店的 `OrderingShops` 列做一次「不改任何值」的更新來鎖住它，之後才檢查送出鍵、暫停、時段名額、菜單版本、售完、待確認數並配發號碼，所以最後一個名額與號碼不會被兩筆同時拿走（不用 raw SQL）。
4. **時段名額**只算預約單，且不算 `cancelled`、`declined`、`expired`；取消就釋出名額。
5. **待確認上限**只算取餐時間還沒到的 `pending` 訂單（和訂位相同），避免在 #400 的逾時處理上線前，舊的待確認單永遠擋住同一支電話。
6. **slug 查詢**：`OrderingLookup`（Infrastructure）是點餐唯一可以跨組織查詢的地方，只回傳組織 ID；token 一律在該店的組織範圍內查詢，所以別家店的 token 是 `404`。
7. **token 放在路徑上**（D4 的網址也是），所以會出現在主機的請求路徑紀錄（logging scope、追蹤的 `url.path`）裡；點餐程式自己的紀錄不寫 token，也不寫姓名、電話、Email、備註。
8. **`/order/` 的提供方式**：`AdminSpaHosting` 加上 `Shop:RootPath`（映像設 `wwwroot/shop`）；`order` 加入 API 保留的第一段路徑，所以後台永遠不會回答 `/order/**`；沒設定時 `/order/**` 和其他沒有端點的路徑一樣是 `401`。檔案規則和後台相同（帶雜湊的檔名快取一年、`index.html` 不快取、缺少的帶副檔名路徑 `404`）。
9. **個資保存期限**（D12）尚未實作，是 #400 的工作。

---

## 5. 名詞

| 名詞 | 意思 |
| --- | --- |
| 點餐設定 | 每家店一份：slug、店名、公告、開放的取餐方式、可點餐時間、時段、準備時間、外送區域、桌號。 |
| 菜單 | 分類、品項、選項群組的樹；每次匯入產生一個新版本。 |
| 選項群組 | 掛在品項或選項下面的一組選擇，有最少、最多、可否重複。 |
| 訂單 | 一次送出的內容；含取餐方式、時間、明細快照、顧客資料、狀態。 |
| 桌次 | 內用時同一桌號、尚未結帳的訂單集合。 |

---

## 6. Slice

### Slice O1（#397）｜點餐設定、菜單與價格計算（後端）
- `OrderingSettings`、`Menu`（版本）、`MenuCategory`、`MenuItem`、`OptionGroup`、`Option` 等資料表與 migration。
- CLI `ordering-shop import|show`：一次驗證整份 JSON 並列出所有錯誤（選項群組的最少不可大於最多、層數不可超過四層、價格不可為負數等）。
- 領域服務：給一組選擇，驗證是否符合每個群組的規則，並算出單價與小計（大量單元測試）。
- 時段計算：依可點餐時間、時段長度、最短準備時間、可預約天數、暫停接單、每時段上限。
- **驗收：** 匯入範例 JSON 成功；錯誤的 JSON 列出全部錯誤且不寫入；價格與時段的測試涵蓋跨午夜與四層選項。

### Slice O2（#398）｜顧客點餐網頁：外帶預約與外帶即時（依賴 O1）
- 公開 API：店家資訊與營業狀態、菜單、可選時段、送出訂單（伺服器重算價格、驗證時段與菜單版本、同一個送出鍵只算一次）、查詢與取消訂單（用 token）。
- `apps/shop`：取餐方式與時間 → 分類菜單 → 品項選項（多層、必選提示、即時算價）→ 購物車（存瀏覽器）→ 結帳（姓名、電話、Email、備註、餐具）→ 訂單狀態頁。
- 每 IP 次數限制；同一支電話待確認的訂單上限。
- **驗收：** 390 與 1280 寬度無橫向捲動；必選未選時無法加入購物車；改過的前端價格會被伺服器擋下；Cypress 走完一次下單。

### Slice O3（#399）｜後台接單（依賴 O2）
- 新權限 `manage-orders`；後台「點餐」頁：待確認、處理中、今日完成三欄或三個分頁。
- 〔接單〕（可改預計時間）、〔婉拒〕（理由）、〔可取餐〕、〔完成〕；狀態變更用 Version 做 compare-and-set，避免兩人同時處理。
- 每 10 秒輪詢、提示音、分頁標題與側邊選單計數；售完切換、暫停接單、即時單準備時間四檔。
- **驗收：** 兩個分頁同時按〔接單〕只有一個成功；顧客頁面在 10 秒內反映新狀態。

### Slice O4（#400）｜Email 通知、逾時與保存期限（依賴 O3）
- 接單、婉拒、逾時、店家取消時寄信給有留 Email 的顧客（背景工作）。
- 逾時未處理自動婉拒（延後執行的背景工作）。
- 90 天個資清理。
- **驗收：** 沒設 SMTP 時其他功能照常；逾時與店員按〔接單〕同時發生時只有一個生效。

### Slice O5（#401）｜外送（依賴 O2）
- 外送區域、運費、最低消費；結帳加地址；後台顯示地址與「已出發」。
- **驗收：** 未達最低消費無法送出；運費計入總額且由伺服器計算。

### Slice O6（#402）｜內用掃碼（依賴 O3）
- 桌號 token、CLI 輸出桌號網址或 QR；掃碼後直接進菜單、不選時間；同桌加點；後台依桌號合併顯示、〔已結帳〕清桌。
- **驗收：** 清桌後同一 QR 開始新的桌次；不同桌號的訂單不會合併。

### 依賴圖

```
O1 → O2 → O3 → O4
      │     └──→ O6
      └──→ O5
```

O1–O4 合起來就能上線（外帶預約與外帶即時）；O5、O6 可以在 O3 之後平行做。

---

## 7. 非目標（第一版）

- 線上付款（LINE Pay、信用卡）、電子發票、退款。
- 會員、簡訊驗證、LINE 登入、集點、優惠券、折扣活動。
- 限量庫存、推薦品項、多語系、文字大小切換。
- 地圖定位與依距離計算運費、外送員派遣。
- 後台的菜單編輯器（先用 CLI）、圖片上傳。
- 新訂單用 LINE 或 Web Push 通知店員（可在 O3 之後加）。
- 和店家既有 POS 的串接、廚房出單機列印（第一版可用瀏覽器列印訂單）。

---

## 8. 待負責人決定

（目前沒有。實作中出現的新問題記在各 Slice 的 PR 與本節。）
