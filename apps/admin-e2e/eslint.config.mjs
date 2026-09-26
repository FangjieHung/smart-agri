import baseConfig from '../../eslint.config.mjs';

export default [
  ...baseConfig,
  {
    files: ['**/*.ts', '**/*.js'],
    // Override or add rules here
    rules: {},
  },
  {
    // Cypress 官方推薦替自訂指令加型別的寫法就是在 `declare global` 底下
    // 擴充 `namespace Cypress`（見 https://on.cypress.io/typescript ）。
    // @typescript-eslint/no-namespace 預設只認「本身標了 declare」的 namespace 為合法 ambient
    // 宣告；巢狀在 `declare global {}` 底下的 namespace 語法上沒有重複 `declare`，
    // 要開 allowDeclarations 讓規則往上層找到 `declare global` 才會放行——
    // 這是規則本來就提供、專門給這種情境用的選項，範圍只鎖定這一個檔案，不是關掉規則。
    files: ['**/support/commands.ts'],
    rules: {
      '@typescript-eslint/no-namespace': ['error', { allowDeclarations: true }],
    },
  },
];
