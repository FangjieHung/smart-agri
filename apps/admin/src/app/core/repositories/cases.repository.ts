import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { catchError, defer, forkJoin, map, of, switchMap, throwError, type Observable } from 'rxjs';
import type { AccountId, AccountRole } from '../domain/account.model';
import {
  CASE_DESCRIPTION_MAX_LENGTH,
  CASE_DESCRIPTION_TOO_LONG_MESSAGE,
  CASE_DUE_IN_PAST_MESSAGE,
  CASE_DUE_REQUIRED_MESSAGE,
  CASE_GROUP_REQUIRED_MESSAGE,
  CASE_TITLE_MAX_LENGTH,
  CASE_TITLE_REQUIRED_MESSAGE,
  CASE_TITLE_TOO_LONG_MESSAGE,
  CASE_TYPE_INACTIVE_MESSAGE,
  CASE_TYPE_REQUIRED_MESSAGE,
  OPEN_CASE_STATUSES,
  type CaseDetailView,
  type CaseEventView,
  type CaseListFilter,
  type CaseOrigin,
  type CaseStatus,
  type CaseSummaryView,
  type CreateCaseRequest,
} from '../domain/case.model';
import type { CaseGroupView, CaseTypeView } from '../domain/case-settings.model';
import { DemoSessionService } from '../session/demo-session.service';
import {
  CASE_FEATURE_DENIED_MESSAGE,
  CASE_TYPE_GROUP_ARCHIVED_MESSAGE,
  CaseSettingsRepository,
} from './case-settings.repository';
import { DEMO_SEED } from './demo-seed';
import type { PermissionDeniedRepositoryView, RepositoryView } from './demo-repository';
import { API_DEMO_REPOSITORY_FACTORY } from './tokens';

export const API_CASES_PATH = '/api/v1/cases';

export function apiCasePath(caseId: string): string {
  return `${API_CASES_PATH}/${encodeURIComponent(caseId)}`;
}

/** 建立案件表單的欄位（與 API 的欄位名稱相同）。 */
export type CaseField = 'typeId' | 'groupId' | 'dueAt' | 'title' | 'description' | 'submissionId' | 'threadId' | 'previousCaseId';
const CASE_FIELDS: readonly CaseField[] = ['typeId', 'groupId', 'dueAt', 'title', 'description', 'submissionId', 'threadId', 'previousCaseId'];

/**
 * `422`：`reason` 是後端的原因（`due-in-past`、`case-type-inactive`、`case-group-archived`、
 * `link-not-available`…；單純的欄位錯誤是 `null`），`fieldErrors` 是每個欄位的第一則錯誤。什麼都沒有寫入。
 */
export interface CaseValidationFailedView {
  readonly status: 'validation-failed';
  readonly reason: string | null;
  readonly message: string;
  readonly fieldErrors: Readonly<Partial<Record<CaseField, string>>>;
}

export type CreateCaseResult = RepositoryView<CaseDetailView> | CaseValidationFailedView;

/** 看不到、不存在、別的組織、外部客戶：後端一律是同一個 `403 case`。 */
const CASE_DENIED: PermissionDeniedRepositoryView = {
  status: 'permission-denied',
  reason: 'case',
  message: CASE_FEATURE_DENIED_MESSAGE,
};

interface MockCase {
  readonly id: string;
  readonly typeId: string;
  readonly groupId: string;
  readonly status: CaseStatus;
  readonly origin: CaseOrigin;
  readonly title: string;
  readonly description: string;
  readonly createdBy: AccountId;
  readonly dueAt: string;
  readonly createdAt: string;
  readonly completedAt: string | null;
  /** 連結的對話；`available: false` 模擬對話已被保存期限刪除。 */
  readonly thread: { readonly assistantId: string; readonly threadId: string; readonly available: boolean } | null;
  readonly record: { readonly databaseId: string; readonly submissionId: string } | null;
  readonly previousCaseId: string | null;
}

