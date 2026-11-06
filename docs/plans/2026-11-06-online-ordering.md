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
| D4 | 顧客怎麼看狀態 | 送出後導到 `/order/{slug}/o#<token>`（token 是隨機長字串，不是流水號，放在網址的 `#` 後面），每 10 秒輪詢；有留 Email 也寄信。 | 免登入也只有本人看得到。token 放在 fragment：瀏覽器不會把 `#` 後面送到伺服器，所以不會出現在請求路徑、紀錄或追蹤裡（#398 改，原本是路徑 `/o/{token}`）。 |
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
- **狀態 token 絕不出現在路徑或查詢字串**：查詢與取消的 API 從請求標頭 `X-Ordering-Token` 讀 token，顧客的狀態頁網址是 `/order/{slug}/o#<token>`（fragment 不會送到伺服器）。原因同 #377：ASP.NET Core 會把請求路徑放進每一筆紀錄的 logging scope（OTLP 會匯出 scope），追蹤也會記 `url.path`；請求標頭則兩者都不記（沒有註冊 HTTP logging，ASP.NET Core 的追蹤 instrumentation 不加標頭的 tag）。前端也不要把 token 放進任何請求的網址。
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
| `GET {slug}/orders/current`（標頭 `X-Ordering-Token`） | 200 `OrderingOrderView` | `404 order-not-found`：沒帶標頭、格式不對、這家店沒有這個 token 的訂單（包括別家店的 token），回應都相同 |
| `POST {slug}/orders/current/cancel`（標頭 `X-Ordering-Token`） | 200 `OrderingOrderView`（已取消） | 只有 `pending` 可以取消；其他狀態（含已取消）`409 not-cancellable`，`orderStatus` 是目前狀態；token 不對時同上 `404` |

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
- 成功 `201`：`token` **只在這裡給一次**（資料庫只存它的 SHA-256），前端立刻存起來（例如 `localStorage` 以 `submissionKey` 為鍵）再導到 `/order/{slug}/o#<token>`（狀態頁從 `location.hash` 讀 token，不要放進路徑或查詢字串）。
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

**輪詢**：訂單狀態頁每 10 秒 `GET {slug}/orders/current`（帶 `X-Ordering-Token` 標頭），頁面不在前景時可以停；狀態變成 `completed`、`declined`、`cancelled`、`expired` 之後停止。一個分頁每分鐘 6 次，在每 IP 120 次的限制內。

**實作決定（可以推翻）**

1. **訂單表 `OrderingOrders`**：取餐方式、狀態（七種都先定義）、`Version`（之後狀態變更的 compare-and-set）、營業日 `BusinessDate` 與當日號碼 `Number`（唯一 `(OrganizationId, BusinessDate, Number)`）、`PickupAt`（UTC；儘快＝送出時間＋估計上限，另存 `AsapMinMinutes`／`AsapMaxMinutes`）、顧客姓名／電話（只存數字）／Email、訂單備註、餐具、明細快照（jsonb：`PricedLine`＋品項備註）、小計與總額（O5 外送會加運費）、使用的菜單版本、狀態 token 的雜湊（唯一）、送出鍵（每店唯一）、`CreatedAt`、`CancelledAt`。
2. **號碼算在取餐的營業日**：預約單算在時段所屬的營業日（跨午夜時段算前一天），儘快單算在送出時所在營業時段的那天；所以先收到的明天預約單會是明天的 #001。
3. **防止競爭**：送出時在同一個交易裡先對這家店的 `OrderingShops` 列做一次「不改任何值」的更新來鎖住它，之後才檢查送出鍵、暫停、時段名額、菜單版本、售完、待確認數並配發號碼，所以最後一個名額與號碼不會被兩筆同時拿走（不用 raw SQL）。
4. **時段名額**只算預約單，且不算 `cancelled`、`declined`、`expired`；取消就釋出名額。
5. **待確認上限**只算取餐時間還沒到的 `pending` 訂單（和訂位相同），避免在 #400 的逾時處理上線前，舊的待確認單永遠擋住同一支電話。
6. **slug 查詢**：`OrderingLookup`（Infrastructure）是點餐唯一可以跨組織查詢的地方，只回傳組織 ID；token 一律在該店的組織範圍內查詢，所以別家店的 token 是 `404`。
7. **token 只放在請求標頭與網址 fragment**（見上方「共通規則」）；整合測試確認 token 不出現在任何紀錄、logging scope 或 span tag。點餐程式自己的紀錄也不寫姓名、電話、Email、備註。
8. **`/order/` 的提供方式**：`AdminSpaHosting` 加上 `Shop:RootPath`（映像設 `wwwroot/shop`）；`order` 加入 API 保留的第一段路徑，所以後台永遠不會回答 `/order/**`；沒設定時 `/order/**` 和其他沒有端點的路徑一樣是 `401`。檔案規則和後台相同（帶雜湊的檔名快取一年、`index.html` 不快取、缺少的帶副檔名路徑 `404`）。
9. **個資保存期限**（D12）在 #400 實作，見第 4.6 節。


