import type { components } from '../api/api-schema';

/** 案件的五個狀態（案件 ADR「狀態與流轉」）：待受理、處理中、待補件為未結案，已完成、已取消為已結案。 */
export type CaseStatus = components['schemas']['CaseStatus'];

/** 案件從哪裡來：平台內手動建立、對話提議、數據庫送出自動建立、處理事項另開。 */
export type CaseOrigin = components['schemas']['CaseOrigin'];

export type CaseEventAction = components['schemas']['CaseEventAction'];

/** 清單的一列（不含說明）。 */
export type CaseSummaryView = components['schemas']['CaseSummaryView'];

/** 詳情：案件、事件（舊的在前）與連結；永遠不含對話文字。 */
export type CaseDetailView = components['schemas']['CaseDetailView'];

export type CaseView = components['schemas']['CaseView'];

export type CaseEventView = components['schemas']['CaseEventView'];

export type CaseLinksView = components['schemas']['CaseLinksView'];

export type CaseRecordLinkState = components['schemas']['CaseRecordLinkState'];

/** `POST /api/v1/cases` 送出的欄位。 */
export type CreateCaseRequest = components['schemas']['CreateCaseRequest'];

/** 動作表的動作（issue #249）：`POST /api/v1/cases/{id}:<動作>`，補充是 `POST …/comments`。 */
export type CaseAction = components['schemas']['CaseAction'];

/** 清單的範圍：我看得到的全部、我建立的、我負責的、我的承辦組。 */
export type CaseListScope = 'all' | 'created' | 'owned' | 'my-groups';

/** 清單的狀態篩選：未結案（預設）、已結案、全部，或單一狀態。 */
export type CaseListStatus = 'open' | 'closed' | 'all' | CaseStatus;

export interface CaseListFilter {
  readonly scope?: CaseListScope;
  readonly status?: CaseListStatus;
  readonly typeId?: string;
  readonly groupId?: string;
  /** 只列逾期的（issue #250，`overdue=true`）。 */
  readonly overdue?: boolean;
  /**
   * 只列在這段期間（UTC 日，含頭尾，`YYYY-MM-DD`）完成或取消的案件（issue #251，從瓶頸統計點開）；
   * 搭配 `status=completed`／`cancelled` 就是統計那一列的件數。
   */
  readonly closedFrom?: string;
  readonly closedTo?: string;
}

export const OPEN_CASE_STATUSES: readonly CaseStatus[] = ['pending', 'in-progress', 'awaiting-info'];

/**
 * 逾期提示（issue #250）：`GET /api/v1/cases/attention`。`overdueCount`（側欄數字）＝我負責的逾期
 * ＋我的承辦組待受理的逾期；`pendingForMeCount` 是待我受理（不論是否逾期）。
 */
export type CaseAttentionView = components['schemas']['CaseAttentionView'];

/** 與後端 `CaseAttention.Overdue` 相同：時限已過（剛好到時限的那一刻還不算）且未結案；待補件照樣計時。 */
export function isCaseOverdue(item: { readonly status: CaseStatus; readonly dueAt: string }, now: Date): boolean {
  return OPEN_CASE_STATUSES.includes(item.status) && Date.parse(item.dueAt) < now.getTime();
}

const CASE_STATUS_LABELS: Readonly<Record<CaseStatus, string>> = {
  pending: '待受理',
  'in-progress': '處理中',
  'awaiting-info': '待補件',
  completed: '已完成',
  cancelled: '已取消',
};

/** 案件狀態的文字：所有畫面都用這一個函式（不要再像處理事項那樣各寫一份）。 */
export function caseStatusLabel(status: CaseStatus): string {
  return CASE_STATUS_LABELS[status];
}

export const CASE_STATUSES: readonly CaseStatus[] = Object.keys(CASE_STATUS_LABELS) as CaseStatus[];

