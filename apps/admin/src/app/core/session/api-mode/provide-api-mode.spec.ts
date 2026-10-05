import { ApplicationInitStatus } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { DEMO_REPOSITORY, loadMockRepositoryModules, resetMockRepositoryModulesForTest } from '../../repositories/tokens';
import { provideApiMode } from './provide-api-mode';

describe('provideApiMode bootstrap', () => {
  afterEach(async () => {
    // Restore the preload from test-setup.ts for other specs in this worker.
    await loadMockRepositoryModules();
  });

  it('loads the mock repository modules before the API initializer injects DEMO_REPOSITORY', async () => {
    resetMockRepositoryModulesForTest();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideApiMode(),
      ],
    });
    const status = TestBed.inject(ApplicationInitStatus);
    await expect(status.donePromise).resolves.toBeUndefined();
    expect(TestBed.inject(DEMO_REPOSITORY)).toBeTruthy();
  });
});
