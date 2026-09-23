# 中小企業 AI 助理 UX Demo（`apps/admin`）

這是「中小企業 AI 助理」的**前端畫面 Demo**。它示範完整的操作流程與畫面狀態，但**沒有後端、沒有真實 AI、沒有真實驗證**。

> ⚠️ **請勿在這個 Demo 輸入任何真實的敏感資料。**
> 不要輸入真實姓名、身分證字號、電話、地址、病歷、訂單編號、信用卡號、密碼、API 金鑰或 LINE 權杖。
> 所有輸入都會以明文存進**你自己瀏覽器的 `localStorage`**，任何能打開這台電腦瀏覽器的人都讀得到，而且這裡的「登入」與「權限」都只是畫面模擬，不具備任何安全性。
> 畫面上也有同樣的聲明：`apps/admin/src/app/core/repositories/demo-repository.ts:473`。

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

`/login` 沒有帳號密碼，只是三顆切換身分的按鈕。身分定義在 `apps/admin/src/app/core/repositories/demo-seed.ts:72`。

| 按鈕 | 內部 accountId | 這個身分看得到什麼 |
| --- | --- | --- |
| **SMB 管理者**（安心商行管理者） | `account-smb-admin` | 完整工作台。擁有 2 個既有助理（客服助理、內部教育訓練助理）、3 個知識庫、2 個資料庫；可以建立助理、管理知識庫與資料庫、設定平台內／官網／LINE 三種發布管道，並查看使用者**同意提交**的結構化紀錄與趨勢比較。 |
| **內部使用者**（安心商行客服同仁） | `account-internal-employee` | 只能使用團隊分享給他的助理（客服助理）。擁有自己的「同仁個人筆記」知識庫與「同仁排班回報」資料庫。看不到管理者的助理設定、其他知識庫與發布管道。 |
| **外部客戶** | `account-external-customer` | 只能開啟開放給外部客戶的助理並對話、填寫授權表單、查看自己的追蹤紀錄。`我的助理`／`知識庫`／`資料庫`／`發布管道` 都會顯示「你還沒有…」或「只有助理擁有者可以設定」的空白狀態，看不到管理者的任何資料。 |

**對話紀錄依帳號隔離。** 每個帳號的對話存在各自的 `localStorage` key（`sme-demo:chat:<accountId>:<assistantId>`），切換身分後看不到前一個身分的對話。這是畫面上的隔離示範，**不是安全邊界**。

### 誰可以在平台內開啟一個助理

判斷只有一個地方：`canOpenInPlatform()`（`apps/admin/src/app/core/repositories/publishing-channels.ts:99-108`），由 `canUseAssistant()` 呼叫（`core/repositories/mock-demo-repository.ts:2594-2604`）。規則是：

1. **擁有者永遠開得了**自己的助理，包含清單是空的、平台內分享已暫停時，這樣他才能自己測試。
2. 其他帳號要**同時**滿足兩個條件：
   - **使用對象（`audience`）決定「哪一種人」**——角色要落在 `AUDIENCE_ROLES` 內（`core/domain/assistant.model.ts:28-36`）。
   - **發布管道「平台內分享」的勾選清單決定「哪些帳號」**——要在 `allowedAccountIds` 內，而且該管道沒有暫停。

`AssistantConfigurationView.sharedWithAccountIds` **不是**第二條授權路徑，它只是這份勾選清單的初始值（`defaultPublishingRecord()`，`publishing-channels.ts:36-56`）；助理一旦有保存的發布設定，就以保存的清單為準。

**取消勾選只收回權限，不會刪掉任何對話。** 該帳號的對話仍留在自己的 `sme-demo:chat:<accountId>:<assistantId>` key 裡，重新勾選後原封不動回來——助理擁有者本來就看不到別人的對話，也不該有一個開關可以單方面銷毀別人的資料。畫面上的說明文字就是這樣寫的（`features/publishing/platform-sharing/platform-sharing.component.html:8-11`）。

