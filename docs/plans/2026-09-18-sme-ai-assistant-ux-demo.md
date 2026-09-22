# 中小企業 AI 助理前端 Demo Implementation Plan

**Goal:** 建立一套可在桌面與手機操作的 AI 助理前端 Demo，完整演示多知識庫／多資料庫、可信回答、追蹤資料、帳號隔離及三種發布管道，並交付正式後端與外部服務介接 hand-off。

**Architecture:** 在專案根目錄新增獨立的 Nx／Angular 前端工作區 `frontend/`。畫面只依賴 typed repository interfaces；第一階段以 in-memory mock adapters、固定 seed data 與可控延遲／錯誤情境驅動畫面，未來後端完成後可替換 adapter，而不重寫 feature components。Demo 的帳號隔離與權限只用於視覺驗證，不宣稱具備正式安全性。

**Tech Stack:** Angular 19+ standalone components、Nx、Angular Router、Angular Material primitives、自訂 SCSS design tokens、Angular signals、Jest、Cypress、cypress-axe。

---

> **2026-09-22 決策更新（覆蓋下方路徑）**：獨立的 `frontend/` 工作區已廢止，Demo 已併入 `apps/admin`（見 commit `1fd2d95`），`apps/admin` 為唯一前端入口，不再維護 `frontend/`。閱讀下列 tasks 時請套用路徑對照：
> - `frontend/apps/assistant-demo/src/` → `apps/admin/src/`
> - `frontend/apps/assistant-demo-e2e/` → `apps/admin-e2e/`
> - `cd frontend && npx nx test assistant-demo --runInBand` → `npx nx test admin`（Angular unit-test builder + Vitest）
> - `npx nx e2e assistant-demo-e2e --spec=...` → `npx nx e2e admin-e2e --spec=...`
> - `npx nx build assistant-demo` → `npx nx build admin`；`frontend/README.md` → `apps/admin/README.md`

## 執行前提

- 產品與 UX 決策以 `docs/plans/2026-09-18-sme-ai-assistant-ux-demo-design.md` 為準。
- 目前專案根目錄尚未初始化 Git。開始實作前需先由專案擁有者決定是否在此目錄建立 Git repository；以下每一項 commit 步驟都以 repository 已存在為前提。
- 不建立後端、不連接真實 AI、不傳送真實文件、不保存真實個資。
- Demo 介面暫用中性產品名稱「AI 助理工作台」，品牌名稱與 Logo 保持可替換。
- 實作視覺前使用 `@design-taste-frontend`，互動與無障礙檢查使用 `@ux-guidelines`；每個功能依 `@superpowers:test-driven-development` 先寫測試。

## 預定路由

```text
/                         未登入產品首頁
/login                    Demo 帳號選擇
/app/home                 登入後首頁
/app/assistants           助理列表
/app/assistants/new/:step 建立助理精靈
/app/assistants/:id/:tab  助理詳情
/app/knowledge            知識庫列表
/app/knowledge/:id        知識庫詳情
/app/databases            資料庫列表
/app/databases/:id        資料庫詳情
/app/activity             對話與回報摘要
/app/channels             發布管道總覽
/app/settings             帳號與設定
/use/:assistantId         終端使用者助理頁
```

### Task 1: 建立 Angular／Nx Demo 工作區

**Files:**
- Create: `frontend/`
- Create: `frontend/apps/assistant-demo/src/app/`
- Create: `frontend/apps/assistant-demo-e2e/`
- Modify: `frontend/package.json`
- Modify: `frontend/apps/assistant-demo/src/styles.scss`

**Step 1: 建立失敗基準**

Run: `test -f frontend/package.json`

Expected: FAIL，因為前端工作區尚不存在。

**Step 2: 建立工作區**

Run:

```bash
npx create-nx-workspace@latest frontend --preset=angular-monorepo --appName=assistant-demo --bundler=esbuild --style=scss --routing --e2eTestRunner=cypress --unitTestRunner=jest --ssr=false --nxCloud=skip --interactive=false
```

