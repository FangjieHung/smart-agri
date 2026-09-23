# 中小企業 AI 助理 UX Demo（`apps/admin`）

這是「中小企業 AI 助理」的**前端畫面 Demo**。它示範完整的操作流程與畫面狀態，但**沒有後端、沒有真實 AI、沒有真實驗證**。

> ⚠️ **請勿在這個 Demo 輸入任何真實的敏感資料。**
> 不要輸入真實姓名、身分證字號、電話、地址、病歷、訂單編號、信用卡號、密碼、API 金鑰或 LINE 權杖。
> 所有輸入都會以明文存進**你自己瀏覽器的 `localStorage`**，任何能打開這台電腦瀏覽器的人都讀得到，而且這裡的「登入」與「權限」都只是畫面模擬，不具備任何安全性。
> 畫面上也有同樣的聲明：`apps/admin/src/app/core/repositories/demo-repository.ts:601`。

相關文件：

- 設計文件：[`docs/plans/2026-09-18-sme-ai-assistant-ux-demo-design.md`](../../docs/plans/2026-09-18-sme-ai-assistant-ux-demo-design.md)
- 展示腳本（5–10 分鐘）：[`docs/demo/demo-script.md`](../../docs/demo/demo-script.md)
- 後端介接 hand-off：
  - [`docs/handoff/ai-assistant-backend-integration-handoff.md`](../../docs/handoff/ai-assistant-backend-integration-handoff.md)：全域驗收清單、外部服務接點與非功能期待
  - [`docs/handoff/mock-to-api-mapping.md`](../../docs/handoff/mock-to-api-mapping.md)：每個 repository 方法對應的 endpoint、授權、錯誤分類與替換檔案
  - [`docs/handoff/route-screen-matrix.md`](../../docs/handoff/route-screen-matrix.md)：每條路由對應的畫面、狀態與 e2e
  - [`docs/handoff/tasks-6-10-backend-handoff.md`](../../docs/handoff/tasks-6-10-backend-handoff.md)：五個功能區的深度細節

---

## 安裝

```bash
# Node 版本：24.x（Angular 22 / Nx 23）
npm install
```

## 啟動

```bash
npm start            # 等同 npx nx serve admin
```

預設使用 **4200** 埠。若 4200 已被其他程式佔用，Angular dev-server 會在終端機問你要不要改用別的埠（`Port 4200 is already in use. Would you like to use a different port?`）——選 `Yes` 即可，網址會印在下一行，請以終端機印出的網址為準。也可以直接指定：

```bash
npx nx serve admin --port 5200
```

啟動後從 `/` 進入未登入首頁，點「進入 Demo」到 `/login` 選一個 Demo 身分。

## 測試

```bash
npx nx test admin                                # 單元測試（vitest）
npx nx e2e admin-e2e --configuration=production  # Cypress E2E
```

**E2E 一定要加 `--configuration=production`。** 只有這個 configuration 會自己啟動 dev-server（埠 4301）；直接跑 `npx nx e2e admin-e2e` 不會啟動伺服器，測試會全部連線失敗。

## Build

```bash
npx nx build admin
```

產出落在 `dist/smart-agri-admin`。**這個目錄有被 git 追蹤**，所以只是為了驗證改動而 build 時，請改成輸出到版控外的路徑：

```bash
npx nx build admin --output-path=/tmp/admin-build
```

## Lint

```bash
npx nx lint admin
```

---

## Demo 帳號清單

`/login` 沒有帳號密碼，只是三顆切換身分的按鈕。身分定義在 `apps/admin/src/app/core/repositories/demo-seed.ts:101`。

| 按鈕 | 內部 accountId | 這個身分看得到什麼 |
| --- | --- | --- |
| **SMB 管理者**（安心商行管理者） | `account-smb-admin` | 完整工作台。擁有 2 個既有助理（客服助理、內部教育訓練助理）、3 個知識庫、2 個資料庫；可以建立助理、管理知識庫與資料庫、設定平台內／官網／LINE 三種發布管道，並查看使用者**同意提交**的結構化紀錄與趨勢比較。 |
| **內部使用者**（安心商行客服同仁） | `account-internal-employee` | 只能使用團隊分享給他的助理（客服助理）。擁有自己的「同仁個人筆記」知識庫與「同仁排班回報」資料庫，並且是後者指定的資料管理者，所以看得到它的收集紀錄。看不到管理者的助理設定、其他知識庫與發布管道，`/app/settings` 的團隊區塊對他顯示拒絕面板。 |
| **外部客戶** | `account-external-customer` | 只能開啟開放給外部客戶的助理並對話、填寫授權表單、查看自己的追蹤紀錄。`我的助理`／`知識庫`／`資料庫`／`發布管道` 都會顯示「你還沒有…」或「只有助理擁有者可以設定」的空白狀態，看不到管理者的任何資料。 |

