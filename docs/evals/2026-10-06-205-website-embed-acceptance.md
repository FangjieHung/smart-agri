# #205 官網嵌入的真實網域驗收（2026-10-06）

- **工單：** #205（M5a 計畫 §5 Slice 13）。
- **基準：** `master` 的 `b0e234d`，加上驗收中發現並修正的 PR #222。
- **結論：** 通過。驗收中發現 3 個問題，已在 #222 修正並重新驗收；另有 3 個後續事項列在最後一節。

## 環境

| 項目 | 設定 |
| --- | --- |
| 客戶官網（模擬） | GitHub Pages：`https://fangjiehung.github.io/smart-agri-embed-test/`（repo `FangjieHung/smart-agri-embed-test`）。頁面插入與客戶貼上時相同的 `<script src="{api}/embed.js" data-assistant="{id}" async>` |
| API | 本機 Development，臨時資料庫 `m5a_205`（沒有使用共用的 `smartagri`），以 Cloudflare quick tunnel（`cloudflared tunnel --url http://localhost:5153`）對外提供 HTTPS 網址 |
| 設定 | `PublicChannels__PublicBaseUrl` = 通道網址；`PublicChannels__TrustedProxies__0=127.0.0.1,::1`（cloudflared 從本機連入）；`Widget__RootPath` 指向 widget 的 production build；`Retrieval__MinScore=0.406` |
| 模型 | 嵌入 OpenAI `text-embedding-3-small`；對話 `gpt-6-luna`（`ReasoningEffort=None`）。金鑰只從 repo 外的檔案載入，紀錄中沒有金鑰 |
| 資料 | `SEED_DEMO_KNOWLEDGE=true` 以真實嵌入載入示範知識庫（3 個知識庫、5 個版本） |
| 助理 | 以 API 建立「安心商行官網客服（M5a 驗收）」，連接 3 個示範知識庫；拒答訊息寫入聯絡電話與 Email；驗收題組 2 題（退貨期限 `company-data`、天氣 `no-result`）以真實模型跑出 2/2 通過；允許網域 `fangjiehung.github.io`；發布後實際服務狀態 `serving` |

## 結果

| # | 項目 | 瀏覽器 | 結果 |
| --- | --- | --- | --- |
| 1 | 允許的網域能嵌入並問答 | Chrome | 通過。「收到商品後幾天內可以申請退貨？」→「收到商品後七天內可以申請退貨。[1]」，引用抽屜顯示退換貨辦法.pdf 的原文（[截圖](2026-10-06-205-website-embed/chrome-answer.png)、[引用](2026-10-06-205-website-embed/chrome-citation.png)） |
| 2 | 查無資料 | Chrome | 通過。顯示擁有者寫的拒答訊息（含電話與 Email），下一步只有「換個說法再問一次」，沒有出現給內部同仁的「請聯絡這個助理的管理者」（[截圖](2026-10-06-205-website-embed/chrome-no-result.png)） |
| 3 | 關閉視窗（跨網域 postMessage） | Chrome | 通過。按「關閉」後載入器收起視窗，`aria-expanded=false`，焦點回到啟動按鈕 |
| 4 | 手機寬度 360 × 740 | Chrome | 通過。視窗全螢幕（360 × 740），`scrollWidth = clientWidth = 360`，沒有水平捲動；重新整理後對話仍在（分頁的 `sessionStorage`）（[截圖](2026-10-06-205-website-embed/chrome-mobile.png)） |
| 5 | 暫停與恢復 | Chrome | 通過（修正後）。暫停時訪客看到「目前暫停服務」與「再試一次」；恢復後按「再試一次」，原本的問題回到輸入框，送出後得到回答，不必重新整理（[截圖](2026-10-06-205-website-embed/chrome-paused-retry.png)） |
| 6 | 不在清單的網域被擋下 | Chrome | 通過。同一個測試頁放在 `http://localhost:8000`，iframe 不載入，DevTools 主控台：`Framing 'https://….trycloudflare.com/' violates the following Content Security Policy directive: "frame-ancestors https://fangjiehung.github.io". The request has been blocked.`（[截圖](2026-10-06-205-website-embed/chrome-blocked.png)） |
| 7 | 被動安裝偵測 | — | 通過。開啟視窗後，`fangjiehung.github.io` 的 `lastSeenAt` 記錄為當下時間 |
| 8 | 允許的網域能嵌入並問答、關閉視窗 | Safari（負責人手動） | 通過 |
| 9 | 不保存訪客對話、用量計入 | 資料庫 | 通過。`ChatThreads`、`ChatMessages` 都是 0 筆；訪客問答的模型呼叫記為 `public-answer`、`AccountId` 為 null（3 次，2,482 tokens，計入每月用量）；回覆統計記在 `website` 管道（2 筆 `company-data`、1 筆 `no-result`） |

## 驗收中發現並修正的問題（PR #222）

1. **widget 嵌入後無法啟動（嚴重）**：iframe 一片空白，主控台是 `SecurityError: Failed to read a named property 'ngOnDestroy' from 'Window': Blocked a frame … from accessing a cross-origin frame.`。`WIDGET_PARENT` 直接把 `window.parent` 交給 Angular DI，而 DI 會讀它的 `ngOnDestroy`；跨來源視窗的任何屬性都讀不得。同源的單元測試與 Cypress E2E 都測不到。改成只提供轉呼叫 `postMessage` 的包裝。
2. **暫停後恢復，已開著視窗的訪客卡住**：暫停狀態沒有輸入框也沒有重試，只能重新整理頁面。新增「再試一次」。
3. **嵌入程式碼沒有帶位置**：載入器只讀網頁上的 `data-position`，伺服器產生的程式碼卻沒有寫入，選「左下角」不會生效。現在會輸出 `data-position="left"`。

## 後續事項（不擋 M5a）

1. **建立助理時不能選對外的服務對象**：後端對 `members-and-external-customers` 仍回 M3 的「對外發布將於後續版本開放，目前僅支援組織內使用」，API 模式的精靈也把這個選項藏起來。網站頻道不檢查這個欄位，所以不影響發布，但文字與限制已經過時。
2. **啟動按鈕的顏色固定是預設綠色**：品牌色只套用在對話視窗內，載入器的按鈕不跟著改。
3. **`@ag-ui/client` 會把預期中的 `403`／`429` 印成主控台錯誤**：不影響功能，但客戶看 DevTools 時可能誤以為壞了。

## 收尾

驗收結束後已停止 API、admin 靜態伺服器與 Cloudflare 通道，並刪除臨時資料庫 `m5a_205`；通道網址隨之失效。測試頁的 repo 保留，之後重測只要更新它的 `config.json`。