Expected: 產生可執行的 Angular app 與 Cypress project。

**Step 3: 安裝 UI 與測試依賴**

Run:

```bash
cd frontend
npm install @angular/material @angular/cdk @angular/animations
npm install -D cypress-axe axe-core
```

Expected: 依賴寫入 `frontend/package.json`，無 unresolved peer dependency。

**Step 4: 建立最小 smoke test**

在 app 測試中斷言 root component 能渲染「AI 助理工作台」，並在 Cypress 首頁測試中斷言 `/` 可開啟。

**Step 5: 執行基準驗證**

Run:

```bash
cd frontend
npx nx test assistant-demo
npx nx e2e assistant-demo-e2e --spec=**/app.cy.ts
```

Expected: unit 與 E2E 全部 PASS。

**Step 6: Commit**

```bash
git add frontend
git commit -m "chore: scaffold AI assistant demo frontend"
```

### Task 2: 建立視覺基礎與共用 UI 元件

**Files:**
- Create: `frontend/apps/assistant-demo/src/styles/_tokens.scss`
- Create: `frontend/apps/assistant-demo/src/styles/_utilities.scss`
- Create: `frontend/apps/assistant-demo/src/app/shared/ui/status-badge/`
- Create: `frontend/apps/assistant-demo/src/app/shared/ui/source-type-icon/`
- Create: `frontend/apps/assistant-demo/src/app/shared/ui/empty-state/`
- Create: `frontend/apps/assistant-demo/src/app/shared/ui/page-header/`
- Create: `frontend/apps/assistant-demo/src/app/shared/ui/state-panel/`
- Test: `frontend/apps/assistant-demo/src/app/shared/ui/**/*.spec.ts`

**Step 1: 撰寫元件失敗測試**

測試至少涵蓋：狀態徽章不只用顏色傳達狀態、資料來源圖示包含文字 label、空白狀態可傳入主要行動、state panel 可顯示錯誤原因與修復按鈕。

**Step 2: 執行測試確認失敗**

Run: `cd frontend && npx nx test assistant-demo --runInBand`

Expected: FAIL，因為共用元件尚未建立。

**Step 3: 建立 design tokens**

定義中性色、品牌強調色、成功／警告／錯誤色、字級、間距、圓角、邊框、焦點環與內容寬度。採清楚、溫和、非科技炫光的 B2B 風格；禁止把狀態只綁定顏色。

**Step 4: 實作共用元件**

以 Angular standalone components 完成最小 API，所有互動元件支援鍵盤與可見焦點。

**Step 5: 驗證**

Run: `cd frontend && npx nx test assistant-demo --runInBand`

Expected: PASS。

**Step 6: Commit**

```bash
git add frontend/apps/assistant-demo/src
git commit -m "feat: add demo design system primitives"
```

### Task 3: 定義 domain models、repository contracts 與 mock data

**Files:**
- Create: `frontend/apps/assistant-demo/src/app/core/domain/account.model.ts`
- Create: `frontend/apps/assistant-demo/src/app/core/domain/assistant.model.ts`
- Create: `frontend/apps/assistant-demo/src/app/core/domain/knowledge-base.model.ts`
- Create: `frontend/apps/assistant-demo/src/app/core/domain/database.model.ts`
- Create: `frontend/apps/assistant-demo/src/app/core/domain/conversation.model.ts`
- Create: `frontend/apps/assistant-demo/src/app/core/domain/publishing.model.ts`
- Create: `frontend/apps/assistant-demo/src/app/core/data/demo-repository.ts`
- Create: `frontend/apps/assistant-demo/src/app/core/data/mock-demo-repository.ts`
- Create: `frontend/apps/assistant-demo/src/app/core/data/demo-seed.ts`
- Create: `frontend/apps/assistant-demo/src/app/core/session/demo-session.service.ts`
- Test: `frontend/apps/assistant-demo/src/app/core/data/mock-demo-repository.spec.ts`
- Test: `frontend/apps/assistant-demo/src/app/core/session/demo-session.service.spec.ts`

