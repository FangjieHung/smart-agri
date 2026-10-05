import { TestBed } from '@angular/core/testing';
import { createEmptyAssistantDraft, type AssistantAnswerRules } from '../../../../core/domain/assistant-draft.model';
import type { PeriodicReportAutoDisabledView } from '../../../../core/domain/assistant-settings.model';
import { AnswerRulesFormComponent } from './answer-rules-form.component';

const RULES: AssistantAnswerRules = {
  ...createEmptyAssistantDraft().rules,
  dataWriteDatabaseId: 'database-customer-records',
  dataWritePurpose: '每月回報滿意度。',
  periodicReport: 'monthly',
};

const AUTO_DISABLED: PeriodicReportAutoDisabledView = {
  disabledAt: '2026-10-05T10:59:36.047524+00:00',
  reason: 'owner-cannot-read',
  skippedPeriods: 3,
  message: '已自動停用：連續 3 期沒有產生報表，最近一期是因為助理擁有者無法讀取這個數據庫的紀錄。恢復權限後可以重新啟用。',
};

function render(autoDisabled: PeriodicReportAutoDisabledView | null, rules: AssistantAnswerRules = RULES) {
  TestBed.configureTestingModule({ imports: [AnswerRulesFormComponent] });
  const fixture = TestBed.createComponent(AnswerRulesFormComponent);
  fixture.componentRef.setInput('rules', rules);
  fixture.componentRef.setInput('writableDatabases', [{ id: 'database-customer-records', name: '客戶回訪紀錄' }]);
  fixture.componentRef.setInput('live', true);
  fixture.componentRef.setInput('autoDisabled', autoDisabled);
  fixture.detectChanges();
  return { fixture, page: fixture.nativeElement as HTMLElement };
}

describe('AnswerRulesFormComponent periodic report auto-disable (issue #179)', () => {
  it('shows 已自動停用 with the reason and a re-enable action, described by the select', () => {
    const { fixture, page } = render(AUTO_DISABLED);
    const resumed = vi.fn();
    fixture.componentInstance.resumeReport.subscribe(resumed);

    const notice = page.querySelector('#periodic-report-auto-disabled');
    expect(notice?.textContent).toContain('已自動停用');
    expect(notice?.textContent).toContain('助理擁有者無法讀取這個數據庫的紀錄');
    expect(notice?.textContent).toContain('不補做停用期間');
    expect(notice?.querySelector('time')?.getAttribute('datetime')).toBe(AUTO_DISABLED.disabledAt);
    expect(page.querySelector('#periodic-report')?.getAttribute('aria-describedby')).toContain('periodic-report-auto-disabled');
    expect((page.querySelector('#periodic-report') as HTMLSelectElement).value).toBe('monthly');

    const button = Array.from(notice?.querySelectorAll('button') ?? []).find((candidate) => candidate.textContent?.includes('重新啟用'));
    button?.click();
    expect(resumed).toHaveBeenCalledTimes(1);
  });

  it('disables the re-enable action while a save is in flight', () => {
    const { fixture, page } = render(AUTO_DISABLED);
    fixture.componentRef.setInput('resuming', true);
    fixture.detectChanges();

    expect((page.querySelector('#periodic-report-auto-disabled button') as HTMLButtonElement).disabled).toBe(true);
  });

  it('shows nothing extra for a running schedule', () => {
    const { page } = render(null);

    expect(page.querySelector('#periodic-report-auto-disabled')).toBeNull();
    expect(page.textContent).not.toContain('已自動停用');
  });

  it('offers no re-enable once the report is set to off', () => {
    const { page } = render(AUTO_DISABLED, { ...RULES, periodicReport: 'off' });

    expect(page.querySelector('#periodic-report-auto-disabled button')).toBeNull();
  });
});