### 4.3 O2（#398）顧客網頁的實作決定（可以推翻）

1. **不用 Angular Router 與 HttpClient**：五個頁面用一個小的瀏覽紀錄輔助程式切換、各自延遲載入，API 用 `fetch`。首次載入 173.66 kB（傳輸約 51 kB）；用 Router 會接近 200 kB。預算警告 200 kB、錯誤 260 kB。
2. **型別由 `apps/api/openapi/v1.json` 產生**（`shop:api-types`，同 admin），CI 檢查產生結果沒有差異。
3. **「現在」一律用伺服器的時間**（回應裡的 `now`），不看手機時鐘：今天／明天、分類供應時間、售完判斷都以它為準。
4. **選填的單選群組（min 0、max 1）用核取方塊**，讓顧客可以取消；只有必選單選才用圓鈕。
5. **送出鍵**：每次進結帳頁產生新的 UUID，連同請求先存在瀏覽器再送出；網路失敗或 5xx 重送時沿用，任何 4xx 或成功後丟棄。顧客的聯絡資料不另外存在裝置上。
6. **菜單版本改變**：重新檢查購物車、移除不再有效的品項並列出、重算價格，請顧客確認後用同一個送出鍵重送。
7. **送出成功後**清空購物車，保留選好的取餐方式與時間；頁面上方顯示進行中訂單的連結（瀏覽器記住這家店最近的訂單 token）。
8. **個資告知**沿用訂位頁的寫法（保存到取餐時間後 90 天）；實際刪除在 O4（#400）。
9. **E2E**：`apps/shop-e2e`（Cypress，4311 埠）對 production 建置回放從真實 API 錄下的回應，CI 的 e2e job 跟在 admin-e2e 後面跑。

### 4.4 O3（#399）後台 API 與實作決定

後端與權限在 #399 的第一個 commit 完成；後台「點餐」頁（含示範模式）由前端票補上。

**權限與共通規則**

- 新權限 `manage-orders`（「處理線上點餐」），排在 `handle-assistant-issues` 之後（`AccountPermissionOrder`、`ACCOUNT_PERMISSIONS`）。預設：開發種子與示範資料的老闆（`smb-admin`）與內部同仁（`internal-employee`）都有；`setup` 建立的第一位管理者本來就拿到所有權限；migration `AddOrderingStaffActions` 把它補給**現有**的所有老闆與內部同仁帳號（#126 新增 `handle-assistant-issues` 時沒有補，這次因為議題寫明「預設給老闆與內部員工」而補）。之後新增的成員照團隊頁勾選。
- 路徑前綴 `/api/v1/ordering`（tag `Ordering (admin)`），要登入且有 `manage-orders`，組織取自登入的權杖。所有回應 `Cache-Control: no-store`。所有欄位一定會送出，沒有值時是 `null`。
- 錯誤沿用後台慣例（`ApiErrors`）：
  - `401`：沒有登入（無 body）。
  - `403 ordering`：沒有 `manage-orders`，**或**這個組織沒有這筆訂單（別家店的訂單 id）——兩種回應逐位元組相同（後台一向不用 404 表示「不存在」，避免探測別的組織）。
  - `404 ordering-not-configured`：這個組織沒有點餐設定或菜單（每個端點都一樣，前端顯示「尚未設定線上點餐」）。`ApiErrors.WithReason` 因此新增 404，只用在「自己的組織沒有設定」，不用在可能屬於別的組織的資源。
  - `404 item-not-found`：目前的菜單沒有這個品項。
  - `422`：`{ message, errors: { 欄位: [訊息] } }`（沒有 `reason`）。
  - `400`：body 不是 JSON，或列舉值不認得（例如 `reason: "bored"`）——框架預設回應。
  - `409`：只有訂單動作會回，body 是 `OrderingStaffConflict`（見下）。