**Step 1: 撰寫資料隔離失敗測試**

涵蓋：帳號 A 看不到帳號 B 的助理設定與對話；助理擁有者看不到其他帳號的對話文字；已同意提交的結構化紀錄可被指定資料管理者讀取；一個助理可以連接多個不同類型來源。

**Step 2: 執行測試確認失敗**

Run: `cd frontend && npx nx test assistant-demo --runInBand`

Expected: FAIL，因為 models 與 repository 尚不存在。

**Step 3: 建立純前端 contracts**

所有 id、狀態、權限與來源類型使用明確 union types；repository 回傳 immutable view models。不得在 component 內直接 import seed data。

**Step 4: 建立三組示範帳號與資料**

- 中小企業管理者：擁有客服助理、三個知識庫與兩個資料庫。
- 一般內部使用者：只能使用被分享的助理，保有自己的對話。
- 外部客戶：可填寫已授權表單，只能查看自己的追蹤資料。

加入可切換的 loading、partial failure、permission denied 與 disconnected channel 情境。

**Step 5: 驗證**

Run: `cd frontend && npx nx test assistant-demo --runInBand`

Expected: PASS，並證明 mock 層符合正式權限邊界。

**Step 6: Commit**

```bash
git add frontend/apps/assistant-demo/src/app/core
git commit -m "feat: add isolated mock domain and repositories"
```

### Task 4: 建立公開首頁、Demo 登入與應用框架

**Files:**
- Create: `frontend/apps/assistant-demo/src/app/features/landing/`
- Create: `frontend/apps/assistant-demo/src/app/features/demo-login/`
- Create: `frontend/apps/assistant-demo/src/app/layout/app-shell/`
- Create: `frontend/apps/assistant-demo/src/app/layout/app-navigation/`
- Modify: `frontend/apps/assistant-demo/src/app/app.routes.ts`
- Test: `frontend/apps/assistant-demo/src/app/features/demo-login/demo-login.component.spec.ts`
- Test: `frontend/apps/assistant-demo-e2e/src/e2e/navigation.cy.ts`

**Step 1: 撰寫路由與登入失敗測試**

驗證未登入首頁沒有公開試用與模板瀏覽；選擇 Demo 帳號後進入 `/app/home`；切換帳號會清除前一帳號的畫面狀態。

**Step 2: 執行確認失敗**

Run: `cd frontend && npx nx test assistant-demo --runInBand`

Expected: FAIL。

**Step 3: 實作公開首頁與 Demo 帳號選擇**

首頁只含價值、適用情境、可信回答與隱私說明、登入／申請入口。登入頁清楚標示「Demo 帳號切換，不是真實驗證」。

**Step 4: 實作響應式 app shell**

桌面採側邊導覽，手機採精簡 header 與 drawer；導覽項目使用核准的中文名稱。

**Step 5: 驗證**

Run:

```bash
cd frontend
npx nx test assistant-demo --runInBand
npx nx e2e assistant-demo-e2e --spec=**/navigation.cy.ts
```

Expected: PASS。

**Step 6: Commit**

```bash
git add frontend/apps/assistant-demo
git commit -m "feat: add landing login and responsive app shell"
```

### Task 5: 建立首頁、助理列表與詳情框架

**Files:**
- Create: `frontend/apps/assistant-demo/src/app/features/home/`
- Create: `frontend/apps/assistant-demo/src/app/features/assistants/assistant-list/`
- Create: `frontend/apps/assistant-demo/src/app/features/assistants/assistant-detail/`
- Create: `frontend/apps/assistant-demo/src/app/features/assistants/components/assistant-card/`
- Test: `frontend/apps/assistant-demo/src/app/features/assistants/**/*.spec.ts`
- Test: `frontend/apps/assistant-demo-e2e/src/e2e/assistant-list.cy.ts`

