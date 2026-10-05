import { TestBed } from '@angular/core/testing';
import { map, of, Subject, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { DatabaseReportView } from '../../../core/domain/database.model';
import type { RepositoryView } from '../../../core/repositories/demo-repository';
import type { MockDemoRepository } from '../../../core/repositories/mock-demo-repository';
import { syncValue } from '../../../core/repositories/sync-value.testing';
import { provideDatabaseTesting } from '../databases.testing';
import { DatabaseReportComponent } from './database-report.component';

const DATABASE = 'database-customer-records';

/** 2026-09-22 的 mock：最近三個完成的月份是 8 月（足夠）、7 月（足夠）、6 月（紀錄不足）。 */
function reportIds(repository: MockDemoRepository): Record<string, string> {
  const list = syncValue(repository.listDatabaseReports(DATABASE));
  if (list.status !== 'ready') throw new Error('expected ready');
  return Object.fromEntries(list.data.reports.map((report) => [report.periodFrom, report.id]));
}

async function render(
  month: '2026-08-01' | '2026-07-01' | '2026-06-01',
  arrange: (repository: MockDemoRepository, id: string) => void = () => undefined,
) {
  const testing = provideDatabaseTesting();
  const ids = reportIds(testing.repository);
  arrange(testing.repository, ids[month]);
  TestBed.configureTestingModule({ imports: [DatabaseReportComponent], providers: testing.providers });
  const fixture = TestBed.createComponent(DatabaseReportComponent);
  fixture.componentRef.setInput('databaseId', DATABASE);
  fixture.componentRef.setInput('reportId', ids[month]);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  const settle = async () => {
    await fixture.whenStable();
    fixture.detectChanges();
  };
  return { fixture, host: fixture.nativeElement as HTMLElement, repository: testing.repository, settle, id: ids[month] };
}

/** 讓 `getDatabaseReport` 回傳 `change(原本的報表)`。 */
function mapReport(change: (report: DatabaseReportView) => DatabaseReportView) {
  return (repository: MockDemoRepository) => {
    const original = repository.getDatabaseReport.bind(repository);
    vi.spyOn(repository, 'getDatabaseReport').mockImplementation((databaseId, reportId) =>
      original(databaseId, reportId).pipe(
        map((view): RepositoryView<DatabaseReportView> => (view.status === 'ready' ? { status: 'ready', data: change(view.data) } : view)),
      ),
    );
  };
}

function rows(host: HTMLElement): string[][] {
  return Array.from(host.querySelectorAll('table.statistics-table tbody tr')).map((row) =>
    Array.from(row.querySelectorAll('th, td')).map((cell) => cell.textContent?.trim() ?? ''),
  );
}

describe('DatabaseReportComponent', () => {
  it('shows the saved statistics with the change, a chart that only scales them, and a labelled AI summary apart', async () => {
    const { host } = await render('2026-08-01');

    expect(host.querySelector('h4')?.textContent).toContain('2026-08-01 至 2026-08-31｜客服助理｜每月報表');
    expect(Array.from(host.querySelectorAll('thead th')).map((cell) => cell.textContent?.trim())).toEqual(['項目', '這一期', '前一期', '變化']);
    expect(rows(host)).toEqual([
      ['紀錄筆數', '2 筆', '1 筆', '+1 筆'],
      ['本月消費金額（加總）', '3,600 元', '2,400 元', '+1,200 元'],
    ]);

    const spend = host.querySelector('[data-chart-row="field-monthly-spend"]');
    expect(spend?.textContent).toContain('3,600 元');
    expect(spend?.textContent).toContain('2,400 元');
    const widths = Array.from(spend?.querySelectorAll<HTMLElement>('.bar') ?? []).map((bar) => bar.style.inlineSize);
    expect(widths).toEqual(['100%', '66.66666666666666%']);
    expect(host.querySelector('figure.chart figcaption')?.textContent).toContain('數字以上表為準');

    const summary = host.querySelector('.ai-summary');
    expect(summary?.getAttribute('data-summary-status')).toBe('ready');
    expect(summary?.querySelector('.ai-label')?.textContent?.trim()).toBe('AI 摘要');
    expect(summary?.querySelector('.summary-text')?.textContent).toBe('整體來看，紀錄筆數：本期 2 筆，前一期 1 筆，變化 +1 筆。');
    expect(summary?.textContent).toContain('一切數字以統計為準');
    expect(host.querySelector('.statistics')?.contains(summary ?? null)).toBe(false);
  });

  it('says 紀錄不足 and shows no change, no chart and no AI summary for a report without two periods of records', async () => {
    const { host } = await render('2026-06-01');

    expect(host.querySelector('.notice.insufficient')?.textContent).toContain('紀錄不足：這一期有 1 筆、前一期有 0 筆紀錄');
    expect(Array.from(host.querySelectorAll('thead th')).map((cell) => cell.textContent?.trim())).toEqual(['項目', '這一期', '前一期']);
    expect(rows(host)[0]).toEqual(['紀錄筆數', '1 筆', '0 筆']);
    expect(host.querySelector('figure.chart')).toBeNull();
    expect(host.querySelector('.ai-summary .summary-text')).toBeNull();
    expect(host.querySelector('.ai-summary')?.textContent).toContain('紀錄不足，不產生 AI 摘要');
  });

  it('keeps the statistics and chart when the AI summary failed, and retries it from the summary area only', async () => {
    const retry = new Subject<RepositoryView<DatabaseReportView>>();
    const { host, repository, settle } = await render('2026-08-01', (repo) => {
      mapReport((report) => ({
        ...report,
        report: { ...report.report, summaryStatus: 'failed' },
        aiSummary: { ...report.aiSummary, status: 'failed', text: null, note: 'AI 摘要暫時無法產生，統計與圖表不受影響。可以稍後重試。' },
      }))(repo);
      vi.spyOn(repo, 'retryDatabaseReportSummary').mockReturnValue(retry);
    });

    expect(rows(host)).toHaveLength(2);
    expect(host.querySelector('figure.chart')).not.toBeNull();
    expect(host.querySelector('.ai-summary .summary-text')).toBeNull();
    expect(host.querySelector('.ai-summary .notice')?.textContent).toContain('統計與圖表不受影響');
    const button = host.querySelector<HTMLButtonElement>('.ai-summary button');
    expect(button?.textContent).toContain('重新產生摘要');

    button?.click();
    await settle();
    // 重試進行中不能再按；統計仍在畫面上。
    expect(host.querySelector<HTMLButtonElement>('.ai-summary button')?.disabled).toBe(true);
    host.querySelector<HTMLButtonElement>('.ai-summary button')?.click();
    expect(repository.retryDatabaseReportSummary).toHaveBeenCalledTimes(1);
    expect(repository.retryDatabaseReportSummary).toHaveBeenCalledWith(DATABASE, expect.any(String));

    retry.next({ status: 'loading' });
    retry.complete();
    await settle();
    expect(repository.getDatabaseReport).toHaveBeenCalledTimes(2);
    expect(host.querySelector<HTMLButtonElement>('.ai-summary button')?.disabled).toBe(false);
  });

  it('offers a retry for a discarded summary and says it was never shown', async () => {
    const { host } = await render(
      '2026-08-01',
      mapReport((report) => ({
        ...report,
        aiSummary: {
          ...report.aiSummary,
          status: 'discarded',
          text: null,
          note: 'AI 摘要含有統計結果裡沒有的數字，已捨棄、沒有顯示。統計與圖表不受影響，可以重試。',
        },
      })),
    );

    expect(host.querySelector('.ai-summary')?.getAttribute('data-summary-status')).toBe('discarded');
    expect(host.querySelector('.ai-summary')?.textContent).toContain('已捨棄、沒有顯示');
    expect(host.querySelector('.ai-summary button')?.textContent).toContain('重新產生摘要');
  });

  it('says so when the retry request itself fails, without touching the report', async () => {
    const { host, settle } = await render('2026-08-01', (repo) => {
      mapReport((report) => ({ ...report, aiSummary: { ...report.aiSummary, status: 'failed', text: null, note: '失敗' } }))(repo);
      vi.spyOn(repo, 'retryDatabaseReportSummary').mockReturnValue(throwError(() => new Error('503')));
    });

    host.querySelector<HTMLButtonElement>('.ai-summary button')?.click();
    await settle();

    expect(host.querySelector('.ai-summary .error')?.textContent).toContain('目前無法重新產生');
    expect(rows(host)).toHaveLength(2);
  });

  it('shows a pending summary as being written, with a way to look again', async () => {
    const { host, repository, settle } = await render(
      '2026-08-01',
      mapReport((report) => ({ ...report, aiSummary: { ...report.aiSummary, status: 'pending', text: null } })),
    );

    expect(host.querySelector('.ai-summary [role="status"]')?.textContent).toContain('正在產生');
    host.querySelector<HTMLButtonElement>('.ai-summary button')?.click();
    await settle();
    expect(repository.getDatabaseReport).toHaveBeenCalledTimes(2);
  });

  it('shows only the reason for a period that produced no report', async () => {
    const { host } = await render(
      '2026-08-01',
      mapReport((report) => ({
        ...report,
        report: { ...report.report, status: 'skipped', skipReason: 'owner-cannot-read', skipMessage: '這一期沒有產生報表：助理擁有者目前無法讀取這個數據庫的紀錄。', dataState: null, dataMessage: null },
        statistics: null,
      })),
    );

    expect(host.querySelector('.notice.skipped')?.textContent).toContain('助理擁有者目前無法讀取');
    expect(host.querySelector('table')).toBeNull();
    expect(host.querySelector('.ai-summary')).toBeNull();
  });

  it('keeps a failed read apart from a permission refusal and lets it be retried', async () => {
    const { host, repository, settle } = await render('2026-08-01', (repo) => {
      const original = repo.getDatabaseReport.bind(repo);
      let calls = 0;
      vi.spyOn(repo, 'getDatabaseReport').mockImplementation((databaseId, reportId) =>
        ++calls === 1 ? throwError(() => new Error('503')) : original(databaseId, reportId),
      );
    });

    expect(host.textContent).toContain('目前無法載入這份報表');
    expect(host.textContent).not.toContain('無法查看這份報表');
    host.querySelector<HTMLButtonElement>('app-state-panel button')?.click();
    await settle();
    expect(repository.getDatabaseReport).toHaveBeenCalledTimes(2);
    expect(host.querySelector('.statistics-table')).not.toBeNull();
  });

  it('shows the refusal, not a report, to someone who may not read the records', async () => {
    const { host } = await render('2026-08-01', (repo) => {
      vi.spyOn(repo, 'getDatabaseReport').mockReturnValue(
        of({ status: 'permission-denied', reason: 'database-records', message: '只有被指定為資料管理者。' }),
      );
    });

    expect(host.textContent).toContain('無法查看這份報表');
    expect(host.querySelector('.statistics-table')).toBeNull();
    expect(host.querySelector('.ai-summary')).toBeNull();
  });
});