**對話紀錄依帳號隔離。** 每個帳號的對話存在各自的 `localStorage` key（`sme-demo:chat:<accountId>:<assistantId>`），切換身分後看不到前一個身分的對話。這是畫面上的隔離示範，**不是安全邊界**。

### 誰可以在平台內開啟一個助理

判斷只有一個地方：`canOpenInPlatform()`（`apps/admin/src/app/core/repositories/publishing-channels.ts:94-103`），由 `canUseAssistant()` 呼叫（`core/repositories/mock-demo-repository.ts:3008-3018`）。規則是：

1. **擁有者永遠開得了**自己的助理，包含清單是空的、平台內分享已暫停時，這樣他才能自己測試。
2. 其他帳號要**同時**滿足兩個條件：
   - **使用對象（`audience`）決定「哪一種人」**——角色要落在 `AUDIENCE_ROLES` 內（`core/domain/assistant.model.ts:28-36`）。
   - **發布管道「平台內分享」的勾選清單決定「哪些帳號」**——要在 `allowedAccountIds` 內，而且該管道沒有暫停。

`AssistantConfigurationView.sharedWithAccountIds` **不是**第二條授權路徑，它只是這份勾選清單的初始值（`defaultPublishingRecord()`，`publishing-channels.ts:31-51`）；助理一旦有保存的發布設定，就以保存的清單為準。

**取消勾選只收回權限，不會刪掉任何對話。** 該帳號的對話仍留在自己的 `sme-demo:chat:<accountId>:<assistantId>` key 裡，重新勾選後原封不動回來——助理擁有者本來就看不到別人的對話，也不該有一個開關可以單方面銷毀別人的資料。畫面上的說明文字就是這樣寫的（`features/publishing/platform-sharing/platform-sharing.component.html:8-11`）。

**未登入的官網訪客不受這份清單影響**：他們由「官網嵌入或 LINE 是否已發布」決定（`isExternallyPublished()`，`publishing-channels.ts:279-291`），平台內分享不算對外。

種子資料示範了這條規則：內部使用者可以開啟**客服助理**（在清單內、使用對象含內部員工），但開不了**內部教育訓練助理**（它的平台內分享是「已暫停」）。

### 誰可以設定一個助理的發布管道

判斷只有一個地方：`canManagePublishing()`（`apps/admin/src/app/core/repositories/publishing-channels.ts:116-124`），由 `publishingTarget()` 呼叫（`core/repositories/mock-demo-repository.ts:3034-3041`），涵蓋 `/app/channels` 總覽與三個管道的所有讀寫。規則是**擁有者 ＋ 帳號有 `manage-publishing`**，兩者缺一不可——擁有權不會自動帶出發布權，「誰負責對外發布」在中小企業裡本來就常常不是助理的建立者。

**收回這個權限不會把已經在用的人踢出去。** 「誰開得了助理」仍由 `canOpenInPlatform()` 決定，已儲存的管道設定也原封不動留著，權限加回來就照舊。要示範：在 `/app/settings` 取消管理者的「管理發布管道」，`/app/channels` 會變成空白狀態，但內部使用者仍然開得了客服助理。

### 誰可以看到收集紀錄

判斷只有一個地方：`canReadConsentedRecords()`（`apps/admin/src/app/core/repositories/database-access.ts:22-30`），由 `canReadRecords()` 呼叫（`core/repositories/mock-demo-repository.ts:2464-2469`），涵蓋收集紀錄與趨勢比較（`:1634`）、資料庫摘要的紀錄與對象數量（`:2517`）、詳情的「你的權限」（`:2486`）與 `listManagedSubmissions()`（`:889`）。要**兩層都通過**：

1. **帳號層級**：團隊設定裡有「查看同意提交的紀錄」（`read-consented-submissions`）。在 `/app/settings` 變更。
2. **資料庫層級**：被這個資料庫**指定為資料管理者**。在資料庫的「權限」頁籤變更，只有擁有者改得動。

**擁有者不會自動通過。** 擁有一個資料庫可以管理它的表單設定，但看得到誰的資料是另一件事——擁有者要把自己列進資料管理者才看得到收集紀錄。這是 §10 權限矩陣「僅能查看使用者明確同意提交的資料」的落點。

