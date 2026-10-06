import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { catchError, defer, map, of, throwError, type Observable } from 'rxjs';
import {
  ISSUE_CASE_ALREADY_OPENED_MESSAGE,
  ISSUE_CHANGED_MESSAGE,
  type AssistantIssueActionResult,
  type AssistantIssueDetailView,
  type AssistantIssueFilters,
  type AssistantIssueOpenedCaseView,
  type AssistantIssueSummaryView,
  type AssistantIssueView,
  type CreateAssistantIssueRequest,
  type OpenCaseField,
  type OpenCaseFromIssueRequest,
  type OpenCaseFromIssueResult,
  type UpdateAssistantIssueRequest,
} from '../domain/assistant-issue.model';
import type { AssistantTestResultView } from '../domain/assistant-acceptance.model';
import { DemoSessionService } from '../session/demo-session.service';
import { DEMO_SEED } from './demo-seed';
import type { RepositoryView } from './demo-repository';
import { checkMockOpenCaseFields, mockIssueCases, recordMockIssueCase, type MockOpenCaseOptions } from './mock-issue-cases';
import { API_DEMO_REPOSITORY_FACTORY } from './tokens';

export const API_ISSUES_PATH = '/api/v1/issues';
export const API_ISSUES_SUMMARY_PATH = `${API_ISSUES_PATH}/summary`;

export interface MockTestIssueContext {
  readonly assistantName: string;
  readonly runId: string;
  readonly result: AssistantTestResultView;
}

export interface CreateAssistantHandoffRequest {
  readonly threadId?: string;
  readonly questionMessageId?: string;
  readonly answerMessageId?: string;
  readonly sharedQuestion?: string;
  readonly sharedAnswer?: string;
  readonly confirmed: true;
}

export interface MockHandoffContext {
  readonly assistantName: string;
  readonly question: string;
  readonly answer: string;
  readonly historyMode: 'saved' | 'not-saved';
}

interface ForwardedAssistantIssueView {
  readonly id: string;
  readonly assistantId: string;
  readonly assistantName: string;
  readonly status: AssistantIssueView['status'];
  readonly resolutionNote: string | null;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly resolvedAt: string | null;
  readonly resolutionKind?: AssistantIssueView['resolutionKind'];
}

export function apiAssistantHandoffsPath(assistantId: string): string {
  return `/api/v1/assistants/${encodeURIComponent(assistantId)}/chat/handoffs`;
}

export function apiIssuePath(issueId: string): string {
  return `${API_ISSUES_PATH}/${encodeURIComponent(issueId)}`;
}

/** 「另開案件」（issue #252）。 */
export function apiIssueOpenCasePath(issueId: string): string {
  return `${apiIssuePath(issueId)}:open-case`;
}

const OPEN_CASE_FIELDS: readonly OpenCaseField[] = ['typeId', 'groupId', 'dueAt', 'title', 'description'];

export function apiAssistantIssuesPath(assistantId: string): string {
  return `/api/v1/assistants/${encodeURIComponent(assistantId)}/issues`;
}

const ISSUE_DENIED = {
  status: 'permission-denied' as const,
  reason: 'assistant-issue' as const,
  message: '找不到這個處理事項，或你的帳號沒有權限查看。',
};

/**
 * M3.5 處理事項的單一資料入口。API 模式讀寫真實端點；純 Demo 模式保留於本次
 * 瀏覽器工作階段，讓畫面仍可展示建立、指派與處理流程。
 */
@Injectable({ providedIn: 'root' })
export class AssistantIssuesRepository {
  private readonly apiMode = inject(API_DEMO_REPOSITORY_FACTORY) !== null;
  private readonly http = inject(HttpClient, { optional: true });
  private readonly session = inject(DemoSessionService);
  private readonly mockDetails = new Map<string, Omit<AssistantIssueDetailView, 'linkedCase'>>();
  private readonly mockOwners = new Map<string, string | null>();

