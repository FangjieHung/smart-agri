import { inject, InjectionToken } from '@angular/core';
import { AnonymousVisitorService } from '../session/anonymous-visitor.service';
import { DemoSessionService } from '../session/demo-session.service';
import type { DemoRepository } from './demo-repository';
import { readDemoScenario } from './demo-scenario-param';
import type { DemoSeed } from './demo-seed';
import type { MockDemoRepositoryOptions } from './mock-demo-repository';

/**
 * mock repository 與 seed 資料約 150 kB，不放進初始 bundle：啟動時由
 * `provideAppInitializer(loadMockRepositoryModules)` 動態載入（獨立 lazy chunk），
 * 之後 `DEMO_REPOSITORY` 的 factory 仍可同步取用，使用端不必改成非同步。
 */
type MockRepositoryModules = {
  repository: typeof import('./mock-demo-repository');
  seed: typeof import('./demo-seed');
};
let mockModules: MockRepositoryModules | null = null;

export async function loadMockRepositoryModules(): Promise<void> {
  if (mockModules !== null) return;
  const [repository, seed] = await Promise.all([
    import('./mock-demo-repository'),
    import('./demo-seed'),
  ]);
  mockModules = { repository, seed };
}

/**
 * API 模式才有的 repository 建構方式（`HybridDemoRepository`），由 `provideApiMode()` 提供。
 * mock 建置（含 GitHub Pages）這裡是 null，所以 bundle 不含任何 HTTP repository 程式碼。
 */
export const API_DEMO_REPOSITORY_FACTORY = new InjectionToken<
  ((seed: DemoSeed, options: MockDemoRepositoryOptions) => DemoRepository) | null
>('API_DEMO_REPOSITORY_FACTORY', { providedIn: 'root', factory: () => null });

export const DEMO_REPOSITORY = new InjectionToken<DemoRepository>(
  'DEMO_REPOSITORY',
  {
    providedIn: 'root',
    factory: () => {
      const session = inject(DemoSessionService);
      const visitor = inject(AnonymousVisitorService);
      const options: MockDemoRepositoryOptions = {
        storage:
          typeof localStorage === 'undefined' ? undefined : localStorage,
        // 未登入訪客的對話只留在這個分頁：關閉分頁就結束，也不與帳號共用儲存。
        visitorStorage:
          typeof sessionStorage === 'undefined' ? undefined : sessionStorage,
        viewer: () => session.activeAccountId(),
        // 對話的非同步契約（issue #79）：已選擇的 Demo 身分優先，其次是這個分頁的
        // 匿名訪客（`embeddedChatGuard` 會先發一個，沒發過時這裡是 null）。
        chatViewer: () => session.activeAccountId() ?? visitor.visitorId(),
      };
      // API 模式：已接上 API 的方法走 HTTP，其餘仍由 mock 回答。
      if (mockModules === null) {
        throw new Error('loadMockRepositoryModules() must resolve before DEMO_REPOSITORY is injected.');
      }
      const { MockDemoRepository } = mockModules.repository;
      const { DEMO_SEED } = mockModules.seed;
      const createApiRepository = inject(API_DEMO_REPOSITORY_FACTORY);
      const repository =
        createApiRepository?.(DEMO_SEED, options) ?? new MockDemoRepository(DEMO_SEED, options);
      // Demo：允許用網址參數預覽載入中／部分失敗／權限不足／連線中斷的畫面。
      const scenario =
        typeof location === 'undefined' ? null : readDemoScenario(location.search);
      if (scenario !== null) repository.setScenario(scenario);
      return repository;
    },
  },
);
