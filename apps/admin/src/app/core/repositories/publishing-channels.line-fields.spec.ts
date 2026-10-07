import sharedCases from '../domain/line-field-cases.json';
import type { LineField } from '../domain/publishing.model';
import { trimLineValue, validateLineField, validateLineNonTextReply } from './publishing-channels';

interface LineFieldCase {
  readonly name: string;
  readonly field: string;
  readonly value: string;
  readonly error: string | null;
  readonly normalized?: string;
}

const LINE_FIELD_IDS: readonly LineField[] = ['officialAccountId', 'channelId', 'channelSecret', 'accessToken'];

/** #291：收到非文字訊息時的回覆，由 `validateLineNonTextReply()`／`LineChannelRules.ValidateNonTextReply` 檢查。 */
const NON_TEXT_REPLY = 'nonTextReply';

function isLineField(field: string): field is LineField {
  return LINE_FIELD_IDS.some((candidate) => candidate === field);
}

/**
 * LINE 四個連接欄位的格式檢查與後端 `LineChannelRules.ValidateField` 同規則（#229），收到非文字訊息時的回覆與
 * `LineChannelRules.ValidateNonTextReply` 同規則（#291）：同一份案例
 * `apps/admin/src/app/core/domain/line-field-cases.json` 也由
 * `apps/api/tests/SmartAgri.Application.Tests/Assistants/LineChannelRulesTests.cs` 執行。
 * 這裡照 mock 儲存時的流程：先 `trimLineValue()`，再 `validateLineField()`（或 `validateLineNonTextReply()`）。
 */
describe('validateLineField (shared LINE field cases with the API)', () => {
  const cases: readonly LineFieldCase[] = sharedCases.cases;

  it('has the shared cases for every field', () => {
    expect(cases.length).toBeGreaterThan(30);
    expect(new Set(cases.map((testCase) => testCase.field))).toEqual(new Set([...LINE_FIELD_IDS, NON_TEXT_REPLY]));
  });

  it.each(cases.map((testCase) => [testCase.name, testCase] as const))('%s', (_name, testCase) => {
    const field = testCase.field;
    let error: string | null;
    if (field === NON_TEXT_REPLY) error = validateLineNonTextReply(testCase.value);
    else if (isLineField(field)) error = validateLineField(field, testCase.value);
    else throw new Error(`unknown field ${field}`);

    expect(error).toBe(testCase.error);
    if (testCase.error === null) expect(trimLineValue(testCase.value)).toBe(testCase.normalized);
  });
});