- 紀錄只寫組織、訂單 id 與號碼、成員帳號 id、動作與結果狀態，不寫顧客姓名、電話、Email、備註、店家給顧客的說明（整合測試檢查）。

**端點**

| 方法與路徑 | 成功 | 內容 |
| --- | --- | --- |
| `GET summary` | 200 `OrderingStaffSummaryView` | `now`、`businessDate`（店家目前的營業日 `yyyy-MM-dd`；跨午夜的營業時段算開始那天）、`timeZone`、`shopName`、`slug`、`pendingCount`（所有待確認，不分取餐日）、`activeCount`（已接單＋可取餐，不分取餐日）、`latestOrderAt`（最新一筆訂單的送出時間，沒有訂單時 `null`）、`changedAt`（任何訂單最後一次狀態改變的時間，包括新訂單與顧客取消；沒有訂單時 `null`）、`paused`、`asapBand`（`under-15`、`15-30`、`30-45`、`45-60`）、`asap{minMinutes,maxMinutes}`（目前檔位的分鐘數）、`services{takeoutScheduled,takeoutAsap}`、`menuVersion`、`shopVersion` |
| `GET orders?view=pending\|active\|done` | 200 `OrderingStaffOrdersView` `{now, view, orders[]}` | `pending`：所有待確認，送出時間舊的在前。`active`：已接單與可取餐，`promisedAt` 早的在前。`done`：完成、婉拒、取消、逾時之中，取餐營業日是今天的，加上今天（店家時區 00:00 起）才結束的（例如今天婉拒的明天預約單），最近結束的在前。每個清單最多 500 筆。`view` 不是這三個：`422`（`errors.view`） |
| `GET orders/{id}` | 200 `OrderingStaffOrderView` | 一筆訂單 |
| `POST orders/{id}/accept` | 200 `OrderingStaffOrderView` | `{ version, promisedAt? }`。`promisedAt`（含時差的 ISO 8601）不填＝顧客選的時間（儘快單是估計上限；若已經過了，則是現在）。預約單不能早於顧客選的時間，儘快單不能早於現在（容許 1 分鐘時鐘誤差），兩者都最多比「顧客選的時間與現在較晚者」晚 24 小時；超出 `422 errors.promisedAt` |
| `POST orders/{id}/decline` | 200 | `{ version, reason, note? }`；`reason`：`too-busy`、`sold-out`、`closed`、`cannot-meet-request`、`other`；`note` 去頭尾空白後最多 100 字、單行，`other` 必填。`note` **會顯示給顧客** |
| `POST orders/{id}/ready` | 200 | `{ version }`：已接單 → 可取餐 |
| `POST orders/{id}/complete` | 200 | `{ version }`：已接單或可取餐 → 完成（可以跳過〔可取餐〕） |
| `POST orders/{id}/cancel` | 200 | `{ version, reason, note? }`：店家取消已接單或可取餐的訂單；`reason`：`customer-no-show`、`customer-request`、`other`（`other` 要 `note`，規則同婉拒，也會顯示給顧客）。待確認的訂單請用〔婉拒〕 |
| `PUT pause` | 200 `OrderingStaffSummaryView` | `{ paused: true\|false }`；沒填 `422 errors.paused` |
| `PUT asap-band` | 200 `OrderingStaffSummaryView` | `{ band }`；沒填 `422 errors.band` |
| `GET items` | 200 `OrderingStaffMenuView` | `{ now, menuVersion, categories[{id, name, items[{id, name, price, availability, resetsAt}]}] }`；`availability`：`available`、`sold-out-today`（到 `resetsAt` 自動恢復）、`unavailable`。售完時間已過的品項是 `available` |
| `PUT items/{itemId}/state` | 200 `OrderingStaffItemView` | `{ state: "available"\|"sold-out-today"\|"unavailable" }`；沒填 `422 errors.state`；品項不在目前的菜單 `404 item-not-found` |

