import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { catchError, defer, forkJoin, map, of, switchMap, throwError, type Observable } from 'rxjs';
import type { AccountId, AccountRole } from '../domain/account.model';
import { cancelReasonRequired, checkCaseAction, commentResumes, allowedCaseActions, type CaseActor } from '../domain/case-actions';
import {
  CASE_ACTION_DENIED_MESSAGE,
  CASE_CHANGED_MESSAGE,
  CASE_COMMENT_REQUIRED_MESSAGE,
  CASE_GROUP_UNCHANGED_MESSAGE,
  CASE_NOTE_MAX_LENGTH,
  CASE_REASON_REQUIRED_MESSAGE,
  CASE_REQUEST_INFO_NOTE_REQUIRED_MESSAGE,
  CASE_RESOLUTION_REQUIRED_MESSAGE,
  CASE_TEXT_TOO_LONG_MESSAGE,
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
  addUtcDays,
  isCaseOverdue,
  resolveCaseStatisticsRange,
  type CaseAction,
  type CaseAttentionView,
  type CaseDetailView,
  type CaseEventAction,
  type CaseEventView,
  type CaseListFilter,
  type CaseOrigin,
  type CaseRecordLinkState,
  type CaseStatisticsRange,
  type CaseStatisticsRowView,
  type CaseStatisticsView,
  type CaseStatus,
  type CaseSummaryView,
  type CreateCaseRequest,
} from '../domain/case.model';
import type { CaseGroupView, CaseTypeView } from '../domain/case-settings.model';
import { DemoSessionService } from '../session/demo-session.service';
import {
  CASE_FEATURE_DENIED_MESSAGE,
  CASE_SETTINGS_ADMIN_DENIED_MESSAGE,
  CASE_TYPE_GROUP_ARCHIVED_MESSAGE,
  CASE_TYPE_GROUP_NOT_FOUND_MESSAGE,
  CaseSettingsRepository,
} from './case-settings.repository';
import { DEMO_SEED } from './demo-seed';
import { mockChatProposedCases } from './mock-chat-cases';
import type { PermissionDeniedRepositoryView, RepositoryView } from './demo-repository';
import { API_DEMO_REPOSITORY_FACTORY } from './tokens';

export const API_CASES_PATH = '/api/v1/cases';

/** 逾期提示（issue #250）。 */
export const API_CASE_ATTENTION_PATH = `${API_CASES_PATH}/attention`;

/** 瓶頸統計（issue #251）。 */
export const API_CASE_STATISTICS_PATH = `${API_CASES_PATH}/statistics`;

export function apiCasePath(caseId: string): string {
  return `${API_CASES_PATH}/${encodeURIComponent(caseId)}`;
}

export function apiCaseActionPath(caseId: string, action: CaseAction): string {
  return action === 'comment' ? `${apiCasePath(caseId)}/comments` : `${apiCasePath(caseId)}:${action}`;
}

/** 建立案件與動作表單的欄位（與 API 的欄位名稱相同）。 */
export type CaseField =
  | 'typeId' | 'groupId' | 'dueAt' | 'title' | 'description' | 'submissionId' | 'threadId' | 'previousCaseId'
  | 'eventCount' | 'note' | 'resolution' | 'reason';
const CASE_FIELDS: readonly CaseField[] = [
  'typeId', 'groupId', 'dueAt', 'title', 'description', 'submissionId', 'threadId', 'previousCaseId',
  'eventCount', 'note', 'resolution', 'reason',
];

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

/**
 * 動作的輸入（issue #249）：`eventCount` 一定要帶畫面上的版本；其他欄位依動作：待補件與補充的
 * `note`（必填）、繼續處理／轉組／調整時限的 `note`（選填）、完成的 `resolution`、取消的 `reason`、
 * 轉組的 `groupId`、調整時限的 `dueAt`（ISO 時間）。
 */
export interface CaseActionInput {
  readonly eventCount: number;
  readonly note?: string;
  readonly resolution?: string;
  readonly reason?: string;
  readonly groupId?: string;
  readonly dueAt?: string;
}

/** `409 case-changed`：畫面上的版本過期或狀態已被別人改變，什麼都沒有寫入；請重新整理。 */
export interface CaseChangedView {
  readonly status: 'changed';
  readonly message: string;
}