  list(filters: AssistantIssueFilters = {}): Observable<RepositoryView<readonly AssistantIssueView[]>> {
    if (this.apiMode) {
      let params = new HttpParams();
      if (filters.scope && filters.scope !== 'all') params = params.set('scope', filters.scope);
      if (filters.status) params = params.set('status', filters.status);
      if (filters.assistantId) params = params.set('assistantId', filters.assistantId);
      return this.client().get<(AssistantIssueView | ForwardedAssistantIssueView)[]>(API_ISSUES_PATH, { params }).pipe(
        map((data) => ({ status: 'ready' as const, data: data.map((issue) => this.normalizeIssue(issue)) })),
        catchError((error: unknown) => this.readError<readonly AssistantIssueView[]>(error)),
      );
    }
    return defer(() => {
      const accountId = this.session.activeAccountId();
      const data = [...this.mockDetails.values()]
        .map((detail) => detail.issue)
        .filter((issue) => {
          if (!this.mockVisible(issue, accountId)) return false;
          if (filters.scope === 'owned' && this.mockOwners.get(issue.id) !== accountId) return false;
          if (filters.scope === 'assigned' && issue.assigneeAccountId !== accountId) return false;
          if (filters.scope === 'forwarded' && issue.reporterAccountId !== accountId) return false;
          return (!filters.status || issue.status === filters.status)
            && (!filters.assistantId || issue.assistantId === filters.assistantId);
        })
        .sort((a, b) => b.createdAt.localeCompare(a.createdAt));
      return of({ status: 'ready' as const, data });
    });
  }

  summary(): Observable<RepositoryView<AssistantIssueSummaryView>> {
    if (this.apiMode) {
      return this.client().get<AssistantIssueSummaryView>(API_ISSUES_SUMMARY_PATH).pipe(
        map((data) => ({ status: 'ready' as const, data })),
        catchError((error: unknown) => this.readError<AssistantIssueSummaryView>(error)),
      );
    }
    return defer(() => {
      const now = new Date().toISOString();
      const accountId = this.session.activeAccountId();
      const issues = [...this.mockDetails.values()].map((detail) => detail.issue)
        .filter((issue) => this.mockVisible(issue, accountId) && issue.status !== 'resolved');
      return of({ status: 'ready' as const, data: {
        openCount: issues.filter((issue) => issue.status === 'open').length,
        inProgressCount: issues.filter((issue) => issue.status === 'in-progress').length,
        assignedToMeCount: issues.filter((issue) => issue.assigneeAccountId === accountId).length,
        overdueCount: issues.filter((issue) => issue.dueAt !== null && issue.dueAt < now).length,
      } });
    });
  }

  get(issueId: string): Observable<RepositoryView<AssistantIssueDetailView>> {
    if (this.apiMode) {
      return this.client().get<AssistantIssueDetailView | { issue: ForwardedAssistantIssueView; events: [] }>(apiIssuePath(issueId)).pipe(
        map((data) => ({ status: 'ready' as const, data: {
          events: data.events, issue: this.normalizeIssue(data.issue),
          // 轉交人只看得到狀態與結果（不含案件連結）。
          linkedCase: 'linkedCase' in data ? data.linkedCase : null,
        } })),
        catchError((error: unknown) => this.readError<AssistantIssueDetailView>(error)),
      );
    }
    return defer(() => {
      const detail = this.mockDetails.get(issueId);
      const accountId = this.session.activeAccountId();
      return of(detail && this.mockVisible(detail.issue, accountId)
        ? { status: 'ready' as const, data: this.mockDetail(issueId, detail, accountId) } : ISSUE_DENIED);
    });
  }

  create(assistantId: string, request: CreateAssistantIssueRequest, context?: MockTestIssueContext): Observable<AssistantIssueActionResult<AssistantIssueView>> {
    if (this.apiMode) {
      return this.client().post<AssistantIssueView>(apiAssistantIssuesPath(assistantId), request).pipe(
        map((data) => ({ status: 'ready' as const, data })),
        catchError((error: unknown) => this.actionError<AssistantIssueView>(error)),
      );
    }
    return defer(() => {
      const now = new Date().toISOString();
      const id = crypto.randomUUID();
      const accountId = this.session.activeAccountId();
      const issue: AssistantIssueView = {
        id, assistantId, assistantName: context?.assistantName ?? '助理', source: 'test-failure', status: 'open',
        title: request.title?.trim() || context?.result.question || '需要處理的測試結果',
        assigneeAccountId: request.assigneeAccountId ?? null, assigneeDisplayName: null,
        reporterAccountId: null, reporterDisplayName: null, dueAt: request.dueAt ?? null,
        testRunId: context?.runId ?? null, testResultId: request.testResultId,
        testFailureReason: context?.result.failureReason ?? null,
        question: context?.result.question ?? request.title ?? null,
        answer: context?.result.answerText ?? null,
        resolutionNote: null, createdAt: now, updatedAt: now, resolvedAt: null,
        viewerIsAssistantOwner: true, viewerIsAssignee: request.assigneeAccountId === accountId,
        handoffUnverified: false, resolutionKind: null, linkedCaseId: null,
      };
      this.mockDetails.set(id, { issue, events: [] });
      this.mockOwners.set(id, accountId);
      return of({ status: 'ready' as const, data: issue });
    });
  }