/**
 * mock 的範例案件（只放在這個檔案，不加進 `demo-seed.ts`：後端有測試會讀它）。承辦組與類型沿用
 * `CaseSettingsRepository` 的範例：設備組（成員：客服同仁）、採購組（成員：管理者）。
 */
const MOCK_CASES: readonly MockCase[] = [
  {
    id: 'case-cold-room', typeId: 'case-type-equipment-repair', groupId: 'case-group-equipment', status: 'pending', origin: 'manual',
    title: '冷藏庫溫度降不下來', description: '二號冷藏庫從早上開始維持在 9 度，請派人到場檢查。',
    createdBy: 'account-internal-employee', dueAt: '2026-10-09T01:00:00.000Z', createdAt: '2026-10-06T01:00:00.000Z', completedAt: null,
    thread: { assistantId: 'assistant-internal-onboarding', threadId: 'thread-cold-room-deleted', available: false },
    record: null, previousCaseId: null,
  },
  {
    id: 'case-compressor-purchase', typeId: 'case-type-equipment-repair', groupId: 'case-group-purchasing', status: 'pending', origin: 'manual',
    title: '採購備用壓縮機', description: '維修廠商建議備一台壓縮機，請採購組詢價。',
    createdBy: 'account-smb-admin', dueAt: '2026-10-12T01:00:00.000Z', createdAt: '2026-10-06T03:00:00.000Z', completedAt: null,
    thread: null, record: { databaseId: 'database-customer-records', submissionId: 'submission-mock-compressor' }, previousCaseId: null,
  },
  {
    id: 'case-irrigation-done', typeId: 'case-type-equipment-repair', groupId: 'case-group-equipment', status: 'completed', origin: 'manual',
    title: '灌溉馬達異音', description: '已更換軸承。',
    createdBy: 'account-internal-employee', dueAt: '2026-10-03T01:00:00.000Z', createdAt: '2026-10-01T02:00:00.000Z',
    completedAt: '2026-10-02T06:00:00.000Z', thread: null, record: null, previousCaseId: null,
  },
];

/** 與後端 `CaseEndpoints` 相同的連結拒絕訊息。 */
export const CASE_RECORD_NOT_AVAILABLE_MESSAGE = '這筆紀錄目前無法連結：你沒有讀取它的權限，或它已撤回、不存在。';
export const CASE_THREAD_NOT_AVAILABLE_MESSAGE = '只能連結你自己的對話，而且對話必須還在。';
export const CASE_PREVIOUS_NOT_AVAILABLE_MESSAGE = '只能連結你看得到、而且已結案的案件。';

const REMOVED_ACCOUNT_NAME = '已停用的帳號';

/**
 * 案件的單一資料入口（issue #248）。API 模式讀寫 `/api/v1/cases`；純 Demo 模式只保存在這次工作
 * 階段，判斷與後端相同：只有內部帳號；看得到的人是建立者、目前承辦組的成員與管理者；時限不能早於
 * 現在，停用的類型與封存的承辦組不能用。只會在 `/app/cases`（lazy）注入，不進初始 bundle。
 */
@Injectable({ providedIn: 'root' })
export class CasesRepository {
  private readonly apiMode = inject(API_DEMO_REPOSITORY_FACTORY) !== null;
  private readonly http = inject(HttpClient, { optional: true });
  private readonly session = inject(DemoSessionService);
  private readonly settings = inject(CaseSettingsRepository);
  private mockCases: MockCase[] = MOCK_CASES.map((item) => ({ ...item }));

