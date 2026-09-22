import { InjectionToken } from '@angular/core';
import type { DemoRepository } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import { MockDemoRepository } from './mock-demo-repository';

export const DEMO_REPOSITORY = new InjectionToken<DemoRepository>(
  'DEMO_REPOSITORY',
  {
    providedIn: 'root',
    factory: () =>
      new MockDemoRepository(DEMO_SEED, {
        storage:
          typeof localStorage === 'undefined' ? undefined : localStorage,
      }),
  },
);