**Step 1: 撰寫失敗測試**

驗證首頁包含建立助理、繼續設定、待處理事項；助理卡片只顯示名稱、用途、狀態、使用對象、發布管道與最近活動；其他帳號擁有的助理不出現。

**Step 2: 執行確認失敗**

Run: `cd frontend && npx nx test assistant-demo --runInBand`

Expected: FAIL。

**Step 3: 實作頁面與詳情 tabs**

詳情建立「概覽｜資料來源｜回答與記錄｜測試｜發布｜使用紀錄」頁籤框架，先使用 placeholder panels，後續 tasks 逐一替換。

**Step 4: 驗證**

Run: `cd frontend && npx nx e2e assistant-demo-e2e --spec=**/assistant-list.cy.ts`

Expected: PASS。

**Step 5: Commit**

```bash
git add frontend/apps/assistant-demo
git commit -m "feat: add assistant dashboard and detail shell"
```

### Task 6: 實作四步驟建立助理精靈

**Files:**
- Create: `frontend/apps/assistant-demo/src/app/features/assistants/assistant-wizard/`
- Create: `frontend/apps/assistant-demo/src/app/features/assistants/assistant-wizard/steps/purpose-step/`
- Create: `frontend/apps/assistant-demo/src/app/features/assistants/assistant-wizard/steps/sources-step/`
- Create: `frontend/apps/assistant-demo/src/app/features/assistants/assistant-wizard/steps/rules-step/`
- Create: `frontend/apps/assistant-demo/src/app/features/assistants/assistant-wizard/steps/test-step/`
- Create: `frontend/apps/assistant-demo/src/app/features/assistants/assistant-wizard/assistant-draft.store.ts`
- Test: `frontend/apps/assistant-demo/src/app/features/assistants/assistant-wizard/**/*.spec.ts`
- Test: `frontend/apps/assistant-demo-e2e/src/e2e/create-assistant.cy.ts`

**Step 1: 撰寫完整建立流程失敗測試**

從「回答客戶問題」模板開始，設定內外部使用、同時連接兩個知識庫與兩個資料庫、保留嚴格回答、完成試問並建立助理。另測試重新整理後能從 mock draft 繼續。

**Step 2: 執行確認失敗**

Run: `cd frontend && npx nx e2e assistant-demo-e2e --spec=**/create-assistant.cy.ts`

Expected: FAIL。

**Step 3: 實作 draft store 與步驟導航**

每一步只驗證必要欄位；可返回修改；自動保存狀態必須在畫面可見。進階角色指令預設收合。

**Step 4: 實作混合資料來源選擇器**

同一清單顯示知識庫與資料庫，以類型、權限、狀態與更新時間辨識；加入只建立 connection id。

**Step 5: 實作規則與測試預覽**

以白話控制嚴格／一般知識、拒答文案、引用、保存自己的對話、資料寫入目的與定期回報。

**Step 6: 驗證**

Run:

```bash
cd frontend
npx nx test assistant-demo --runInBand
npx nx e2e assistant-demo-e2e --spec=**/create-assistant.cy.ts
```

Expected: PASS，核心流程在預設 fixture 下可於 10 分鐘內完成。

**Step 7: Commit**

```bash
git add frontend/apps/assistant-demo
git commit -m "feat: add guided assistant creation flow"
```

### Task 7: 實作知識庫管理畫面

**Files:**
- Create: `frontend/apps/assistant-demo/src/app/features/knowledge/knowledge-list/`
- Create: `frontend/apps/assistant-demo/src/app/features/knowledge/knowledge-detail/`
- Create: `frontend/apps/assistant-demo/src/app/features/knowledge/components/document-row/`
- Create: `frontend/apps/assistant-demo/src/app/features/knowledge/components/sharing-panel/`
- Test: `frontend/apps/assistant-demo/src/app/features/knowledge/**/*.spec.ts`
- Test: `frontend/apps/assistant-demo-e2e/src/e2e/knowledge.cy.ts`

