import { InjectionToken } from '@angular/core';
import type { DemoRepository } from './demo-repository';
import { MockDemoRepository } from './mock-demo-repository';

export const DEMO_REPOSITORY = new InjectionToken<DemoRepository>(
  'DEMO_REPOSITORY',
  {
    providedIn: 'root',
    factory: () => new MockDemoRepository(),
  },
);
