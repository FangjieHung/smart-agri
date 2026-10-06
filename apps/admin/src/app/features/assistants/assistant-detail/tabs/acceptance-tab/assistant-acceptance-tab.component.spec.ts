import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { Router } from '@angular/router';
import { NEVER, Subject, of, throwError, type Observable } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type {
  AssistantTestCaseView,
  AssistantTestRunView,
} from '../../../../../core/domain/assistant-acceptance.model';
import type { OrganizationChatModelView } from '../../../../../core/domain/organization-settings.model';
import type { RepositoryView } from '../../../../../core/repositories/demo-repository';
import { DEMO_REPOSITORY } from '../../../../../core/repositories/tokens';
import { AssistantIssuesRepository } from '../../../../../core/repositories/assistant-issues.repository';
import { AssistantAcceptanceTabComponent } from './assistant-acceptance-tab.component';

const testCase: AssistantTestCaseView = {
  id: 'case-1',
  assistantId: 'assistant-1',
  question: '退貨期限是多久？',
  category: 'common',
  expectedKind: 'company-data',
  expectedDocumentIds: ['document-1'],
  followUpOfId: null,
  ordinal: 1,
  createdAt: '2026-09-30T08:00:00Z',
  updatedAt: '2026-09-30T08:00:00Z',
};

function run(overrides: Partial<AssistantTestRunView>): AssistantTestRunView {
  return {
    id: 'run-1',
    assistantId: 'assistant-1',
    trigger: 'manual',
    status: 'completed',
    rerunRequested: false,
    queuedAt: '2026-10-01T08:00:00Z',
    startedAt: '2026-10-01T08:00:01Z',
    completedAt: '2026-10-01T08:00:09Z',
    passedCount: 1,
    failedCount: 0,
    promptVersion: 'v1',
    model: 'fake-chat-dev',
    minScore: 0.3,
    ...overrides,
  };
}

function chatModel(model: string): OrganizationChatModelView {
  const effective = { id: model, displayName: model, model };
  return {
    options: [effective],
    selectedId: null,
    effective,
    source: 'deployment-default',
    canChange: true,
    lastChange: null,
    revision: 0,
  };
}

function testRepository(
  cases: Observable<RepositoryView<readonly AssistantTestCaseView[]>>,
  runs: readonly AssistantTestRunView[] = [],
  currentModel = 'fake-chat-dev',
) {
  return {
    listAssistantTestCases: vi.fn(() => cases),
    listAssistantTestRuns: vi.fn(() => of({ status: 'ready' as const, data: runs })),
    getOrganizationChatModel: vi.fn(() =>
      of({ status: 'ready' as const, data: chatModel(currentModel) }),
    ),
    getAssistantTestRun: vi.fn(),
    createAssistantTestCase: vi.fn(),
    updateAssistantTestCase: vi.fn(),
    deleteAssistantTestCase: vi.fn(),
    importAssistantTestCases: vi.fn(),
    exportAssistantTestCases: vi.fn(),
    createAssistantTestRun: vi.fn(),
  };
}

