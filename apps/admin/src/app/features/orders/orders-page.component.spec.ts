import { DOCUMENT } from '@angular/common';
import { signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { of, throwError, type Observable } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AccountPermission } from '../../core/domain/account.model';
import type { OrderingListView, OrderingOrderView, OrderingSummaryView } from '../../core/domain/ordering.model';
import { OrderingRepository, type OrderingActionResult, type OrderingRead } from '../../core/repositories/ordering.repository';
import { ApiSessionService } from '../../core/session/api-session.service';
import { NAV_COUNTS_CHANGED_EVENT } from '../../layout/workspace-layout/nav-counts';
import { ORDER_ALERT_SOUND, ORDER_SOUND_STORAGE_KEY, type OrderAlertSound } from './order-alert-sound';
import { OrdersPageComponent, ORDERING_POLL_MS } from './orders-page.component';

/** 一定要找到的元素（找不到就讓測試失敗）。 */
function el<T extends Element = HTMLElement>(root: ParentNode, selector: string): T {
  const found = root.querySelector<T>(selector as never);
  if (!found) throw new Error(`no ${selector}`);
  return found;
}

const NOW = Date.parse('2026-11-07T03:40:00Z'); // 11:40 in Taipei

function order(fields: Partial<OrderingOrderView> & Pick<OrderingOrderView, 'id' | 'number' | 'status'>): OrderingOrderView {
  return {
    displayNumber: `#${String(fields.number).padStart(3, '0')}`, version: 1, serviceType: 'takeout-scheduled', businessDate: '2026-11-07',
    pickupAt: '2026-11-07T04:00:00Z', estimate: null, promisedAt: null, customerName: '陳小姐', customerPhone: '0912345678',
    customerPhoneDisplay: '0912-345-678', customerEmail: null,
    lines: [{ itemId: 'zhajiang', itemName: '炸醬麵', itemPrice: 80, quantity: 2, unitPrice: 80, lineTotal: 160, comment: '不要蔥', options: [
      { groupId: 'spicy', groupName: '辣度', optionId: 'mild', optionName: '小辣', price: 0, count: 1, options: [] },
    ] }],
    orderComment: null, tableware: true, subtotal: 160, total: 160, menuVersion: 1, createdAt: '2026-11-07T03:35:00Z',
    statusChangedAt: '2026-11-07T03:35:00Z', accepted: null, ready: null, completed: null, declined: null, cancelled: null,
    actions: fields.status === 'pending' ? ['accept', 'decline'] : fields.status === 'accepted' ? ['ready', 'complete', 'cancel'] : [],
    ...fields,
  };
}

function summaryOf(fields: Partial<OrderingSummaryView> = {}): OrderingSummaryView {
  return {
    now: new Date(NOW).toISOString(), businessDate: '2026-11-07', timeZone: 'Asia/Taipei', shopName: '王記麵店', slug: 'wangji-noodles',
    pendingCount: 1, activeCount: 1, latestOrderAt: '2026-11-07T03:35:00Z', changedAt: '2026-11-07T03:35:00Z', paused: false,
    asapBand: '15-30', asap: { minMinutes: 15, maxMinutes: 30 }, services: { takeoutScheduled: true, takeoutAsap: true },
    menuVersion: 1, shopVersion: 1, ...fields,
  };
}

class FakeSound implements OrderAlertSound {
  readonly unlock = vi.fn(async () => true);
  readonly play = vi.fn(() => true);
  readonly close = vi.fn();
}

interface Setup {
  readonly permissions?: readonly AccountPermission[];
  readonly summary?: () => Observable<OrderingRead<OrderingSummaryView>>;
  readonly lists?: Partial<Record<OrderingListView, OrderingOrderView[]>>;
  readonly act?: (id: string, request: unknown) => Observable<OrderingActionResult>;
}

