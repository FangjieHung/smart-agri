import { TestBed } from '@angular/core/testing';
import { throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { TrackedSubjectView } from '../../../core/domain/database.model';
import { provideDatabaseTesting } from '../databases.testing';
import { PeriodSummaryComponent } from './period-summary.component';

function subject(id: string): TrackedSubjectView {
  const result = provideDatabaseTesting().repository.readDatabaseTracking('account-smb-admin', 'database-customer-records');
  if (result.status !== 'ready') throw new Error('expected ready');
  const found = result.data.subjects.find((candidate) => candidate.id === id);
  if (!found) throw new Error(`no subject ${id}`);
  return found;
}

async function render(value: TrackedSubjectView, prepare?: (repository: ReturnType<typeof provideDatabaseTesting>['repository']) => void) {
  const testing = provideDatabaseTesting();
  prepare?.(testing.repository);
  TestBed.configureTestingModule({ imports: [PeriodSummaryComponent], providers: testing.providers });
  const fixture = TestBed.createComponent(PeriodSummaryComponent);
  fixture.componentRef.setInput('databaseId', 'database-customer-records');
  fixture.componentRef.setInput('subject', value);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, repository: testing.repository };
}

function rows(host: HTMLElement): string[][] {
  return Array.from(host.querySelectorAll('table.summary-table tbody tr')).map((row) =>
    Array.from(row.querySelectorAll('th, td')).map((cell) => cell.textContent?.trim() ?? ''),
  );
}

describe('PeriodSummaryComponent', () => {
  it('shows the count and the sums of the period next to the period before, as the repository computed them', async () => {
    const { host } = await render(subject('subject-wang'));

    expect(host.querySelector('.range')?.textContent).toContain('近 30 天（2026-08-24 至 2026-09-22）');
    expect(Array.from(host.querySelectorAll('thead th')).map((cell) => cell.textContent?.trim())).toEqual([
      '項目',
      '這一期',
      '前一期',
      '變化',
    ]);
    expect(rows(host)).toEqual([
      ['紀錄筆數', '1 筆', '1 筆', '持平'],
      ['本月消費金額（加總）', '3,200 元', '2,100 元', '+1,100 元'],
    ]);
  });

  it('asks the repository again when another period is chosen', async () => {
    const { fixture, host, repository } = await render(subject('subject-wang'), (repo) => vi.spyOn(repo, 'getDatabasePeriodSummary'));
    const select = host.querySelector<HTMLSelectElement>('select');

    select!.value = 'last-month';
    select!.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(repository.getDatabasePeriodSummary).toHaveBeenLastCalledWith('database-customer-records', {
      period: 'last-month',
      subjectId: 'subject-wang',
    });
    expect(host.querySelector('.range')?.textContent).toContain('上月（2026-08-01 至 2026-08-31）');
    expect(rows(host)[1]).toEqual(['本月消費金額（加總）', '2,100 元', '2,400 元', '-300 元']);
  });

  it('says plainly that there is nothing in either period instead of drawing zeros as a result', async () => {
    const { fixture, host } = await render(subject('subject-chen'));
    const select = host.querySelector<HTMLSelectElement>('select');

    select!.value = 'this-week';
    select!.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(host.querySelector('.empty')?.textContent).toContain('這兩個期間都沒有有效紀錄');
    expect(rows(host)[0]).toEqual(['紀錄筆數', '0 筆', '0 筆', '持平']);
  });

  it('keeps a failed read apart from "no records" and offers a retry', async () => {
    const { fixture, host, repository } = await render(subject('subject-wang'), (repo) => {
      vi.spyOn(repo, 'getDatabasePeriodSummary').mockReturnValueOnce(throwError(() => new Error('503')));
    });

    expect(host.textContent).toContain('目前無法載入期間統計');
    expect(host.querySelector('table')).toBeNull();
    expect(host.textContent).not.toContain('都沒有有效紀錄');

    host.querySelector<HTMLButtonElement>('app-state-panel button')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(repository.getDatabasePeriodSummary).toHaveBeenCalledTimes(2);
    expect(host.querySelector('table.summary-table')).not.toBeNull();
  });
});