**移除只收回查看權限，不刪除任何紀錄。** 取消勾選後紀錄仍在儲存裡，重新指定就原封不動回來（和平台內分享取消勾選不刪對話是同一個原則）。被拒絕時畫面不顯示追蹤對象、紀錄內容，連筆數都不顯示。

**兩個權限刻意沒有接到行為**，畫面上標示為「尚未接到行為」並寫出原因：

- `use-shared-assistants`：會和「使用對象＋平台內勾選清單」重複成第二條授權路徑，而 `canOpenInPlatform()` 是刻意收斂出來的唯一判斷點。
- `read-own-tracking`：若真的檢查，等於允許管理者關掉別人「看自己資料」的能力，與 §10「一般使用者可查看自己的」相反。

正式版要不要把它們變成真的授權關係，見 `docs/handoff/tasks-6-10-backend-handoff.md` 第 8 節 Open questions 第 9 點。

## Fixture 情境（`?demoScenario=`）

在任何工作台網址後面加上 `?demoScenario=<值>`，可以直接預覽載入中、部分失敗、權限不足等狀態，不需要真的製造錯誤。參數只影響 mock repository，重新整理或換頁時參數消失就恢復正常。

定義：`apps/admin/src/app/core/repositories/demo-scenario-param.ts`、`apps/admin/src/app/core/repositories/demo-repository.ts:86`。

| 值 | 效果 | 建議示範網址 |
| --- | --- | --- |
| `ready` | 預設狀態（等同不加參數） | — |
| `loading` | 所有資料查詢停在載入中，示範骨架／等待畫面 | `/app/knowledge?demoScenario=loading` |
| `partial-failure` | 資料仍可使用，但頂部顯示「部分知識庫同步暫時無法讀取，其他知識庫仍可正常使用」 | `/app/knowledge?demoScenario=partial-failure` |
| `permission-denied` | 顯示「無法查看…／目前的帳號沒有權限」，且**不揭露資源名稱或內容** | `/app/knowledge?demoScenario=permission-denied` |
| `disconnected-channel` | 已發布的官網嵌入改標示為「需要處理：官網連線中斷（模擬情境）」，同一助理的其他管道不受影響 | `/app/channels?demoScenario=disconnected-channel` |

另外，不用參數也能看到的固定錯誤狀態（來自 seed 資料）：

- 文件五種處理狀態：`/app/knowledge/knowledge-product-guide/content`（可使用／處理中／等待處理／部分內容無法讀取／處理失敗）
- LINE 逐欄驗證失敗：`/app/assistants/assistant-customer-service/publishing?channel=line`
- 紀錄不足不顯示趨勢：`/app/databases/database-customer-records/trends?subject=subject-chen`
- 已撤回同意的紀錄只留軌跡：`/app/databases/database-customer-records/records?subject=subject-lin`（時間軸 2 筆，下方另有一筆不含內容的撤回軌跡）
- 同一種公司資料回答、兩種引用出處設定：`/app/chat/assistant-customer-service`（開著，有「查看引用來源」）與 `/app/chat/assistant-internal-onboarding`（關著，問「皮革怎麼清洗」仍標示「根據你的資料」，但沒有引用來源按鈕）
- 定期回報：`/app/databases/database-customer-records/trends`（客服助理每月一次，顯示下次回報日期與變化摘要）；`/app/databases/database-orders/records` 沒有助理回報到這裡，所以沒有面板

---

## 已知限制

### 這不是真的

- **模擬登入，不是真實驗證。** `/login` 只是切換一個字串，沒有密碼、沒有 token、沒有伺服器。所有「權限」判斷都在瀏覽器端，任何人都能改寫。正式後端必須自己重做完整授權檢查。
- **沒有真實 AI。** 所有回答都是 `demo-seed.ts` 裡預先寫好的固定字串，依「已連接的資料來源」與「回答規則」挑選其中一種（根據你的資料／一般知識補充／資料中沒有答案）。輸入自由文字時只會比對預先準備的題目。**引用來源也是寫死的文件名與摘錄**，沒有真正的檢索。
- **沒有真實上傳。** 知識庫的「加入示範文件」只模擬處理進度，不會讀取或傳送任何檔案。
- **沒有外部服務。** 官網嵌入碼指向 `widget.demo.invalid`、LINE Webhook 指向 `webhook.demo.invalid`，「檢查安裝狀態」與「傳送測試訊息」的結果都是寫死的模擬結果，**不可貼到正式環境使用**。