  createHandoff(assistantId: string, request: CreateAssistantHandoffRequest, context: MockHandoffContext): Observable<AssistantIssueActionResult<AssistantIssueView>> {
    if (this.apiMode) {
      return this.client().post<AssistantIssueView>(apiAssistantHandoffsPath(assistantId), request).pipe(
        map((data) => ({ status: 'ready' as const, data })),
        catchError((error: unknown) => this.actionError<AssistantIssueView>(error)),
      );
    }
    return defer(() => {
      const accountId = this.session.activeAccountId();
      if (!accountId) return of(ISSUE_DENIED);
      const now = new Date().toISOString();
      const id = crypto.randomUUID();
      const owner = DEMO_SEED.assistants.find((assistant) => assistant.id === assistantId)?.ownerAccountId ?? null;
      const reporterName = DEMO_SEED.accounts.find((account) => account.id === accountId)?.displayName ?? '目前帳號';
      const issue: AssistantIssueView = {
        id, assistantId, assistantName: context.assistantName, source: 'handoff', status: 'open',
        title: context.question, assigneeAccountId: null, assigneeDisplayName: null,
        reporterAccountId: accountId, reporterDisplayName: reporterName, dueAt: null,
        testRunId: null, testResultId: null, testFailureReason: null,
        question: context.question, answer: context.answer, resolutionNote: null,
        createdAt: now, updatedAt: now, resolvedAt: null,
        viewerIsAssistantOwner: owner === accountId, viewerIsAssignee: false,
        handoffUnverified: context.historyMode === 'not-saved', resolutionKind: null, linkedCaseId: null,
      };
      this.mockDetails.set(id, { issue, events: [{
        id: crypto.randomUUID(), action: 'created', actorAccountId: accountId,
        actorDisplayName: reporterName, at: now, note: null,
        assigneeAccountId: null, assigneeDisplayName: null, status: 'open', dueAt: null,
      }] });
      this.mockOwners.set(id, owner);
      return of({ status: 'ready' as const, data: issue });
    });
  }

  update(issueId: string, request: UpdateAssistantIssueRequest): Observable<AssistantIssueActionResult<AssistantIssueDetailView>> {
    if (this.apiMode) {
      return this.client().patch<AssistantIssueDetailView>(apiIssuePath(issueId), request).pipe(
        map((data) => ({ status: 'ready' as const, data })),
        catchError((error: unknown) => this.actionError<AssistantIssueDetailView>(error)),
      );
    }
    return defer(() => {
      const current = this.mockDetails.get(issueId);
      const accountId = this.session.activeAccountId();
      if (!current || (this.mockOwners.get(issueId) !== accountId && current.issue.assigneeAccountId !== accountId)) return of(ISSUE_DENIED);
      const now = new Date().toISOString();
      const status = request.status === 'open' || request.status === 'in-progress' || request.status === 'resolved'
        ? request.status : current.issue.status;
      const statusChanged = status !== current.issue.status;
      const issue: AssistantIssueView = {
        ...current.issue,
        assigneeAccountId: request.unassign ? null : request.assigneeAccountId ?? current.issue.assigneeAccountId,
        dueAt: request.clearDueAt ? null : request.dueAt ?? current.issue.dueAt,
        status,
        // 與後端相同：解決時記下結案方式（助理已修正），重開時清空結案方式與案件連結。
        resolutionNote: !statusChanged ? current.issue.resolutionNote : status === 'resolved' ? request.note?.trim() || null : null,
        resolvedAt: !statusChanged ? current.issue.resolvedAt : status === 'resolved' ? now : null,
        resolutionKind: !statusChanged ? current.issue.resolutionKind : status === 'resolved' ? 'fixed' : null,
        linkedCaseId: statusChanged ? null : current.issue.linkedCaseId,
        updatedAt: now,
      };
      const events = [...current.events];
      const base = { actorAccountId: this.session.activeAccountId() ?? '', actorDisplayName: '目前帳號',
        at: now, note: null, assigneeAccountId: null, assigneeDisplayName: null, status: null, dueAt: null };
      if (issue.assigneeAccountId !== current.issue.assigneeAccountId) {
        events.push({ ...base, id: crypto.randomUUID(), action: 'assigned', assigneeAccountId: issue.assigneeAccountId });
      }
      if (issue.dueAt !== current.issue.dueAt) {
        events.push({ ...base, id: crypto.randomUUID(), action: 'due-date-changed', dueAt: issue.dueAt });
      }
      if (issue.status !== current.issue.status) {
        events.push({ ...base, id: crypto.randomUUID(), action: 'status-changed', status: issue.status, note: request.note?.trim() || null });
      } else if (request.note?.trim()) {
        events.push({ ...base, id: crypto.randomUUID(), action: 'commented', note: request.note.trim() });
      }
      const detail = { issue, events };
      this.mockDetails.set(issueId, detail);
      return of({ status: 'ready' as const, data: this.mockDetail(issueId, detail, accountId) });
    });
  }

