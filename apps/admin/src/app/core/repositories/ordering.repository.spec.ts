import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AccountId, AccountPermission } from '../domain/account.model';
import { ApiSessionService } from '../session/api-session.service';
import { DemoSessionService } from '../session/demo-session.service';
import { OrderingRepository } from './ordering.repository';
import { DEMO_REPOSITORY } from './tokens';

const NOW = Date.parse('2026-11-07T03:40:00Z'); // 11:40 in Taipei

function mockRepository(permissions: readonly AccountPermission[] = ['manage-orders'], account: AccountId | null = 'account-internal-employee') {
  vi.useFakeTimers({ now: NOW, toFake: ['Date'] });
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>(account) } },
      { provide: ApiSessionService, useValue: { permissions: signal(permissions) } },
      {
        provide: DEMO_REPOSITORY,
        useValue: { listAccounts: () => ({ status: 'ready', data: [{ id: 'account-internal-employee', displayName: '安心商行客服同仁', role: 'internal-employee', permissions }] }) },
      },
    ],
  });
  return TestBed.inject(OrderingRepository);
}

async function ready<T>(source: Promise<{ status: string; data?: T }>): Promise<T> {
  const view = await source;
  if (view.status !== 'ready') throw new Error(`expected ready, got ${view.status}`);
  return view.data as T;
}