/** `403 case`（看不到）、`403 case-action`（不是你能做的）、`409`、`422`，或成功後的詳情。 */
export type CaseActionResult = RepositoryView<CaseDetailView> | CaseValidationFailedView | CaseChangedView;

/** 非管理者是 `403 organization-settings`；期間錯誤是 validation-failed（`422 invalid-date-range`）。 */
export type CaseStatisticsResult = RepositoryView<CaseStatisticsView> | CaseValidationFailedView;

/** 看不到、不存在、別的組織、外部客戶：後端一律是同一個 `403 case`。 */
const CASE_DENIED: PermissionDeniedRepositoryView = {
  status: 'permission-denied',
  reason: 'case',
  message: CASE_FEATURE_DENIED_MESSAGE,
};

interface MockEvent {
  readonly id: string;
  readonly action: CaseEventAction;
  readonly actor: AccountId | null;
  readonly at: string;
  readonly note: string | null;
  readonly status: CaseStatus | null;
  readonly owner: AccountId | null;
  readonly fromGroupId: string | null;
  readonly toGroupId: string | null;
  readonly dueAt: string | null;
}

interface MockCase {
  readonly id: string;
  readonly typeId: string;
  groupId: string;
  status: CaseStatus;
  readonly origin: CaseOrigin;
  readonly title: string;
  readonly description: string;
  /** 數據庫送出後自動開的案件沒有建立者（決定 M）。 */
  readonly createdBy: AccountId | null;
  owner: AccountId | null;
  dueAt: string;
  readonly createdAt: string;
  updatedAt: string;
  acceptedAt: string | null;
  completedAt: string | null;
  cancelledAt: string | null;
  resolution: string | null;
  cancelReason: string | null;
  /** 連結的對話；`available: false` 模擬對話已被保存期限刪除。 */
  readonly thread: { readonly assistantId: string; readonly threadId: string; readonly available: boolean } | null;
  /** 連結的數據庫紀錄與它目前的狀態（`withdrawn` 模擬送出者已撤回）。 */
  readonly record: {
    readonly databaseId: string;
    readonly submissionId: string;
    readonly databaseName: string;
    readonly state: CaseRecordLinkState;
  } | null;
  readonly previousCaseId: string | null;
  /** 交接軌跡，舊的在前；`eventCount` 就是它的長度。 */
  events: MockEvent[];
}

type MockSeed = Pick<MockCase, 'id' | 'typeId' | 'groupId' | 'title' | 'description' | 'createdBy' | 'dueAt' | 'createdAt' | 'thread' | 'record'>
  & {
    readonly origin?: CaseOrigin;
    readonly completed?: { readonly by: AccountId; readonly acceptedAt: string; readonly at: string; readonly resolution: string };
  };

/**
 * mock 的範例案件（只放在這個檔案，不加進 `demo-seed.ts`：後端有測試會讀它）。承辦組與類型沿用
 * `CaseSettingsRepository` 的範例：設備組（成員：客服同仁）、採購組（成員：管理者）。
 */
