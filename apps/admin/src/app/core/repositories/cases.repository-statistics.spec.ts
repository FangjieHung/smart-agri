import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AccountId } from '../domain/account.model';
import type { CaseListFilter, CaseStatisticsRowView, CaseStatisticsView } from '../domain/case.model';
import { DemoSessionService } from '../session/demo-session.service';
import { CASE_SETTINGS_ADMIN_DENIED_MESSAGE } from './case-settings.repository';
import { CasesRepository } from './cases.repository';

/**
 * 瓶頸統計的 Demo 模式（issue #251）：與後端 `CaseStatistics` 相同的判斷。Hybrid 見
 * `cases.repository-hybrid-statistics.spec.ts`（實際錄下的 API 回應）。
 */
function mockRepository(accountId: AccountId | null = 'account-smb-admin') {
  const activeAccountId = signal<AccountId | null>(accountId);
  TestBed.configureTestingModule({ providers: [{ provide: DemoSessionService, useValue: { activeAccountId } }] });
  return { repository: TestBed.inject(CasesRepository), activeAccountId };
}

function ready<T>(result: { status: string; data?: T }): T {
  if (result.status !== 'ready' || result.data === undefined) throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

/** 只假造 `Date`：未結案與逾期看「現在」，預設期間是到今天（UTC）為止的 30 天。 */
function at(iso: string): void {
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(new Date(iso));
}

function summary(view: CaseStatisticsView): unknown[][] {
  return view.rows.map((row) => [
    row.group.name, row.openCount, row.overdueCount, row.completedCount, row.cancelledCount, row.averageHandlingHours,
  ]);
}

/** 統計那一格點開的清單（與 `CaseStatisticsComponent.listParams` 相同的條件）。 */
async function listed(repository: CasesRepository, row: CaseStatisticsRowView, view: CaseStatisticsView, measure: 'open' | 'overdue' | 'completed' | 'cancelled') {
  const closed = measure === 'completed' || measure === 'cancelled';
  const filter: CaseListFilter = {
    scope: 'all',
    status: closed ? measure : 'open',
    typeId: row.type.id,
    groupId: row.group.id,
    ...(measure === 'overdue' ? { overdue: true } : {}),
    ...(closed ? { closedFrom: view.from, closedTo: view.to } : {}),
  };
  return ready(await firstValueFrom(repository.list(filter))).map((item) => item.id);
}

describe('CasesRepository statistics (mock, issue #251)', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('counts open and overdue now, completions within the range with creation → completion, and 「—」 without any', async () => {
    at('2026-10-07T01:00:00.000Z');
    const { repository } = mockRepository();

    const view = ready(await firstValueFrom(repository.statistics()));
    expect([view.from, view.to]).toEqual(['2026-09-08', '2026-10-07']);
    // 「灌溉馬達異音」10/1 02:00 建立、10/2 06:00 完成：28 小時。「溫室感測器離線」待受理且已逾期。
    expect(summary(view)).toEqual([
      ['採購組', 1, 0, 0, 0, null],
      ['設備組', 2, 1, 1, 0, 28],
    ]);

    // 只含完成那一天的期間；完成之後的那一天沒有完成件數。
    expect(summary(ready(await firstValueFrom(repository.statistics({ from: '2026-10-02', to: '2026-10-02' })))))
      .toEqual([['採購組', 1, 0, 0, 0, null], ['設備組', 2, 1, 1, 0, 28]]);
    expect(summary(ready(await firstValueFrom(repository.statistics({ from: '2026-10-03', to: '2026-10-07' })))))
      .toEqual([['採購組', 1, 0, 0, 0, null], ['設備組', 2, 1, 0, 0, null]]);

    expect(await firstValueFrom(repository.statistics({ from: '2026-10-07', to: '2026-10-01' })))
      .toEqual({ status: 'validation-failed', reason: 'invalid-date-range', message: '起始日期必須不晚於結束日期。', fieldErrors: {} });
    expect(await firstValueFrom(repository.statistics({ from: '2026-01-01', to: '2026-10-07' })))
      .toMatchObject({ status: 'validation-failed', message: '日期範圍最長 180 天。' });
  });

  it('counts a transferred case for the group that completed it, never averages a cancelled one, and every number equals its list', async () => {
    at('2026-10-06T01:00:00.000Z');
    const { repository, activeAccountId } = mockRepository('account-internal-employee');
    // 同仁受理「冷藏庫溫度降不下來」（10/6 01:00 建立）後轉給採購組；管理者受理、隔天同一時間完成：24 小時。
    ready(await firstValueFrom(repository.act('case-cold-room', 'accept', { eventCount: 1 })));
    ready(await firstValueFrom(repository.act('case-cold-room', 'transfer', { eventCount: 2, groupId: 'case-group-purchasing' })));
    activeAccountId.set('account-smb-admin');
    ready(await firstValueFrom(repository.act('case-cold-room', 'accept', { eventCount: 3 })));
    at('2026-10-07T01:00:00.000Z');
    ready(await firstValueFrom(repository.act('case-cold-room', 'complete', { eventCount: 4, resolution: '已請廠商更換壓縮機' })));
    // 管理者在受理前取消自己建立的「採購備用壓縮機」。
    ready(await firstValueFrom(repository.act('case-compressor-purchase', 'cancel', { eventCount: 1 })));

    const view = ready(await firstValueFrom(repository.statistics()));
    expect(summary(view)).toEqual([
      ['採購組', 0, 0, 1, 1, 24],
      ['設備組', 1, 1, 1, 0, 28],
    ]);

    for (const row of view.rows) {
      expect(await listed(repository, row, view, 'open')).toHaveLength(row.openCount);
      expect(await listed(repository, row, view, 'overdue')).toHaveLength(row.overdueCount);
      expect(await listed(repository, row, view, 'completed')).toHaveLength(row.completedCount);
      expect(await listed(repository, row, view, 'cancelled')).toHaveLength(row.cancelledCount);
    }
    const purchasing = view.rows[0];
    expect(await listed(repository, purchasing, view, 'completed')).toEqual(['case-cold-room']);
    expect(await listed(repository, purchasing, view, 'cancelled')).toEqual(['case-compressor-purchase']);
    // 期間外：沒有任何結案的案件。
    expect(ready(await firstValueFrom(repository.list({ status: 'closed', closedFrom: '2026-09-01', closedTo: '2026-09-30' })))).toEqual([]);
  });

  it('is only for the manager: everyone else gets 403 organization-settings', async () => {
    const { repository, activeAccountId } = mockRepository('account-internal-employee');
    const denied = { status: 'permission-denied', reason: 'organization-settings', message: CASE_SETTINGS_ADMIN_DENIED_MESSAGE };
    expect(await firstValueFrom(repository.statistics())).toEqual(denied);
    expect(repository.viewerIsManager()).toBe(false);
    activeAccountId.set('account-external-customer');
    expect(await firstValueFrom(repository.statistics())).toEqual(denied);
    activeAccountId.set('account-smb-admin');
    expect(repository.viewerIsManager()).toBe(true);
    expect((await firstValueFrom(repository.statistics())).status).toBe('ready');
  });
});
