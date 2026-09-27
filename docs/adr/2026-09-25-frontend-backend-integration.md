# ADR｜前後端整合：同一個 monorepo、OpenAPI 產生型別、AG-UI 串流

**狀態：** 已確認（2026-09-25）；2026-09-27 修訂後端的 AG-UI 實作方式（見文末「修訂」）

## 決策

- 後端放在本 Nx monorepo 的 `apps/api`，與 Angular 前端同一個 repo。
- API 規格由 .NET 10 內建功能產生 OpenAPI 文件，再自動產生前端 TypeScript 型別；CI 檢查產生結果沒有未提交的差異。前端 `DemoRepository` 依交接文件的順序（知識庫 → 數據庫 → 發布 → 對話）逐區換成真實 API。
- 對話回覆以 **AG-UI 協定**（SSE）串流，後端以 AG-UI 官方 .NET SDK（`AGUI.*`）輸出事件；前端只使用 `@ag-ui/client`（MIT），**不使用** CopilotKit 的畫面元件，沿用現有對話畫面。引用、表單請求、提交回執等以 AG-UI 自訂事件傳送，對應既有五種回覆類型。
- 暫不使用 SignalR；等需要伺服器主動推播（例如文件處理完成）時再評估。

## 理由

同一個 PR 可同時修改前後端，型別由規格產生可防止兩邊不一致。AG-UI 是標準協定且 Agent Framework 內建支援，後端幾乎不必自訂串流格式；只取用戶端套件可保留既有 UI，不被第三方元件綁住。

## 考慮過的選項

- **自訂 SSE 事件格式**：可行，但要自己維護協定與前後端解析。
- **CopilotKit Angular 元件**：會取代現有對話畫面與五種回覆的呈現方式。
- **後端獨立 repo**：跨 repo 同步規格與版本成本較高。

## 修訂（2026-09-27）

原決策寫的是「後端使用 Agent Framework 的 AG-UI hosting」。M3 規劃時查證（[M3 計畫](../plans/2026-09-27-backend-milestone-3-in-platform-chat.md) §2.1）：`Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` 仍只有 preview 版，而且 API 名稱已經改過一次（`MapAGUI` → `MapAGUIServer`），不符合 [後端技術棧 ADR](2026-09-25-backend-stack.md)「核心只使用正式版套件」。AG-UI 協定組織在 2026-09 發布了正式版 .NET SDK（`AGUI.Abstractions`、`AGUI.Formatting`、`AGUI.Server` 1.0.0，MIT），`AGUI.Server` 可以直接把 `IChatClient` 的串流轉成 AG-UI 事件。

因此改為：Api 層以 Minimal API 自寫端點，body 用 `RunAgentInput`，以 `AGUI.Formatting` 輸出 SSE。事件格式與官方 hosting 相同，前端仍只用 `@ag-ui/client`。Agent Framework 的 hosting 轉為正式版後，可以只替換 Api 層的端點，Application 層不受影響。
