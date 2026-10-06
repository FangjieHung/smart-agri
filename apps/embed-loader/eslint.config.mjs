import baseConfig from '../../eslint.config.mjs';

export default [
  ...baseConfig,
  {
    // embed.js is a classic browser script (ES2019, no modules).
    files: ['src/embed.js'],
    languageOptions: {
      sourceType: 'script',
      globals: { window: 'readonly', document: 'readonly', location: 'readonly', HTMLScriptElement: 'readonly', URL: 'readonly' },
    },
  },
];