**`OrderingStaffOrderView`**：`id`、`number`、`displayNumber`（「#001」）、`status`、`version`、`serviceType`、`businessDate`、`pickupAt`（顧客選的時間；儘快單是送出時的估計上限）、`estimate{minMinutes,maxMinutes}`（儘快單才有）、`promisedAt`（接單時給的時間，接單前 `null`）、`customerName`、`customerPhone`（只有數字，給 `tel:`）、`customerPhoneDisplay`（「0912-345-678」）、`customerEmail`、`lines`（同公開 API 的明細快照，含品項備註 `comment`）、`orderComment`、`tableware`、`subtotal`、`total`、`menuVersion`、`createdAt`、`statusChangedAt`、`accepted`／`ready`／`completed`（`{at, by}` 或 `null`）、`declined`（`{at, by, reason, note}` 或 `null`）、`cancelled`（`{at, by: "customer"|"shop", member, reason, note}` 或 `null`；顧客取消時 `member`、`reason`、`note` 是 `null`）、`actions`（現在可以做的動作，依序：`accept`、`decline`、`ready`、`complete`、`cancel`——按鈕依它顯示）。`by`／`member` 是 `{id, displayName}`，帳號已不存在時 `null`。

**同時操作（compare-and-set）**：每個動作都帶畫面上看到的 `version`。伺服器在交易裡讀出訂單：`version` 不同 → `409 order-changed`；`version` 相同但狀態不允許這個動作 → `409 action-not-allowed`；否則改狀態、`version` 加一，以 `Version` 當並行權杖寫回（`UPDATE … WHERE Version = 讀到的值`）——兩個人（或店員與取消的顧客）同時從同一個版本出手，只有一個寫得進去，另一個也得到 `409 order-changed`（顧客那邊是 `409 not-cancellable`）。`409` 的 body 是 `{ type, title, status: 409, reason, message, order }`，`order` 是**目前**的 `OrderingStaffOrderView`：前端直接用它取代畫面上的那一筆並顯示 `message`，不必再查一次。

**輪詢與新訂單提示（給前端）**

- 側邊選單與點餐頁每 10 秒 `GET summary`（每個分頁每分鐘 6 次）。徽章顯示 `pendingCount`；分頁標題可用 `pendingCount`。
- **有新訂單**：`latestOrderAt` 比上一次看到的新（第一次載入只記下、不響）。這時播提示音（使用者在頁面上互動過、而且開關開著才播）並重新讀 `pending`。
- **清單過期**：`changedAt` 或 `shopVersion` 跟上一次不同時，重新讀目前分頁的清單（或品項）。其他分頁的店員接單、顧客取消都會改 `changedAt`。
- 時間比較一律用回應的 `now`（伺服器時間），不看裝置時鐘。`done` 分頁跨日時自動換成新的營業日。

**顧客端看到的（公開 API 的 `OrderingOrderView` 新增欄位）**：`promisedAt`、`declineReason`、`declineNote`、`cancelledBy`（`customer`|`shop`）、`cancelReason`、`cancelNote`。接單後 `canCancel` 是 `false`、取消回 `409 not-cancellable`。`apps/shop` 的狀態頁已顯示：店家給的時間（預約單晚於原時間時加「（店家調整了取餐時間）」，儘快單顯示「預計 HH:mm 可取餐」）、婉拒與店家取消的原因與說明、店家取消的標題「店家已取消這筆訂單」；舊的錄製回應沒有這些欄位時當成沒有。

**實作決定（可以推翻）**

