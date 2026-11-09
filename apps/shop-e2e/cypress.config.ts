const { nxE2EPreset } = require('@nx/cypress/plugins/cypress-preset');
const { defineConfig } = require('cypress');

// 顧客點餐網頁（apps/shop）的 E2E：production 建置、4311 埠（admin-e2e 用 4301），公開點餐 API 全部用
// cy.intercept 回放 src/fixtures/ 裡從真實 API 錄下的回應（見 src/support/ordering-api.ts），不需要後端。
const preset = nxE2EPreset(__filename, {
  cypressDir: 'src',
  webServerCommands: {
    default: 'npx nx run shop:serve --port=4311',
    production: 'npx nx run shop:serve --configuration=production --port=4311',
  },
  ciWebServerCommand: 'npx nx run shop:serve --configuration=production --port=4311',
  ciBaseUrl: 'http://localhost:4311/order/',
});

interface A11yViolationRow {
  rule: string;
  impact: string | null | undefined;
  nodes: number;
  target: string;
  help: string;
  why: string;
}

module.exports = defineConfig({
  e2e: {
    ...preset,
    baseUrl: 'http://localhost:4311/order/',
    specPattern: 'src/e2e/**/*.cy.{js,jsx,ts,tsx}',
    async setupNodeEvents(on: Cypress.PluginEvents, config: Cypress.PluginConfigOptions) {
      const updated = await preset.setupNodeEvents?.(on, config);
      on('task', {
        a11yViolations(rows: A11yViolationRow[]) {
          if (Array.isArray(rows) && rows.length > 0) {
            console.table(rows);
          }
          return null;
        },
      });
      return updated ?? config;
    },
  },
});