  /**
   * 「另開案件」（issue #252）：建立案件、處理事項以「非助理問題」結案並互相連結，全部一起成功或都不寫入。
   * `options` 只給純 Demo 模式：對話框選得到的類型與承辦組（mock 以它判斷停用與封存）。
   */
  openCase(issueId: string, request: OpenCaseFromIssueRequest, options: MockOpenCaseOptions): Observable<OpenCaseFromIssueResult> {
    if (this.apiMode) {
      return this.client().post<AssistantIssueOpenedCaseView>(apiIssueOpenCasePath(issueId), request).pipe(
        map((data): OpenCaseFromIssueResult => ({ status: 'ready', data })),
        catchError((error: unknown) => this.openCaseError(error)),
      );
    }
    return defer(() => {
      const current = this.mockDetails.get(issueId);
      const accountId = this.session.activeAccountId();
      const role = DEMO_SEED.accounts.find((account) => account.id === accountId)?.role;
      if (!current || !accountId || role === 'external-customer'
        || (this.mockOwners.get(issueId) !== accountId && current.issue.assigneeAccountId !== accountId)) {
        return of<OpenCaseFromIssueResult>(ISSUE_DENIED);
      }
      const existing = mockIssueCases().find((item) => item.issueId === issueId);
      if (existing) {
        return of<OpenCaseFromIssueResult>({
          status: 'conflict', reason: 'case-already-opened', message: ISSUE_CASE_ALREADY_OPENED_MESSAGE, caseId: existing.id,
        });
      }
      if (current.issue.status === 'resolved') {
        return of<OpenCaseFromIssueResult>({ status: 'conflict', reason: 'issue-changed', message: ISSUE_CHANGED_MESSAGE });
      }
      const checked = checkMockOpenCaseFields(request, options, new Date());
      if ('fieldErrors' in checked) return of<OpenCaseFromIssueResult>({ status: 'validation-failed', ...checked });

      const now = new Date().toISOString();
      const caseId = crypto.randomUUID();
      recordMockIssueCase({ id: caseId, issueId, ...checked, createdBy: accountId, createdAt: now });
      const actorDisplayName = DEMO_SEED.accounts.find((account) => account.id === accountId)?.displayName ?? '目前帳號';
      const detail = {
        issue: {
          ...current.issue, status: 'resolved' as const, resolvedAt: now, resolutionNote: null,
          resolutionKind: 'not-assistant-issue' as const, linkedCaseId: caseId, updatedAt: now,
        },
        events: [...current.events, {
          id: crypto.randomUUID(), action: 'case-opened' as const, actorAccountId: accountId, actorDisplayName,
          at: now, note: null, assigneeAccountId: null, assigneeDisplayName: null, status: 'resolved' as const, dueAt: null,
        }],
      };
      this.mockDetails.set(issueId, detail);
      return of<OpenCaseFromIssueResult>({ status: 'ready', data: { caseId, issue: this.mockDetail(issueId, detail, accountId) } });
    });
  }

