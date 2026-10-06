# widget

嵌在客戶網站 iframe 裡的訪客對話視窗（M5a Slice 8，#199）。standalone、zoneless，沒有 router、Angular Material、Tailwind、oidc；只依賴 `@smart-agri/chat`（`scope:widget` 只能用 `scope:chat`）。計畫見 `docs/plans/2026-10-06-backend-milestone-5a-website-embed.md` 第 3 節 A、D、E。載入器與 widget 的契約見 `apps/embed-loader/README.md`。

指令：`nx test widget`、`nx lint widget`、`nx build widget`（輸出 `dist/smart-agri-widget/browser/`）、`tsc -p apps/widget/tsconfig.spec.json --noEmit`。

## 給 API（#201）的提供方式

| 項目 | 要求 |
| --- | --- |
| 頁面網址 | `GET {前綴}/use/{assistantId}?host={嵌入頁面的 origin}`，回 `dist/smart-agri-widget/browser/index.html`（每次請求都送，不快取）。`{前綴}` 可以是空字串，也可以是 API 掛的路徑前綴：widget 取 `location.pathname` 最後的 `/use/{id}`，其前面的部分當成 API 前綴，所有 API 網址都是同源相對網址 `{前綴}/api/v1/public/…`。 |
| `<base href>` | production 建置固定 `/widget/`。所以資產要掛在 `/widget/*`（根路徑，不含前綴）；API 掛在路徑前綴下時要自行改 `baseHref` 重新建置（`--base-href`）。 |
| 靜態資產 | `/widget/main-*.js`、`chunk-*.js`（含延後載入的 `@ag-ui/client`）、`styles-*.css`，檔名含雜湊，可長期快取（`immutable`）。 |
| 沒有 inline script／handler | production 建置關掉 critical CSS 內嵌（`optimization.styles.inlineCritical: false`），`index.html` 只有 `<link rel="stylesheet">`、`<link rel="modulepreload">` 與 `<script type="module" src>`；沒有 `onload` 屬性、沒有 inline `<script>`。 |
| **CSP 的 `style-src`** | **`default-src 'self'` 不夠。** Angular 在執行期用 `<style>` 元素注入元件樣式（含 `libs/chat` 的元件），在只有 `default-src 'self'` 時會被擋，畫面完全沒有樣式（已在瀏覽器驗證，console 為 `Applying inline style violates … 'default-src 'self''`）。**API（#201）採用 nonce**：`index.html` 逐請求輸出，`<app-root ngCspNonce="{nonce}">` 帶入隨機 nonce，標頭是 `default-src 'self'; script-src 'self'; style-src 'self' 'nonce-{nonce}'; connect-src 'self'; img-src 'self' data:; base-uri 'self'; form-action 'none'; frame-ancestors …`，不開 `unsafe-inline`（見 `apps/api/README.md`「Serving the chat window and `embed.js`」）。 |
| 其他連線 | 只有同源的 `fetch`（`connect-src 'self'` 即可）：沒有字型、圖片、第三方網址。品牌色用 CSSOM（`style.setProperty`）設定，不寫 `style` 屬性。 |
| 不用 cookie | 所有 `fetch` 都是 `credentials: 'omit'`；訪客 token 與前文只放 iframe 自己的 `sessionStorage`。 |

## 訪客 API 契約（widget 端實作，後端是 #196）

- `POST {前綴}/api/v1/public/assistants/{id}/visitor-sessions`，本文 `{ "host": <?host 驗證過的 origin 或 null> }` → `201 { token, expiresAt, assistant: { displayName, welcomeMessage, brandColor, showCitations } }`；`403`（任何不在服務中的狀態）→「這個對話視窗目前無法使用」；`429` + `Retry-After` → 倒數後可重試。
- `POST …/chat/runs`，標頭 `Authorization: Visitor <token>`，本文 AG-UI `RunAgentInput`（`threadId: ''`、`messages` = 最多 20 則前文 + 這一題）；回應 SSE，只有一個自訂事件 `smartagri.reply`。開始串流前：`401`（token 過期：重新建立工作階段一次並重送同一題，第二次還是 `401` 才報錯）、`403`（「目前暫停服務」）、`409`、`422`（顯示伺服器的說明）、`429`（「問題太頻繁了，請稍後再試」＋倒數）、`503`；串流中 `RUN_ERROR`。
- `brandColor` 線上值：`forest`／`ocean`／`amber`／`plum`（admin 的 `WebsiteBrandColor`）；不認得的值用 `forest`。`brandColor`、顯示名稱、歡迎訊息只來自工作階段回應。

## 與 `libs/chat` 的接法

`AgUiChatRunner` 以 `runsPath`（訪客路徑）、`authorizationHeader`（`Visitor <token>`；#199 新增的向下相容選項，`accessToken` 仍包成 `Bearer`）與包過的 `fetch` 建立。`@ag-ui/client` 維持動態 import，送出第一個問題時才載入（獨立 chunk）。`429` 的 `Retry-After` 無法從 `@ag-ui/client` 的錯誤取得，所以由包過的 `fetch` 記下最近一次回應狀態。

## 預算

`project.json` 的 production 預算：初始 `maximumWarning 120kB`／`maximumError 150kB`。目前的量測值見 PR 說明：Angular zoneless 的 hello world 單獨就是 96 kB（原始大小），預算實際上無法達成。