const MOCK_SEEDS: readonly MockSeed[] = [
  {
    id: 'case-cold-room', typeId: 'case-type-equipment-repair', groupId: 'case-group-equipment',
    title: '冷藏庫溫度降不下來', description: '二號冷藏庫從早上開始維持在 9 度，請派人到場檢查。',
    createdBy: 'account-internal-employee', dueAt: '2026-10-09T01:00:00.000Z', createdAt: '2026-10-06T01:00:00.000Z',
    thread: { assistantId: 'assistant-internal-onboarding', threadId: 'thread-cold-room-deleted', available: false },
    record: null,
  },
  {
    id: 'case-compressor-purchase', typeId: 'case-type-equipment-repair', groupId: 'case-group-purchasing',
    title: '採購備用壓縮機', description: '維修廠商建議備一台壓縮機，請採購組詢價。',
    createdBy: 'account-smb-admin', dueAt: '2026-10-12T01:00:00.000Z', createdAt: '2026-10-06T03:00:00.000Z',
    thread: null,
    record: { databaseId: 'database-customer-records', submissionId: 'submission-mock-compressor', databaseName: '客戶資料庫', state: 'available' },
  },
  {
    // 數據庫送出後自動開的案件（issue #255）：沒有建立者、不含紀錄內容；送出者之後撤回了紀錄。
    id: 'case-auto-customer-record', typeId: 'case-type-equipment-repair', groupId: 'case-group-equipment',
    title: '客戶資料庫：新紀錄', description: '由數據庫送出自動建立，內容請開啟紀錄查看。', origin: 'database-submission',
    createdBy: null, dueAt: '2026-10-08T02:00:00.000Z', createdAt: '2026-10-05T02:00:00.000Z',
    thread: null,
    record: { databaseId: 'database-customer-records', submissionId: 'submission-mock-withdrawn', databaseName: '客戶資料庫', state: 'withdrawn' },
  },
  {
    // 逾期提示（issue #250）：一直待受理、時限已過，設備組成員（客服同仁）的側欄數字因此至少是 1。
    id: 'case-greenhouse-sensor', typeId: 'case-type-equipment-repair', groupId: 'case-group-equipment',
    title: '溫室感測器離線', description: '一號溫室的溫濕度感測器從前天起沒有回傳資料。',
    createdBy: 'account-smb-admin', dueAt: '2026-10-04T01:00:00.000Z', createdAt: '2026-10-02T01:00:00.000Z',
    thread: null, record: null,
  },
  {
    id: 'case-irrigation-done', typeId: 'case-type-equipment-repair', groupId: 'case-group-equipment',
    title: '灌溉馬達異音', description: '三號溫室的灌溉馬達運轉時有異音。',
    createdBy: 'account-internal-employee', dueAt: '2026-10-03T01:00:00.000Z', createdAt: '2026-10-01T02:00:00.000Z',
    thread: null, record: null,
    completed: { by: 'account-internal-employee', acceptedAt: '2026-10-01T03:00:00.000Z', at: '2026-10-02T06:00:00.000Z', resolution: '已更換軸承。' },
  },
];

function seedCase(seed: MockSeed): MockCase {
  const item: MockCase = {
    id: seed.id, typeId: seed.typeId, groupId: seed.groupId, status: 'pending', origin: seed.origin ?? 'manual', title: seed.title,
    description: seed.description, createdBy: seed.createdBy, owner: null, dueAt: seed.dueAt, createdAt: seed.createdAt,
    updatedAt: seed.createdAt, acceptedAt: null, completedAt: null, cancelledAt: null, resolution: null, cancelReason: null,
    thread: seed.thread, record: seed.record, previousCaseId: null, events: [],
  };
  item.events.push(createdEvent(item));
  if (seed.completed) {
    const { by, acceptedAt, at, resolution } = seed.completed;
    Object.assign(item, { status: 'completed', owner: by, acceptedAt, completedAt: at, updatedAt: at, resolution });
    item.events.push(
      mockEvent(item, 'accepted', by, acceptedAt, { status: 'in-progress', owner: by }),
      mockEvent(item, 'completed', by, at, { status: 'completed', note: resolution }),
    );
  }
  return item;
}

function createdEvent(item: MockCase): MockEvent {
  return mockEvent(item, 'created', item.createdBy, item.createdAt, { status: 'pending', toGroupId: item.groupId, dueAt: item.dueAt });
}

function mockEvent(
  item: MockCase, action: CaseEventAction, actor: AccountId | null, at: string, values: Partial<Omit<MockEvent, 'id' | 'action' | 'actor' | 'at'>> = {},
): MockEvent {
  return {
    id: `${item.id}-event-${item.events.length + 1}`, action, actor, at, note: null, status: null, owner: null,
    fromGroupId: null, toGroupId: null, dueAt: null, ...values,
  };
}

/** 與後端 `CaseEndpoints` 相同的連結拒絕訊息。 */
export const CASE_RECORD_NOT_AVAILABLE_MESSAGE = '這筆紀錄目前無法連結：你沒有讀取它的權限，或它已撤回、不存在。';
export const CASE_THREAD_NOT_AVAILABLE_MESSAGE = '只能連結你自己的對話，而且對話必須還在。';
export const CASE_PREVIOUS_NOT_AVAILABLE_MESSAGE = '只能連結你看得到、而且已結案的案件。';

