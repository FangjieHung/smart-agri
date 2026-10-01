import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { DemoSessionService } from '../session/demo-session.service';
import { AssistantIssuesRepository, API_ISSUES_PATH, API_ISSUES_SUMMARY_PATH, apiIssuePath } from './assistant-issues.repository';
import { API_DEMO_REPOSITORY_FACTORY } from './tokens';

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
