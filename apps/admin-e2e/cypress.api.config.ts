import { defineConfig } from 'cypress';

/**
 * API 模式的 E2E（M2 Slice 17，issue #51）：對真實後端跑，只收 `src/e2e-api/` 底下的 spec。
 *
 * 與 cypress.config.ts（mock 模式）不同，這裡不透過 Nx preset 啟動 dev server——API、資料庫與
 * `nx serve admin --configuration=api` 都要先準備好（CI 的 `e2e-api` job；本機做法見
 * apps/admin-e2e/README.md）。Nx 的 @nx/cypress plugin 只認得 `cypress.config.*`，所以這個檔案
 * 不會被推斷成 target，改由 project.json 的 `e2e-api` target 以 `--config-file` 指定。
 *
 * 環境變數：
 * - `ADMIN_E2E_API_BASE_URL`：admin 的網址（預設 http://localhost:4200，即 `nx serve` 的預設埠）。
 * - `SEED_DEMO_PASSWORD`：`migrate` 建立示範帳號時用的密碼，spec 用它登入；沒有設定時 spec 直接失敗。
 */
export default defineConfig({
  e2e: {
    baseUrl: process.env['ADMIN_E2E_API_BASE_URL'] || 'http://localhost:4200',
    specPattern: 'src/e2e-api/**/*.cy.ts',
    supportFile: 'src/support/e2e.ts',
    fixturesFolder: 'src/fixtures',
    fileServerFolder: '.',
    screenshotsFolder: '../../dist/cypress/apps/admin-e2e-api/screenshots',
    videosFolder: '../../dist/cypress/apps/admin-e2e-api/videos',
    video: false,
    chromeWebSecurity: false,
    // 背景工作處理文件、PKCE 導轉都比 mock 慢；個別等待處理完成的斷言另外放寬。
    defaultCommandTimeout: 10000,
    // 密碼只經 `cy.env()` 讀取；關掉舊的 `Cypress.env()`，受測頁面的程式碼就讀不到它。
    allowCypressEnv: false,
    env: {
      demoPassword: process.env['SEED_DEMO_PASSWORD'] ?? '',
    },
  },
});