  /** 看得到的案件，新的在前；預設只列未結案（決定 Q）。 */
  list(filter: CaseListFilter = {}): Observable<RepositoryView<readonly CaseSummaryView[]>> {
    if (this.apiMode) {
      let params = new HttpParams();
      for (const key of ['scope', 'status', 'typeId', 'groupId'] as const) {
        const value = filter[key];
        if (value) params = params.set(key, value);
      }
      return this.client().get<CaseSummaryView[]>(API_CASES_PATH, { params }).pipe(
        map((data): RepositoryView<readonly CaseSummaryView[]> => ({ status: 'ready', data })),
        catchError((error: unknown) => this.denied<readonly CaseSummaryView[]>(error)),
      );
    }
    return this.withMockContext((context) => {
      const viewer = context.viewer;
      const scope = filter.scope ?? 'all';
      const status = filter.status ?? 'open';
      const rows = this.mockCases
        .filter((item) => this.mockVisible(item, context))
        .filter((item) => scope !== 'created' || item.createdBy === viewer)
        .filter(() => scope !== 'owned')
        .filter((item) => scope !== 'my-groups' || this.mockIsMember(item.groupId, context))
        .filter((item) => status === 'all'
          || (status === 'open' ? OPEN_CASE_STATUSES.includes(item.status)
            : status === 'closed' ? !OPEN_CASE_STATUSES.includes(item.status)
              : item.status === status))
        .filter((item) => !filter.typeId || item.typeId === filter.typeId)
        .filter((item) => !filter.groupId || item.groupId === filter.groupId)
        .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
        .map((item) => this.mockSummary(item, context));
      return of<RepositoryView<readonly CaseSummaryView[]>>({ status: 'ready', data: rows });
    });
  }

  /** 案件、事件與連結；看不到時是 `403 case`。 */
  get(caseId: string): Observable<RepositoryView<CaseDetailView>> {
    if (this.apiMode) {
      return this.client().get<CaseDetailView>(apiCasePath(caseId)).pipe(
        map((data): RepositoryView<CaseDetailView> => ({ status: 'ready', data })),
        catchError((error: unknown) => this.denied<CaseDetailView>(error)),
      );
    }
    return this.withMockContext((context) => {
      const item = this.mockCases.find((candidate) => candidate.id === caseId);
      if (!item || !this.mockVisible(item, context)) return of(CASE_DENIED);
      return of<RepositoryView<CaseDetailView>>({ status: 'ready', data: this.mockDetail(item, context) });
    });
  }

  /** 建立案件（內部帳號）：欄位、時限、類型、承辦組與連結的錯誤都是 validation-failed。 */
  create(request: CreateCaseRequest): Observable<CreateCaseResult> {
    if (this.apiMode) {
      return this.client().post<CaseDetailView>(API_CASES_PATH, request).pipe(
        map((data): CreateCaseResult => ({ status: 'ready', data })),
        catchError((error: unknown) => {
          if (error instanceof HttpErrorResponse && error.status === 422) return of(validationFailed(error));
          return this.denied<CaseDetailView>(error);
        }),
      );
    }
    return this.withMockContext<CreateCaseResult>((context) => {
      const checked = this.mockCheck(request, context);
      if ('status' in checked) return of(checked);
      const now = new Date().toISOString();
      const item: MockCase = {
        id: crypto.randomUUID(), typeId: checked.typeId, groupId: checked.groupId, status: 'pending', origin: 'manual',
        title: checked.title, description: checked.description, createdBy: context.viewer, dueAt: checked.dueAt,
        createdAt: now, completedAt: null, thread: null, record: null, previousCaseId: checked.previousCaseId,
      };
      this.mockCases = [...this.mockCases, item];
      return of<CreateCaseResult>({ status: 'ready', data: this.mockDetail(item, context) });
    });
  }

  private client(): HttpClient {
    if (!this.http) throw new Error('API 模式缺少 HttpClient');
    return this.http;
  }

  /** `403`：後端帶的原因（`case`、`password-change-required`）照用；其他狀態照常拋出。 */
  private denied<T>(error: unknown): Observable<RepositoryView<T>> {
    if (!(error instanceof HttpErrorResponse) || error.status !== 403) return throwError(() => error);
    const body = error.error && typeof error.error === 'object' ? error.error as Record<string, unknown> : {};
    const reason = body['reason'];
    const message = typeof body['message'] === 'string' ? body['message'] : CASE_DENIED.message;
    if (reason === 'case' || reason === 'password-change-required') return of({ status: 'permission-denied', reason, message });
    return of(CASE_DENIED);
  }

