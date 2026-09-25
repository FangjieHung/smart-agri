const { nxE2EPreset } = require('@nx/cypress/plugins/cypress-preset');
const { defineConfig } = require('cypress');

const preset = nxE2EPreset(__filename, {
  cypressDir: 'src',
  webServerCommands: {
    default: 'npx nx run admin:serve --port=4301',
    production: 'npx nx run admin:serve --configuration=production --port=4301',
  },
  ciWebServerCommand: 'npx nx run admin:serve --configuration=production --port=4301',
  ciBaseUrl: 'http://localhost:4301',
});

// `on('task', { a11yViolations })` 收到的是 a11y.ts 裡 `reportViolations()`
// map 出來的那一列列違規摘要；型別只寫給這個 task 用，不對外匯出。
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
    baseUrl: 'http://localhost:4301',
    async setupNodeEvents(on: Cypress.PluginEvents, config: Cypress.PluginConfigOptions) {
      // 保留 Nx preset 的 dev server 啟動邏輯，再加上自己的 task。
      const updated = await preset.setupNodeEvents?.(on, config);

      // 讓 axe 的違規細節印到終端機，而不是只留在瀏覽器 log。
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