async function render(
  cases: Observable<RepositoryView<readonly AssistantTestCaseView[]>>,
  settle = true,
  runs: readonly AssistantTestRunView[] = [],
  currentModel = 'fake-chat-dev',
) {
  TestBed.resetTestingModule();
  const repository = testRepository(cases, runs, currentModel);
  await TestBed.configureTestingModule({
    imports: [AssistantAcceptanceTabComponent],
    providers: [
      { provide: DEMO_REPOSITORY, useValue: repository },
      { provide: AssistantIssuesRepository, useValue: { create: vi.fn() } },
      { provide: Router, useValue: { navigate: vi.fn() } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(AssistantAcceptanceTabComponent);
  fixture.componentRef.setInput('assistantId', 'assistant-1');
  fixture.componentRef.setInput('acceptanceStatus', 'not-accepted');
  fixture.detectChanges();
  if (settle) await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, repository };
}

function page(fixture: ComponentFixture<AssistantAcceptanceTabComponent>): HTMLElement {
  return fixture.nativeElement as HTMLElement;
}

describe('AssistantAcceptanceTabComponent', () => {
  it('distinguishes loading, permission, error, empty, and ready states', async () => {
    const loading = await render(NEVER, false);
    expect(page(loading.fixture).textContent).toContain('正在載入題組與重跑歷史');

    const denied = await render(
      of({ status: 'permission-denied', reason: 'assistant-configuration', message: '沒有權限' }),
    );
    expect(page(denied.fixture).textContent).toContain('沒有權限');

    const failed = await render(throwError(() => new Error('offline')));
    expect(page(failed.fixture).textContent).toContain('目前無法載入驗收資料');

    const empty = await render(of({ status: 'ready', data: [] }));
    expect(page(empty.fixture).textContent).toContain('還沒有測試題');

    const ready = await render(of({ status: 'ready', data: [testCase] }));
    expect(page(ready.fixture).textContent).toContain('退貨期限是多久？');
  });

  it('prevents submitting the same new case twice while its request is pending', async () => {
    const { fixture, repository } = await render(of({ status: 'ready', data: [] }));
    const pending = new Subject<RepositoryView<AssistantTestCaseView>>();
    repository.createAssistantTestCase.mockReturnValue(pending);
    const form = page(fixture).querySelector<HTMLFormElement>('.case-form');
    const question = form?.querySelector<HTMLTextAreaElement>('textarea');
    if (!form || !question) throw new Error('missing case form');
    question.value = '退貨期限是多久？';
    question.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    form.dispatchEvent(new Event('submit', { cancelable: true }));
    form.dispatchEvent(new Event('submit', { cancelable: true }));
    expect(repository.createAssistantTestCase).toHaveBeenCalledTimes(1);
    fixture.detectChanges();
    expect(form.querySelector<HTMLButtonElement>('button[type=submit]')?.disabled).toBe(true);
    pending.next({ status: 'ready', data: testCase });
    pending.complete();
  });

  it('does not queue a second run while a run request is pending', async () => {
    const { fixture, repository } = await render(of({ status: 'ready', data: [testCase] }));
    const pending = new Subject<RepositoryView<AssistantTestRunView>>();
    repository.createAssistantTestRun.mockReturnValue(pending);
    const run = page(fixture).querySelector<HTMLButtonElement>('.acceptance__summary button');
    run?.click();
    run?.click();
    expect(repository.createAssistantTestRun).toHaveBeenCalledTimes(1);
  });

  it('suggests a rerun when the latest run used another model (issue #240)', async () => {
    const runs = [
      run({ id: 'run-old', queuedAt: '2026-09-30T08:00:00Z', model: 'fake-chat-dev' }),
      run({ id: 'run-new', queuedAt: '2026-10-02T08:00:00Z', model: 'fake-chat-second' }),
    ];
    const { fixture } = await render(of({ status: 'ready', data: [testCase] }), true, runs, 'fake-chat-dev');

    expect(page(fixture).querySelector('[data-model-changed]')?.textContent?.trim()).toBe(
      '上次測試使用模型 fake-chat-second，現在是 fake-chat-dev。建議重跑題組。',
    );
  });

  it('says nothing when the latest run used the current model, or there is no run yet', async () => {
    const same = await render(of({ status: 'ready', data: [testCase] }), true, [
      run({ id: 'run-old', queuedAt: '2026-09-30T08:00:00Z', model: 'fake-chat-second' }),
      run({ id: 'run-new', queuedAt: '2026-10-02T08:00:00Z', model: 'fake-chat-dev' }),
    ]);
    expect(page(same.fixture).querySelector('[data-model-changed]')).toBeNull();

    const none = await render(of({ status: 'ready', data: [testCase] }), true, [], 'fake-chat-second');
    expect(page(none.fixture).querySelector('[data-model-changed]')).toBeNull();
  });

  it('does not suggest a rerun while one is queued (it will use the current model)', async () => {
    const { fixture } = await render(
      of({ status: 'ready', data: [testCase] }),
      false,
      [
        run({ id: 'run-done', queuedAt: '2026-09-30T08:00:00Z', model: 'fake-chat-second' }),
        run({ id: 'run-queued', queuedAt: '2026-10-02T08:00:00Z', status: 'queued', model: null, startedAt: null, completedAt: null }),
      ],
      'fake-chat-dev',
    );
    // 有進行中的重跑時會輪詢，不能等 whenStable；讓 resource 先送出結果再看畫面。
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();

    expect(page(fixture).textContent).toContain('排隊中');
    expect(page(fixture).querySelector('[data-model-changed]')).toBeNull();
    fixture.destroy();
  });
});
