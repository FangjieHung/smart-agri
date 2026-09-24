# ADR｜前後端整合：同一個 monorepo、OpenAPI 產生型別、AG-UI 串流

**狀態：** 已確認（2026-09-25）

## 決策

- 後端放在本 Nx monorepo 的 `apps/api`，與 Angular 前端同一個 repo。
- API 規格由 .NET 10 內建功能產生 OpenAPI 文件，再自動產生前端 TypeScript 型別；CI 檢查產生結果沒有未提交的差異。前端 `DemoRepository` 依交接文件的順序（知識庫 → 數據庫 → 發布 → 對話）逐區換成真實 API。
- 對話回覆以 **AG-UI 協定**（SSE）串流，後端使用 Agent Framework 的 AG-UI hosting；前端只使用 `@ag-ui/client`（MIT），**不使用** CopilotKit 的畫面元件，沿用現有對話畫面。引用、表單請求、提交回執等以 AG-UI 自訂事件傳送，對應既有五種回覆類型。
- 暫不使用 SignalR；等需要伺服器主動推播（例如文件處理完成）時再評估。

## 理由

同一個 PR 可同時修改前後端，型別由規格產生可防止兩邊不一致。AG-UI 是標準協定且 Agent Framework 內建支援，後端幾乎不必自訂串流格式；只取用戶端套件可保留既有 UI，不被第三方元件綁住。

## 考慮過的選項

- **自訂 SSE 事件格式**：可行，但要自己維護協定與前後端解析。
- **CopilotKit Angular 元件**：會取代現有對話畫面與五種回覆的呈現方式。
- **後端獨立 repo**：跨 repo 同步規格與版本成本較高。