1. **狀態機在領域模型**：`OrderingOrder.Allows(status, action)`；`Accept`、`Decline`、`MarkReady`、`Complete`、`CancelByShop`、`CancelByCustomer` 各自檢查、`Version` 加一、記 `StatusChangedAt`。新欄位：`StatusChangedAt`、`PromisedAt`、`AcceptedAt/ByAccountId`、`ReadyAt/ByAccountId`、`CompletedAt/ByAccountId`、`DeclinedAt/ByAccountId`、`DeclineReason`、`DeclineNote`、`CancelledBy`、`CancelledByAccountId`、`CancelReason`、`CancelNote`，由 check constraint 保持一致（例如婉拒才有理由、店家取消才有成員與理由）。成員只存帳號 id，不設外鍵；名稱在回應時查。
2. **改時間不動名額**：接單時改的時間存在 `PromisedAt`；`PickupAt`（顧客選的時段）不變，所以時段名額、當日號碼、待確認上限照舊依它計算。
3. **顧客取消也改成同一套 compare-and-set**（原本是 `ExecuteUpdate`），並記 `CancelledBy = customer`；migration 把既有的已取消訂單補成 `customer`、`StatusChangedAt` 補成取消或送出時間。
4. **`IOrderingOrderOutcomes.Decided`**（#400 的寄信接點，現在是不做事的 `NoOrderingOrderOutcomes`）：只在贏得 compare-and-set 之後、同一個交易裡呼叫，涵蓋接單、婉拒、顧客取消、店家取消；可取餐與完成不呼叫。輸掉的人不會呼叫（整合測試以記錄用的實作確認每筆只呼叫一次）。
5. **店家開關**（暫停、即時單檔位、品項狀態）是頻繁切換的操作：**不寫組織活動紀錄**；有改到時 `OrderingShops.Version` 加一（重按同一個值不加），讓其他分頁從 `summary.shopVersion` 看得出來。暫停與檔位用條件式 `ExecuteUpdate`（值不同才更新）；品項狀態先鎖住店家那一列（與送出訂單相同），所以和同時送出的訂單不會各看到一半。
6. **今日售完**沿用 `OrderingItemState.SoldOutToday`：到下一個營業日第一段可點餐時間自動恢復，不需要排程；過了恢復時間的那一列等下一次切換時才被取代，查詢時已當成可點。
7. 店家婉拒或取消時的說明會顯示給顧客，前端的輸入框要說明這一點。


### 4.5 O3（#399）後台「點餐」頁的實作決定（可以推翻）

1. **側邊選單的待確認數**：沒有另外的全域輪詢。和「案件」「轉真人」一樣在換頁時更新；開著「點餐」頁時，它自己每 10 秒的輪詢在待確認數改變或處理完一筆時通知側邊選單更新。所以停在別的頁面時，數字要換頁才會變。
2. **權限**：側邊選單項目可設定所需權限，沒有 `manage-orders` 就不顯示；直接開網址顯示標準的「沒有權限」並且不發任何請求。
3. **排序**：待確認新的在上；處理中依（調整後的）預計時間；今日完成依最後變更時間，新的在上。
4. **版面**：內容寬度 960px 以上三欄並排，以下用三個切換鈕一次看一欄。
5. **重新載入**：`latestOrderAt`、`changedAt`、`shopVersion` 任一改變就重新載入三個清單；較新的載入會取消較舊的，舊回應不會蓋掉剛做完的動作。
6. **衝突**：其他人先處理時顯示「#NNN 這筆訂單剛被其他人處理了，畫面已更新成目前的狀態。」並換成伺服器回傳的狀態。
7. **接單對話框**：維持顧客的時間，或延後 10／20／30 分鐘，或指定時間；即時單以「現在起」計算。〔可取餐〕〔完成〕直接執行、不跳對話框；暫停接單要確認，恢復不必。
8. **提示音**：Web Audio 產生的兩聲短音，按〔開啟提示音〕（同時試播）後才會響，選擇記在瀏覽器；第一次讀到的摘要不響。
9. **示範資料**：王記麵店 #011–#016，時間相對於第一次使用時；放在 `ordering.mock.ts`，不放 `demo-seed.ts`（後端測試會讀 demo-seed）。

### 4.6 O4（#400）實作決定（可以推翻）

**通知信**

1. **五種信**：接單（含預計取餐時間；預約單晚於顧客選的時間時加「（店家調整了取餐時間）」）、婉拒（理由的白話＋店員的說明）、店家取消（同上）、顧客自己取消（確認信）、逾時自動取消（「很抱歉，店家未能及時確認，這筆訂單已自動取消」）。可取餐與完成不寄。理由的說法與 `apps/shop` 狀態頁相同（`OrderingOrderEmail.DeclineWords`／`CancelWords`）。
2. **寄送方式同訂位**：`IOrderingOrderOutcomes` 換成 `EmailOrderingOrderOutcomes`，只在贏得 compare-and-set 的交易裡加一筆 `ordering.order-email` 背景工作（輸的、重按的、重跑的逾時工作都不加）。是否寄只有一條規則 `OrderingOrderEmail.Sends`：顧客有 Email（而且還沒被清除）、狀態有對應的信、`IEmailSender.IsConfigured`。工作執行時重讀訂單與店家，狀態已經不是工作的狀態就不寄（例如接單後、信還沒寄出前就被店家取消：只寄取消信）。SMTP 失敗交給工作佇列重試，錯誤訊息不含收件地址，**不改訂單狀態**；沒有「已寄出」欄位（同訂位，極少數情況可能重寄一次）。
3. **內容**：純文字、繁體中文；寄件人顯示名稱是店名（地址是 `Smtp:From`）。訂單編號、店名、取餐方式與時間（接單後是約定的時間；儘快單未接單時是「儘快（11/17（二）約 12:15–12:30）」；跨午夜時段寫「（隔天）」）、「付款：取餐時付款」、店家電話、明細（品名 × 數量、金額、選項同狀態頁的寫法）與合計。設了 `PublicChannels:PublicBaseUrl` 時附店家的點餐網頁 `/order/{slug}`。**不附訂單狀態頁**：資料庫只存 token 的雜湊，token 也不能放進網址的路徑或查詢字串（D4）。**不含顧客填的任何內容**（姓名、電話、備註），所以寄錯地址也只透露一碗麵。紀錄只寫訂單與組織 ID、結果與第幾次嘗試。

