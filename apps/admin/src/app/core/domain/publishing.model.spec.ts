import sharedCases from './allowed-domain-cases.json';
import { normalizeDomain, validateAllowedDomain } from './publishing.model';

interface AllowedDomainCase {
  readonly name: string;
  readonly raw: string;
  readonly existing: readonly string[];
  readonly error: string | null;
  readonly normalized?: string;
}

/**
 * 允許網域的驗證與後端 `WebsiteChannelRules.ValidateDomain` 同規則（#194）：同一份案例
 * `allowed-domain-cases.json`（與這個檔案同目錄）也由
 * `apps/api/tests/SmartAgri.Application.Tests/Assistants/WebsiteChannelRulesTests.cs` 執行。
 */
describe('validateAllowedDomain (shared cases with the API)', () => {
  const cases: readonly AllowedDomainCase[] = sharedCases.cases;

  it('has the shared cases', () => {
    expect(cases.length).toBeGreaterThan(20);
  });

  it.each(cases.map((testCase) => [testCase.name, testCase] as const))('%s', (_name, testCase) => {
    expect(validateAllowedDomain(testCase.raw, testCase.existing)).toBe(testCase.error);
    if (testCase.error === null) {
      expect(normalizeDomain(testCase.raw)).toBe(testCase.normalized);
    }
  });
});
