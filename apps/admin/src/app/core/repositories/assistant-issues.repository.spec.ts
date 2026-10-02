import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { DemoSessionService } from '../session/demo-session.service';
import { AssistantIssuesRepository, API_ISSUES_PATH, API_ISSUES_SUMMARY_PATH, apiAssistantHandoffsPath, apiIssuePath } from './assistant-issues.repository';
import { API_DEMO_REPOSITORY_FACTORY } from './tokens';

// Captured verbatim from AssistantHandoffEndpointsTests POST /chat/handoffs against PostgreSQL on 2026-10-02.
const SAVED_HANDOFF_API_JSON = '{"id":"01a0faf7-13c7-7aae-b0f8-5ca9991314c1","assistantId":"01a0faf7-1261-7fea-8a03-4c701e040b94","assistantName":"退貨小幫手","source":"handoff","status":"open","title":"可信問題","assigneeAccountId":"01a0faf7-0e40-7120-a532-20818aaf9a77","assigneeDisplayName":null,"reporterAccountId":"01a0faf7-127f-7d43-b7e0-cc067a849f51","reporterDisplayName":null,"dueAt":null,"testRunId":null,"testResultId":null,"testFailureReason":null,"question":"可信問題","answer":"可信回答","resolutionNote":null,"createdAt":"2026-10-02T04:54:51.590909+00:00","updatedAt":"2026-10-02T04:54:51.590909+00:00","resolvedAt":null,"viewerIsAssistantOwner":false,"viewerIsAssignee":false,"handoffUnverified":false}';

function provideApiIssues() {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: API_DEMO_REPOSITORY_FACTORY, useValue: () => null },
      { provide: DemoSessionService, useValue: { activeAccountId: () => 'account-1' } },
    ],
  });
  return {
    repository: TestBed.inject(AssistantIssuesRepository),
    http: TestBed.inject(HttpTestingController),
  };
}

describe('AssistantIssuesRepository', () => {
  it('loads the issue summary from the API for the home card', async () => {
    const { repository, http } = provideApiIssues();
    const result = firstValueFrom(repository.summary());
    http.expectOne(API_ISSUES_SUMMARY_PATH).flush({
      openCount: 2, inProgressCount: 1, assignedToMeCount: 1, overdueCount: 1,
    });
    expect(await result).toEqual({ status: 'ready', data: {
      openCount: 2, inProgressCount: 1, assignedToMeCount: 1, overdueCount: 1,
    } });
    http.verify();
  });

  it('passes filters to the API and leaves the default scope off the URL', async () => {
    const { repository, http } = provideApiIssues();
    const result = firstValueFrom(repository.list({ scope: 'assigned', status: 'open', assistantId: 'assistant-1' }));
    const request = http.expectOne((candidate) => candidate.url === API_ISSUES_PATH);
    expect(request.request.params.get('scope')).toBe('assigned');
    expect(request.request.params.get('status')).toBe('open');
    expect(request.request.params.get('assistantId')).toBe('assistant-1');
    request.flush([]);
    expect(await result).toEqual({ status: 'ready', data: [] });
    http.verify();
  });

  it('maps a concurrent update to a recoverable conflict', async () => {
    const { repository, http } = provideApiIssues();
    const result = firstValueFrom(repository.update('issue-1', { status: 'resolved', note: '已修正' }));
    http.expectOne(apiIssuePath('issue-1')).flush(
      { reason: 'issue-changed', message: '這個處理事項剛被其他人更新，請重新整理後再試。' },
      { status: 409, statusText: 'Conflict' },
    );
    expect(await result).toEqual({
      status: 'conflict', reason: 'issue-changed',
      message: '這個處理事項剛被其他人更新，請重新整理後再試。', issueId: undefined,
    });
    http.verify();
  });

  it('posts only saved message IDs after explicit handoff confirmation', async () => {
    const { repository, http } = provideApiIssues();
    const payload = { threadId: 'thread-1', questionMessageId: 'question-1', answerMessageId: 'answer-1', confirmed: true as const };
    const result = firstValueFrom(repository.createHandoff('assistant-1', payload, {
      assistantName: '客服助理', question: '如何退貨？', answer: '七天內可退貨。', historyMode: 'saved',
    }));
    const request = http.expectOne(apiAssistantHandoffsPath('assistant-1'));
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual(payload);
    request.flush(JSON.parse(SAVED_HANDOFF_API_JSON));
    expect(await result).toMatchObject({ status: 'ready', data: {
      id: '01a0faf7-13c7-7aae-b0f8-5ca9991314c1', source: 'handoff',
      question: '可信問題', answer: '可信回答',
    } });
    http.verify();
  });

  it('normalizes the reporter-only slim list and detail without exposing the exchange', async () => {
    const { repository, http } = provideApiIssues();
    const slim = {
      id: 'issue-1', assistantId: 'assistant-1', assistantName: '客服助理', status: 'open',
      resolutionNote: null, createdAt: '2026-10-02T08:00:00Z', updatedAt: '2026-10-02T08:00:00Z', resolvedAt: null,
    };
    const list = firstValueFrom(repository.list({ scope: 'forwarded' }));
    http.expectOne((candidate) => candidate.url === API_ISSUES_PATH && candidate.params.get('scope') === 'forwarded').flush([slim]);
    expect(await list).toMatchObject({ status: 'ready', data: [{
      title: '轉交給專人的問答', source: 'handoff', question: null, answer: null,
    }] });

    const detail = firstValueFrom(repository.get('issue-1'));
    http.expectOne(apiIssuePath('issue-1')).flush({ issue: slim, events: [] });
    expect(await detail).toMatchObject({ status: 'ready', data: { issue: {
      title: '轉交給專人的問答', viewerIsAssistantOwner: false,
    }, events: [] } });
    http.verify();
  });

  it('keeps one consented unsaved exchange in a mock handoff for its reporter', async () => {
    TestBed.configureTestingModule({
      providers: [{ provide: DemoSessionService, useValue: { activeAccountId: () => 'account-1' } }],
    });
    const repository = TestBed.inject(AssistantIssuesRepository);
    const result = await firstValueFrom(repository.createHandoff('assistant-1', {
      sharedQuestion: '如何退貨？', sharedAnswer: '七天內可退貨。', confirmed: true,
    }, { assistantName: '客服助理', question: '如何退貨？', answer: '七天內可退貨。', historyMode: 'not-saved' }));
    expect(result.status).toBe('ready');
    const list = await firstValueFrom(repository.list({ scope: 'forwarded' }));
    expect(list).toMatchObject({ status: 'ready', data: [{ source: 'handoff', question: '如何退貨？', answer: '七天內可退貨。' }] });
  });

  it('keeps mock summaries in sync with newly opened issues', async () => {
    TestBed.configureTestingModule({
      providers: [{ provide: DemoSessionService, useValue: { activeAccountId: () => 'account-1' } }],
    });
    const repository = TestBed.inject(AssistantIssuesRepository);
    const created = await firstValueFrom(repository.create('assistant-1', { testResultId: 'result-1', title: '引用錯誤' }));
    expect(created.status).toBe('ready');
    const summary = await firstValueFrom(repository.summary());
    expect(summary).toEqual({ status: 'ready', data: {
      openCount: 1, inProgressCount: 0, assignedToMeCount: 0, overdueCount: 0,
    } });
  });
});