**逾時自動取消**

4. **設定**：點餐設定新增 `pendingTimeoutMinutes`（5–120，預設 15），`ordering-shop show` 顯示「待確認逾時：營業中 N 分鐘沒有處理就自動取消」；資料庫 check constraint 同範圍。
5. **只在可點餐時間內計算**：送出時就算好 `ExpiresAt = min(開放起點 + 逾時, 取餐時間)` 存在訂單上，開放起點是送出當下（正在某段可點餐時間內，含跨午夜時段的凌晨），否則是下一段的開始（`OrderingOrder.ExpiryFor`）。所以晚上送出的明天預約單不會在半夜被取消；第一個時段的預約單最晚在取餐時間取消。之後改設定只影響新訂單（不像訂位每次重讀設定）。
6. **背景工作 `ordering.expire-order`**：和訂單在同一個交易裡排入，`runAfter = ExpiresAt`。執行時對 `Version` 做 compare-and-set，pending → expired（`OrderingOrder.Expire`），贏了才呼叫 `IOrderingOrderOutcomes`；店員已接單／婉拒、或顧客已取消就什麼都不做；還沒到時間（只有時鐘被調時會發生）就重新排一次。逾時後、工作執行前店員按〔接單〕仍然有效（不另外擋），兩者同時發生只有一個生效。逾時的訂單本來就不佔時段名額。
7. **既有待確認訂單的回填**：migration 把既有訂單的 `ExpiresAt` 設成 `LEAST(CreatedAt + 15 分鐘, PickupAt)`，並替每筆仍待確認的訂單插入一個逾時工作。沒有用營業時間規則，因為 SQL 讀不到每週營業時間與時區；而且線上點餐還沒有店家實際使用，這些只會是開發或測試資料，已經過時的待確認單在下一次工作佇列執行時逾時是合理的結果。
8. **後台**：`OrderingStaffOrderView` 新增 `expiresAt`；待確認卡片顯示「HH:mm 前未確認將自動取消」，剩不到 5 分鐘（以伺服器時間計）時紅字加粗。示範資料的 `expiresAt` 是送出後 15 分鐘或取餐時間（示範模式不會自動逾時）。顧客狀態頁的逾時標題改成「店家未能及時確認，訂單已自動取消」，與信件一致。

**個資保存期限（D12）**

9. **時間**：取餐時刻（接單時約定的 `PromisedAt`，否則顧客選的 `PickupAt`）滿 90×24 小時（含剛好 90 天）就清除，用的是確切時刻而不是訂位的「90 天前那一天的 00:00」；每天 03:00 的清理執行，所以實際清除落在滿 90 天後的一天內。
10. **清什麼**：姓名與電話改成空字串、Email 與訂單備註改成 `null`、明細快照裡每一行的品項備註改成 `null`，記下 `PersonalDataClearedAt`；訂單編號、狀態、所有時間、品項、選項與金額、店員的婉拒或取消說明保留（統計營業額）。不是狀態變更，`Version` 不變。check constraint 保證已清除的訂單沒有聯絡資料。
11. **在哪裡跑**：`RetentionCleanupService.ClearOrderPersonalDataAsync`，每日的 `HandoffRetentionSweep`（每個組織都跑，各自一個 `FixedOrganizationContext` 的 `AppDbContext`，不用 `IgnoreQueryFilters`）與 `retention-cleanup` 指令都會執行；每批 1000 筆各自一個交易，已清除的不會再讀。不寫組織活動（資料是顧客的），只有紀錄與計數（`record = ordering-order`）。
12. **清除後的畫面**：後台與公開 API 的姓名都顯示「（已刪除）」，電話是空字串（後台不顯示撥號連結，狀態頁不顯示「電話末三碼」），後台的 `personalDataClearedAt` 有值。
13. **隱私權政策範本**新增第 5.6 節「線上點餐」，照實際保存的欄位與期限寫；`deploy/README.md` 第 18.3 節說明通知信、逾時與保存期限。


