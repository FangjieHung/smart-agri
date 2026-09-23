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

module.exports = defineConfig({
  e2e: {
    ...preset,
    baseUrl: 'http://localhost:4301',
    async setupNodeEvents(on, config) {
      // 保留 Nx preset 的 dev server 啟動邏輯，再加上自己的 task。
      const updated = await preset.setupNodeEvents?.(on, config);

      // 讓 axe 的違規細節印到終端機，而不是只留在瀏覽器 log。
      on('task', {
        a11yViolations(rows) {
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
