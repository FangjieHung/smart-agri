import type { EnvironmentProviders, Provider } from '@angular/core';

/** 放在獨立檔案：`environment.ts` 會被 fileReplacements 換掉，不能從它匯入型別。 */
export interface AdminEnvironment {
  /** true 時登入與工作階段走真實 API；false 為 GitHub Pages 的純 mock Demo。 */
  readonly apiMode: boolean;
  /** 只在 API 模式存在的 provider（HttpClient、攔截器、登入流程）。 */
  readonly providers: readonly (Provider | EnvironmentProviders)[];
}
