import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { NEVER, Subject, of, throwError, type Observable } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { AssistantIssueDetailView, AssistantIssueView } from '../../core/domain/assistant-issue.model';
import { AssistantIssuesRepository } from '../../core/repositories/assistant-issues.repository';
import type { RepositoryView } from '../../core/repositories/demo-repository';
import { DEMO_REPOSITORY } from '../../core/repositories/tokens';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { IssuesPageComponent } from './issues-page.component';

const issue: AssistantIssueView = {
  id: 'issue-1', assistantId: 'assistant-1', assistantName: '客服助理',
  source: 'test-failure', status: 'open', title: '退貨條件引用錯誤',
  assigneeAccountId: null, assigneeDisplayName: null,
  reporterAccountId: null, reporterDisplayName: null, dueAt: null,
  testRunId: 'run-1', testResultId: 'result-1', testFailureReason: 'missing-document',
  question: '如何退貨？', answer: '請聯絡客服。', resolutionNote: null,
  createdAt: '2026-09-30T08:00:00Z', updatedAt: '2026-09-30T08:00:00Z', resolvedAt: null,
  viewerIsAssistantOwner: true, viewerIsAssignee: false,
};

async function setup(
  list: Observable<RepositoryView<readonly AssistantIssueView[]>> = of({ status: 'ready', data: [issue] }),
  settle = true,
  detail: AssistantIssueDetailView = { issue, events: [] },
) {
  TestBed.resetTestingModule();
  const update = vi.fn(() => of({ status: 'ready' as const, data: { issue, events: [] } as AssistantIssueDetailView }));
  const repo = {
    list: vi.fn(() => list),
    get: vi.fn(() => of({ status: 'ready' as const, data: detail })),
    update,
  };
  await TestBed.configureTestingModule({
    imports: [IssuesPageComponent],
    providers: [
      provideRouter([]),
      { provide: AssistantIssuesRepository, useValue: repo },
      { provide: DEMO_REPOSITORY, useValue: { getTeam: () => of({ status: 'ready', data: { members: [], permissions: [], savedAt: null } }) } },
      { provide: DemoSessionService, useValue: { activeAccountId: () => 'account-1' } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(IssuesPageComponent);
  fixture.detectChanges();
  if (settle) await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, repo };
}

function text(fixture: ComponentFixture<IssuesPageComponent>): string {
  return (fixture.nativeElement as HTMLElement).textContent ?? '';
}

describe('IssuesPageComponent', () => {
  it('distinguishes loading, permission, error, and empty states', async () => {
    const loading = await setup(NEVER, false);
    expect(text(loading.fixture)).toContain('正在載入處理事項');
    TestBed.resetTestingModule();

    const denied = await setup(of({ status: 'permission-denied' as const, reason: 'assistant-issue' as const, message: '沒有權限' }));
    expect(text(denied.fixture)).toContain('沒有權限');
    TestBed.resetTestingModule();

    const failed = await setup(throwError(() => new Error('offline')));
    expect(text(failed.fixture)).toContain('目前無法載入處理事項');
    TestBed.resetTestingModule();

    const empty = await setup(of({ status: 'ready' as const, data: [] as readonly AssistantIssueView[] }));
    expect(text(empty.fixture)).toContain('目前沒有符合條件的處理事項');
  });

  it('shows the selected issue snapshot and history', async () => {
    const { fixture } = await setup();
    expect(text(fixture)).toContain('退貨條件引用錯誤');
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('.issue-item')?.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(text(fixture)).toContain('如何退貨？');
    expect(text(fixture)).toContain('請聯絡客服。');
    expect(text(fixture)).toContain('處理紀錄');
  });

  it('shows readable labels for the reporter, failure reason and event action', async () => {
    const reportedIssue = { ...issue, reporterDisplayName: '安心商行管理者', testFailureReason: 'kind-mismatch' as const };
    const detail: AssistantIssueDetailView = {
      issue: reportedIssue,
      events: [{ id: 'event-1', action: 'created', actorAccountId: 'account-1',
        actorDisplayName: '安心商行管理者', at: issue.createdAt, note: null, assigneeAccountId: null,
        assigneeDisplayName: null, status: 'open', dueAt: null }],
    };
    const { fixture } = await setup(of({ status: 'ready', data: [reportedIssue] }), true, detail);
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('.issue-item')?.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(text(fixture)).toContain('建立人');
    expect(text(fixture)).toContain('回答類型與預期不符');
    expect(text(fixture)).toContain('建立事項');
    expect(text(fixture)).not.toContain('kind-mismatch');
  });

  it('marks an unsaved handoff snapshot as not verifiable against a conversation', async () => {
    const unverified = { ...issue, source: 'handoff' as const, handoffUnverified: true };
    const { fixture } = await setup(of({ status: 'ready', data: [unverified] }), true, { issue: unverified, events: [] });
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('.issue-item')?.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(text(fixture)).toContain('無法與原對話核對');
  });

  it('prevents a second update while the first is pending', async () => {
    const { fixture, repo } = await setup();
    const pending = new Subject<{ status: 'ready'; data: AssistantIssueDetailView }>();
    repo.update.mockReturnValue(pending);
    const root = fixture.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('.issue-item')?.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const status = root.querySelector<HTMLSelectElement>('.issue-edit select');
    if (!status) throw new Error('status selector missing');
    status.value = 'in-progress';
    status.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    const save = root.querySelector<HTMLButtonElement>('.issue-edit button');
    save?.click();
    save?.click();
    expect(repo.update).toHaveBeenCalledTimes(1);
    fixture.detectChanges();
    expect(save?.disabled).toBe(true);
    pending.next({ status: 'ready', data: { issue, events: [] } });
    pending.complete();
  });
});
