const { nxE2EPreset } = require('@nx/cypress/plugins/cypress-preset');
const { defineConfig } = require('cypress');
module.exports = defineConfig({
  e2e: {
    ...nxE2EPreset(__filename, {
      cypressDir: 'src',
      webServerCommands: {
        default: 'npx nx run assistant-demo:serve',
        production: 'npx nx run assistant-demo:serve-static',
      },
      ciWebServerCommand: 'npx nx run assistant-demo:serve-static',
      ciBaseUrl: 'http://localhost:4300',
    }),
    baseUrl: 'http://localhost:4300',
  },
});
