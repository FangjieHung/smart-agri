const { nxE2EPreset } = require('@nx/cypress/plugins/cypress-preset');
const { defineConfig } = require('cypress');
module.exports = defineConfig({
  e2e: {
    ...nxE2EPreset(__filename, {
      cypressDir: 'src',
      webServerCommands: {
        default: 'npx nx run admin:serve --port=4301',
        production: 'npx nx run admin:serve --configuration=production --port=4301',
      },
      ciWebServerCommand: 'npx nx run admin:serve --configuration=production --port=4301',
      ciBaseUrl: 'http://localhost:4301',
    }),
    baseUrl: 'http://localhost:4301',
  },
});
