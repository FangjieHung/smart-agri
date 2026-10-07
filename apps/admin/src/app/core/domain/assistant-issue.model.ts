import type { components } from '../api/api-schema';

export type AssistantIssueView = components['schemas']['AssistantIssueView'];
export type AssistantIssueDetailView = components['schemas']['AssistantIssueDetailView'];
export type AssistantIssueSummaryView = components['schemas']['AssistantIssueSummaryView'];
export type AssistantIssueEventView = components['schemas']['AssistantIssueEventView'];
export type AssistantIssueStatus = components['schemas']['AssistantIssueStatus'];
export type CreateAssistantIssueRequest = components['schemas']['CreateAssistantIssueRequest'];
export type UpdateAssistantIssueRequest = components['schemas']['UpdateAssistantIssueRequest'];

export type AssistantIssueScope = 'all' | 'owned' | 'assigned' | 'forwarded';

export interface AssistantIssueFilters {
  readonly scope?: AssistantIssueScope;
  readonly status?: AssistantIssueStatus;
  readonly assistantId?: string;
}

export interface AssistantIssueActionError {
  readonly status: 'validation-failed' | 'conflict' | 'permission-denied';
  readonly reason: string;
  readonly message: string;
  readonly issueId?: string;
}

export type AssistantIssueActionResult<T> =
  | { readonly status: 'ready'; readonly data: T }
  | AssistantIssueActionError;

/** 結案方式（issue #252）：`fixed` 助理已修正；`not-assistant-issue` 非助理問題，已另開案件。 */
export type AssistantIssueResolutionKind = components['schemas']['AssistantIssueResolutionKind'];
/** 另開的案件：只有你現在能不能開啟它，不含案件內容。 */
export type AssistantIssueCaseLinkView = components['schemas']['AssistantIssueCaseLinkView'];
/** `POST /api/v1/issues/{id}:open-case` 送出的欄位（與建立案件相同，不含其他連結）。 */
export type OpenCaseFromIssueRequest = components['schemas']['OpenCaseFromIssueRequest'];
/** 另開案件成功（`201`）：新案件的 id，以及結案後的處理事項。 */
export type AssistantIssueOpenedCaseView = components['schemas']['AssistantIssueOpenedCaseView'];

/** 另開案件表單的欄位（與 API 的欄位名稱相同）。 */
export type OpenCaseField = 'typeId' | 'groupId' | 'dueAt' | 'title' | 'description';

/**
 * 另開案件的結果：成功、`403 assistant-issue`（不是你能更新的處理事項，或外部客戶）、`409`（`issue-changed`
 * 已解決或剛被別人改過；`case-already-opened` 已另開過，帶 `caseId`）、`422`（與建立案件相同的原因與欄位錯誤）。
 * 失敗時什麼都沒有寫入。
 */
export type OpenCaseFromIssueResult =
  | { readonly status: 'ready'; readonly data: AssistantIssueOpenedCaseView }
  | { readonly status: 'permission-denied'; readonly reason: string; readonly message: string }
  | { readonly status: 'conflict'; readonly reason: string; readonly message: string; readonly caseId?: string }
  | {
    readonly status: 'validation-failed';
    readonly reason: string | null;
    readonly message: string;
    readonly fieldErrors: Readonly<Partial<Record<OpenCaseField, string>>>;
  };

/** 與後端 `AssistantIssueEndpoints` 相同的訊息。 */
export const ISSUE_CASE_ALREADY_OPENED_MESSAGE = '這個處理事項已經另開過案件。';
export const ISSUE_CHANGED_MESSAGE = '這個處理事項剛被其他人更新，請重新整理後再試。';

/** 處理事項的結案方式文字。 */
export function issueResolutionKindLabel(kind: AssistantIssueResolutionKind): string {
  return kind === 'not-assistant-issue' ? '非助理問題' : '助理已修正';
}
