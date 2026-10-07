import { ApplicationInitStatus } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AgUiChatRunner } from '@smart-agri/chat';
import { CHAT_RUNNER } from '../../chat/chat-runner';
import { MockChatRunner } from '../../chat/mock-chat-runner';
import { DEMO_SEED } from '../../repositories/demo-seed';
import { HybridDemoRepository } from '../../repositories/hybrid-demo-repository';
import {
  API_DEMO_REPOSITORY_FACTORY,
  DEMO_REPOSITORY,
  loadMockRepositoryModules,
  resetMockRepositoryModulesForTest,
} from '../../repositories/tokens';
import {
  isApiModeRuntimeLoadedForTest,
  loadApiModeRuntime,
  provideApiMode,
  resetApiModeRuntimeForTest,
} from './provide-api-mode';

describe('provideApiMode bootstrap', () => {
  beforeEach(() => {
    resetMockRepositoryModulesForTest();
    resetApiModeRuntimeForTest();
  });

  afterEach(async () => {
    // Restore the preload from test-setup.ts for other specs in this worker.
    await loadMockRepositoryModules();
    await loadApiModeRuntime();
  });

  function configure(): void {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideApiMode()],
    });
  }

  it('loads the mock repository modules and the API runtime before the API initializer injects DEMO_REPOSITORY', async () => {
    configure();
    const status = TestBed.inject(ApplicationInitStatus);
    await expect(status.donePromise).resolves.toBeUndefined();
    expect(isApiModeRuntimeLoadedForTest()).toBe(true);
    expect(TestBed.inject(DEMO_REPOSITORY)).toBeInstanceOf(HybridDemoRepository);
  });

  it('loads HybridDemoRepository with a dynamic import, not at module load (issue #308)', async () => {
    configure();
    // Before the initializer runs, the runtime chunk has not been loaded. The factory token can still be
    // injected (cases/case-settings/assistant-issues only test it for null to detect API mode), but
    // building the repository fails with a clear message instead of silently falling back to the mock.
    const createRepository = TestBed.inject(API_DEMO_REPOSITORY_FACTORY);
    // TestBed started the initializer on the first inject; its import() has not resolved yet.
    expect(isApiModeRuntimeLoadedForTest()).toBe(false);
    expect(createRepository).not.toBeNull();
    expect(() => createRepository?.(DEMO_SEED, {})).toThrow(
      'loadApiModeRuntime() must resolve before the API repository or chat runner is used.',
    );

    await TestBed.inject(ApplicationInitStatus).donePromise;
    expect(isApiModeRuntimeLoadedForTest()).toBe(true);
    expect(createRepository?.(DEMO_SEED, {})).toBeInstanceOf(HybridDemoRepository);
  });

  it('uses the AG-UI chat runner in API mode, loaded with the runtime', async () => {
    configure();
    await TestBed.inject(ApplicationInitStatus).donePromise;
    expect(TestBed.inject(CHAT_RUNNER)).toBeInstanceOf(AgUiChatRunner);
  });
});

describe('CHAT_RUNNER without provideApiMode', () => {
  it('stays the mock runner', () => {
    expect(TestBed.inject(CHAT_RUNNER)).toBeInstanceOf(MockChatRunner);
  });
});
