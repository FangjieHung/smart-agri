import { InjectionToken } from '@angular/core';
import type { DemoRepository } from './demo-repository';
import { readDemoScenario } from './demo-scenario-param';
import { DEMO_SEED } from './demo-seed';
import { MockDemoRepository } from './mock-demo-repository';

export const DEMO_REPOSITORY = new InjectionToken<DemoRepository>(
  'DEMO_REPOSITORY',
  {
    providedIn: 'root',
    factory: () => {
      const repository = new MockDemoRepository(DEMO_SEED, {
        storage:
          typeof localStorage === 'undefined' ? undefined : localStorage,
        // 未登入訪客的對話只留在這個分頁：關閉分頁就結束，也不與帳號共用儲存。
        visitorStorage:
          typeof sessionStorage === 'undefined' ? undefined : sessionStorage,
      });
      // Demo：允許用網址參數預覽載入中／部分失敗／權限不足／連線中斷的畫面。
      const scenario =
        typeof location === 'undefined' ? null : readDemoScenario(location.search);
      if (scenario !== null) repository.setScenario(scenario);
      return repository;
    },
  },
);
