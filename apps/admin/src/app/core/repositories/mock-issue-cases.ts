import type { AccountId } from '../domain/account.model';
import type { OpenCaseField, OpenCaseFromIssueRequest } from '../domain/assistant-issue.model';
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
} from '../domain/case.model';

/*
 * 純 Demo 模式的處理事項「另開案件」（issue #252）。建立的案件只保存在這次工作階段；`CasesRepository` 的
 * mock 會一併列出（來源 `assistant-issue`、連結處理事項），讓兩邊的連結打得開。比照 `mock-chat-cases.ts`，
 * 不加進 `demo-seed.ts`（後端有測試會讀它）。
 */

/** 從處理事項另開的案件：確認過的標題與說明，加上處理事項的連結。 */
export interface MockIssueCase {
  readonly id: string;
  readonly issueId: string;
  readonly typeId: string;
  readonly groupId: string;
  readonly title: string;
  readonly description: string;
  readonly createdBy: AccountId;
  readonly dueAt: string;
  readonly createdAt: string;
}

/** 對話框選得到的類型（啟用中）與承辦組（未封存）：mock 用它們判斷 `422`，與後端的順序相同。 */
export interface MockOpenCaseOptions {
  readonly activeTypeIds: readonly string[];
  readonly groupIds: readonly string[];
}

export interface MockOpenCaseFields {
  readonly typeId: string;
  readonly groupId: string;
  readonly dueAt: string;
  readonly title: string;
  readonly description: string;
}

export interface MockOpenCaseRefusal {
  readonly reason: string | null;
  readonly message: string;
  readonly fieldErrors: Readonly<Partial<Record<OpenCaseField, string>>>;
}

let issueCases: readonly MockIssueCase[] = [];

export function recordMockIssueCase(item: MockIssueCase): void {
  issueCases = [...issueCases, item];
}

export function mockIssueCases(): readonly MockIssueCase[] {
  return issueCases;
}

/** 只給測試：清掉這次工作階段另開的案件。 */
export function resetMockIssueCasesForTest(): void {
  issueCases = [];
}

/**
 * 與 `POST /api/v1/cases` 相同的檢查順序：先一次列出所有欄位錯誤，再看時限早於現在、類型停用、承辦組封存。
 */
export function checkMockOpenCaseFields(
  request: OpenCaseFromIssueRequest,
  options: MockOpenCaseOptions,
  now: Date,
): MockOpenCaseFields | MockOpenCaseRefusal {
  const fieldErrors: Partial<Record<OpenCaseField, string>> = {};
  const title = (request.title ?? '').trim();
  const description = (request.description ?? '').trim();
  if (!request.typeId) fieldErrors.typeId = CASE_TYPE_REQUIRED_MESSAGE;
  if (!request.groupId) fieldErrors.groupId = CASE_GROUP_REQUIRED_MESSAGE;
  if (!request.dueAt) fieldErrors.dueAt = CASE_DUE_REQUIRED_MESSAGE;
  if (title.length === 0) fieldErrors.title = CASE_TITLE_REQUIRED_MESSAGE;
  else if (title.length > CASE_TITLE_MAX_LENGTH) fieldErrors.title = CASE_TITLE_TOO_LONG_MESSAGE;
  if (description.length > CASE_DESCRIPTION_MAX_LENGTH) fieldErrors.description = CASE_DESCRIPTION_TOO_LONG_MESSAGE;
  if (Object.keys(fieldErrors).length > 0) {
    return { reason: null, message: Object.values(fieldErrors)[0] ?? '', fieldErrors };
  }
  const refuse = (reason: string, field: OpenCaseField, message: string): MockOpenCaseRefusal =>
    ({ reason, message, fieldErrors: { [field]: message } });
  const due = new Date(request.dueAt as string);
  if (Number.isNaN(due.getTime())) return refuse('due-in-past', 'dueAt', CASE_DUE_REQUIRED_MESSAGE);
  if (due.getTime() < now.getTime()) return refuse('due-in-past', 'dueAt', CASE_DUE_IN_PAST_MESSAGE);
  if (!options.activeTypeIds.includes(request.typeId as string)) {
    return refuse('case-type-inactive', 'typeId', CASE_TYPE_INACTIVE_MESSAGE);
  }
  if (!options.groupIds.includes(request.groupId as string)) {
    return refuse('case-group-archived', 'groupId', '這個承辦組已封存，請選擇其他承辦組。');
  }
  return { typeId: request.typeId as string, groupId: request.groupId as string, dueAt: due.toISOString(), title, description };
}