const REMOVED_ACCOUNT_NAME = '已停用的帳號';

/**
 * 案件的單一資料入口（issue #248）。API 模式讀寫 `/api/v1/cases`；純 Demo 模式只保存在這次工作
 * 階段，判斷與後端相同：只有內部帳號；看得到的人是建立者、目前承辦組的成員與管理者；時限不能早於
 * 現在，停用的類型與封存的承辦組不能用。只會在 lazy 的程式注入（`/app/cases`、首頁，以及側欄以動態
 * `import()` 載入的 `case-attention.loader.ts`），不進初始 bundle。
 */
@Injectable({ providedIn: 'root' })
export class CasesRepository {
  private readonly apiMode = inject(API_DEMO_REPOSITORY_FACTORY) !== null;
  private readonly http = inject(HttpClient, { optional: true });
  private readonly session = inject(DemoSessionService);
  private readonly settings = inject(CaseSettingsRepository);
  private mockCases: MockCase[] = MOCK_SEEDS.map(seedCase);

  /** 看得到的案件，新的在前；預設只列未結案（決定 Q）。 */
  list(filter: CaseListFilter = {}): Observable<RepositoryView<readonly CaseSummaryView[]>> {
    if (this.apiMode) {
      let params = new HttpParams();
      for (const key of ['scope', 'status', 'typeId', 'groupId', 'closedFrom', 'closedTo'] as const) {
        const value = filter[key];
        if (value) params = params.set(key, value);
      }
      if (filter.overdue) params = params.set('overdue', 'true');
      return this.client().get<CaseSummaryView[]>(API_CASES_PATH, { params }).pipe(
        map((data): RepositoryView<readonly CaseSummaryView[]> => ({ status: 'ready', data })),
        catchError((error: unknown) => this.denied<readonly CaseSummaryView[]>(error)),
      );
    }
    return this.withMockContext((context) => {
      const viewer = context.viewer;
      const scope = filter.scope ?? 'all';
      const status = filter.status ?? 'open';
      const now = new Date();
      const closed = filter.closedFrom || filter.closedTo ? resolveCaseStatisticsRange(filter.closedFrom, filter.closedTo, now) : null;
      // 與 API 的 `422 invalid-date-range` 相同：只有手動改壞網址才會發生，清單顯示錯誤狀態。
      if (closed && 'message' in closed) return throwError(() => new Error(closed.message));
      const rows = this.allMockCases()
        .filter((item) => this.mockVisible(item, context))
        .filter((item) => scope !== 'created' || item.createdBy === viewer)
        .filter((item) => scope !== 'owned' || item.owner === viewer)
        .filter((item) => scope !== 'my-groups' || this.mockIsMember(item.groupId, context))
        .filter((item) => status === 'all'
          || (status === 'open' ? OPEN_CASE_STATUSES.includes(item.status)
            : status === 'closed' ? !OPEN_CASE_STATUSES.includes(item.status)
              : item.status === status))
        .filter((item) => !filter.typeId || item.typeId === filter.typeId)
        .filter((item) => !filter.groupId || item.groupId === filter.groupId)
        .filter((item) => !filter.overdue || isCaseOverdue(item, now))
        .filter((item) => !closed || closedWithin(item, closed))
        .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
        .map((item) => this.mockSummary(item, context));
      return of<RepositoryView<readonly CaseSummaryView[]>>({ status: 'ready', data: rows });
    });
  }

  /**
   * 逾期提示（issue #250）：側欄數字與首頁卡片。判斷與後端 `CaseAttention` 相同——我負責的逾期、
   * 我的承辦組待受理的逾期（兩者不重疊），以及待我受理；管理者不因為是管理者而多算。外部客戶是
   * `403 case`（側欄本來就不會替外部客戶發出這個請求）。
   */
  attention(): Observable<RepositoryView<CaseAttentionView>> {
    if (this.apiMode) {
      return this.client().get<CaseAttentionView>(API_CASE_ATTENTION_PATH).pipe(
        map((data): RepositoryView<CaseAttentionView> => ({ status: 'ready', data })),
        catchError((error: unknown) => this.denied<CaseAttentionView>(error)),
      );
    }
    return this.withMockContext((context) => {
      const now = new Date();
      const visible = this.allMockCases().filter((item) => this.mockVisible(item, context));
      const owned = visible.filter((item) => item.owner === context.viewer && isCaseOverdue(item, now)).length;
      const pending = visible.filter((item) => item.status === 'pending' && this.mockIsMember(item.groupId, context));
      const groupPending = pending.filter((item) => isCaseOverdue(item, now)).length;
      return of<RepositoryView<CaseAttentionView>>({
        status: 'ready',
        data: {
          overdueCount: owned + groupPending,
          ownedOverdueCount: owned,
          groupPendingOverdueCount: groupPending,
          pendingForMeCount: pending.length,
        },
      });
    });
  }

