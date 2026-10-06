import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { NEVER, of, throwError, type Observable } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { AssistantIssueView } from '../../core/domain/assistant-issue.model';
import { AssistantIssuesRepository } from '../../core/repositories/assistant-issues.repository';
import type { RepositoryView } from '../../core/repositories/demo-repository';
import { DEMO_REPOSITORY } from '../../core/repositories/tokens';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { ActivityPageComponent } from './activity-page.component';

const forwarded: AssistantIssueView = {
  id: 'issue-1', assistantId: 'assistant-1', assistantName: '客服助理', source: 'handoff',
  status: 'resolved', title: '如何退貨？', assigneeAccountId: null, assigneeDisplayName: null,
  reporterAccountId: 'account-1', reporterDisplayName: '客戶', dueAt: null,
  testRunId: null, testResultId: null, testFailureReason: null,
  question: null, answer: null, resolutionNote: '已補上退貨說明',
  createdAt: '2026-09-30T08:00:00Z', updatedAt: '2026-09-30T09:00:00Z', resolvedAt: '2026-09-30T09:00:00Z',
  viewerIsAssistantOwner: false, viewerIsAssignee: false, handoffUnverified: false,
  resolutionKind: 'fixed', linkedCaseId: null,
};

async function render(response: Observable<RepositoryView<readonly AssistantIssueView[]>>, settle = true): Promise<string> {
  TestBed.resetTestingModule();
  const list = vi.fn(() => response);
  await TestBed.configureTestingModule({
    imports: [ActivityPageComponent],
    providers: [
      provideRouter([]),
      { provide: AssistantIssuesRepository, useValue: { list } },
      // 「我送出的資料」區塊另有自己的測試（own-submissions.component.spec.ts）。
      { provide: DEMO_REPOSITORY, useValue: { listOwnDatabaseSubmissions: () => of({ status: 'ready', data: [] }) } },
      { provide: DemoSessionService, useValue: { activeAccountId: () => 'account-1' } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(ActivityPageComponent);
  fixture.detectChanges();
  if (settle) await fixture.whenStable();
  fixture.detectChanges();
  expect(list).toHaveBeenCalledWith({ scope: 'forwarded' });
  return (fixture.nativeElement as HTMLElement).textContent ?? '';
}

describe('ActivityPageComponent', () => {
  it('shows loading, denied, error and empty states', async () => {
    expect(await render(NEVER, false)).toContain('正在載入轉交紀錄');
    expect(await render(of({ status: 'permission-denied', reason: 'assistant-issue', message: '沒有權限' }))).toContain('沒有權限');
    expect(await render(throwError(() => new Error('offline')))).toContain('目前無法載入轉交紀錄');
    expect(await render(of({ status: 'ready', data: [] }))).toContain('尚無轉交事項');
  });

  it('shows only forwarded issue status and resolution', async () => {
    const text = await render(of({ status: 'ready', data: [forwarded] }));
    expect(text).toContain('如何退貨？');
    expect(text).toContain('已解決');
    expect(text).toContain('已補上退貨說明');
  });
});
