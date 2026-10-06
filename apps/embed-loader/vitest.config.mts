import { defineConfig } from 'vitest/config';

export default defineConfig({
  root: __dirname,
  cacheDir: '../../node_modules/.vite/embed-loader',
  test: {
    environment: 'jsdom',
    include: ['src/**/*.spec.ts'],
  },
});