  /** mock 需要的承辦組（含成員）與啟用中的類型；外部客戶或沒有身分時一律 `403 case`。 */
  private withMockContext<T>(build: (context: MockContext) => Observable<T | PermissionDeniedRepositoryView>): Observable<T | PermissionDeniedRepositoryView> {
    return defer(() => {
      const viewer = this.session.activeAccountId();
      const role = DEMO_SEED.accounts.find((account) => account.id === viewer)?.role ?? null;
      if (viewer === null || role === null || role === 'external-customer') return of(CASE_DENIED);
      return forkJoin([this.settings.listCaseGroups(), this.settings.listCaseTypes()]).pipe(
        map(([groups, types]) => ({
          viewer,
          role,
          groups: groups.status === 'ready' ? groups.data.groups : [],
          types: types.status === 'ready' ? types.data.types : [],
        })),
        switchMap(build),
      );
    });
  }

  private mockVisible(item: MockCase, context: MockContext): boolean {
    return context.role === 'smb-admin' || item.createdBy === context.viewer || this.mockIsMember(item.groupId, context);
  }

  private mockIsMember(groupId: string, context: MockContext): boolean {
    return context.groups.some((group) => group.id === groupId && group.members.some((member) => member.id === context.viewer));
  }

  private mockCheck(request: CreateCaseRequest, context: MockContext):
    { typeId: string; groupId: string; dueAt: string; title: string; description: string; previousCaseId: string | null }
    | CaseValidationFailedView {
    const fieldErrors: Partial<Record<CaseField, string>> = {};
    const title = (request.title ?? '').trim();
    const description = (request.description ?? '').trim();
    if (!request.typeId) fieldErrors.typeId = CASE_TYPE_REQUIRED_MESSAGE;
    if (!request.groupId) fieldErrors.groupId = CASE_GROUP_REQUIRED_MESSAGE;
    if (!request.dueAt) fieldErrors.dueAt = CASE_DUE_REQUIRED_MESSAGE;
    if (title.length === 0) fieldErrors.title = CASE_TITLE_REQUIRED_MESSAGE;
    else if (title.length > CASE_TITLE_MAX_LENGTH) fieldErrors.title = CASE_TITLE_TOO_LONG_MESSAGE;
    if (description.length > CASE_DESCRIPTION_MAX_LENGTH) fieldErrors.description = CASE_DESCRIPTION_TOO_LONG_MESSAGE;
    if (Object.keys(fieldErrors).length > 0) {
      return { status: 'validation-failed', reason: null, message: Object.values(fieldErrors)[0] ?? '', fieldErrors };
    }
    const refuse = (reason: string, field: CaseField, message: string): CaseValidationFailedView =>
      ({ status: 'validation-failed', reason, message, fieldErrors: { [field]: message } });
    const dueAt = new Date(request.dueAt as string);
    if (Number.isNaN(dueAt.getTime())) return refuse('due-in-past', 'dueAt', CASE_DUE_REQUIRED_MESSAGE);
    if (dueAt.getTime() < Date.now()) return refuse('due-in-past', 'dueAt', CASE_DUE_IN_PAST_MESSAGE);
    if (!context.types.some((type) => type.id === request.typeId)) return refuse('case-type-inactive', 'typeId', CASE_TYPE_INACTIVE_MESSAGE);
    if (!context.groups.some((group) => group.id === request.groupId)) {
      return refuse('case-group-archived', 'groupId', CASE_TYPE_GROUP_ARCHIVED_MESSAGE);
    }
    if (request.databaseId || request.submissionId) return refuse('link-not-available', 'submissionId', CASE_RECORD_NOT_AVAILABLE_MESSAGE);
    if (request.assistantId || request.threadId) return refuse('link-not-available', 'threadId', CASE_THREAD_NOT_AVAILABLE_MESSAGE);
    const previous = request.previousCaseId ? this.mockCases.find((item) => item.id === request.previousCaseId) : null;
    if (request.previousCaseId && (!previous || !this.mockVisible(previous, context) || OPEN_CASE_STATUSES.includes(previous.status))) {
      return refuse('link-not-available', 'previousCaseId', CASE_PREVIOUS_NOT_AVAILABLE_MESSAGE);
    }
    return {
      typeId: request.typeId as string, groupId: request.groupId as string, dueAt: dueAt.toISOString(), title, description,
      previousCaseId: previous?.id ?? null,
    };
  }

