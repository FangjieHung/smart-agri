import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import {
  type EnvironmentProviders,
  inject,
  makeEnvironmentProviders,
  provideAppInitializer,
} from '@angular/core';
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
    // 重新整理頁面時在背景重讀 `/me`，不擋住啟動。
    provideAppInitializer(() => {
      void inject(ApiSessionService).refreshIdentity();
    }),
  ]);
}