function setup(options: Setup = {}) {
  TestBed.resetTestingModule();
  const lists: Record<OrderingListView, OrderingOrderView[]> = {
    pending: [order({ id: 'o-16', number: 16, status: 'pending' })],
    active: [order({ id: 'o-14', number: 14, status: 'accepted', promisedAt: '2026-11-07T04:10:00Z' })],
    done: [],
    ...options.lists,
  };
  let current = summaryOf();
  const repository = {
    summary: vi.fn(options.summary ?? (() => of<OrderingRead<OrderingSummaryView>>({ status: 'ready', data: current }))),
    orders: vi.fn((view: OrderingListView) => of<OrderingRead<unknown>>({ status: 'ready', data: { now: current.now, view, orders: lists[view] } })),
    act: vi.fn(options.act ?? (() => of<OrderingActionResult>({ status: 'ready', data: lists.pending[0] }))),
    items: vi.fn(() => of({ status: 'ready', data: { now: current.now, menuVersion: 1, categories: [] } })),
    setPaused: vi.fn(),
    setAsapBand: vi.fn(),
    setItemState: vi.fn(),
  };
  const sound = new FakeSound();
  TestBed.configureTestingModule({
    imports: [OrdersPageComponent],
    providers: [
      { provide: OrderingRepository, useValue: repository },
      { provide: ApiSessionService, useValue: { permissions: signal(options.permissions ?? ['manage-orders']) } },
      { provide: ORDER_ALERT_SOUND, useValue: () => sound },
    ],
  });
  const fixture = TestBed.createComponent(OrdersPageComponent);
  fixture.detectChanges();
  return {
    fixture,
    repository,
    sound,
    lists,
    page: fixture.nativeElement as HTMLElement,
    setSummary: (fields: Partial<OrderingSummaryView>) => (current = summaryOf({ ...current, ...fields })),
  };
}

function click(fixture: ComponentFixture<unknown>, selector: string): void {
  const element = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(selector);
  if (!element) throw new Error(`no ${selector}`);
  element.click();
  fixture.detectChanges();
}

async function tick(fixture: ComponentFixture<unknown>, ms = ORDERING_POLL_MS): Promise<void> {
  await vi.advanceTimersByTimeAsync(ms);
  fixture.detectChanges();
}

