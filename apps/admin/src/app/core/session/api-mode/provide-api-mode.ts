import { HttpClient, provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import {
  type EnvironmentProviders,
  inject,
  Injector,
  makeEnvironmentProviders,
  provideAppInitializer,
} from '@angular/core';
import type { components } from '../../api/api-schema';
import { API_CHAT_RUNNER } from '../../chat/api-chat-runner';
import type { DemoSeed } from '../../repositories/demo-seed';
import type { MockDemoRepositoryOptions } from '../../repositories/mock-demo-repository';
import { API_DEMO_REPOSITORY_FACTORY, loadMockRepositoryModules } from '../../repositories/tokens';
import { API_SESSION_BACKEND, ApiSessionService } from '../api-session.service';
import { bearerTokenInterceptor } from './bearer-token.interceptor';
import { HttpSessionBackend } from './http-session-backend';
import { unauthorizedInterceptor } from './unauthorized.interceptor';

/**
 * `HybridDemoRepository`（連同它繼承的 mock repository 與 seed）與 AG-UI 的 ChatRunner 不放進
 * 初始 bundle（issue #308）：和 mock 模式（#176，見 `tokens.ts`）一樣，由下方的 app initializer
 * 動態載入成 lazy chunk，之後的 factory 與串流回覆的轉換仍同步取用，使用端不必改成非同步。
 */
type ApiModeRuntime = typeof import('./api-mode-runtime');
let runtime: ApiModeRuntime | null = null;

export async function loadApiModeRuntime(): Promise<void> {
  if (runtime !== null) return;
  runtime = await import('./api-mode-runtime');
}

/** 只給測試：檢查與清掉載入快取，重現「尚未載入」的啟動順序。 */
export function isApiModeRuntimeLoadedForTest(): boolean {
  return runtime !== null;
}
export function resetApiModeRuntimeForTest(): void {
  runtime = null;
}

function loadedRuntime(): ApiModeRuntime {
  if (runtime === null) {
    throw new Error('loadApiModeRuntime() must resolve before the API repository or chat runner is used.');
  }
  return runtime;
}

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
        // 注入這個 token 不需要 runtime 已載入（cases 等 repository 只用 `!== null` 判斷是否為
        // API 模式）；真的建立 repository 時才取用，此時 initializer 已載入完成。
        return (seed: DemoSeed, options: MockDemoRepositoryOptions) =>
          new (loadedRuntime().HybridDemoRepository)(seed, options, {
            http,
            viewerPermissions: () => backend.restore(),
          });
      },
    },
    // 對話的串流回答走 AG-UI（issue #80）；`@ag-ui/client` 在第一次送出時才動態載入。
    {
      provide: API_CHAT_RUNNER,
      useFactory: () => {
        const backend = inject(HttpSessionBackend);
        const apiSession = inject(ApiSessionService);
        const { AgUiChatRunner, toAdminChatReply } = loadedRuntime();
        return new AgUiChatRunner({
          accessToken: () => backend.accessToken(),
          onUnauthorized: () => apiSession.endExpiredSession(),
          // 串流的 `smartagri.reply` 與 `GET chat` 的訊息同一個形狀，表單、收據與數據庫查詢用同一個轉換。
          replyExtension: (reply) => toAdminChatReply(reply as components['schemas']['ChatReplyView']),
        });
      },
    },
    // 重新整理頁面時在背景重讀 `/me`，不擋住啟動。
    // Angular 不會等前一個非同步 initializer 完成才呼叫下一個，所以這裡自己等 mock 模組與 API 模式的
    // runtime 載入完才解析 ApiSessionService（它在欄位初始化時注入 DEMO_REPOSITORY）。
    // 兩者都是 lazy chunk；任一載入失敗時 initializer reject、啟動失敗，與 mock 模式相同。
    provideAppInitializer(async () => {
      const injector = inject(Injector);
      await Promise.all([loadMockRepositoryModules(), loadApiModeRuntime()]);
      void injector.get(ApiSessionService).refreshIdentity();
    }),
  ]);
}
