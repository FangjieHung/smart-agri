import { TestBed } from '@angular/core/testing';
import type { TrackedSubjectView } from '../../../core/domain/database.model';
import { provideDatabaseTesting } from '../databases.testing';
import { TrendViewComponent } from './trend-view.component';

function subject(index: number): TrackedSubjectView {
  const result = provideDatabaseTesting().repository.getDatabaseTracking('account-smb-admin', 'database-customer-records');
  if (result.status !== 'ready') throw new Error('expected ready');
  return result.data.subjects[index];
}

function render(value: TrackedSubjectView) {
  TestBed.configureTestingModule({ imports: [TrendViewComponent] });
  const fixture = TestBed.createComponent(TrendViewComponent);
  fixture.componentRef.setInput('subject', value);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

function cells(row: Element | null | undefined): string[] {
  return Array.from(row?.querySelectorAll('th, td') ?? []).map((cell) => cell.textContent?.trim() ?? '');
}

describe('TrendViewComponent', () => {
  it('compares current, previous and first values using the precomputed differences', () => {
    const host = render(subject(0));
    const table = host.querySelector('table.comparison-table');

    expect(cells(table?.querySelector('thead tr'))).toEqual(['指標', '首次', '上次', '本次', '較上次', '較首次']);
    const satisfaction = Array.from(table?.querySelectorAll('tbody tr') ?? []).find((row) =>
      row.textContent?.includes('整體滿意度'),
    );
    expect(cells(satisfaction)).toEqual(['整體滿意度', '3 / 5', '3 / 5', '5 / 5', '+2 分', '+2 分']);
  });

  it('turns the precomputed summaries into the trend conclusion', () => {
    const conclusion = render(subject(0)).querySelector('.trend-conclusion');

    expect(conclusion?.textContent).toContain('整體滿意度：本次 5 / 5，較上次 +2 分，較首次 +2 分。');
    expect(conclusion?.textContent).toContain('本月消費金額：本次 3,200 元，較上次 +1,100 元，較首次 +1,400 元。');
  });

  it('draws one accessible trend chart per numeric metric', () => {
    const host = render(subject(0));

    expect(host.querySelectorAll('app-trend-chart svg[role="img"]')).toHaveLength(2);
    expect(host.querySelectorAll('app-trend-chart tbody tr')).toHaveLength(8);
  });

  it('does not show any trend conclusion with fewer than two records', () => {
    const host = render(subject(2));

    expect(host.querySelector('.trend-conclusion')).toBeNull();
    expect(host.querySelector('app-trend-chart')).toBeNull();
    expect(host.querySelector('table.comparison-table')).toBeNull();
    expect(host.querySelector('.insufficient-records')?.textContent).toContain('累積 2 筆以上');
  });
});