describe('OrdersPageComponent (issue #399)', () => {
  let document: Document;
  let navEvents: number;
  const countNav = () => navEvents++;

  beforeEach(() => {
    vi.useFakeTimers({ now: NOW });
    document = TestBed.inject(DOCUMENT);
    document.title = 'Smart Agri';
    navEvents = 0;
    document.addEventListener(NAV_COUNTS_CHANGED_EVENT, countNav);
    localStorage.removeItem(ORDER_SOUND_STORAGE_KEY);
  });

  afterEach(() => {
    document.removeEventListener(NAV_COUNTS_CHANGED_EVENT, countNav);
    TestBed.resetTestingModule();
    vi.useRealTimers();
  });

  it('shows the permission-denied state without asking the API when the account cannot handle orders', () => {
    const { page, repository } = setup({ permissions: ['use-shared-assistants'] });
    expect(page.textContent).toContain('無法使用線上點餐');
    expect(page.textContent).toContain('處理線上點餐');
    expect(repository.summary).not.toHaveBeenCalled();
  });

  it('explains how to set ordering up when the organization has none', () => {
    const { page, repository } = setup({ summary: () => of({ status: 'not-configured', message: '這個組織還沒有設定線上點餐。' }) });
    expect(page.textContent).toContain('尚未設定線上點餐');
    expect(page.textContent).toContain('ordering-shop import');
    expect(repository.orders).not.toHaveBeenCalled();
  });

  it('shows an error with retry when the first read fails', () => {
    let fail = true;
    const { fixture, page, repository } = setup({ summary: () => (fail ? throwError(() => new Error('offline')) : of({ status: 'ready', data: summaryOf() })) });
    expect(page.textContent).toContain('目前無法載入線上點餐');
    fail = false;
    click(fixture, '.ui-panel-action');
    expect(repository.summary).toHaveBeenCalledTimes(2);
    expect(page.querySelector('[data-orders-column="pending"] [data-order="o-16"]')).not.toBeNull();
  });

  it('renders the three lists and a card with time, customer, lines and only the buttons in actions[]', () => {
    const { page } = setup({
      lists: {
        active: [order({ id: 'o-13', number: 13, status: 'ready', actions: ['complete'], promisedAt: '2026-11-07T03:50:00Z' })],
        done: [order({ id: 'o-11', number: 11, status: 'declined', declined: { at: '2026-11-07T03:00:00Z', by: { id: 'a', displayName: '店長' }, reason: 'sold-out', note: '麵賣完了' } })],
      },
    });
    const card = el(page, '[data-orders-column="pending"] [data-order="o-16"]');
    expect(card.textContent).toContain('#016');
    expect(card.textContent).toContain('外帶預約');
    expect(card.textContent).toContain('12:00 取餐');
    expect(card.textContent).toContain('剩 20 分鐘');
    expect(card.textContent).toContain('5 分鐘前送出');
    expect(card.querySelector('a[href="tel:0912345678"]')?.textContent?.trim()).toBe('0912-345-678');
    expect(card.textContent).toContain('2 ×');
    expect(card.textContent).toContain('小辣');
    expect(card.textContent).toContain('備註：不要蔥');
    expect(card.textContent).toContain('需要餐具');
    expect(card.textContent).toContain('NT$160');
    expect(Array.from(card.querySelectorAll('[data-action]')).map((button) => button.textContent?.trim())).toEqual(['接單', '婉拒']);

    const ready = el(page, '[data-order="o-13"]');
    expect(ready.textContent).toContain('預計 11:50 可取餐');
    expect(Array.from(ready.querySelectorAll('[data-action]')).map((button) => button.getAttribute('data-action'))).toEqual(['complete']);
    const declined = el(page, '[data-order="o-11"]');
    expect(declined.textContent).toContain('婉拒，店長：有品項賣完了');
    expect(declined.textContent).toContain('給顧客的說明：麵賣完了');
    expect(declined.querySelectorAll('[data-action]')).toHaveLength(0);

    expect(Array.from(page.querySelectorAll('[data-orders-tab]')).map((tab) => tab.textContent?.replace(/\s+/g, ''))).toEqual(['待確認1', '處理中1', '今日完成1']);
  });

  it('accepts through the dialog with the version it shows, moves the card and refreshes the side-nav count', () => {
    const accepted = order({ id: 'o-16', number: 16, status: 'accepted', version: 2, promisedAt: '2026-11-07T04:00:00Z' });
    const { fixture, page, repository, lists } = setup({ act: () => of({ status: 'ready', data: accepted }) });
    click(fixture, '[data-order="o-16"] [data-action="accept"]');
    const dialog = el(page, '[data-order-dialog="accept"]');
    expect(dialog.getAttribute('role')).toBe('dialog');
    expect(dialog.textContent).toContain('接單 #016');
    expect(dialog.textContent).toContain('維持 12:00');
    expect(document.activeElement).toBe(dialog.querySelector('input'));

    lists.pending = [];
    lists.active = [accepted, ...lists.active];
    click(fixture, '[data-order-dialog-confirm]');
    expect(repository.act).toHaveBeenCalledWith('o-16', { action: 'accept', version: 1, promisedAt: null });
    expect(page.querySelector('[data-order-dialog]')).toBeNull();
    expect(page.querySelector('[data-orders-column="active"] [data-order="o-16"]')).not.toBeNull();
    expect(page.querySelector('[data-orders-column="pending"] [data-order="o-16"]')).toBeNull();
    expect(page.querySelector('[data-orders-notice]')?.textContent).toContain('已接單 #016，預計 12:00 可取餐。');
    expect(navEvents).toBe(1);
    expect(document.title).toBe('Smart Agri');
  });

  it('sends a later time picked in the accept dialog', () => {
    const { fixture, repository } = setup();
    click(fixture, '[data-order="o-16"] [data-action="accept"]');
    const later = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<HTMLLabelElement>('.dialog__choice'))
      .find((label) => label.textContent?.includes('延後 20 分鐘'));
    later?.querySelector('input')?.click();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-accept-result]')?.textContent).toContain('預計 12:20 可取餐');
    click(fixture, '[data-order-dialog-confirm]');
    expect(repository.act).toHaveBeenCalledWith('o-16', { action: 'accept', version: 1, promisedAt: '2026-11-07T04:20:00.000Z' });
  });

  it('refuses a picked time before the customer\'s, and closes with Escape giving focus back', () => {
    const { fixture, page, repository } = setup();
    const trigger = el<HTMLButtonElement>(page, '[data-order="o-16"] [data-action="accept"]');
    trigger.focus();
    click(fixture, '[data-order="o-16"] [data-action="accept"]');
    click(fixture, '[data-accept-custom]');
    const time = el<HTMLInputElement>(page, '[data-accept-time]');
    expect(time.value).toBe('12:00');
    time.value = '11:50';
    time.dispatchEvent(new Event('input'));
    click(fixture, '[data-order-dialog-confirm]');
    expect(page.querySelector('[data-order-dialog] [role="alert"]')?.textContent).toContain('預計取餐時間不能早於顧客選的時間（12:00）。');
    expect(repository.act).not.toHaveBeenCalled();

    el(page, '[role="dialog"]').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();
    expect(page.querySelector('[data-order-dialog]')).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });

  it('cancels an accepted order only with a note when the reason is 其他', () => {
    const { fixture, page, repository } = setup({
      act: () => of({ status: 'ready', data: order({ id: 'o-14', number: 14, status: 'cancelled', version: 2 }) }),
    });
    click(fixture, '[data-order="o-14"] [data-action="cancel"]');
    expect(page.querySelector('[data-order-dialog="cancel"]')?.textContent).toContain('取消訂單 #014');
    click(fixture, 'input[name="order-reason"][value="other"]');
    expect(page.querySelector('[data-order-dialog]')?.textContent).toContain('給顧客的說明（必填）');
    click(fixture, '[data-order-dialog-confirm]');
    expect(page.querySelector('[data-order-dialog] [role="alert"]')?.textContent).toContain('選「其他」時請寫明原因。');
    const note = el<HTMLInputElement>(page, '[data-order-note]');
    note.value = '顧客改天再來';
    note.dispatchEvent(new Event('input'));
    click(fixture, '[data-order-dialog-confirm]');
    expect(repository.act).toHaveBeenCalledWith('o-14', { action: 'cancel', version: 1, reason: 'other', note: '顧客改天再來' });
    expect(page.querySelector('[data-orders-notice]')?.textContent).toContain('已取消 #014。');
  });

  it('declines with a reason and a note the customer will see', () => {
    const { fixture, page, repository } = setup({
      act: () => of({ status: 'ready', data: order({ id: 'o-16', number: 16, status: 'declined', version: 2 }) }),
    });
    click(fixture, '[data-order="o-16"] [data-action="decline"]');
    expect(page.textContent).toContain('顧客會在訂單頁看到這段說明');
    click(fixture, '[data-order-dialog-confirm]');
    expect(page.querySelector('[data-order-dialog] [role="alert"]')?.textContent).toContain('請選擇原因。');
    expect(repository.act).not.toHaveBeenCalled();

    click(fixture, 'input[name="order-reason"][value="too-busy"]');
    const note = el<HTMLInputElement>(page, '[data-order-note]');
    note.value = '中午太忙了，抱歉';
    note.dispatchEvent(new Event('input'));
    click(fixture, '[data-order-dialog-confirm]');
    expect(repository.act).toHaveBeenCalledWith('o-16', { action: 'decline', version: 1, reason: 'too-busy', note: '中午太忙了，抱歉' });
  });

  it('replaces the card with the server\'s order on a 409 and says someone else handled it', () => {
    const current = order({ id: 'o-16', number: 16, status: 'accepted', version: 2, accepted: { at: '2026-11-07T03:39:00Z', by: { id: 'b', displayName: '阿明' } } });
    const { fixture, page, lists } = setup({
      act: () => of({ status: 'conflict', reason: 'order-changed', message: '這筆訂單剛剛被更新了，請確認目前的狀態。', order: current }),
    });
    lists.pending = [];
    lists.active = [current, ...lists.active];
    click(fixture, '[data-order="o-16"] [data-action="accept"]');
    click(fixture, '[data-order-dialog-confirm]');
    expect(page.querySelector('[data-order-dialog]')).toBeNull();
    const card = el(page, '[data-orders-column="active"] [data-order="o-16"]');
    expect(card.textContent).toContain('已接單');
    expect(card.textContent).toContain('接單，阿明');
    expect(page.querySelector('[data-orders-notice]')?.textContent).toContain('#016 這筆訂單剛被其他人處理了');
  });

  it('keeps the dialog open with the server\'s 422 message', () => {
    const { fixture, page } = setup({
      act: () => of({ status: 'validation-failed', message: '預計取餐時間最多延後 24 小時。', errors: { promisedAt: ['預計取餐時間最多延後 24 小時。'] } }),
    });
    click(fixture, '[data-order="o-16"] [data-action="accept"]');
    click(fixture, '[data-order-dialog-confirm]');
    expect(page.querySelector('[data-order-dialog] [role="alert"]')?.textContent).toContain('預計取餐時間最多延後 24 小時。');
  });

  it('marks ready without a dialog', () => {
    const { fixture, repository } = setup();
    click(fixture, '[data-order="o-14"] [data-action="ready"]');
    expect(repository.act).toHaveBeenCalledWith('o-14', { action: 'ready', version: 1 });
  });

  it('polls the summary every 10 seconds, prefixes the title while orders wait, and stops when destroyed', async () => {
    const { fixture, repository, setSummary } = setup();
    expect(repository.summary).toHaveBeenCalledTimes(1);
    expect(document.title).toBe('(1) Smart Agri');
    await tick(fixture, ORDERING_POLL_MS - 1);
    expect(repository.summary).toHaveBeenCalledTimes(1);
    await tick(fixture, 1);
    expect(repository.summary).toHaveBeenCalledTimes(2);
    // Same cursors: the lists are not read again.
    expect(repository.orders).toHaveBeenCalledTimes(3);

    setSummary({ pendingCount: 3, latestOrderAt: '2026-11-07T03:40:05Z', changedAt: '2026-11-07T03:40:05Z' });
    await tick(fixture);
    expect(document.title).toBe('(3) Smart Agri');
    expect(navEvents).toBe(1);
    expect(repository.orders).toHaveBeenCalledTimes(6);

    setSummary({ pendingCount: 0, changedAt: '2026-11-07T03:40:30Z' });
    await tick(fixture);
    expect(document.title).toBe('Smart Agri');

    fixture.destroy();
    await vi.advanceTimersByTimeAsync(ORDERING_POLL_MS * 3);
    expect(repository.summary).toHaveBeenCalledTimes(4);
  });

  it('restores the title when leaving the page', () => {
    const { fixture } = setup();
    expect(document.title).toBe('(1) Smart Agri');
    fixture.destroy();
    expect(document.title).toBe('Smart Agri');
  });

  it('plays the alert only after the toggle, and only when latestOrderAt moves forward', async () => {
    const { fixture, page, sound, lists, setSummary } = setup();
    expect(page.querySelector('[data-sound-toggle]')?.textContent).toContain('開啟提示音');
    setSummary({ latestOrderAt: '2026-11-07T03:40:05Z', pendingCount: 2 });
    await tick(fixture);
    expect(sound.play).not.toHaveBeenCalled();

    click(fixture, '[data-sound-toggle]');
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
    expect(sound.unlock).toHaveBeenCalledTimes(1);
    expect(sound.play).toHaveBeenCalledTimes(1); // the test beep
    expect(localStorage.getItem(ORDER_SOUND_STORAGE_KEY)).toBe('on');
    expect(page.querySelector('[data-sound-toggle]')?.textContent).toContain('關閉提示音');

    setSummary({ changedAt: '2026-11-07T03:40:20Z' });
    await tick(fixture);
    expect(sound.play).toHaveBeenCalledTimes(1);

    lists.pending = [order({ id: 'o-17', number: 17, status: 'pending', createdAt: '2026-11-07T03:40:25Z' }), ...lists.pending];
    setSummary({ latestOrderAt: '2026-11-07T03:40:25Z', pendingCount: 3 });
    await tick(fixture);
    expect(sound.play).toHaveBeenCalledTimes(2);
    expect(page.querySelector('[data-orders-notice]')?.textContent).toContain('新訂單：#017（外帶預約 12:00）。');

    click(fixture, '[data-sound-toggle]');
    expect(localStorage.getItem(ORDER_SOUND_STORAGE_KEY)).toBeNull();
    setSummary({ latestOrderAt: '2026-11-07T03:40:35Z' });
    await tick(fixture);
    expect(sound.play).toHaveBeenCalledTimes(2);
  });

  it('never plays for the orders already there on the first read, and waits for a gesture when the choice was remembered', async () => {
    localStorage.setItem(ORDER_SOUND_STORAGE_KEY, 'on');
    const { fixture, page, sound, setSummary } = setup();
    expect(sound.play).not.toHaveBeenCalled();
    expect(page.querySelector('[data-sound-state]')?.textContent).toContain('點一下這一頁的任何地方');
    setSummary({ latestOrderAt: '2026-11-07T03:40:05Z' });
    await tick(fixture);
    expect(sound.play).not.toHaveBeenCalled();

    document.body.dispatchEvent(new Event('pointerdown', { bubbles: true }));
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
    expect(sound.unlock).toHaveBeenCalledTimes(1);
    setSummary({ latestOrderAt: '2026-11-07T03:40:15Z' });
    await tick(fixture);
    expect(sound.play).toHaveBeenCalledTimes(1);
  });

  it('says so when the browser blocks the sound', async () => {
    const { fixture, page, sound } = setup();
    sound.unlock.mockResolvedValueOnce(false);
    click(fixture, '[data-sound-toggle]');
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
    expect(page.querySelector('[data-sound-state]')?.textContent).toContain('瀏覽器擋下了提示音');
  });
});
