import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { OrderingApi, type ApiResult } from '../api/ordering-api';
import type { Menu, ShopInfo, Slots } from '../api/types';
import { ShopStore } from '../state/shop-store';
import {
  cartKey,
  loadCart,
  priceLine,
  revalidate,
  saveCart,
  type CartLine,
  type StoredCart,
} from './cart';
import { BEEF, menu, WONTON } from './menu.testing';

const beefLine: CartLine = {
  key: 'a',
  itemId: 'beef-noodle',
  name: '紅燒牛肉麵',
  quantity: 2,
  comment: null,
  selections: [
    { groupId: 'size', optionId: 'large', count: 1, selections: null },
    { groupId: 'noodle', optionId: 'thin', count: 1, selections: null },
  ],
};
const wontonLine: CartLine = {
  key: 'b',
  itemId: 'wonton-soup',
  name: '鮮肉餛飩湯',
  quantity: 1,
  comment: null,
  selections: null,
};
const cart = (lines: CartLine[], menuVersion = 1): StoredCart => ({
  menuVersion,
  lines,
  orderComment: '',
  tableware: null,
  service: { type: 'takeout-scheduled', date: '2026-11-07', slot: '2026-11-07T12:30' },
});

describe('cart', () => {
  beforeEach(() => localStorage.clear());

  it('survives a reload: saved per shop in localStorage', () => {
    saveCart('wangji', cart([beefLine]));
    expect(loadCart('wangji')).toEqual({ ...cart([beefLine]), notice: null });
    expect(loadCart('other').lines).toEqual([]);
    localStorage.setItem(cartKey('broken'), '{not json');
    expect(loadCart('broken').lines).toEqual([]);
  });

  it('prices a line on the menu', () => {
    expect(priceLine(menu(1), beefLine)).toMatchObject({
      unitPrice: 170,
      lineTotal: 340,
      summary: '大碗、細麵',
    });
    expect(priceLine(menu(1), { ...beefLine, selections: null })).toBeNull();
  });

  it('keeps valid lines and names the removed ones when the menu changes', () => {
    const v2 = menu(2, [{ ...BEEF, price: 160 }]);
    const { cart: next, removed } = revalidate(cart([beefLine, wontonLine]), v2);
    expect(removed).toEqual(['鮮肉餛飩湯']);
    expect(next.lines).toEqual([beefLine]);
    expect(next.menuVersion).toBe(2);
    expect(priceLine(v2, next.lines[0])?.lineTotal).toBe(360);
  });

  it('drops a line whose options no longer exist', () => {
    const v2 = menu(2, [
      { ...BEEF, optionGroups: BEEF.optionGroups.filter((group) => group.id !== 'size') },
      WONTON,
    ]);
    expect(revalidate(cart([beefLine, wontonLine]), v2).removed).toEqual(['紅燒牛肉麵']);
  });
});

