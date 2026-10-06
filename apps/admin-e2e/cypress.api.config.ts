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
 * - `ADMIN_E2E_API_URL`：API 的網址（預設 http://localhost:5153），也是 `PublicChannels:PublicBaseUrl`；
 *   website-embed-api.cy.ts 直接開 API 提供的對話視窗 `/use/{id}`，並以 `cy.request` 檢查它的標頭。
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
      apiUrl: process.env['ADMIN_E2E_API_URL'] || 'http://localhost:5153',
    },
    setupNodeEvents(on) {
      // 跨 test 保留的值（只在這次 `cypress run` 的記憶體裡）。spec 的 test 換到另一個來源（例如 API 提供的
      // 對話視窗 `/use/{id}`）時，Cypress 會在新來源重新載入 spec，模組裡的變數就沒了，所以存在 Node 這一側。
      const values = new Map<string, unknown>();
      on('task', {
        rememberValue({ key, value }: { key: string; value: unknown }) {
          values.set(key, value);
          return null;
        },
        recallValue(key: string) {
          return values.get(key) ?? null;
        },
      });
    },
  },
});
