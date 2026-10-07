# embed-loader

手寫、無依賴的 ES2019 載入器 `src/embed.js`（未壓縮 ≤ 5 kB，由單元測試檢查）。客戶網站貼：

```html
<script src="https://<api>/embed.js" data-assistant="<assistantId>" async></script>
```

API（#201）原樣提供 `apps/embed-loader/src/embed.js`；widget（#199，`apps/widget`）依下列契約實作。計畫見 `docs/plans/2026-10-06-backend-milestone-5a-website-embed.md` 第 3 節 A、H。

指令：`nx test embed-loader`、`nx lint embed-loader`、`nx typecheck embed-loader`（`tsc --checkJs`）、`nx build embed-loader`（只複製檔案到 `dist/embed-loader/`）。

## 載入器行為

- **API 基底**：`document.currentScript.src`；取不到時（例如以動態方式載入）退回第一個帶 `data-assistant` 且 `src`（不含 query／hash）以 `/embed.js` 結尾的 `<script>`。基底是 `origin + 路徑去掉結尾的 /embed.js`，所以 API 掛在路徑前綴下也成立。
- **設定屬性**（只讀這三個）：`data-assistant`（必填，沒有就什麼都不做）、`data-position`（`left` 靠左；其他值或未設定為靠右）、`data-brand`（啟動按鈕與其焦點框的顏色，#225）。顯示名稱、歡迎訊息**不**從頁面讀取，由 widget 自己向 API 取得；對話視窗裡的品牌色也只來自 API，`data-brand` 只影響載入器自己畫的啟動按鈕。
- **`data-brand` 對照表**：與 widget 的 `apps/widget/src/app/brand-color.ts`、admin 的 `WEBSITE_BRAND_COLORS`、後端的 `WebsiteBrandColor` 是同一份（`embed.spec.ts` 檢查四處一致）。按鈕圖示是白色，四種顏色與白色的對比都 ≥ 4.5:1（非文字元件的要求是 3:1）。

  | `data-brand` | 顏色 |
  | --- | --- |
  | `forest`（預設） | `#1f6f5c` |
  | `ocean` | `#1d5fa8` |
  | `amber` | `#a3520c` |
  | `plum` | `#6b3fa0` |

  未設定、空字串或不認得的值一律用 `forest`，所以 #225 之前貼上的嵌入程式碼（沒有這個屬性）維持原本的綠色。伺服器產生嵌入程式碼時（`PublicChannelsOptions.EmbedCode`）只在品牌色不是 `forest` 時寫入 `data-brand`，與 `data-position` 一樣：擁有者改了品牌色或位置後，客戶網站上的程式碼要重新複製貼上才會生效。
- **冪等**：以 `window.__smartagriEmbed` 為旗標，同一頁貼多次（含不同助理 id）只初始化第一個。
- **延遲建立 iframe**：第一次開啟時才建立；之後只切換顯示，不重建（widget 的狀態保留到頁面離開）。
- **不使用** cookie、`localStorage`、`sessionStorage`，不發任何網路請求（iframe 除外）。
- 開啟時焦點移到 iframe；關閉（按鈕、`Esc`、收到關閉訊息）時焦點還給啟動按鈕。`Esc` 只在頁面本身有焦點時有效（鍵盤事件不會穿過 iframe），所以 **widget 必須自己處理 `Esc`**，並送 `smartagri:close`。
- 視窗寬度 ≤ 480 px：iframe 全螢幕（`position: fixed; inset: 0`），啟動按鈕隱藏；因此 widget 內必須有看得到的關閉按鈕。
- 桌面：iframe 380 × 600 px（高度上限 `100vh - 108px`），位於按鈕上方。

## iframe 契約

```
{base}/use/{encodeURIComponent(assistantId)}?host={encodeURIComponent(location.origin)}
```

- `host`：嵌入頁面的 `location.origin`（例如 `https://shop.example.com`），供被動安裝偵測使用（計畫第 3 節 H）：widget 建立訪客工作階段時帶上它，伺服器若它屬於允許網域就更新 `LastSeenAt`。**只供參考，不是安全判斷**；實際的網域限制是 `frame-ancestors`。
- 沒有其他 query 參數，沒有 `sandbox`／`allow` 屬性（iframe 本身就是跨來源）。`title="客服對話"`。

## postMessage 契約（widget → 嵌入頁面）

載入器只接受同時滿足下列條件的訊息，其餘一律忽略：

1. `event.origin === new URL(base).origin`；
2. `event.source === iframe.contentWindow`；
3. `event.data` 是物件。

| `data.type` | 動作 |
| --- | --- |
| `smartagri:close` | 關閉視窗，焦點回啟動按鈕 |

widget 送出時請指定目標來源，不要用 `'*'`：`window.parent.postMessage({ type: 'smartagri:close' }, hostOrigin)`，其中 `hostOrigin` 取自 `host` 查詢參數。目前沒有其他訊息類型，載入器也不會傳訊息給 widget；新增訊息類型要先改這份文件與測試。

## 注入的元素

| 元素 | id | class |
| --- | --- | --- |
| 樣式 | `smartagri-style` | — |
| 容器（`position: fixed`，`bottom: 20px`，左右 `20px`） | `smartagri-embed` | `smartagri-root`；開啟時加 `smartagri-open` |
| 啟動按鈕（`aria-label="開啟客服對話"`、`aria-expanded`、`aria-controls="smartagri-frame"`；名稱固定，狀態由 `aria-expanded` 表達） | `smartagri-launcher` | `smartagri-launcher` |
| iframe（第一次開啟時才有） | `smartagri-frame` | `smartagri-frame` |

所有 class 以 `smartagri-` 為前綴，容器、按鈕、iframe 各自 `all: initial` 隔離客戶網站的樣式。容器 `z-index: 2147483000`。
