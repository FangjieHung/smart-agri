import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { NEVER, Subject, of, throwError, type Observable } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { AssistantIssueDetailView, AssistantIssueView } from '../../core/domain/assistant-issue.model';
import { AssistantIssuesRepository } from '../../core/repositories/assistant-issues.repository';
import { CaseSettingsRepository } from '../../core/repositories/case-settings.repository';
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
  viewerIsAssistantOwner: true, viewerIsAssignee: false, handoffUnverified: false,
  resolutionKind: null, linkedCaseId: null,
};

async function setup(
  list: Observable<RepositoryView<readonly AssistantIssueView[]>> = of({ status: 'ready', data: [issue] }),
  settle = true,
  detail: AssistantIssueDetailView = { issue, events: [], linkedCase: null },
) {
  TestBed.resetTestingModule();
  const update = vi.fn(() => of({ status: 'ready' as const, data: { issue, events: [], linkedCase: null } as AssistantIssueDetailView }));
  const repo = {
    list: vi.fn(() => list),
    get: vi.fn(() => of({ status: 'ready' as const, data: detail })),
    update,
    openCase: vi.fn(() => of({
      status: 'ready' as const,
      data: { caseId: 'case-9', issue: { ...detail, linkedCase: { caseId: 'case-9', canOpen: true } } },
    })),
  };
  await TestBed.configureTestingModule({
    imports: [IssuesPageComponent],
    providers: [
      provideRouter([]),
      { provide: AssistantIssuesRepository, useValue: repo },
      { provide: DEMO_REPOSITORY, useValue: { getTeam: () => of({ status: 'ready', data: { members: [], permissions: [], savedAt: null } }) } },
      { provide: DemoSessionService, useValue: { activeAccountId: () => 'account-1' } },
      {
        provide: CaseSettingsRepository,
        useValue: {
          listCaseTypes: () => of({ status: 'ready', data: { canManage: false, types: [{
            id: 'type-1', name: '設備故障報修', description: '', defaultGroup: { id: 'group-1', name: '設備組', archived: false },
            defaultDueHours: 72, isActive: true, createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
          }] } }),
          listCaseGroups: () => of({ status: 'ready', data: { canManage: false, candidates: [], groups: [{
            id: 'group-1', name: '設備組', archived: false, archivedAt: null, members: [],
            createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
          }] } }),
        },
      },
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
      linkedCase: null,
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
    const { fixture } = await setup(of({ status: 'ready', data: [unverified] }), true, { issue: unverified, events: [], linkedCase: null });
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
    pending.next({ status: 'ready', data: { issue, events: [], linkedCase: null } });
    pending.complete();
  });

  async function openDetail(fixture: ComponentFixture<IssuesPageComponent>): Promise<HTMLElement> {
    const root = fixture.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('.issue-item')?.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return root;
  }

  it('opens a case from an unresolved issue through the dialog and links to it afterwards (issue #252)', async () => {
    const { fixture, repo } = await setup();
    const root = await openDetail(fixture);
    const button = root.querySelector<HTMLButtonElement>('[data-issue-open-case]');
    expect(button?.textContent).toBe('另開案件');
    button?.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect((root.querySelector<HTMLInputElement>('#issue-case-title'))?.value).toBe('退貨條件引用錯誤');
    expect((root.querySelector<HTMLTextAreaElement>('#issue-case-description'))?.value).toBe('如何退貨？');

    const type = root.querySelector<HTMLSelectElement>('#issue-case-type');
    if (!type) throw new Error('type selector missing');
    type.value = 'type-1';
    type.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    root.querySelector<HTMLButtonElement>('.confirm-open-case')?.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(repo.openCase).toHaveBeenCalledWith('issue-1', expect.objectContaining({ typeId: 'type-1', groupId: 'group-1' }), expect.anything());
    expect(root.querySelector('[role="dialog"]')).toBeNull();
    const notice = root.querySelector('[data-issue-case-notice]');
    expect(notice?.textContent).toContain('非助理問題');
    expect(notice?.querySelector('a')?.getAttribute('href')).toBe('/app/cases?case=case-9');
    expect(repo.get).toHaveBeenCalledTimes(2);
  });

  it('shows a 非助理問題 resolution with its case link and no 另開案件 button', async () => {
    const resolved = { ...issue, status: 'resolved' as const, resolvedAt: '2026-10-01T08:00:00Z',
      resolutionKind: 'not-assistant-issue' as const, linkedCaseId: 'case-9' };
    const openable = await setup(of({ status: 'ready', data: [resolved] }), true, {
      issue: resolved, linkedCase: { caseId: 'case-9', canOpen: true },
      events: [{ id: 'event-2', action: 'case-opened', actorAccountId: 'account-1', actorDisplayName: '安心商行管理者',
        at: resolved.resolvedAt, note: null, assigneeAccountId: null, assigneeDisplayName: null, status: 'resolved', dueAt: null }],
    });
    const root = await openDetail(openable.fixture);
    expect(root.querySelector('.issue-item')?.textContent).toContain('已解決（非助理問題）');
    expect(root.querySelector('[data-issue-resolution-kind]')?.textContent).toContain('非助理問題');
    expect(root.querySelector('[data-issue-linked-case] a')?.getAttribute('href')).toBe('/app/cases?case=case-9');
    expect(root.querySelector('[data-issue-open-case]')).toBeNull();
    expect(text(openable.fixture)).toContain('另開案件（非助理問題）');

    const hidden = await setup(of({ status: 'ready', data: [resolved] }), true, {
      issue: resolved, linkedCase: { caseId: 'case-9', canOpen: false }, events: [],
    });
    const hiddenRoot = await openDetail(hidden.fixture);
    expect(hiddenRoot.querySelector('[data-issue-linked-case] a')).toBeNull();
    expect(hiddenRoot.querySelector('[data-issue-linked-case]')?.textContent).toContain('無法開啟');
  });
});