  private mockSummary(item: MockCase, context: MockContext): CaseSummaryView {
    return {
      id: item.id,
      title: item.title,
      status: item.status,
      origin: item.origin,
      type: this.mockType(item.typeId, context),
      group: this.mockGroup(item.groupId, context),
      createdBy: this.mockAccount(item.createdBy),
      owner: null,
      dueAt: item.dueAt,
      createdAt: item.createdAt,
      updatedAt: item.completedAt ?? item.createdAt,
    };
  }

  private mockDetail(item: MockCase, context: MockContext): CaseDetailView {
    const created: CaseEventView = {
      id: `${item.id}-created`, ordinal: 1, action: 'created', actor: this.mockAccount(item.createdBy), at: item.createdAt,
      note: null, status: 'pending', owner: null, fromGroup: null, toGroup: this.mockGroup(item.groupId, context), dueAt: item.dueAt,
    };
    const summary = this.mockSummary(item, context);
    return {
      case: {
        ...summary,
        description: item.description,
        resolution: null,
        cancelReason: null,
        acceptedAt: null,
        completedAt: item.completedAt,
        cancelledAt: null,
        eventCount: 1,
      },
      events: [created],
      links: {
        record: item.record && {
          ...item.record,
          state: 'available',
          canRead: context.role === 'smb-admin',
        },
        thread: item.thread && {
          assistantId: item.thread.assistantId,
          threadId: item.thread.threadId,
          canOpen: item.thread.available && item.createdBy === context.viewer,
        },
        assistantIssue: null,
        previousCase: item.previousCaseId ? { caseId: item.previousCaseId, canOpen: true } : null,
      },
    };
  }

  /** 類型與承辦組的名稱：清單裡只有啟用中的類型與未封存的組，其他的用 mock 範例名稱。 */
  private mockType(typeId: string, context: MockContext): CaseSummaryView['type'] {
    return { id: typeId, name: context.types.find((type) => type.id === typeId)?.name ?? '設備故障報修' };
  }

  private mockGroup(groupId: string, context: MockContext): CaseSummaryView['group'] {
    const group = context.groups.find((candidate) => candidate.id === groupId);
    return { id: groupId, name: group?.name ?? '已封存的承辦組', archived: !group };
  }

  private mockAccount(accountId: AccountId): CaseSummaryView['createdBy'] {
    return {
      id: accountId,
      displayName: DEMO_SEED.accounts.find((account) => account.id === accountId)?.displayName ?? REMOVED_ACCOUNT_NAME,
    };
  }
}

interface MockContext {
  readonly viewer: AccountId;
  readonly role: AccountRole;
  readonly groups: readonly CaseGroupView[];
  readonly types: readonly CaseTypeView[];
}

function validationFailed(error: HttpErrorResponse): CaseValidationFailedView {
  const body = error.error && typeof error.error === 'object' ? error.error as Record<string, unknown> : {};
  const errors = body['errors'] && typeof body['errors'] === 'object' ? body['errors'] as Record<string, unknown> : {};
  const fieldErrors: Partial<Record<CaseField, string>> = {};
  for (const field of CASE_FIELDS) {
    const messages = errors[field];
    if (Array.isArray(messages) && typeof messages[0] === 'string') fieldErrors[field] = messages[0];
  }
  return {
    status: 'validation-failed',
    reason: typeof body['reason'] === 'string' ? body['reason'] : null,
    message: typeof body['message'] === 'string' ? body['message'] : Object.values(fieldErrors)[0] ?? CASE_TITLE_REQUIRED_MESSAGE,
    fieldErrors,
  };
}
