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
3. **API**（5153，背景工作 worker 預設開啟，`appsettings.Development.json` 已經用 `Fake` 嵌入與對話）：

   ```sh
   dotnet run --project apps/api/src/SmartAgri.Api
   ```

4. **admin**（4200，`apps/admin/proxy.api.json` 把 `/api`、`/connect`、`/.well-known` 轉給 5153）：

   ```sh
   npx nx run admin:serve:api
   ```

5. **跑 spec**（同一個 shell 要有 `SEED_DEMO_PASSWORD`）：

   ```sh
   npx nx run admin-e2e:e2e-api
   ```

   截圖在 `dist/cypress/apps/admin-e2e-api/screenshots/`。

### 換埠跑（避開正在用的 5153／4200）

- API：`ASPNETCORE_URLS=http://localhost:5163`，資料庫另開時用 `ConnectionStrings__Default=...` 指過去。
- admin 的 redirect URI 是 `migrate` 依 `Authentication:AdminSpa:Origins` 寫進資料庫的，換埠要在 `migrate`
  時一起設定：`Authentication__AdminSpa__Origins__0=http://localhost:4210`。
- proxy 目標寫在 `apps/admin/proxy.api.json`：複製一份改成新的 API 埠，以
  `npx nx run admin:serve:api --port=4210 --proxy-config=<複製的檔案>` 啟動。
- spec：`ADMIN_E2E_API_BASE_URL=http://localhost:4210 npx nx run admin-e2e:e2e-api`。

## CI 的測試密碼

`e2e-api` job 的 `SEED_DEMO_PASSWORD` 是寫在 `ci.yml` 裡的公開測試值，只用在那個 job 自己的一次性
資料庫。`tools/check-no-demo-secrets.sh` 只容許 `ci.yml` 使用這一個值，其他地方或其他值都會失敗
（見該腳本開頭的註解）。
