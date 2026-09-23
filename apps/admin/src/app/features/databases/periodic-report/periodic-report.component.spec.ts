import { TestBed } from '@angular/core/testing';
import type { PeriodicReportView } from '../../../core/domain/database.model';
import { PeriodicReportComponent } from './periodic-report.component';

const report: PeriodicReportView = {
  assistantName: '客服助理',
  scheduleLabel: '每月一次',
  anchorLabel: '2026-09-15',
  nextReportLabel: '2026-10-15',
  purpose: '記錄每次回訪的滿意度與消費變化。',
  lines: ['王小姐 · 整體滿意度：本次 5 / 5，較上次 +2 分，較首次 +2 分。'],
  note: '摘要直接引用「趨勢比較」已算好的差異值，助理不會重新計算數字。',
};

function render(view: PeriodicReportView) {
  TestBed.configureTestingModule({ imports: [PeriodicReportComponent] });
  const fixture = TestBed.createComponent(PeriodicReportComponent);
  fixture.componentRef.setInput('report', view);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('PeriodicReportComponent', () => {
  it('names the assistant, its schedule and the next report date', () => {
    const host = render(report);

    expect(host.querySelector('.periodic-report')?.textContent).toContain('客服助理');
    expect(host.querySelector('.schedule')?.textContent).toContain('每月一次');
    expect(host.querySelector('.next-report')?.textContent).toContain('2026-10-15');
    expect(host.querySelector('.next-report')?.textContent).toContain('2026-09-15');
  });

  it('repeats the precomputed comparison summaries verbatim and says so', () => {
    const host = render(report);

    expect(host.querySelector('ul.report-lines li')?.textContent).toBe(report.lines[0]);
    expect(host.textContent).toContain('不會重新計算數字');
  });

  it('says there is nothing to summarise rather than inventing one', () => {
    const host = render({ ...report, lines: [] });

    expect(host.querySelector('ul.report-lines li')).toBeNull();
    expect(host.textContent).toContain('還沒有可比較的紀錄');
  });
});
