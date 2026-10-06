import type { ChatCaseProposalView } from '@smart-agri/chat';

/**
 * 助理提議開案（M7 計畫第 3 節 H、決定 T，issue #254）的前端規則：mock 用與後端
 * `CaseProposalRules` 相同的關鍵字判斷；確認與錯誤訊息與後端相同。真正的判斷在伺服器。
 */

/** 與後端 `CaseProposalRules.CaseIntentWords` 相同。 */
export const CASE_INTENT_WORDS: readonly string[] = ['報修', '維修', '修理', '叫修', '申請', '請款', '退貨', '派人', '安排', '開案', '立案'];

/** 與後端 `Case.TitleMaxLength` 相同。 */
export const CASE_PROPOSAL_TITLE_MAX_LENGTH = 120;

/** 與後端 `ChatCaseProposalEndpoints` 相同的訊息。 */
export const CASE_PROPOSAL_CLOSED_MESSAGE = '這個提議已經處理過了：已建立案件，或已選擇不用了。';
export const CASE_PROPOSAL_NOT_PROPOSABLE_MESSAGE = '這個案件類型已停用，或助理已不再提議這個類型，無法建立案件。';
export const CASE_PROPOSAL_FAILED_MESSAGE = '目前無法建立案件，請稍後再試一次；案件還沒有建立。';

/** 一個可以提議的類型（類型、預設承辦組、處理時限）。 */
export interface ProposableCaseType {
  readonly typeId: string;
  readonly name: string;
  readonly group: ChatCaseProposalView['group'];
  readonly dueHours: number;
}

/** 回覆文字：「這件事可以開一件「設備報修」案件，請確認內容。」 */
export function caseProposalText(typeName: string): string {
  return `這件事可以開一件「${typeName}」案件，請確認內容。`;
}

function normalize(text: string): string {
  return text.replace(/\s+/g, '');
}

/** 問題的前 120 字（空白收成一格，不切斷代理對）。 */
export function caseTitleFromQuestion(question: string): string {
  const text = question.trim().replace(/\s+/g, ' ');
  if (text.length <= CASE_PROPOSAL_TITLE_MAX_LENGTH) return text;
  const last = text.charCodeAt(CASE_PROPOSAL_TITLE_MAX_LENGTH - 1);
  const length = last >= 0xd800 && last <= 0xdbff ? CASE_PROPOSAL_TITLE_MAX_LENGTH - 1 : CASE_PROPOSAL_TITLE_MAX_LENGTH;
  return text.slice(0, length).trimEnd();
}

/**
 * 決定 T：命中開案關鍵字，而且問題含某個類型的名稱（多個時取最長的），或只有一個可提議類型時才提議。
 */
export function keywordCaseProposal(question: string, types: readonly ProposableCaseType[]): ProposableCaseType | null {
  const normalized = normalize(question);
  if (types.length === 0 || !CASE_INTENT_WORDS.some((word) => normalized.includes(word))) return null;
  const named = types
    .filter((type) => normalize(type.name).length > 0 && normalized.includes(normalize(type.name)))
    .sort((a, b) => normalize(b.name).length - normalize(a.name).length)[0];
  return named ?? (types.length === 1 ? (types[0] ?? null) : null);
}
