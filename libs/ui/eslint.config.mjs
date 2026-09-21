import nx from '@nx/eslint-plugin';
import baseConfig from '../../eslint.config.mjs';

export default [
  ...nx.configs['flat/angular'],
  ...nx.configs['flat/angular-template'],
  ...baseConfig,
  {
    files: ['**/*.json'],
    rules: {
      '@nx/dependency-checks': [
        'error',
        {
          // spec 檔只在測試期間執行、不進入 build 產物，vitest 不該因此變成消費端的
          // peerDependencies；排除掉才不會被這條規則誤判成「缺少依賴」。
          ignoredFiles: [
            '{projectRoot}/eslint.config.{js,cjs,mjs,ts,cts,mts}',
            '{projectRoot}/src/**/*.spec.ts',
          ],
        },
      ],
    },
    languageOptions: {
      parser: await import('jsonc-eslint-parser'),
    },
  },
  {
    files: ['**/*.ts'],
    rules: {
      '@angular-eslint/directive-selector': [
        'error',
        {
          type: 'attribute',
          prefix: 'lib',
          style: 'camelCase',
        },
      ],
      '@angular-eslint/component-selector': [
        'error',
        {
          type: 'element',
          prefix: 'lib',
          style: 'kebab-case',
        },
      ],
    },
  },
  {
    files: ['**/*.html'],
    // Override or add rules here
    rules: {},
  },
  {
    // DataTable 投影插槽的屬性指令刻意用 `dt` 命名空間（dtCell / dtHead / dtBody），
    // 與元件 selector 的 `lib-` 前綴分工不同：元件本身仍是 `lib-data-table`，
    // 這三個只是給使用端在 <ng-template> 上標記插槽用，已是 9 個遷移頁面在用的公開 API，
    // 不能改名，故只為這兩個檔案放寬 directive-selector 的前綴，其餘規則與範圍不動。
    files: [
      '**/data-table-cell.directive.ts',
      '**/data-table-slot.directives.ts',
    ],
    rules: {
      '@angular-eslint/directive-selector': [
        'error',
        {
          type: 'attribute',
          prefix: 'dt',
          style: 'camelCase',
        },
      ],
    },
  },
];