**Step 1: 撰寫狀態與權限失敗測試**

涵蓋五種文件狀態、部分失敗不阻擋其他文件、三種分享範圍、連接多個助理及無權限時不洩漏資源名稱。

**Step 2: 執行確認失敗**

Run: `cd frontend && npx nx test assistant-demo --runInBand`

Expected: FAIL。

**Step 3: 實作列表與詳情**

詳情 tabs 為「內容｜已連接助理｜分享權限」。檔案加入操作只模擬狀態進度，明確標示不會真正上傳。

**Step 4: 驗證與 Commit**

Run: `cd frontend && npx nx e2e assistant-demo-e2e --spec=**/knowledge.cy.ts`

Expected: PASS。

```bash
git add frontend/apps/assistant-demo
git commit -m "feat: add knowledge base demo screens"
```

### Task 8: 實作資料庫、表單與追蹤畫面

**Files:**
- Create: `frontend/apps/assistant-demo/src/app/features/databases/database-list/`
- Create: `frontend/apps/assistant-demo/src/app/features/databases/database-detail/`
- Create: `frontend/apps/assistant-demo/src/app/features/databases/form-designer/`
- Create: `frontend/apps/assistant-demo/src/app/features/databases/records-table/`
- Create: `frontend/apps/assistant-demo/src/app/features/databases/trend-view/`
- Create: `frontend/apps/assistant-demo/src/app/shared/ui/trend-chart/`
- Test: `frontend/apps/assistant-demo/src/app/features/databases/**/*.spec.ts`
- Test: `frontend/apps/assistant-demo-e2e/src/e2e/tracking.cy.ts`

**Step 1: 撰寫追蹤流程失敗測試**

驗證可從模板建立資料庫、修改常見欄位、試填、查看時間軸、本次／上次／首次比較；不足兩筆紀錄時不顯示趨勢結論。

**Step 2: 執行確認失敗**

Run: `cd frontend && npx nx e2e assistant-demo-e2e --spec=**/tracking.cy.ts`

Expected: FAIL。

**Step 3: 實作資料庫詳情 tabs**

完成「表單設計｜收集紀錄｜趨勢比較｜已連接助理｜權限」。第一版只支援文字、數字、日期、單選、多選與量尺欄位，不加入條件跳題或公式。

**Step 4: 實作無第三方圖表依賴的簡單趨勢圖**

使用可存取的 SVG 或 HTML 呈現，並提供等價文字摘要與資料表。Demo 的差異值由 fixture 預先計算，不在 component 內自行推導業務結果。

**Step 5: 驗證與 Commit**

Run:

```bash
cd frontend
npx nx test assistant-demo --runInBand
npx nx e2e assistant-demo-e2e --spec=**/tracking.cy.ts
```

Expected: PASS。

```bash
git add frontend/apps/assistant-demo
git commit -m "feat: add structured data and tracking demo"
```

### Task 9: 實作終端對話、引用與同意流程

**Files:**
- Create: `frontend/apps/assistant-demo/src/app/features/assistant-use/chat-shell/`
- Create: `frontend/apps/assistant-demo/src/app/features/assistant-use/message/`
- Create: `frontend/apps/assistant-demo/src/app/features/assistant-use/citation-drawer/`
- Create: `frontend/apps/assistant-demo/src/app/features/assistant-use/inline-form/`
- Create: `frontend/apps/assistant-demo/src/app/features/assistant-use/consent-confirmation/`
- Test: `frontend/apps/assistant-demo/src/app/features/assistant-use/**/*.spec.ts`
- Test: `frontend/apps/assistant-demo-e2e/src/e2e/private-conversations.cy.ts`
- Test: `frontend/apps/assistant-demo-e2e/src/e2e/consented-submission.cy.ts`