const CASE_ORIGIN_LABELS: Readonly<Record<CaseOrigin, string>> = {
  manual: '平台內建立',
  'chat-proposal': '對話中提議',
  'database-submission': '數據庫送出後自動建立',
  'assistant-issue': '處理事項另開',
};

export function caseOriginLabel(origin: CaseOrigin): string {
  return CASE_ORIGIN_LABELS[origin];
}

const CASE_EVENT_LABELS: Readonly<Record<CaseEventAction, string>> = {
  created: '建立案件',
  accepted: '受理',
  'info-requested': '要求補件',
  commented: '補充說明',
  resumed: '繼續處理',
  completed: '完成',
  cancelled: '取消',
  transferred: '轉組',
  'due-changed': '調整時限',
};

export function caseEventLabel(action: CaseEventAction): string {
  return CASE_EVENT_LABELS[action];
}

/** 連結的數據庫紀錄目前的狀態。 */
export function caseRecordStateLabel(state: CaseRecordLinkState): string {
  return state === 'available' ? '紀錄可查看' : state === 'withdrawn' ? '紀錄已撤回' : '紀錄已不存在';
}

/** 與後端 `CaseRules` 相同的上限與訊息（issue #248）。 */
export const CASE_TITLE_MAX_LENGTH = 120;
export const CASE_DESCRIPTION_MAX_LENGTH = 4000;
export const CASE_TITLE_REQUIRED_MESSAGE = '請輸入案件標題。';
export const CASE_TITLE_TOO_LONG_MESSAGE = `案件標題請在 ${CASE_TITLE_MAX_LENGTH} 個字以內。`;
export const CASE_DESCRIPTION_TOO_LONG_MESSAGE = '說明請在 4,000 個字以內。';
export const CASE_TYPE_REQUIRED_MESSAGE = '請選擇案件類型。';
export const CASE_GROUP_REQUIRED_MESSAGE = '請選擇承辦組。';
export const CASE_DUE_REQUIRED_MESSAGE = '請填寫處理時限。';
/** 決定 H：建立與調整時都不能填早於現在的時間（`422 due-in-past`）；畫面在送出前就提示。 */
export const CASE_DUE_IN_PAST_MESSAGE = '時限不能早於現在。';
export const CASE_TYPE_INACTIVE_MESSAGE = '這個案件類型已停用或不存在，請選擇其他類型。';
export const CASE_THREAD_UNAVAILABLE_TEXT = '這個對話無法開啟';

const CASE_ACTION_LABELS: Readonly<Record<CaseAction, string>> = {
  accept: '受理',
  'request-info': '要求補件',
  resume: '繼續處理',
  complete: '完成',
  cancel: '取消案件',
  transfer: '轉組',
  'set-due': '調整時限',
  comment: '補充說明',
};

/** 詳情頁動作按鈕的文字。 */
export function caseActionLabel(action: CaseAction): string {
  return CASE_ACTION_LABELS[action];
}

/** 動作表的順序（與後端 `CaseActionRules.All` 相同）。 */
export const CASE_ACTIONS: readonly CaseAction[] = Object.keys(CASE_ACTION_LABELS) as CaseAction[];

/** 與後端 `CaseRules`／`CaseActionEndpoints` 相同的上限與訊息（issue #249）。 */
export const CASE_NOTE_MAX_LENGTH = 2000;
export const CASE_TEXT_TOO_LONG_MESSAGE = '請在 2,000 個字以內。';
export const CASE_REQUEST_INFO_NOTE_REQUIRED_MESSAGE = '請說明需要補充哪些資料。';
export const CASE_COMMENT_REQUIRED_MESSAGE = '請輸入補充內容。';
export const CASE_RESOLUTION_REQUIRED_MESSAGE = '請填寫處理結果。';
export const CASE_REASON_REQUIRED_MESSAGE = '請填寫取消原因。';
export const CASE_GROUP_UNCHANGED_MESSAGE = '案件已經在這個承辦組，請選擇其他承辦組。';
/** `409 case-changed`：畫面上的版本（`eventCount`）已過期，或別人剛改了狀態。 */
export const CASE_CHANGED_MESSAGE = '這件案件剛被其他人更新，請重新整理後再試。';
/** `403 case-action`：看得到案件，但這個動作不是你能做的。 */
export const CASE_ACTION_DENIED_MESSAGE = '你不能對這件案件執行這個動作。';

