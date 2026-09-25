import type { AdminEnvironment } from './environment.model';

/**
 * 預設（development／production）：純 mock。API 模式的程式碼只從
 * `environment.api.ts` 匯入，所以正式建置的 bundle 完全不含它們。
 */
export const environment: AdminEnvironment = {
  apiMode: false,
  providers: [],
};