describe('ShopStore cart and menu version', () => {
  function setup(menuResult: Menu) {
    const api = {
      menu: vi.fn(async (): Promise<ApiResult<Menu>> => ({
        ok: true,
        status: 200,
        data: menuResult,
      })),
    };
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: OrderingApi, useValue: api }],
    });
    const store = TestBed.inject(ShopStore);
    store.slug.set('wangji');
    return store;
  }

  beforeEach(() => localStorage.clear());

  it('merges the same item with the same options and persists every change', () => {
    const store = setup(menu(1));
    store.cart.set(cart([]));
    const line: Omit<CartLine, 'key'> = {
      itemId: beefLine.itemId,
      name: beefLine.name,
      quantity: 2,
      comment: null,
      selections: beefLine.selections,
    };
    store.addLine(line);
    store.addLine({ ...line, quantity: 1 });
    expect(store.cart().lines).toHaveLength(1);
    expect(store.cart().lines[0].quantity).toBe(3);
    store.addLine({ ...line, comment: '不要蔥' });
    expect(store.cart().lines).toHaveLength(2);
    expect(loadCart('wangji').lines).toHaveLength(2);
  });

  it('re-validates the stored cart against a newer menu and tells the customer', async () => {
    saveCart('wangji', cart([beefLine, wontonLine], 1));
    const store = setup(menu(2, [{ ...BEEF, price: 160 }]));
    store.cart.set(loadCart('wangji'));
    await store.loadMenu();
    expect(store.cart().lines.map((line) => line.itemId)).toEqual(['beef-noodle']);
    expect(store.cart().menuVersion).toBe(2);
    expect(store.cartNotice()).toContain('「鮮肉餛飩湯」');
    // 重新整理後仍顯示，直到顧客按「知道了」。
    expect(loadCart('wangji').notice).toContain('「鮮肉餛飩湯」');
    store.dismissNotice();
    expect(loadCart('wangji').notice).toBeNull();
    expect(store.subtotal()).toBe(2 * 180);
    expect(loadCart('wangji').menuVersion).toBe(2);
  });

  it('leaves the cart alone when the menu version is the same', async () => {
    saveCart('wangji', cart([beefLine], 3));
    const store = setup(menu(3));
    store.cart.set(loadCart('wangji'));
    await store.loadMenu();
    expect(store.cartNotice()).toBeNull();
    expect(store.cart().lines).toEqual([beefLine]);
  });
});

describe('ShopStore: can the shop take an order now', () => {
  const shop = (patch: Partial<ShopInfo>): ShopInfo =>
    ({
      paused: false,
      services: { takeoutScheduled: true, takeoutAsap: true },
      asap: null,
      timeZone: 'Asia/Taipei',
      now: '2026-11-06T14:30:00Z',
      ...patch,
    }) as ShopInfo;
  const slots: Slots = {
    now: '2026-11-06T14:30:00Z',
    dates: [
      {
        date: '2026-11-07',
        slots: [
          {
            value: '2026-11-07T12:30',
            at: '2026-11-07T12:30:00+08:00',
            time: '12:30',
            isNextDay: false,
            isFull: false,
          },
        ],
      },
    ],
  };

  function store() {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    return TestBed.inject(ShopStore);
  }

  it('paused: no ordering at all', () => {
    const s = store();
    s.shop.set(shop({ paused: true, asap: { minMinutes: 15, maxMinutes: 30 } }));
    s.slots.set(slots);
    expect(s.canOrder()).toBe(false);
  });

  it('closed with scheduled pickups: orderable by choosing a time, and the time decides the pickup instant', () => {
    const s = store();
    s.shop.set(shop({}));
    s.slots.set(slots);
    expect(s.asapAvailable()).toBe(false);
    expect(s.canOrder()).toBe(true);
    s.chooseService({ type: 'takeout-scheduled', date: '2026-11-07', slot: '2026-11-07T12:30' });
    expect(s.serviceLabel()).toEqual({ kind: '外帶，預約取餐', when: '明天 12:30' });
    expect(s.pickupAt()?.toISOString()).toBe('2026-11-07T04:30:00.000Z');
  });

  it('ASAP: shows the estimate and picks up at now + the upper bound (server clock)', () => {
    const s = store();
    s.shop.set(shop({ asap: { minMinutes: 15, maxMinutes: 30 } }));
    s.slots.set({ ...slots, dates: [] });
    s.chooseService({ type: 'takeout-asap', date: null, slot: null });
    expect(s.serviceLabel()).toEqual({ kind: '外帶，儘快取餐', when: '儘快，約 15–30 分鐘' });
    expect(s.pickupAt()?.toISOString()).toBe('2026-11-06T15:00:00.000Z');
  });

  it('a full slot cannot be the chosen time', () => {
    const s = store();
    s.shop.set(shop({}));
    s.slots.set({
      ...slots,
      dates: [{ ...slots.dates[0], slots: [{ ...slots.dates[0].slots[0], isFull: true }] }],
    });
    s.chooseService({ type: 'takeout-scheduled', date: '2026-11-07', slot: '2026-11-07T12:30' });
    expect(s.serviceReady()).toBe(false);
    expect(s.canOrder()).toBe(false);
  });
});