  /** mock 的詳情：檢視者旗標，以及另開的案件（建立者與管理者打得開，其他人看不到）。 */
  private mockDetail(
    issueId: string, detail: Omit<AssistantIssueDetailView, 'linkedCase'>, accountId: string | null,
  ): AssistantIssueDetailView {
    const linked = detail.issue.linkedCaseId ? mockIssueCases().find((item) => item.id === detail.issue.linkedCaseId) : undefined;
    const role = DEMO_SEED.accounts.find((account) => account.id === accountId)?.role;
    return {
      ...detail,
      issue: {
        ...detail.issue,
        viewerIsAssistantOwner: this.mockOwners.get(issueId) === accountId,
        viewerIsAssignee: detail.issue.assigneeAccountId === accountId,
      },
      linkedCase: detail.issue.linkedCaseId
        ? { caseId: detail.issue.linkedCaseId, canOpen: role === 'smb-admin' || linked?.createdBy === accountId }
        : null,
    };
  }

  private client(): HttpClient {
    if (!this.http) throw new Error('API 模式缺少 HttpClient');
    return this.http;
  }

  private normalizeIssue(issue: AssistantIssueView | ForwardedAssistantIssueView): AssistantIssueView {
    if ('title' in issue) return issue;
    return {
      ...issue,
      title: '轉交給專人的問答', source: 'handoff',
      assigneeAccountId: null, assigneeDisplayName: null,
      reporterAccountId: this.session.activeAccountId(), reporterDisplayName: null,
      dueAt: null, testRunId: null, testResultId: null, testFailureReason: null,
      question: null, answer: null,
      viewerIsAssistantOwner: false, viewerIsAssignee: false,
      handoffUnverified: false, resolutionKind: issue.resolutionKind ?? null, linkedCaseId: null,
    };
  }

  private mockVisible(issue: AssistantIssueView, accountId: string | null): boolean {
    return accountId !== null && (
      this.mockOwners.get(issue.id) === accountId ||
      issue.assigneeAccountId === accountId ||
      issue.reporterAccountId === accountId
    );
  }

  private readError<T>(error: unknown): Observable<RepositoryView<T>> {
    if (error instanceof HttpErrorResponse && (error.status === 403 || error.status === 404)) {
      return of(ISSUE_DENIED);
    }
    return throwError(() => error);
  }

  /** `403`／`404` 是 permission-denied、`409` 是 conflict（帶既有案件的 `caseId`）、`422` 帶欄位錯誤。 */
  private openCaseError(error: unknown): Observable<OpenCaseFromIssueResult> {
    if (!(error instanceof HttpErrorResponse)) return throwError(() => error);
    const body = error.error && typeof error.error === 'object' ? error.error as Record<string, unknown> : {};
    const reason = typeof body['reason'] === 'string' ? body['reason'] : null;
    const message = typeof body['message'] === 'string' ? body['message'] : null;
    if (error.status === 403 || error.status === 404) {
      return of({ status: 'permission-denied', reason: reason ?? ISSUE_DENIED.reason, message: message ?? ISSUE_DENIED.message });
    }
    if (error.status === 409) {
      return of({
        status: 'conflict', reason: reason ?? 'issue-changed', message: message ?? ISSUE_CHANGED_MESSAGE,
        ...(typeof body['caseId'] === 'string' ? { caseId: body['caseId'] } : {}),
      });
    }
    if (error.status === 422) {
      const errors = body['errors'] && typeof body['errors'] === 'object' ? body['errors'] as Record<string, unknown> : {};
      const fieldErrors: Partial<Record<OpenCaseField, string>> = {};
      for (const field of OPEN_CASE_FIELDS) {
        const messages = errors[field];
        if (Array.isArray(messages) && typeof messages[0] === 'string') fieldErrors[field] = messages[0];
      }
      return of({ status: 'validation-failed', reason, message: message ?? Object.values(fieldErrors)[0] ?? '', fieldErrors });
    }
    return throwError(() => error);
  }

  private actionError<T>(error: unknown): Observable<AssistantIssueActionResult<T>> {
    if (!(error instanceof HttpErrorResponse)) return throwError(() => error);
    if (error.status !== 403 && error.status !== 404 && error.status !== 409 && error.status !== 422) {
      return throwError(() => error);
    }
    const body = error.error && typeof error.error === 'object' ? error.error as Record<string, unknown> : {};
    const status = error.status === 409 ? 'conflict' : error.status === 422 ? 'validation-failed' : 'permission-denied';
    return of({
      status,
      reason: typeof body['reason'] === 'string' ? body['reason'] : 'assistant-issue',
      message: typeof body['message'] === 'string' ? body['message'] : ISSUE_DENIED.message,
      issueId: typeof body['issueId'] === 'string' ? body['issueId'] : undefined,
    });
  }
}