**未登入的官網訪客不受這份清單影響**：他們由「官網嵌入或 LINE 是否已發布」決定（`isExternallyPublished()`，`publishing-channels.ts:263-275`），平台內分享不算對外。

種子資料示範了這條規則：內部使用者可以開啟**客服助理**（在清單內、使用對象含內部員工），但開不了**內部教育訓練助理**（它的平台內分享是「已暫停」）。

## Fixture 情境（`?demoScenario=`）

在任何工作台網址後面加上 `?demoScenario=<值>`，可以直接預覽載入中、部分失敗、權限不足等狀態，不需要真的製造錯誤。參數只影響 mock repository，重新整理或換頁時參數消失就恢復正常。

定義：`apps/admin/src/app/core/repositories/demo-scenario-param.ts`、`apps/admin/src/app/core/repositories/demo-repository.ts:65`。

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
- **其餘資料存在 `localStorage`，key 一律以 `sme-demo:` 開頭**（`sme-demo:created-assistants`、`sme-demo:assistant-draft:<accountId>`、`sme-demo:knowledge:<id>`、`sme-demo:created-databases`、`sme-demo:database-fields:<id>`、`sme-demo:chat:<accountId>:<assistantId>`、`sme-demo:publishing:<assistantId>`、`sme-demo:assistant-settings:<assistantId>`、`sme-demo:chat-records`）。清空這些 key 就會回到初始 seed 狀態，清除方式見展示腳本的「事前準備」。

### 功能缺口

- **「對話與回報紀錄」（`/app/activity`）只提供入口說明**，不顯示跨助理的合併清單。依設計，對話屬於發起對話的帳號，管理端只能看匿名統計，所以合併清單要等正式介接後再定義能顯示哪些欄位。
- **「團隊與設定」（`/app/settings`）目前只有外觀（質地／配色）設定**，沒有團隊成員管理。
- **趨勢比較的差異值由 mock repository 計算**（本次／上次／首次、較上次／較首次），不是手寫在 fixture 裡，也不是後端算的。正式版本的計算與四捨五入規則需要後端定義。「定期回報」面板的變化摘要**整段沿用同一份算好的字串**，沒有另一套分析。
- **回答規則只剩「找不到資料時的回覆」還沒接到終端對話。** 其餘規則都會改變行為：嚴格／一般知識（`knowledgeScope`）、保存自己的對話（`keepOwnConversations`）、顯示引用出處（`showCitations`）、定期回報（`periodicReport` 搭配寫入的資料庫）。`refusalMessage` 目前只用在建立精靈的試問預覽，終端對話的「查無資料」仍是 seed 的固定文案。
- **撤回同意可以用了，但稽核軌跡很淺。** 提交者可以在對話的送出收據上撤回自己送出的紀錄：撤回會清掉內容、紀錄立刻離開收集紀錄與趨勢比較，只在收集紀錄留下「提交日期、撤回日期、來源」的軌跡。資料管理者**不能**代為撤回或代為刪除。軌跡沒有操作者、IP 或同意條款版本——mock 存不住，所以也沒有假裝存著。未登入訪客只能在**同一個瀏覽器分頁內**撤回，分頁一關就再也指認不到那筆紀錄，畫面對此直說。

### 測試現況

- `npx nx test admin`：62 個檔案、347 個測試全部通過。
- `npx nx e2e admin-e2e --configuration=production`：15 個 spec、114 個測試全部通過。
- `npx nx lint admin`、`npx nx build admin`：通過。
- **`libs/theme-pack` 與 `libs/ui` 有既存的失敗 spec**（`theme-pack` 3 / 7 失敗、`ui` 1 / 90 失敗），與這個 Demo 無關，也不在 `admin` 的測試目標內。跑全 workspace 的 `npx nx run-many -t test` 會看到它們失敗；驗收這個 Demo 時請只跑 `admin` 與 `admin-e2e` 兩個目標。
