import { TestBed } from '@angular/core/testing';
import { throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { AccountId } from '../../../core/domain/account.model';
import type { MockDemoRepository } from '../../../core/repositories/mock-demo-repository';
import { provideDatabaseTesting } from '../databases.testing';
import { DatabaseReportsComponent } from './database-reports.component';

async function render(
  databaseId: string,
  options: { readonly viewer?: AccountId; readonly arrange?: (repository: MockDemoRepository) => void } = {},
) {
  const testing = provideDatabaseTesting(options.viewer);
  options.arrange?.(testing.repository);
  TestBed.configureTestingModule({ imports: [DatabaseReportsComponent], providers: testing.providers });
  const fixture = TestBed.createComponent(DatabaseReportsComponent);
  fixture.componentRef.setInput('databaseId', databaseId);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  const settle = async () => {
    await fixture.whenStable();
    fixture.detectChanges();
  };
  return { fixture, host: fixture.nativeElement as HTMLElement, repository: testing.repository, settle };
}

describe('DatabaseReportsComponent', () => {
  it('lists the schedule and the reports, newest first, and opens the newest one', async () => {
    const { host } = await render('database-customer-records');

    expect(host.querySelector('.schedules')?.textContent).toContain('客服助理');
    expect(host.querySelector('.schedules')?.textContent).toContain('每月報表');
    expect(host.querySelector('.schedules')?.textContent).toContain('2026-10-01');
    const items = Array.from(host.querySelectorAll('ul.report-list li button'));
    expect(items.map((item) => item.querySelector('.period')?.textContent?.trim())).toEqual([
      '2026-08-01 至 2026-08-31',
      '2026-07-01 至 2026-07-31',
      '2026-06-01 至 2026-06-30',
    ]);
    expect(items[0].getAttribute('aria-current')).toBe('true');
    expect(host.querySelector('app-database-report h4')?.textContent).toContain('2026-08-01 至 2026-08-31');
    // 說明只在站內查看。
    expect(host.textContent).toContain('不會寄 Email 或 LINE');
  });

  it('marks the report with too few records and the ones with an AI summary', async () => {
    const { host } = await render('database-customer-records');
    const badges = Array.from(host.querySelectorAll('ul.report-list li')).map((item) =>
      Array.from(item.querySelectorAll('app-status-badge')).map((badge) => badge.textContent?.trim()),
    );

    expect(badges).toEqual([['含 AI 摘要'], ['含 AI 摘要'], ['紀錄不足']]);
  });

  it('opens another report when it is chosen', async () => {
    const { host, repository, settle } = await render('database-customer-records', {
      arrange: (repo) => vi.spyOn(repo, 'getDatabaseReport'),
    });

    const june = host.querySelectorAll<HTMLButtonElement>('ul.report-list li button')[2];
    june.click();
    await settle();

    expect(june.getAttribute('aria-current')).toBe('true');
    expect(host.querySelector('app-database-report h4')?.textContent).toContain('2026-06-01 至 2026-06-30');
    expect(host.querySelector('app-database-report .notice.insufficient')).not.toBeNull();
    expect(repository.getDatabaseReport).toHaveBeenLastCalledWith('database-customer-records', expect.stringContaining('2026-06-01'));
  });

  it('says there is no schedule and no report on a database nobody reports on', async () => {
    const { host } = await render('database-orders');

    expect(host.textContent).toContain('目前沒有助理為這個資料庫設定定期報表');
    expect(host.textContent).toContain('還沒有報表');
    expect(host.querySelector('app-database-report')).toBeNull();
  });

  it('shows a refusal, with nothing of the reports, to someone who may not see the database', async () => {
    const { host } = await render('database-customer-records', { viewer: 'account-external-customer' });

    expect(host.textContent).toContain('無法查看定期報表');
    expect(host.querySelector('ul.report-list')).toBeNull();
    expect(host.querySelector('.schedules')).toBeNull();
  });

  it('keeps a failed read apart from "no reports" and lets it be retried', async () => {
    let calls = 0;
    const { host, repository, settle } = await render('database-customer-records', {
      arrange: (repo) => {
        const original = repo.listDatabaseReports.bind(repo);
        vi.spyOn(repo, 'listDatabaseReports').mockImplementation((id) =>
          ++calls === 1 ? throwError(() => new Error('503')) : original(id),
        );
      },
    });

    expect(host.textContent).toContain('目前無法載入定期報表');
    expect(host.textContent).not.toContain('還沒有報表');
    host.querySelector<HTMLButtonElement>('app-state-panel button')?.click();
    await settle();
    expect(repository.listDatabaseReports).toHaveBeenCalledTimes(2);
    expect(host.querySelectorAll('ul.report-list li')).toHaveLength(3);
  });
});