### 4.7 O5（#401）外送實作決定（可以推翻）

**設定（匯入檔 `shop` 段）**

1. **開關**：`services.delivery`（外送預約）與 `services.deliveryAsap`（外送儘快），兩個都選填、預設 `false`，所以舊的匯入檔不必改。
   `deliveryAsap` 只能和 `delivery` 一起開（否則匯入錯誤）；外送預約一定有，因為外送儘快也要先選區域。`services` 仍然至少要開一種。
2. **`delivery` 段**：`{ zones, orderingHours?, minLeadMinutes?, asapExtraMinutes?, note? }`。`services.delivery` 開著時必填；關著時可以留著（照樣檢查、
   存下來但不用），方便店家暫時關外送。區域 `{ id, name, fee, minimumSubtotal? }`：1–50 個、`id` 同菜單 id 規則且不重複、名稱一行最多 40 字、
   運費 0–10,000、最低消費 0–100,000（不填＝ 0，以餐點小計計算、不含運費）。`orderingHours` 不填＝外帶的可點餐時間；`minLeadMinutes`
   預設 45（0–1440）；`asapExtraMinutes` 預設 20（0–120）；`note` 最多 300 字（外送範圍說明）。錯誤和其他欄位一樣逐條列出路徑。
3. **儲存**：`OrderingShops` 新增 `Delivery`、`DeliveryAsap` 兩個布林與 `DeliverySettings`（jsonb，匯入檔格式，`OrderingShopJson.WriteDelivery`）；
   check constraint 保證開了外送就有設定、外送儘快必須同時開外送。組織紀錄的變更欄位多一個 `delivery`（只記有改，不記內容）。
4. **時段**：外送和外帶共用 `slotMinutes`、`daysAhead`、`maxOrdersPerSlot` 的規則，但用外送自己的時間與最短準備時間；**名額分開算**
   （`OrderingSlots` 多一個 `OrderingChannel` 參數，`SlotCountsAsync` 依取餐方式只算同一種的預約單）。當日號碼仍然整家店一起編。
5. **外送儘快**：外送儘快開著、店家沒暫停、現在在外送時間內，而且「現在＋（後台即時單檔位上限＋`asapExtraMinutes`）」不超過這段外送時間的結束，
   才開放；估計是檔位的兩端各加 `asapExtraMinutes`（15–30 → 約 35–50 分鐘）。不另外設檔位。

**訂單**

6. **取餐方式**：`delivery-scheduled`、`delivery-asap`。訂單新增 `DeliveryZoneId`、`DeliveryZoneName`、`DeliveryFee`（送出時區域的快照；外帶是
   `null`／`null`／0）、`DeliveryAddress`（必填，一行，最多 120 字）、`DeliveryNote`（給外送的備註，選填，最多 100 字）。`Total = Subtotal + DeliveryFee`
   （check constraint），總額上限仍是 1,000,000（含運費）。`PickupAt` 對外送是「預計送達」；逾時、號碼、接單改時間的規則都照舊，
   逾時只算外送時間內（`ExpiryFor` 收外送的時間）。
7. **伺服器的檢查順序**（接在 §4.2 的表後面）：欄位錯誤（`deliveryZoneId` 沒選、`deliveryAddress` 必填與長度、`deliveryNote` 長度，同
   `invalid-order` 的 `errors`）→ 暫停 → **沒開外送（或外送儘快）`409 service-unavailable`** → **區域不在目前的設定 `422 unknown-zone`** → 時段 →
   菜單版本 → 選項 → 總額 → 售完 → **未達最低消費 `422 below-minimum`**（`OrderingProblem` 新增 `minimumSubtotal` 與 `shortfall`，其他回應是 `null`）
   → 待確認上限。運費一律用伺服器當下的區域設定，請求裡的任何金額欄位都忽略。
