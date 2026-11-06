import { Injector, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, Subject, throwError, type Observable } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { AccountId, AccountPermission } from '../../core/domain/account.model';
import type { OrderingSummaryView } from '../../core/domain/ordering.model';
import { OrderingRepository, type OrderingRead } from '../../core/repositories/ordering.repository';
import { ApiSessionService } from '../../core/session/api-session.service';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { refreshOrderPendingCount } from './order-attention.loader';

type Read = OrderingRead<OrderingSummaryView>;
const ready = (pendingCount: number): Read => ({ status: 'ready', data: { pendingCount } as OrderingSummaryView });

function setup(accountId: AccountId | null, permissions: readonly AccountPermission[], answers: Observable<Read>[]) {
  TestBed.resetTestingModule();
  const summary = vi.fn<() => Observable<Read>>();
  for (const answer of answers) summary.mockReturnValueOnce(answer);
  TestBed.configureTestingModule({
    providers: [
      { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>(accountId) } },
      { provide: ApiSessionService, useValue: { permissions: signal(permissions) } },
      { provide: OrderingRepository, useValue: { summary } },
    ],
  });
  return { injector: TestBed.inject(Injector), summary, count: signal(0) };
}

/** 側欄「點餐」待確認數字的讀取程式（issue #399）：shell 換頁或收到 NAV_COUNTS_CHANGED_EVENT 時以動態 `import()` 呼叫。 */
describe('refreshOrderPendingCount', () => {
  it('writes the pending number of an account that handles orders', async () => {
    const { injector, summary, count } = setup('account-internal-employee', ['manage-orders'], [of(ready(3)), of(ready(1))]);
    await refreshOrderPendingCount(injector, count);
    expect(count()).toBe(3);
    await refreshOrderPendingCount(injector, count);
    expect(count()).toBe(1);
    expect(summary).toHaveBeenCalledTimes(2);
  });

  it('never asks without manage-orders or without an account', async () => {
    for (const [account, permissions] of [['account-external-customer', ['use-shared-assistants']], [null, ['manage-orders']]] as const) {
      const { injector, summary, count } = setup(account, permissions, [of(ready(5))]);
      count.set(4);
      await refreshOrderPendingCount(injector, count);
      expect(summary).not.toHaveBeenCalled();
      expect(count()).toBe(0);
    }
  });

  it('is 0 when denied or not configured, keeps the last number on failure, and ignores a late older answer', async () => {
    const denied = setup('account-smb-admin', ['manage-orders'], [of({ status: 'permission-denied', reason: 'ordering', message: '' })]);
    denied.count.set(2);
    await refreshOrderPendingCount(denied.injector, denied.count);
    expect(denied.count()).toBe(0);

    const missing = setup('account-smb-admin', ['manage-orders'], [of({ status: 'not-configured', message: '' })]);
    missing.count.set(2);
    await refreshOrderPendingCount(missing.injector, missing.count);
    expect(missing.count()).toBe(0);

    const offline = setup('account-smb-admin', ['manage-orders'], [throwError(() => new Error('offline'))]);
    offline.count.set(2);
    await refreshOrderPendingCount(offline.injector, offline.count);
    expect(offline.count()).toBe(2);

    const slow = new Subject<Read>();
    const race = setup('account-smb-admin', ['manage-orders'], [slow, of(ready(4))]);
    const first = refreshOrderPendingCount(race.injector, race.count);
    await refreshOrderPendingCount(race.injector, race.count);
    slow.next(ready(9));
    slow.complete();
    await first;
    expect(race.count()).toBe(4);
  });
});