  /**
   * 瓶頸統計（issue #251）：只有管理者，其他人（含外部客戶）是 `403 organization-settings`。與後端
   * `CaseStatistics` 相同：未結案與逾期（`isCaseOverdue`，與 M7-5 同一個判斷）看現在；完成與取消看期間
   * （UTC 日）；平均處理時間是建立 → 完成，只算期間內完成的，歸到完成時的承辦組（結案後不能轉組）。
   */
  statistics(range: Partial<CaseStatisticsRange> = {}): Observable<CaseStatisticsResult> {
    if (this.apiMode) {
      let params = new HttpParams();
      if (range.from) params = params.set('from', range.from);
      if (range.to) params = params.set('to', range.to);
      return this.client().get<CaseStatisticsView>(API_CASE_STATISTICS_PATH, { params }).pipe(
        map((data): CaseStatisticsResult => ({ status: 'ready', data })),
        catchError((error: unknown) => {
          if (error instanceof HttpErrorResponse && error.status === 422) return of(validationFailed(error));
          return this.denied<CaseStatisticsView>(error);
        }),
      );
    }
    return defer(() => {
      if (!this.viewerIsManager()) return of<CaseStatisticsResult>(STATISTICS_DENIED);
      const now = new Date();
      const resolved = resolveCaseStatisticsRange(range.from, range.to, now);
      if ('message' in resolved) {
        return of<CaseStatisticsResult>({ status: 'validation-failed', reason: 'invalid-date-range', message: resolved.message, fieldErrors: {} });
      }
      return this.withMockContext<CaseStatisticsResult>((context) => of<CaseStatisticsResult>({
        status: 'ready',
        data: { from: resolved.from, to: resolved.to, rows: this.mockStatisticsRows(resolved, now, context) },
      }));
    });
  }