8. **狀態**：**不新增狀態**。外送的「已出發」就是 `ready`（〔可取餐〕→ 外送卡片上叫〔已出發〕），顧客頁與信件依取餐方式換說法；
   這樣狀態機、compare-and-set、後台的三個清單、逾時與保存期限都不必改，`ready` 之後一樣是〔完成〕（送達）。
9. **API 形狀**：`GET {slug}` 的 `services` 多 `delivery`、`deliveryAsap`（店家開了外送且有區域時才是 `true`），並新增 `delivery`
   （沒開外送時 `null`）：`zones[{id,name,fee,minimumSubtotal}]`、`note`、`open`（依外送時間）、`asap`（現在能點外送儘快時才有）、`minLeadMinutes`、
   `orderingHours`（外送時間）、`addressMaxLength: 120`、`noteMaxLength: 100`。`GET {slug}/slots` 新增 `deliveryDates`（形狀同 `dates`）。
   送出訂單新增 `deliveryZoneId`、`deliveryAddress`、`deliveryNote`（外帶不填）。顧客與後台的訂單都新增 `deliveryFee` 與
   `delivery: {zoneId, zoneName, fee, address, note} | null`；顧客頁本來就要 token，所以給完整地址。後台 `summary.services` 同上。
10. **個資**：地址與給外送的備註是個資：90 天清除時地址改成空字串、備註 `null`（check constraint 也檢查），區域名稱與運費保留。
    紀錄不寫地址；**通知信只寫區域名稱，不寫地址與備註**，信裡寫「外送」「預計送達」與「付款：貨到付款」，並列出小計、運費、合計。

**顧客網頁（`apps/shop`）**

11. **選方式**：時間面板列出店家開放的方式（外帶儘快、外帶預約、外送（儘快）、外送（預約）），現在不能用的照列但不能選；選外送時先選區域
    （運費與「滿 NT$X 外送」），再選時間（外送自己的日期與時段）。選好的區域記在購物車的取餐方式裡（`zoneId`）；區域被店家拿掉時當成沒選。
12. **門檻**：購物車與結帳都顯示「還差 NT$X 達外送門檻」並擋住結帳（前端算的只是提示，伺服器會再檢查；收到 `below-minimum` 時請顧客回購物車加點，
    收到 `unknown-zone` 時重新打開面板選區域）。小計、外送費、合計分開顯示，付款寫「貨到付款」。
13. **地址**：結帳頁的「外送地址」（必填）與「給外送的備註」只在記憶體裡，不存在裝置上（同姓名與電話；只有送出結果不明、要重送時才和送出內容一起暫存）。
14. **狀態頁**：外送訂單顯示「外送：預計 HH:mm 送達」、區域、地址、外送備註、外送費與「貨到付款」；進度條最後一步是「已出發」，`ready` 的標題是「外送已出發」，
    `completed` 是「已送達」。舊的錄製回應沒有 `delivery` 欄位時依 `serviceType` 判斷。
15. **錄製**：`apps/shop-e2e/src/fixtures/` 與後台 Hybrid spec 的回應在 2026-11-07 用 #401 的 API 重新錄（5291 埠、暫存資料庫
    `ordering401pub`、`ordering401adm`，腳本在 scratchpad `ordering/401rec/`），因為所有回應都多了新欄位。錄製時間是週六凌晨（休息中），
    所以既有的 E2E 從「明天 12:30」改成「今天 12:30」，菜單第 2 版的訂單號碼從 #002 變成 #003（中間多了一筆外送訂單 #002）。

**後台「點餐」頁**

16. 外送卡片的取餐方式寫成「外送預約」／「外送即時」小標籤；顧客那一行下面是區域（粗體）與完整地址（Google 地圖搜尋連結
    `https://www.google.com/maps/search/?api=1&query=…`，另開分頁）；地址清除後顯示「（已刪除）」、沒有連結。給外送的備註放在明細上方的備註框，
    運費在合計前面。時間一律說「送達」（「12:30 送達」「預計 HH:mm 送達」、接單對話框的「預計送達時間」）；`ready` 的按鈕、狀態與完成行都叫「已出發」。
    即時單準備時間檔位在外帶即時或外送即時任一開著時顯示（外送儘快用同一個檔位）。示範資料多一筆已接單的外送訂單 #010（王太太，新營區其他里）。
    清單與篩選不變。顧客網頁的選項寫「外送（預約）」「外送（儘快）」，後台沿用外帶的「預約／即時」說法。

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