describe('OrderingRepository (mock mode, issue #399)', () => {
  afterEach(() => vi.useRealTimers());

  it('has 王記麵店 with at least one order in each view, sorted like the page', async () => {
    const repository = mockRepository();
    const summary = await ready(firstValueFrom(repository.summary()));
    expect([summary.shopName, summary.timeZone, summary.pendingCount, summary.activeCount, summary.paused, summary.asapBand])
      .toEqual(['王記麵店', 'Asia/Taipei', 2, 5, false, '15-30']);
    expect(summary.latestOrderAt).not.toBeNull();

    const pending = await ready(firstValueFrom(repository.orders('pending')));
    expect(pending.orders.map((order) => order.displayNumber)).toEqual(['#016', '#015']);
    expect(pending.orders.every((order) => order.actions.join() === 'accept,decline')).toBe(true);
    const active = await ready(firstValueFrom(repository.orders('active')));
    expect(active.orders.map((order) => [order.displayNumber, order.status]))
      .toEqual([['#008', 'ready'], ['#009', 'accepted'], ['#013', 'ready'], ['#014', 'accepted'], ['#010', 'accepted']]);
    // 外送單（#401）：總額含運費，外帶單是 0 與 null。
    const delivery = active.orders.find((order) => order.serviceType === 'delivery-scheduled');
    expect(delivery && [delivery.customerName, delivery.subtotal, delivery.deliveryFee, delivery.total, delivery.delivery?.zoneName, delivery.delivery?.address, delivery.delivery?.note])
      .toEqual(['王太太', 370, 30, 400, '新營區其他里', '台南市新營區中山路 1 號 3 樓', '請按電鈴']);
    expect([...pending.orders, ...active.orders].filter((order) => order !== delivery).every((order) => order.deliveryFee === 0 && order.delivery === null)).toBe(true);
    expect(summary.services).toEqual({ takeoutScheduled: true, takeoutAsap: true, delivery: true, deliveryAsap: true, dineIn: true });
    expect(summary.openTableCount).toBe(1);
    // 內用（#402）：沒有電話，自動接單，掛在 A1 桌。
    const dineIn = active.orders.filter((order) => order.serviceType === 'dine-in');
    expect(dineIn.map((order) => [order.displayNumber, order.customerName, order.customerPhone, order.customerPhoneDisplay, order.autoAccepted, order.table?.name, order.accepted?.by]))
      .toEqual([['#008', '', '', '', true, 'A1', null], ['#009', '小明', '', '', true, 'A1', null]]);
    const done = await ready(firstValueFrom(repository.orders('done')));
    expect(done.orders.map((order) => order.status)).toEqual(['completed', 'declined']);
  });

  it('runs the state machine with compare-and-set: accept, a stale version is a 409, ready, complete', async () => {
    const repository = mockRepository();
    const before = await ready(firstValueFrom(repository.summary()));

    const accepted = await firstValueFrom(repository.act('order-016', { action: 'accept', version: 1, promisedAt: null }));
    expect(accepted.status === 'ready' && [accepted.data.status, accepted.data.version, accepted.data.actions, accepted.data.accepted?.by?.displayName])
      .toEqual(['accepted', 2, ['ready', 'complete', 'cancel'], '安心商行客服同仁']);
    // 不改時間：預約單是顧客選的時間。
    expect(accepted.status === 'ready' && accepted.data.promisedAt).toBe(accepted.status === 'ready' && accepted.data.pickupAt);

    const stale = await firstValueFrom(repository.act('order-016', { action: 'decline', version: 1, reason: 'too-busy', note: null }));
    expect(stale.status === 'conflict' && [stale.reason, stale.order.status, stale.order.version]).toEqual(['order-changed', 'accepted', 2]);
    const notAllowed = await firstValueFrom(repository.act('order-016', { action: 'accept', version: 2, promisedAt: null }));
    expect(notAllowed.status === 'conflict' && notAllowed.reason).toBe('action-not-allowed');

    await firstValueFrom(repository.act('order-016', { action: 'ready', version: 2 }));
    const completed = await firstValueFrom(repository.act('order-016', { action: 'complete', version: 3 }));
    expect(completed.status === 'ready' && [completed.data.status, completed.data.actions]).toEqual(['completed', []]);

    vi.setSystemTime(NOW + 1000);
    const after = await ready(firstValueFrom(repository.summary()));
    expect(after.pendingCount).toBe(1);
    expect(after.changedAt).not.toBe(before.changedAt);
    expect(after.latestOrderAt).toBe(before.latestOrderAt);
  });

  it('checks the promised time, the reason and the note like the API', async () => {
    const repository = mockRepository();
    const pending = await ready(firstValueFrom(repository.orders('pending')));
    const scheduled = pending.orders.find((order) => order.estimate === null);
    if (!scheduled) throw new Error('no scheduled order');
    const early = new Date(Date.parse(scheduled.pickupAt) - 60_000).toISOString();
    expect(await firstValueFrom(repository.act(scheduled.id, { action: 'accept', version: 1, promisedAt: early })))
      .toMatchObject({ status: 'validation-failed', errors: { promisedAt: ['預計取餐時間不能早於顧客選的時間。'] } });
    const late = new Date(Date.parse(scheduled.pickupAt) + 25 * 3_600_000).toISOString();
    expect(await firstValueFrom(repository.act(scheduled.id, { action: 'accept', version: 1, promisedAt: late })))
      .toMatchObject({ status: 'validation-failed', errors: { promisedAt: ['預計取餐時間最多延後 24 小時。'] } });

    expect(await firstValueFrom(repository.act('order-015', { action: 'decline', version: 1, reason: 'other', note: '  ' })))
      .toMatchObject({ status: 'validation-failed', message: '選「其他」時請寫明原因。' });
    const declined = await firstValueFrom(repository.act('order-015', { action: 'decline', version: 1, reason: 'other', note: '  今天提早打烊 ' }));
    expect(declined.status === 'ready' && declined.data.declined).toMatchObject({ reason: 'other', note: '今天提早打烊' });
  });

  it('lists the open table A1 and closes it: its served orders complete, the table is gone and changedAt moves (#402)', async () => {
    const repository = mockRepository();
    const before = await ready(firstValueFrom(repository.summary()));
    const tables = await ready(firstValueFrom(repository.tables()));
    expect(tables.sessions.map((session) => [session.tableName, session.version, session.total, session.pendingCount, session.orders.map((order) => order.displayNumber)]))
      .toEqual([['A1', 2, 410, 0, ['#008', '#009']]]);
    const [a1] = tables.sessions;

    const stale = await firstValueFrom(repository.closeTable(a1.id, 1));
    expect(stale.status === 'table-conflict' && [stale.reason, stale.message, stale.session.version, stale.session.closedAt])
      .toEqual(['session-changed', '這桌剛剛有新的變動，請確認金額後再結帳。', 2, null]);

    vi.setSystemTime(NOW + 1000);
    const closed = await ready(firstValueFrom(repository.closeTable(a1.id, 2)));
    expect([closed.version, closed.closedAt, closed.closedBy?.displayName, closed.total, closed.orders.map((order) => [order.status, order.actions])])
      .toEqual([3, new Date(NOW + 1000).toISOString(), '安心商行客服同仁', 410, [['completed', []], ['completed', []]]]);
    expect((await ready(firstValueFrom(repository.tables()))).sessions).toEqual([]);
    const done = await ready(firstValueFrom(repository.orders('done')));
    expect(done.orders.slice(0, 2).map((order) => order.displayNumber).sort()).toEqual(['#008', '#009']);
    const after = await ready(firstValueFrom(repository.summary()));
    expect([after.openTableCount, after.activeCount, after.changedAt !== before.changedAt]).toEqual([0, 3, true]);

    const again = await firstValueFrom(repository.closeTable(a1.id, 3));
    expect(again.status === 'table-conflict' && again.reason).toBe('session-changed');
    expect(await firstValueFrom(repository.closeTable('no-such-session', 1))).toMatchObject({ status: 'permission-denied', reason: 'ordering' });
  });

  it('refuses to close a table with a pending order, and accepts a dine-in order without a time (#402)', async () => {
    const repository = mockRepository();
    const [a1] = (await ready(firstValueFrom(repository.tables()))).sessions;
    // 顧客加點了一筆、還沒接（mock 沒有顧客端，直接放進示範資料）。
    const state = (repository as unknown as { state(): { orders: typeof a1.orders } }).state();
    const added = { ...a1.orders[1], id: 'order-017', number: 17, displayNumber: '#017', status: 'pending' as const, version: 1,
      accepted: null, autoAccepted: false, actions: ['accept', 'decline'] as typeof a1.orders[number]['actions'] };
    state.orders.push(added);

    const refused = await firstValueFrom(repository.closeTable(a1.id, 2));
    expect(refused.status === 'table-conflict' && [refused.reason, refused.message, refused.session.pendingCount, refused.session.total])
      .toEqual(['table-has-pending', '這桌還有待確認的訂單，請先接單或婉拒再結帳。', 1, 480]);

    expect(await firstValueFrom(repository.act('order-017', { action: 'accept', version: 1, promisedAt: '2026-11-07T04:00:00Z' })))
      .toMatchObject({ status: 'validation-failed', errors: { promisedAt: ['內用訂單不需要指定時間。'] } });
    const accepted = await firstValueFrom(repository.act('order-017', { action: 'accept', version: 1 }));
    expect(accepted.status === 'ready' && [accepted.data.status, accepted.data.promisedAt, accepted.data.autoAccepted])
      .toEqual(['accepted', added.pickupAt, false]);
    expect((await firstValueFrom(repository.closeTable(a1.id, 2))).status).toBe('ready');
  });

  it('toggles the shop switches and items, bumping shopVersion only on a change', async () => {
    const repository = mockRepository();
    const paused = await ready(firstValueFrom(repository.setPaused(true)));
    expect([paused.paused, paused.shopVersion]).toEqual([true, 2]);
    expect((await ready(firstValueFrom(repository.setPaused(true)))).shopVersion).toBe(2);
    const band = await ready(firstValueFrom(repository.setAsapBand('45-60')));
    expect([band.asapBand, band.asap]).toEqual(['45-60', { minMinutes: 45, maxMinutes: 60 }]);

    const menu = await ready(firstValueFrom(repository.items()));
    expect(menu.categories.flatMap((category) => category.items).find((item) => item.id === 'braised-platter'))
      .toMatchObject({ availability: 'sold-out-today', resetsAt: '2026-11-08T03:00:00.000Z' });
    const soldOut = await ready(firstValueFrom(repository.setItemState('beef-noodle', 'sold-out-today')));
    expect(soldOut).toMatchObject({ availability: 'sold-out-today', resetsAt: '2026-11-08T03:00:00.000Z' });
    expect(await ready(firstValueFrom(repository.setItemState('beef-noodle', 'available')))).toMatchObject({ availability: 'available', resetsAt: null });
    expect(await firstValueFrom(repository.setItemState('no-such-item', 'unavailable'))).toMatchObject({ status: 'item-not-found' });
  });

  it('refuses an account without manage-orders, like the API', async () => {
    const repository = mockRepository(['use-shared-assistants']);
    expect(await firstValueFrom(repository.summary())).toMatchObject({ status: 'permission-denied', reason: 'ordering' });
    expect(await firstValueFrom(repository.act('order-016', { action: 'accept', version: 1, promisedAt: null }))).toMatchObject({ status: 'permission-denied' });
    expect(await firstValueFrom(repository.setPaused(true))).toMatchObject({ status: 'permission-denied' });
  });
});
