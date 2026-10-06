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

/** 清單的範圍：我看得到的全部、我建立的、我負責的、我的承辦組。 */
export type CaseListScope = 'all' | 'created' | 'owned' | 'my-groups';

/** 清單的狀態篩選：未結案（預設）、已結案、全部，或單一狀態。 */
export type CaseListStatus = 'open' | 'closed' | 'all' | CaseStatus;

export interface CaseListFilter {
  readonly scope?: CaseListScope;
  readonly status?: CaseListStatus;
  readonly typeId?: string;
  readonly groupId?: string;
}

export const OPEN_CASE_STATUSES: readonly CaseStatus[] = ['pending', 'in-progress', 'awaiting-info'];

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
