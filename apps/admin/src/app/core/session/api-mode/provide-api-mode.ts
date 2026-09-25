import { HttpClient, provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import {
  type EnvironmentProviders,
  inject,
  makeEnvironmentProviders,
  provideAppInitializer,
} from '@angular/core';
import type { DemoSeed } from '../../repositories/demo-seed';
import { HybridDemoRepository } from '../../repositories/hybrid-demo-repository';
import type { MockDemoRepositoryOptions } from '../../repositories/mock-demo-repository';
import { API_DEMO_REPOSITORY_FACTORY } from '../../repositories/tokens';
import { API_SESSION_BACKEND, ApiSessionService } from '../api-session.service';
import { bearerTokenInterceptor } from './bearer-token.interceptor';
import { HttpSessionBackend } from './http-session-backend';
import { unauthorizedInterceptor } from './unauthorized.interceptor';

/**
 * API 模式才有的 provider。只由 `environment.api.ts` 匯入：mock 建置（含 GitHub Pages）
 * 不含 HttpClient、攔截器與 `oidc-client-ts`。
 */
export function provideApiMode(): EnvironmentProviders {
  return makeEnvironmentProviders([
    provideHttpClient(
      withFetch(),
      withInterceptors([bearerTokenInterceptor, unauthorizedInterceptor]),
    ),
    HttpSessionBackend,
    { provide: API_SESSION_BACKEND, useExisting: HttpSessionBackend },
    // 團隊走 HTTP，其餘功能區仍是 mock，但 mock 的權限判斷改用 API 的權限。
    {
      provide: API_DEMO_REPOSITORY_FACTORY,
      useFactory: () => {
        const http = inject(HttpClient);
        const backend = inject(HttpSessionBackend);
        return (seed: DemoSeed, options: MockDemoRepositoryOptions) =>
          new HybridDemoRepository(seed, options, {
            http,
            viewerPermissions: () => backend.restore(),
          });
      },
    },
    // 重新整理頁面時在背景重讀 `/me`，不擋住啟動。
    provideAppInitializer(() => {
      void inject(ApiSessionService).refreshIdentity();
    }),
  ]);
}
