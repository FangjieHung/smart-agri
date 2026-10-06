# admin-e2e

`apps/admin` 的 Cypress E2E，分成兩組，彼此不重疊：

| 模式 | spec | 設定檔 | 指令 | CI job |
| --- | --- | --- | --- | --- |
| mock（不需要後端） | `src/e2e/` | `cypress.config.ts` | `npx nx run admin-e2e:e2e --configuration=production` | `e2e` |
| API（真實後端） | `src/e2e-api/` | `cypress.api.config.ts` | `npx nx run admin-e2e:e2e-api` | `e2e-api` |

mock 模式會自己啟動 dev server（4301 埠），用法見 `apps/admin/README.md`。

## 在本機跑 API 模式

API 模式的 spec 不會自己啟動任何伺服器，要先準備好資料庫、API 與 `api` configuration 的 admin，
步驟與 `.github/workflows/ci.yml` 的 `e2e-api` job 相同。以下指令都在 repo 根目錄執行。

1. **資料庫**：`docker compose -f deploy/docker-compose.dev.yml up -d postgres`（5432）。
   spec 會自己建立、最後刪除它用到的知識庫，可以用平常開發的資料庫重跑。
2. **migrate 並建立示範帳號**：`SEED_DEMO_PASSWORD` 自己選一個符合密碼規則的值（至少 12 字元，
   含大小寫、數字與符號），之後 Cypress 也用同一個值登入：

   ```sh
   export SEED_DEMO_PASSWORD='Choose-a-strong-password-1!'
   dotnet run --project apps/api/src/SmartAgri.Api -- migrate
   ```

   帳號已經存在時 `migrate` 不會改密碼，這時請用當初建立帳號的密碼。
3. **假的 LINE 伺服器**（5180，`line-api.cy.ts` 需要；只用 node 內建模組，不必安裝任何套件）：

   ```sh
   node tools/fake-line-server/server.mjs
   ```

   它模擬 API 用到的 LINE Messaging API 端點（bot info、設定 Webhook 網址、Webhook 測試、reply、push、
   輸入中動畫）。「Webhook 測試」會真的以 Channel secret 簽章，送一個測試事件到 API 的 webhook，和 LINE 一樣。
   spec 透過它的控制 API（`/__control/bots`、`/__control/messages`、`/__control/reset`，說明在檔案開頭）
   新增這次執行的官方帳號、讀取收到的 reply／push；`cypress.api.config.ts` 的 `line*` task 包好了這些呼叫，
   也負責產生有簽章的 webhook 請求（模擬 LINE 使用者提問）。換埠用 `FAKE_LINE_PORT`，跑 spec 時設
   `ADMIN_E2E_FAKE_LINE_URL`（預設 `http://127.0.0.1:5180`）。
4. **API**（5153，背景工作 worker 預設開啟，`appsettings.Development.json` 已經用 `Fake` 嵌入與對話）。
   `website-embed-api.cy.ts` 會開 API 提供的訪客對話視窗 `/use/{id}`，所以先建置 widget，再讓 API 指到產出
   （沒有建置時 `/use/{id}` 一律 `503`）：

   ```sh
   npx nx build widget --configuration=production
   Widget__RootPath="$PWD/dist/smart-agri-widget/browser" \
   Widget__EmbedScriptPath="$PWD/apps/embed-loader/src/embed.js" \
   Line__ApiBaseUrl=http://127.0.0.1:5180 \
   dotnet run --project apps/api/src/SmartAgri.Api
   ```

   `Line__ApiBaseUrl` 讓 LINE 的呼叫都送到上一步的假伺服器（Production 只接受 `https://api.line.me`，
   Development 可以指到任何 http(s) 網址）。LINE 的 Webhook 網址是 `{PublicChannels:PublicBaseUrl}/api/v1/line/webhook/{id}`，
   `appsettings.Development.json` 已設成 `http://localhost:5153`；換埠時一起改（見下方）。

   `website-embed-api.cy.ts` 也會用 `cy.exec` 執行 `dotnet run --no-build --project ../api/src/SmartAgri.Api -- set-token-limit`
   暫時調整 安心商行 的用量上限（最後恢復成 `default`），所以跑 Cypress 的 shell 要能連到同一個資料庫
   （換資料庫時一樣設定 `ConnectionStrings__Default`）。

5. **admin**（4200，`apps/admin/proxy.api.json` 把 `/api`、`/connect`、`/.well-known` 轉給 5153）：

   ```sh
   npx nx run admin:serve:api
   ```

6. **跑 spec**（同一個 shell 要有 `SEED_DEMO_PASSWORD`）：

   ```sh
   npx nx run admin-e2e:e2e-api
   # 只跑一支：路徑相對於 apps/admin-e2e（cypress 設定檔所在目錄）
   npx nx run admin-e2e:e2e-api --spec=src/e2e-api/line-api.cy.ts
   ```

   截圖在 `dist/cypress/apps/admin-e2e-api/screenshots/`。

### 換埠跑（避開正在用的 5153／4200）

- API：`ASPNETCORE_URLS=http://localhost:5163`，資料庫另開時用 `ConnectionStrings__Default=...` 指過去；
  對外網址也要一起改：`PublicChannels__PublicBaseUrl=http://localhost:5163`，跑 spec 時設
  `ADMIN_E2E_API_URL=http://localhost:5163`。
- admin 的 redirect URI 是 `migrate` 依 `Authentication:AdminSpa:Origins` 寫進資料庫的，換埠要在 `migrate`
  時一起設定：`Authentication__AdminSpa__Origins__0=http://localhost:4210`。
- proxy 目標寫在 `apps/admin/proxy.api.json`：複製一份改成新的 API 埠，以
  `npx nx run admin:serve:api --port=4210 --proxy-config=<複製的檔案>` 啟動。
- spec：`ADMIN_E2E_API_BASE_URL=http://localhost:4210 npx nx run admin-e2e:e2e-api`。

## CI 的測試密碼

`e2e-api` job 的 `SEED_DEMO_PASSWORD` 是寫在 `ci.yml` 裡的公開測試值，只用在那個 job 自己的一次性
資料庫。`tools/check-no-demo-secrets.sh` 只容許 `ci.yml` 使用這一個值，其他地方或其他值都會失敗
（見該腳本開頭的註解）。
