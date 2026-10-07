import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import type { OpenCaseFromIssueRequest } from '../domain/assistant-issue.model';
import { DemoSessionService } from '../session/demo-session.service';
import { AssistantIssuesRepository } from './assistant-issues.repository';
import { CasesRepository } from './cases.repository';
import { resetMockIssueCasesForTest } from './mock-issue-cases';
import { API_DEMO_REPOSITORY_FACTORY } from './tokens';

/** 純 Demo 模式的「另開案件」（issue #252）：與後端相同的權限、409、422，以及案件那一邊看得到連結。 */
function mockRepositories() {
  const viewer = signal<AccountId | null>('account-smb-admin');
  TestBed.configureTestingModule({
    providers: [
      { provide: API_DEMO_REPOSITORY_FACTORY, useValue: null },
      { provide: DemoSessionService, useValue: { activeAccountId: viewer } },
    ],
  });
  return { viewer, issues: TestBed.inject(AssistantIssuesRepository), cases: TestBed.inject(CasesRepository) };
}

const OPTIONS = { activeTypeIds: ['case-type-equipment-repair'], groupIds: ['case-group-equipment', 'case-group-purchasing'] };

function request(overrides: Partial<OpenCaseFromIssueRequest> = {}): OpenCaseFromIssueRequest {
  return {
    typeId: 'case-type-equipment-repair',
    groupId: 'case-group-equipment',
    dueAt: new Date(Date.now() + 72 * 3600_000).toISOString(),
    title: '派人檢查冷藏庫',
    description: '冷藏庫的溫度一直降不下來，可以派人來看嗎？',
    ...overrides,
  };
}

async function createIssue(issues: AssistantIssuesRepository, assigneeAccountId?: string): Promise<string> {
  const created = await firstValueFrom(issues.create('assistant-1', { testResultId: 'result-1', title: '冷藏庫溫度', assigneeAccountId }));
  if (created.status !== 'ready') throw new Error(created.status);
  return created.data.id;
}

describe('AssistantIssuesRepository.openCase (demo mode)', () => {
  afterEach(() => resetMockIssueCasesForTest());

  it('opens a case linked both ways and resolves the issue as 非助理問題', async () => {
    const { issues, cases, viewer } = mockRepositories();
    const issueId = await createIssue(issues);

    const result = await firstValueFrom(issues.openCase(issueId, request(), OPTIONS));
    if (result.status !== 'ready') throw new Error(result.status);
    const caseId = result.data.caseId;
    expect(result.data.issue.issue).toMatchObject({ status: 'resolved', resolutionKind: 'not-assistant-issue', linkedCaseId: caseId });
    expect(result.data.issue.linkedCase).toEqual({ caseId, canOpen: true });
    expect(result.data.issue.events.at(-1)).toMatchObject({ action: 'case-opened', status: 'resolved' });

    const detail = await firstValueFrom(cases.get(caseId));
    if (detail.status !== 'ready') throw new Error(detail.status);
    expect(detail.data.case).toMatchObject({ origin: 'assistant-issue', title: '派人檢查冷藏庫', status: 'pending' });
    expect(detail.data.links.assistantIssue).toEqual({ issueId, canOpen: true });

    // The case behaves like any other: the 設備組 member can accept it.
    viewer.set('account-internal-employee');
    const accepted = await firstValueFrom(cases.act(caseId, 'accept', { eventCount: 1 }));
    expect(accepted.status).toBe('ready');
    if (accepted.status === 'ready') expect(accepted.data.links.assistantIssue).toEqual({ issueId, canOpen: false });
  });

  it('is 409 with the existing case for a second 另開案件, also after reopening, and issue-changed for a resolved issue', async () => {
    const { issues } = mockRepositories();
    const issueId = await createIssue(issues);
    const first = await firstValueFrom(issues.openCase(issueId, request(), OPTIONS));
    if (first.status !== 'ready') throw new Error(first.status);

    expect(await firstValueFrom(issues.openCase(issueId, request(), OPTIONS))).toEqual({
      status: 'conflict', reason: 'case-already-opened', message: '這個處理事項已經另開過案件。', caseId: first.data.caseId,
    });

    const reopened = await firstValueFrom(issues.update(issueId, { status: 'open' }));
    if (reopened.status !== 'ready') throw new Error(reopened.status);
    expect(reopened.data.issue).toMatchObject({ status: 'open', resolutionKind: null, linkedCaseId: null, resolvedAt: null });
    expect(reopened.data.linkedCase).toBeNull();
    expect(await firstValueFrom(issues.openCase(issueId, request(), OPTIONS))).toMatchObject({
      status: 'conflict', reason: 'case-already-opened', caseId: first.data.caseId,
    });

    const fixed = await createIssue(issues);
    const resolved = await firstValueFrom(issues.update(fixed, { status: 'resolved', note: '已修正' }));
    if (resolved.status !== 'ready') throw new Error(resolved.status);
    expect(resolved.data.issue.resolutionKind).toBe('fixed');
    expect(await firstValueFrom(issues.openCase(fixed, request(), OPTIONS))).toMatchObject({ status: 'conflict', reason: 'issue-changed' });
  });

  it('refuses a field like creating a case does and leaves the issue as it was', async () => {
    const { issues } = mockRepositories();
    const issueId = await createIssue(issues);
    const before = await firstValueFrom(issues.get(issueId));

    for (const [overrides, reason, field] of [
      [{ typeId: 'case-type-inactive' }, 'case-type-inactive', 'typeId'],
      [{ groupId: 'case-group-archived' }, 'case-group-archived', 'groupId'],
      [{ dueAt: new Date(Date.now() - 60_000).toISOString() }, 'due-in-past', 'dueAt'],
      [{ title: '  ' }, null, 'title'],
    ] as const) {
      const result = await firstValueFrom(issues.openCase(issueId, request(overrides), OPTIONS));
      expect(result.status).toBe('validation-failed');
      if (result.status === 'validation-failed') {
        expect(result.reason).toBe(reason);
        expect(result.fieldErrors[field]).toBeTruthy();
      }
    }

    expect(await firstValueFrom(issues.get(issueId))).toEqual(before);
  });

  it('is the issue 403 for someone who may not change the issue and for an external customer', async () => {
    const { issues, viewer } = mockRepositories();
    const issueId = await createIssue(issues, 'account-external-customer');

    for (const account of ['account-internal-employee', 'account-external-customer'] as const) {
      viewer.set(account);
      expect(await firstValueFrom(issues.openCase(issueId, request(), OPTIONS))).toMatchObject({
        status: 'permission-denied', reason: 'assistant-issue',
      });
    }
    viewer.set('account-smb-admin');
    const issue = await firstValueFrom(issues.get(issueId));
    expect(issue.status === 'ready' && issue.data.issue.status).toBe('open');
  });
});
