import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { catchError, defer, map, of, throwError, type Observable } from 'rxjs';
import type {
  AssistantIssueActionResult,
  AssistantIssueDetailView,
  AssistantIssueFilters,
  AssistantIssueSummaryView,
  AssistantIssueView,
  CreateAssistantIssueRequest,
  UpdateAssistantIssueRequest,
} from '../domain/assistant-issue.model';
import type { AssistantTestResultView } from '../domain/assistant-acceptance.model';
import { DemoSessionService } from '../session/demo-session.service';
import type { RepositoryView } from './demo-repository';
import { API_DEMO_REPOSITORY_FACTORY } from './tokens';

export const API_ISSUES_PATH = '/api/v1/issues';
export const API_ISSUES_SUMMARY_PATH = `${API_ISSUES_PATH}/summary`;

export interface MockTestIssueContext {
  readonly assistantName: string;
  readonly runId: string;
  readonly result: AssistantTestResultView;
}

export function apiIssuePath(issueId: string): string {
  return `${API_ISSUES_PATH}/${encodeURIComponent(issueId)}`;
}

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
  private readonly mockDetails = new Map<string, AssistantIssueDetailView>();
  private readonly mockOwners = new Map<string, string | null>();

  list(filters: AssistantIssueFilters = {}): Observable<RepositoryView<readonly AssistantIssueView[]>> {
    if (this.apiMode) {
      let params = new HttpParams();
      if (filters.scope && filters.scope !== 'all') params = params.set('scope', filters.scope);
      if (filters.status) params = params.set('status', filters.status);
      if (filters.assistantId) params = params.set('assistantId', filters.assistantId);
      return this.client().get<AssistantIssueView[]>(API_ISSUES_PATH, { params }).pipe(
        map((data) => ({ status: 'ready' as const, data })),
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
      return this.client().get<AssistantIssueDetailView>(apiIssuePath(issueId)).pipe(
        map((data) => ({ status: 'ready' as const, data })),
        catchError((error: unknown) => this.readError<AssistantIssueDetailView>(error)),
      );
    }
    return defer(() => {
      const detail = this.mockDetails.get(issueId);
      return of(detail && this.mockVisible(detail.issue, this.session.activeAccountId())
        ? { status: 'ready' as const, data: detail } : ISSUE_DENIED);
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
      };
      this.mockDetails.set(id, { issue, events: [] });
      this.mockOwners.set(id, accountId);
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
      if (!current || !this.mockVisible(current.issue, this.session.activeAccountId())) return of(ISSUE_DENIED);
      const now = new Date().toISOString();
      const issue: AssistantIssueView = {
        ...current.issue,
        assigneeAccountId: request.unassign ? null : request.assigneeAccountId ?? current.issue.assigneeAccountId,
        dueAt: request.clearDueAt ? null : request.dueAt ?? current.issue.dueAt,
        status: request.status === 'open' || request.status === 'in-progress' || request.status === 'resolved'
          ? request.status : current.issue.status,
        resolutionNote: request.status === 'resolved' ? request.note ?? current.issue.resolutionNote : current.issue.resolutionNote,
        resolvedAt: request.status === 'resolved' ? now : current.issue.resolvedAt,
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
      return of({ status: 'ready' as const, data: detail });
    });
  }

  private client(): HttpClient {
    if (!this.http) throw new Error('API 模式缺少 HttpClient');
    return this.http;
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