### 資料保存方式

- **身分存在 `sessionStorage`（key：`demo-session`），閒置 30 分鐘逾時。** 只屬於單一瀏覽器分頁，所以可以開兩個分頁同時示範兩個身分；關閉分頁或逾時後回到 `/login`，並顯示「Demo 登入已逾時」的說明。設定見 `apps/admin/src/app/core/session/demo-session.service.ts:28`。
- **其餘資料存在 `localStorage`，key 一律以 `sme-demo:` 開頭**（`sme-demo:created-assistants`、`sme-demo:assistant-draft:<accountId>`、`sme-demo:knowledge:<id>`、`sme-demo:created-databases`、`sme-demo:database-fields:<id>`、`sme-demo:chat:<accountId>:<assistantId>`、`sme-demo:publishing:<assistantId>`、`sme-demo:assistant-settings:<assistantId>`、`sme-demo:chat-records`、`sme-demo:team-permissions`、`sme-demo:database-access:<databaseId>`）。清空這些 key 就會回到初始 seed 狀態，清除方式見展示腳本的「事前準備」。

### 功能缺口

- **「對話與回報紀錄」（`/app/activity`）只提供入口說明**，不顯示跨助理的合併清單。依設計，對話屬於發起對話的帳號，管理端只能看匿名統計，所以合併清單要等正式介接後再定義能顯示哪些欄位。
- **「團隊與設定」（`/app/settings`）的團隊管理只能改權限，不能改成員。** 畫面列出三個 Demo 身分、角色與該角色可以做什麼，具備 `manage-assistants` 的帳號可以逐項變更每個成員的權限；但沒有邀請、沒有離職、沒有密碼，**成員清單固定就是 seed 的三個帳號**。兩個權限（`use-shared-assistants`、`read-own-tracking`）在畫面上標示「尚未接到行為」——改它們不會改變任何畫面，理由見下方「誰可以看到收集紀錄」。
- **趨勢比較的差異值由 mock repository 計算**（本次／上次／首次、較上次／較首次），不是手寫在 fixture 裡，也不是後端算的。正式版本的計算與四捨五入規則需要後端定義。「定期回報」面板的變化摘要**整段沿用同一份算好的字串**，沒有另一套分析。
- **回答規則已經全部接到行為，沒有純裝飾的設定值。** 嚴格／一般知識（`knowledgeScope`）、保存自己的對話（`keepOwnConversations`）、顯示引用出處（`showCitations`）、定期回報（`periodicReport` 搭配寫入的資料庫），以及「找不到資料時的回覆」（`refusalMessage`）都會改變行為。`refusalMessage` 既用在建立精靈的試問預覽（`core/repositories/mock-demo-repository.ts:1240`），也用在終端對話的「查無資料」（`:2322`）；種子助理的預設值就是原本那句固定文案（`demo-seed.ts` 的 `ASSISTANT_RULE_DEFAULTS`），所以**未編輯過的助理行為不變**，而「回答與記錄」頁籤顯示的就是對話真的會說的那一句。和 `showCitations` 一樣，**已經保存的回答不會被改寫**，規則只影響之後的新回答。
- **撤回同意可以用了，但稽核軌跡很淺。** 提交者可以在對話的送出收據上撤回自己送出的紀錄：撤回會清掉內容、紀錄立刻離開收集紀錄與趨勢比較，只在收集紀錄留下「提交日期、撤回日期、來源」的軌跡。資料管理者**不能**代為撤回或代為刪除。軌跡沒有操作者、IP 或同意條款版本——mock 存不住，所以也沒有假裝存著。未登入訪客只能在**同一個瀏覽器分頁內**撤回，分頁一關就再也指認不到那筆紀錄，畫面對此直說。

### 測試現況

- `npx nx test admin`：67 個檔案、380 個測試全部通過。
- `npx nx e2e admin-e2e --configuration=production`：16 個 spec、126 個測試全部通過（其中 `accessibility.cy.ts` 27 個，critical／serious 違規皆為 0）。
- `npx nx lint admin`、`npx nx build admin`：通過。
- **`libs/theme-pack` 與 `libs/ui` 有既存的失敗 spec**（`theme-pack` 3 / 7 失敗、`ui` 1 / 90 失敗），與這個 Demo 無關，也不在 `admin` 的測試目標內。跑全 workspace 的 `npx nx run-many -t test` 會看到它們失敗；驗收這個 Demo 時請只跑 `admin` 與 `admin-e2e` 兩個目標。
