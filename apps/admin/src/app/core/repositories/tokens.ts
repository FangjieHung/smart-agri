import { inject, InjectionToken } from '@angular/core';
import { DemoSessionService } from '../session/demo-session.service';
import type { DemoRepository } from './demo-repository';
import { readDemoScenario } from './demo-scenario-param';
import { DEMO_SEED, type DemoSeed } from './demo-seed';
import { MockDemoRepository, type MockDemoRepositoryOptions } from './mock-demo-repository';

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
      const options: MockDemoRepositoryOptions = {
        storage:
          typeof localStorage === 'undefined' ? undefined : localStorage,
        // 未登入訪客的對話只留在這個分頁：關閉分頁就結束，也不與帳號共用儲存。
        visitorStorage:
          typeof sessionStorage === 'undefined' ? undefined : sessionStorage,
        viewer: () => session.activeAccountId(),
      };
      // API 模式：已接上 API 的方法走 HTTP，其餘仍由 mock 回答。
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
