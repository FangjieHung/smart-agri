import sharedCases from '../domain/line-field-cases.json';
import type { LineField, LineSettingsInput } from '../domain/publishing.model';
import {
  DEMO_LINE_CHANNEL_ID,
  DEMO_LINE_CHANNEL_SECRET,
  type PublishingRecord,
} from './demo-seed-publishing';
import { lineChecks, trimLineSettings } from './publishing-channels';

interface LineFieldCase {
  readonly name: string;
  readonly field: string;
  readonly value: string;
  readonly error: string | null;
  readonly normalized?: string;
}

const LINE_FIELD_IDS: readonly LineField[] = [
  'officialAccountId',
  'channelId',
  'channelSecret',
  'accessToken',
];

const VALID_SETTINGS: LineSettingsInput = {
  officialAccountId: '@anxin-demo',
  channelId: DEMO_LINE_CHANNEL_ID,
  channelSecret: DEMO_LINE_CHANNEL_SECRET,
  accessToken: 'A'.repeat(40),
};

function isLineField(field: string): field is LineField {
  return LINE_FIELD_IDS.some((candidate) => candidate === field);
}

/**
 * LINE 四個連接欄位的格式檢查與後端 `LineChannelRules.ValidateField` 同規則（#229）：同一份案例
 * `apps/admin/src/app/core/domain/line-field-cases.json` 也由
 * `apps/api/tests/SmartAgri.Application.Tests/Assistants/LineChannelRulesTests.cs` 執行。
 * 這裡照 mock 儲存時的流程：先 `trimLineSettings()`，再 `lineChecks()`。
 */
describe('lineChecks (shared LINE field cases with the API)', () => {
  const cases: readonly LineFieldCase[] = sharedCases.cases;

  it('has the shared cases for every field', () => {
    expect(cases.length).toBeGreaterThan(30);
    expect(new Set(cases.map((testCase) => testCase.field))).toEqual(new Set(LINE_FIELD_IDS));
  });

  it.each(cases.map((testCase) => [testCase.name, testCase] as const))('%s', (_name, testCase) => {
    const field = testCase.field;
    if (!isLineField(field)) throw new Error(`unknown field ${field}`);

    const trimmed = trimLineSettings({ ...VALID_SETTINGS, [field]: testCase.value });
    const line: PublishingRecord['line'] = {
      ...trimmed,
      checked: true,
      enabled: false,
      lastTest: null,
      paused: false,
      updatedAt: '2026-10-06T00:00:00.000Z',
    };
    const checks = lineChecks(line);

    for (const other of checks.filter((check) => check.field !== field)) {
      expect(other.state).toBe('passed');
    }
    const check = checks.find((candidate) => candidate.field === field);
    if (testCase.error === null) {
      expect(check?.state).toBe('passed');
      expect(trimmed[field]).toBe(testCase.normalized);
    } else {
      expect(check).toMatchObject({ state: 'failed', message: testCase.error });
    }
  });
});