/**
 * 瓶頸統計（issue #251）：`GET /api/v1/cases/statistics?from=&to=`，只有管理者。依「案件類型 × 目前承辦組」
 * 各一列：未結案與逾期件數（現在）、期間內完成與取消件數、平均處理時間（建立 → 完成，只算期間內完成的；
 * 沒有完成件數時是 `null`）。只有名稱與數字，不含任何案件文字。
 */
export type CaseStatisticsView = components['schemas']['CaseStatisticsView'];

export type CaseStatisticsRowView = components['schemas']['CaseStatisticsRowView'];

/** 與後端 `AnswerAnalyticsRange` 相同：預設最近 30 天（含今天，UTC），最長 180 天。 */
export const CASE_STATISTICS_DEFAULT_DAYS = 30;
export const CASE_STATISTICS_MAX_DAYS = 180;
export const CASE_STATISTICS_RANGE_ORDER_MESSAGE = '起始日期必須不晚於結束日期。';
export const CASE_STATISTICS_RANGE_TOO_LONG_MESSAGE = `日期範圍最長 ${CASE_STATISTICS_MAX_DAYS} 天。`;
export const CASE_STATISTICS_RANGE_INVALID_MESSAGE = '請輸入有效的日期。';

/** 平均處理時間的文字：沒有完成件數時是「—」，其他以小時表示到小數一位。 */
export function formatHandlingHours(hours: number | null): string {
  if (hours === null) return '—';
  return `${hours.toLocaleString('zh-TW', { minimumFractionDigits: 1, maximumFractionDigits: 1 })} 小時`;
}

/** 統計的期間：`YYYY-MM-DD` 的 UTC 日，含頭尾。 */
export interface CaseStatisticsRange {
  readonly from: string;
  readonly to: string;
}

const DAY_MS = 86_400_000;

/** `date` 的 UTC 日（`YYYY-MM-DD`）。 */
export function utcDay(date: Date): string {
  return date.toISOString().slice(0, 10);
}

export function addUtcDays(day: string, days: number): string {
  return utcDay(new Date(Date.parse(`${day}T00:00:00Z`) + days * DAY_MS));
}

/**
 * 與後端 `AnswerAnalyticsRange.Resolve` 相同：省略 `to` 是今天（UTC）、省略 `from` 是 `to` 往前 29 天；
 * `from` 晚於 `to`、或超過 180 天時回傳錯誤訊息。
 */
export function resolveCaseStatisticsRange(
  from: string | null | undefined, to: string | null | undefined, now: Date,
): CaseStatisticsRange | { readonly message: string } {
  const resolvedTo = to || utcDay(now);
  const resolvedFrom = from || addUtcDays(resolvedTo, -(CASE_STATISTICS_DEFAULT_DAYS - 1));
  if (Number.isNaN(Date.parse(`${resolvedFrom}T00:00:00Z`)) || Number.isNaN(Date.parse(`${resolvedTo}T00:00:00Z`))) {
    return { message: CASE_STATISTICS_RANGE_INVALID_MESSAGE };
  }
  if (resolvedFrom > resolvedTo) return { message: CASE_STATISTICS_RANGE_ORDER_MESSAGE };
  const days = (Date.parse(`${resolvedTo}T00:00:00Z`) - Date.parse(`${resolvedFrom}T00:00:00Z`)) / DAY_MS + 1;
  if (days > CASE_STATISTICS_MAX_DAYS) return { message: CASE_STATISTICS_RANGE_TOO_LONG_MESSAGE };
  return { from: resolvedFrom, to: resolvedTo };
}
