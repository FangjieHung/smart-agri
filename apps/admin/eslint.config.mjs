import nx from '@nx/eslint-plugin';
import baseConfig from '../../eslint.config.mjs';

export default [
  ...nx.configs['flat/angular'],
  ...nx.configs['flat/angular-template'],
  ...baseConfig,
  {
    files: ['**/*.ts'],
    rules: {
      '@angular-eslint/directive-selector': [
        'error',
        {
          type: 'attribute',
          prefix: 'app',
          style: 'camelCase',
        },
      ],
      '@angular-eslint/component-selector': [
        'error',
        {
          type: 'element',
          prefix: 'app',
          style: 'kebab-case',
        },
      ],
    },
  },
  {
    // 初始 bundle 的上限是 600 kB（issue #308）：啟動就會載入的程式不可靜態匯入 mock 資料層、
    // API 模式的 runtime，或只有對話頁才用到的元件。型別匯入不受影響；要用到值請走既有的動態
    // import()（`tokens.ts` 的 `loadMockRepositoryModules`、`provide-api-mode.ts` 的 `loadApiModeRuntime`）。
    files: [
      'src/main.ts',
      'src/environments/**/*.ts',
      'src/app/app.ts',
      'src/app/app.config.ts',
      'src/app/app.routes.ts',
      'src/app/layout/**/*.ts',
      'src/app/core/session/**/*.ts',
      'src/app/core/chat/chat-runner.ts',
      'src/app/core/chat/api-chat-runner.ts',
    ],
    ignores: ['**/*.spec.ts', '**/*.testing.ts', 'src/app/core/session/api-mode/api-mode-runtime.ts'],
    rules: {
      '@typescript-eslint/no-restricted-imports': [
        'error',
        {
          patterns: [
            {
              group: [
                '**/repositories/mock-demo-repository',
                '**/repositories/hybrid-demo-repository',
                '**/repositories/demo-seed',
                '**/repositories/demo-seed-*',
                '**/api-mode/api-mode-runtime',
                './api-mode-runtime',
              ],
              allowTypeImports: true,
              message: '啟動時載入的程式不可靜態匯入（issue #308）：用 import type，或既有的動態 import()。',
            },
            {
              group: ['**/conversation-rail/conversation-rail.component', '**/confirm-dialog/confirm-dialog.component'],
              allowTypeImports: true,
              message: '只有對話頁才用到，不放進 shell 的初始 bundle（issue #308）。',
            },
          ],
        },
      ],
    },
  },
  {
    files: ['**/*.html'],
    // Override or add rules here
    rules: {},
  },
];
