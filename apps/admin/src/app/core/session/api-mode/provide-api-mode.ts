import { HttpClient, provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import {
  type EnvironmentProviders,
  inject,
  Injector,
  makeEnvironmentProviders,
  provideAppInitializer,
} from '@angular/core';
import { AgUiChatRunner } from '../../chat/ag-ui-chat-runner';
import { CHAT_RUNNER } from '../../chat/chat-runner';
import type { DemoSeed } from '../../repositories/demo-seed';
import { HybridDemoRepository } from '../../repositories/hybrid-demo-repository';
import type { MockDemoRepositoryOptions } from '../../repositories/mock-demo-repository';
import { API_DEMO_REPOSITORY_FACTORY, loadMockRepositoryModules } from '../../repositories/tokens';
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
    // 對話的串流回答走 AG-UI（issue #80）；`@ag-ui/client` 在第一次送出時才動態載入。
    {
      provide: CHAT_RUNNER,
      useFactory: () => {
        const backend = inject(HttpSessionBackend);
        const apiSession = inject(ApiSessionService);
        return new AgUiChatRunner({
          accessToken: () => backend.accessToken(),
          onUnauthorized: () => apiSession.endExpiredSession(),
        });
      },
    },
    // 重新整理頁面時在背景重讀 `/me`，不擋住啟動。
    // Angular 不會等前一個非同步 initializer 完成才呼叫下一個，所以這裡自己等 mock 模組
    // 載入完才解析 ApiSessionService（它在欄位初始化時注入 DEMO_REPOSITORY）。
    provideAppInitializer(async () => {
      const injector = inject(Injector);
      await loadMockRepositoryModules();
      void injector.get(ApiSessionService).refreshIdentity();
    }),
  ]);
}