**Step 1: 撰寫信任狀態失敗測試**

涵蓋：公司資料回答可展開引用；一般知識獨立標示；查無資料顯示下一步；不同帳號看不到彼此對話；助理擁有者也不能查看使用者對話文字。

**Step 2: 撰寫同意流程失敗測試**

未勾選同意不得提交；同意畫面必須顯示接收者、用途、可查看者與敏感資料提示；提交後只有指定資料管理者可在結構化紀錄中看到資料。

**Step 3: 執行確認失敗**

Run:

```bash
cd frontend
npx nx e2e assistant-demo-e2e --spec=**/private-conversations.cy.ts
npx nx e2e assistant-demo-e2e --spec=**/consented-submission.cy.ts
```

Expected: FAIL。

**Step 4: 實作對話與同意元件**

所有回覆來自 fixtures；輸入新問題時從預定 response map 取回結果。未知問題回覆 Demo 拒答，不模擬真正 LLM。

**Step 5: 驗證與 Commit**

Run: `cd frontend && npx nx e2e assistant-demo-e2e --spec=**/private-conversations.cy.ts --spec=**/consented-submission.cy.ts`

Expected: PASS。

```bash
git add frontend/apps/assistant-demo
git commit -m "feat: add private chat and consented data flows"
```

### Task 10: 實作三種發布管道 Demo

**Files:**
- Create: `frontend/apps/assistant-demo/src/app/features/publishing/channel-overview/`
- Create: `frontend/apps/assistant-demo/src/app/features/publishing/platform-sharing/`
- Create: `frontend/apps/assistant-demo/src/app/features/publishing/website-embed/`
- Create: `frontend/apps/assistant-demo/src/app/features/publishing/line-setup/`
- Test: `frontend/apps/assistant-demo/src/app/features/publishing/**/*.spec.ts`
- Test: `frontend/apps/assistant-demo-e2e/src/e2e/publishing.cy.ts`

**Step 1: 撰寫發布狀態失敗測試**

驗證三張管道卡片、五種統一狀態、平台帳號限制、官網桌機／手機預覽、允許網域、LINE 欄位逐項檢查與測試訊息；單一管道故障不影響其他管道。

**Step 2: 執行確認失敗**

Run: `cd frontend && npx nx e2e assistant-demo-e2e --spec=**/publishing.cy.ts`

Expected: FAIL。

**Step 3: 實作管道設定**

嵌入碼、LINE token 與驗證結果皆為不可用於正式環境的示範資料。敏感欄位預設遮蔽，畫面明確標記「Demo，不會連接外部服務」。

**Step 4: 驗證與 Commit**

Run: `cd frontend && npx nx e2e assistant-demo-e2e --spec=**/publishing.cy.ts`

Expected: PASS。

```bash
git add frontend/apps/assistant-demo
git commit -m "feat: add publishing channel demos"
```

### Task 11: 補齊錯誤情境、響應式與無障礙驗證

**Files:**
- Create: `frontend/apps/assistant-demo-e2e/src/e2e/error-states.cy.ts`
- Create: `frontend/apps/assistant-demo-e2e/src/e2e/responsive.cy.ts`
- Create: `frontend/apps/assistant-demo-e2e/src/e2e/accessibility.cy.ts`
- Modify: `frontend/apps/assistant-demo/src/app/**/*.html`
- Modify: `frontend/apps/assistant-demo/src/app/**/*.scss`

**Step 1: 撰寫失敗 E2E 測試**

逐一檢查空白、載入、成功、部分成功、無結果、權限不足、處理失敗、連線失敗、登入逾時與帳號切換。以 360px 手機與 1280px 桌面 viewport 跑核心流程。

**Step 2: 執行 axe 與響應式測試確認問題**

Run:

```bash
cd frontend
npx nx e2e assistant-demo-e2e --spec=**/accessibility.cy.ts
npx nx e2e assistant-demo-e2e --spec=**/responsive.cy.ts
```

