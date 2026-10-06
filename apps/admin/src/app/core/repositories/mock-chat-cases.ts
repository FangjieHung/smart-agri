import type { AccountId } from '../domain/account.model';
import type { ProposableCaseType } from '../domain/case-proposal';

/*
 * 純 Demo 模式的助理提議開案（issue #254）。可提議的類型沿用 `CaseSettingsRepository` 範例裡啟用中的
 * 類型（同一個 id、名稱、承辦組與時限）；在 mock 裡停用類型不會影響這份清單。確認後建立的案件只保存在
 * 這次工作階段，`CasesRepository` 的 mock 會一併列出，讓案件連結打得開。不加進 `demo-seed.ts`（後端有測試會讀它）。
 */

export const MOCK_PROPOSABLE_CASE_TYPES: readonly ProposableCaseType[] = [
  {
    typeId: 'case-type-equipment-repair',
    name: '設備故障報修',
    group: { id: 'case-group-equipment', name: '設備組', archived: false },
    dueHours: 72,
  },
];

/** 從對話確認建立的案件：只有確認過的標題與說明，加上對話串的連結。 */
export interface MockChatProposedCase {
  readonly id: string;
  readonly typeId: string;
  readonly groupId: string;
  readonly title: string;
  readonly description: string;
  readonly createdBy: AccountId;
  readonly dueAt: string;
  readonly createdAt: string;
  readonly assistantId: string;
  readonly threadId: string;
}

let proposedCases: readonly MockChatProposedCase[] = [];

export function recordMockChatProposedCase(item: MockChatProposedCase): void {
  proposedCases = [...proposedCases, item];
}

export function mockChatProposedCases(): readonly MockChatProposedCase[] {
  return proposedCases;
}

/** 只給測試：清掉這次工作階段建立的案件。 */
export function resetMockChatProposedCasesForTest(): void {
  proposedCases = [];
}