  /** 決定 A：管理者是角色 `smb-admin`（API 模式的登入者也對應到同角色的 Demo 身分）。只用來決定要不要顯示統計分頁。 */
  viewerIsManager(): boolean {
    const viewer = this.session.activeAccountId();
    return DEMO_SEED.accounts.find((account) => account.id === viewer)?.role === 'smb-admin';
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
      const item = this.allMockCases().find((candidate) => candidate.id === caseId);
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
        title: checked.title, description: checked.description, createdBy: context.viewer, owner: null, dueAt: checked.dueAt,
        createdAt: now, updatedAt: now, acceptedAt: null, completedAt: null, cancelledAt: null, resolution: null, cancelReason: null,
        thread: null, record: null, previousCaseId: checked.previousCaseId, events: [],
      };
      item.events.push(createdEvent(item));
      this.mockCases = [...this.mockCases, item];
      return of<CreateCaseResult>({ status: 'ready', data: this.mockDetail(item, context) });
    });
  }

  /** 範例與建立的案件，加上從對話確認建立的（issue #254，見 `mock-chat-cases.ts`）。 */
  private allMockCases(): readonly MockCase[] {
    return [
      ...this.mockCases,
      ...mockChatProposedCases().map((item): MockCase => ({
        ...seedCase({
          id: item.id, typeId: item.typeId, groupId: item.groupId, title: item.title, description: item.description,
          createdBy: item.createdBy, dueAt: item.dueAt, createdAt: item.createdAt,
          thread: { assistantId: item.assistantId, threadId: item.threadId, available: true }, record: null,
        }),
        origin: 'chat-proposal',
      })),
    ];
  }

  /**
   * 動作表的一個動作（issue #249）。看不到是 `403 case`、不是你能做的是 `403 case-action`、版本過期或
   * 狀態不符是 `changed`（`409`）、必填空白或時限早於現在是 validation-failed（`422`）；成功時是新的詳情。
   */
  act(caseId: string, action: CaseAction, input: CaseActionInput): Observable<CaseActionResult> {
    if (this.apiMode) {
      return this.client().post<CaseDetailView>(apiCaseActionPath(caseId, action), input).pipe(
        map((data): CaseActionResult => ({ status: 'ready', data })),
        catchError((error: unknown) => {
          if (error instanceof HttpErrorResponse && error.status === 409) return of<CaseActionResult>(changed(error));
          if (error instanceof HttpErrorResponse && error.status === 422) return of<CaseActionResult>(validationFailed(error));
          return this.denied<CaseDetailView>(error);
        }),
      );
    }
    return this.withMockContext<CaseActionResult>((context) => {
      const item = this.mockCases.find((candidate) => candidate.id === caseId);
      if (!item || !this.mockVisible(item, context)) return of(CASE_DENIED);
      return of(this.mockAct(item, action, input, context));
    });
  }

  private client(): HttpClient {
    if (!this.http) throw new Error('API 模式缺少 HttpClient');
    return this.http;
  }

  /** `403`：後端帶的原因（`case`、`case-action`、`organization-settings`、`password-change-required`）照用；其他狀態照常拋出。 */
  private denied<T>(error: unknown): Observable<RepositoryView<T>> {
    if (!(error instanceof HttpErrorResponse) || error.status !== 403) return throwError(() => error);
    const body = error.error && typeof error.error === 'object' ? error.error as Record<string, unknown> : {};
    const reason = body['reason'];
    const message = typeof body['message'] === 'string' ? body['message'] : CASE_DENIED.message;
    if (reason === 'case' || reason === 'case-action' || reason === 'organization-settings' || reason === 'password-change-required') {
      return of({ status: 'permission-denied', reason, message });
    }
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

  /** 與後端 `CaseVisibility` 相同：管理者、建立者、目前承辦組的成員，以及曾經受理過的人。 */
  private mockVisible(item: MockCase, context: MockContext): boolean {
    return context.role === 'smb-admin' || item.createdBy === context.viewer || this.mockIsMember(item.groupId, context)
      || item.events.some((event) => event.action === 'accepted' && event.owner === context.viewer);
  }

  private mockActor(item: MockCase, context: MockContext): CaseActor {
    return {
      isCreator: item.createdBy === context.viewer,
      isOwner: item.owner === context.viewer,
      isGroupMember: this.mockIsMember(item.groupId, context),
      isManager: context.role === 'smb-admin',
    };
  }

  /** 與後端 `CaseActionEndpoints` 相同的檢查順序；拒絕時什麼都不改。 */
  private mockAct(item: MockCase, action: CaseAction, input: CaseActionInput, context: MockContext): CaseActionResult {
    const refuse = (reason: string | null, field: CaseField, message: string): CaseValidationFailedView =>
      ({ status: 'validation-failed', reason, message, fieldErrors: { [field]: message } });
    const texts: readonly (readonly [CaseField, string | undefined])[] = [['note', input.note], ['resolution', input.resolution], ['reason', input.reason]];
    for (const [field, text] of texts) {
      if ((text ?? '').trim().length > CASE_NOTE_MAX_LENGTH) return refuse(null, field, CASE_TEXT_TOO_LONG_MESSAGE);
    }
    if (action === 'transfer' && !input.groupId) return refuse(null, 'groupId', CASE_GROUP_REQUIRED_MESSAGE);
    if (action === 'set-due' && !input.dueAt) return refuse(null, 'dueAt', CASE_DUE_REQUIRED_MESSAGE);
    if (input.eventCount !== item.events.length) return { status: 'changed', message: CASE_CHANGED_MESSAGE };
    const actor = this.mockActor(item, context);
    const check = checkCaseAction(action, item.status, actor);
    if (check === 'wrong-status') return { status: 'changed', message: CASE_CHANGED_MESSAGE };
    if (check === 'not-yours') return { status: 'permission-denied', reason: 'case-action', message: CASE_ACTION_DENIED_MESSAGE };

    const note = input.note?.trim() || null;
    const now = new Date().toISOString();
    const viewer = context.viewer;
    const record = (eventAction: CaseEventAction, values: Partial<Omit<MockEvent, 'id' | 'action' | 'actor' | 'at'>> = {}) => {
      item.events.push(mockEvent(item, eventAction, viewer, now, values));
      item.updatedAt = now;
    };
    switch (action) {
      case 'accept':
        Object.assign(item, { status: 'in-progress', owner: viewer, acceptedAt: now });
        record('accepted', { status: 'in-progress', owner: viewer });
        break;
      case 'request-info':
        if (!note) return refuse('note-required', 'note', CASE_REQUEST_INFO_NOTE_REQUIRED_MESSAGE);
        item.status = 'awaiting-info';
        record('info-requested', { status: 'awaiting-info', note });
        break;
      case 'resume':
        item.status = 'in-progress';
        record('resumed', { status: 'in-progress', note });
        break;
      case 'comment': {
        if (!note) return refuse('note-required', 'note', CASE_COMMENT_REQUIRED_MESSAGE);
        const resumes = commentResumes(item.status, actor);
        if (resumes) item.status = 'in-progress';
        record('commented', { note, status: resumes ? 'in-progress' : null });
        break;
      }
      case 'complete': {
        const resolution = input.resolution?.trim();
        if (!resolution) return refuse('resolution-required', 'resolution', CASE_RESOLUTION_REQUIRED_MESSAGE);
        Object.assign(item, { status: 'completed', resolution, completedAt: now });
        record('completed', { status: 'completed', note: resolution });
        break;
      }
      case 'cancel': {
        const reason = input.reason?.trim() || null;
        if (!reason && cancelReasonRequired(item.status, actor)) return refuse('reason-required', 'reason', CASE_REASON_REQUIRED_MESSAGE);
        Object.assign(item, { status: 'cancelled', cancelReason: reason, cancelledAt: now });
        record('cancelled', { status: 'cancelled', note: reason });
        break;
      }
      case 'transfer': {
        const target = context.groups.find((group) => group.id === input.groupId);
        if (!target) return refuse('case-group-not-found', 'groupId', CASE_TYPE_GROUP_NOT_FOUND_MESSAGE);
        if (target.id === item.groupId) return refuse('case-group-unchanged', 'groupId', CASE_GROUP_UNCHANGED_MESSAGE);
        const from = item.groupId;
        Object.assign(item, { status: 'pending', groupId: target.id, owner: null });
        record('transferred', { status: 'pending', fromGroupId: from, toGroupId: target.id, note });
        break;
      }
      case 'set-due': {
        const due = new Date(input.dueAt as string);
        if (Number.isNaN(due.getTime())) return refuse(null, 'dueAt', CASE_DUE_REQUIRED_MESSAGE);
        if (due.getTime() < Date.now()) return refuse('due-in-past', 'dueAt', CASE_DUE_IN_PAST_MESSAGE);
        item.dueAt = due.toISOString();
        record('due-changed', { dueAt: item.dueAt, note });
        break;
      }
    }
    return { status: 'ready', data: this.mockDetail(item, context) };
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

  /** 一列一個（類型, 承辦組）；什麼都沒有的組合沒有列。依類型名稱、承辦組名稱排序（與後端相同）。 */
  private mockStatisticsRows(range: CaseStatisticsRange, now: Date, context: MockContext): CaseStatisticsRowView[] {
    const rows = new Map<string, { typeId: string; groupId: string; open: number; overdue: number; cancelled: number; hours: number[] }>();
    const row = (item: MockCase) => {
      const key = `${item.typeId}|${item.groupId}`;
      const found = rows.get(key) ?? { typeId: item.typeId, groupId: item.groupId, open: 0, overdue: 0, cancelled: 0, hours: [] };
      rows.set(key, found);
      return found;
    };
    for (const item of this.allMockCases()) {
      if (OPEN_CASE_STATUSES.includes(item.status)) row(item).open += 1;
      if (isCaseOverdue(item, now)) row(item).overdue += 1;
      if (item.status === 'completed' && item.completedAt && inRange(item.completedAt, range)) {
        row(item).hours.push((Date.parse(item.completedAt) - Date.parse(item.createdAt)) / 3_600_000);
      }
      if (item.status === 'cancelled' && item.cancelledAt && inRange(item.cancelledAt, range)) row(item).cancelled += 1;
    }
    return [...rows.values()]
      .map((entry): CaseStatisticsRowView => ({
        type: this.mockType(entry.typeId, context),
        group: this.mockGroup(entry.groupId, context),
        openCount: entry.open,
        overdueCount: entry.overdue,
        completedCount: entry.hours.length,
        cancelledCount: entry.cancelled,
        averageHandlingHours: entry.hours.length === 0 ? null : entry.hours.reduce((sum, hours) => sum + hours, 0) / entry.hours.length,
      }))
      .sort((a, b) => compareOrdinal(a.type.name, b.type.name) || compareOrdinal(a.group.name, b.group.name));
  }

  private mockSummary(item: MockCase, context: MockContext): CaseSummaryView {
    return {
      id: item.id,
      title: item.title,
      status: item.status,
      origin: item.origin,
      type: this.mockType(item.typeId, context),
      group: this.mockGroup(item.groupId, context),
      createdBy: item.createdBy ? this.mockAccount(item.createdBy) : null,
      owner: item.owner ? this.mockAccount(item.owner) : null,
      dueAt: item.dueAt,
      createdAt: item.createdAt,
      updatedAt: item.updatedAt,
    };
  }

  private mockDetail(item: MockCase, context: MockContext): CaseDetailView {
    const events = item.events.map((event, index): CaseEventView => ({
      id: event.id, ordinal: index + 1, action: event.action, actor: event.actor ? this.mockAccount(event.actor) : null, at: event.at,
      note: event.note, status: event.status, owner: event.owner ? this.mockAccount(event.owner) : null,
      fromGroup: event.fromGroupId ? this.mockGroup(event.fromGroupId, context) : null,
      toGroup: event.toGroupId ? this.mockGroup(event.toGroupId, context) : null, dueAt: event.dueAt,
    }));
    const summary = this.mockSummary(item, context);
    const actor = this.mockActor(item, context);
    return {
      case: {
        ...summary,
        description: item.description,
        resolution: item.resolution,
        cancelReason: item.cancelReason,
        acceptedAt: item.acceptedAt,
        completedAt: item.completedAt,
        cancelledAt: item.cancelledAt,
        eventCount: item.events.length,
      },
      events,
      allowedActions: allowedCaseActions(item.status, actor),
      cancelReasonRequired: cancelReasonRequired(item.status, actor),
      links: {
        record: item.record && {
          databaseId: item.record.databaseId,
          submissionId: item.record.submissionId,
          state: item.record.state,
          canRead: item.record.state === 'available' && context.role === 'smb-admin',
          databaseName: item.record.databaseName,
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

/** 統計只給管理者：與後端 `RequireOrganizationAdmin(organization-settings)` 相同的拒絕。 */
const STATISTICS_DENIED: PermissionDeniedRepositoryView = {
  status: 'permission-denied',
  reason: 'organization-settings',
  message: CASE_SETTINGS_ADMIN_DENIED_MESSAGE,
};

/** 時間落在期間內：`[from 00:00Z, to 隔天 00:00Z)`。 */
function inRange(at: string, range: CaseStatisticsRange): boolean {
  const time = Date.parse(at);
  return time >= Date.parse(`${range.from}T00:00:00Z`) && time < Date.parse(`${addUtcDays(range.to, 1)}T00:00:00Z`);
}

/** 與後端 `CaseStatistics.ClosedWithin` 相同：期間內完成或取消。 */
function closedWithin(item: MockCase, range: CaseStatisticsRange): boolean {
  return (item.status === 'completed' && item.completedAt !== null && inRange(item.completedAt, range))
    || (item.status === 'cancelled' && item.cancelledAt !== null && inRange(item.cancelledAt, range));
}

/** 與後端 `StringComparer.Ordinal` 相同的排序。 */
function compareOrdinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

function changed(error: HttpErrorResponse): CaseChangedView {
  const body = error.error && typeof error.error === 'object' ? error.error as Record<string, unknown> : {};
  return { status: 'changed', message: typeof body['message'] === 'string' ? body['message'] : CASE_CHANGED_MESSAGE };
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