Expected: 初次執行會列出尚未修正的可存取性或版面問題。

**Step 3: 修正所有 critical／serious 問題**

包含 landmark、標籤、焦點順序、dialog focus trap、鍵盤操作、錯誤摘要、對比與 reduced motion。

**Step 4: 驗證全部狀態**

Run:

```bash
cd frontend
npx nx test assistant-demo --runInBand
npx nx e2e assistant-demo-e2e
```

Expected: 全部 PASS，無 critical／serious axe violation。

**Step 5: Commit**

```bash
git add frontend/apps
git commit -m "test: cover responsive accessible demo states"
```

### Task 12: 撰寫正式介接 hand-off

**Files:**
- Create: `docs/handoff/ai-assistant-backend-integration-handoff.md`
- Create: `docs/handoff/route-screen-matrix.md`
- Create: `docs/handoff/mock-to-api-mapping.md`

**Step 1: 先建立 hand-off 驗收清單**

清單必須涵蓋：路由、元件狀態、domain 關係、權限、分頁、上傳處理、回答與引用、拒答、一般知識標記、表單同意、追蹤比較、登入、LINE、網站嵌入、錯誤訊息與 mock replacement。

**Step 2: 由現有 TypeScript contracts 產生欄位對照**

逐一列出 request／response 範例。不得把 fixture shape 當成已定案 API；每一節標示必填欄位、可選欄位、狀態列舉與前端依賴。

**Step 3: 建立 mock-to-API mapping**

對每個 `DemoRepository` method 列出建議 endpoint、認證需求、成功狀態、可恢復錯誤、不可恢復錯誤與前端替換檔案。

**Step 4: 進行 hand-off 自查**

Run:

```bash
rg -n "TODO|TBD|FIXME" docs/handoff
rg -n "DemoRepository|MockDemoRepository" frontend/apps/assistant-demo/src/app docs/handoff
```

Expected: 第一個指令無未解決項目；第二個指令中的每個 repository method 都能在 mapping 找到對應說明。

**Step 5: Commit**

```bash
git add docs/handoff
git commit -m "docs: add backend and channel integration handoff"
```

### Task 13: 最終 Demo 驗證與交付說明

**Files:**
- Create: `frontend/README.md`
- Create: `docs/demo/demo-script.md`
- Modify: `docs/plans/2026-09-18-sme-ai-assistant-ux-demo-design.md`

**Step 1: 撰寫五到十分鐘展示腳本**

腳本依序演示：切換管理者帳號、建立助理、混接來源、可信回答、同意提交、帳號隔離、追蹤趨勢、官網與 LINE 發布狀態。

**Step 2: 撰寫啟動與限制說明**

`frontend/README.md` 必須包含安裝、啟動、測試、Demo 帳號、fixture 情境、已知限制與「不可輸入真實敏感資料」警告。

**Step 3: 執行完整驗證**

Run:

```bash
cd frontend
npx nx test assistant-demo --runInBand
npx nx e2e assistant-demo-e2e
npx nx build assistant-demo
```

Expected: unit、E2E、build 全部成功。

**Step 4: 依驗收標準手動走查**

使用桌面與手機 viewport 各完整執行一次展示腳本，確認沒有死路、截斷文字、不可回復狀態或跨帳號資料殘留。

**Step 5: Commit**

```bash
git add frontend/README.md docs/demo docs/plans/2026-09-18-sme-ai-assistant-ux-demo-design.md
git commit -m "docs: finalize demo delivery and walkthrough"
```

## 完成定義

- 所有 unit tests、Cypress E2E 與 production build 通過。
- Demo 可完整演示已核准的核心流程與主要錯誤狀態。
- 不會把模擬登入、模擬權限或 mock 資料誤呈現為正式安全功能。
- 所有真實後端與外部介接缺口都能在 hand-off 文件找到對應 contract 與替換位置。
- 沒有未解決的 TODO／TBD／FIXME。
