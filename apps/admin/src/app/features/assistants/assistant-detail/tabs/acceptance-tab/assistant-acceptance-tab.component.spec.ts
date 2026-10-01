import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { NEVER, Subject, of, throwError, type Observable } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type {
  AssistantTestCaseView,
  AssistantTestRunView,
} from '../../../../../core/domain/assistant-acceptance.model';
import type { RepositoryView } from '../../../../../core/repositories/demo-repository';
import { DEMO_REPOSITORY } from '../../../../../core/repositories/tokens';
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

function testRepository(cases: Observable<RepositoryView<readonly AssistantTestCaseView[]>>) {
  return {
    listAssistantTestCases: vi.fn(() => cases),
    listAssistantTestRuns: vi.fn(() =>
      of({ status: 'ready' as const, data: [] as readonly AssistantTestRunView[] }),
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
) {
  TestBed.resetTestingModule();
  const repository = testRepository(cases);
  await TestBed.configureTestingModule({
    imports: [AssistantAcceptanceTabComponent],
    providers: [{ provide: DEMO_REPOSITORY, useValue: repository }],
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
});
