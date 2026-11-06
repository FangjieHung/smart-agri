import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { OrderingApi } from '../api/ordering-api';
import type { Order, ShopInfo } from '../api/types';
import { ShopStore } from '../state/shop-store';
import StatusPage, { POLL_MS } from './status-page';

const TOKEN = 'kq3Vd8u6m0yJxT2bA9cR4eLw7sZpN1fH5gUiOoE_-Qa';

const order = (status: Order['status']): Order =>
  ({
    number: 1,
    displayNumber: '#001',
    status,
    serviceType: 'takeout-scheduled',
    businessDate: '2026-11-07',
    pickupAt: '2026-11-07T04:30:00+00:00',
    estimate: null,
    createdAt: '2026-11-06T14:32:36+00:00',
    cancelledAt: null,
    lines: [],
    subtotal: 270,
    total: 270,
    menuVersion: 1,
    customerName: '陳小姐',
    phoneLastDigits: '678',
    orderComment: null,
    tableware: false,
    canCancel: status === 'pending',
    promisedAt: null,
    declineReason: null,
    declineNote: null,
    cancelledBy: null,
    cancelReason: null,
    cancelNote: null,
    deliveryFee: 0,
    delivery: null,
  }) as Order;

describe('StatusPage', () => {
  afterEach(() => vi.useRealTimers());

  it('reads the token from the fragment, polls every 10 s with the header, and stops on a final status', async () => {
    history.replaceState(null, '', `/order/wangji-noodles/o#${TOKEN}`);
    const statuses: Order['status'][] = ['pending', 'accepted', 'declined'];
    const api = {
      order: vi.fn(async (...args: [string, string]) => ({
        args,
        ok: true as const,
        status: 200,
        data: order(statuses.shift() ?? 'declined'),
      })),
    };
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: OrderingApi, useValue: api }],
    });
    const store = TestBed.inject(ShopStore);
    store.slug.set('wangji-noodles');
    store.shop.set({
      name: '王記麵店',
      phone: '06-123-4567',
      timeZone: 'Asia/Taipei',
      now: '2026-11-06T14:33:00Z',
    } as ShopInfo);

    const fixture = TestBed.createComponent(StatusPage);
    const root = fixture.nativeElement as HTMLElement;
    const settle = async () => {
      for (let i = 0; i < 5; i++) {
        await Promise.resolve();
        fixture.detectChanges();
      }
    };
    fixture.detectChanges();
    await settle();
    expect(api.order).toHaveBeenCalledTimes(1);
    expect(api.order).toHaveBeenLastCalledWith('wangji-noodles', TOKEN);
    expect(root.querySelector('[data-testid="order-status"]')?.textContent?.trim()).toBe('等待店家確認');
    expect(root.querySelector('[data-testid="order-pickup"]')?.textContent).toContain('明天 12:30');

    await vi.advanceTimersByTimeAsync(POLL_MS);
    await settle();
    expect(api.order).toHaveBeenCalledTimes(2);
    expect(root.querySelector('[data-testid="order-status"]')?.textContent?.trim()).toBe(
      '店家已接單，正在準備',
    );

    await vi.advanceTimersByTimeAsync(POLL_MS);
    await settle();
    expect(api.order).toHaveBeenCalledTimes(3);
    expect(root.querySelector('[data-testid="order-status"]')?.textContent?.trim()).toBe(
      '店家無法接這筆訂單',
    );

    await vi.advanceTimersByTimeAsync(POLL_MS * 3);
    await settle();
    expect(api.order).toHaveBeenCalledTimes(3);
    fixture.destroy();
  });

  it('shows the time the shop promised and the reason it declined or cancelled (#399)', async () => {
    history.replaceState(null, '', `/order/wangji-noodles/o#${TOKEN}`);
    const answers: Order[] = [
      {
        ...order('accepted'),
        promisedAt: '2026-11-07T04:45:00+00:00',
      } as Order,
      {
        ...order('declined'),
        declineReason: 'sold-out',
        declineNote: '牛肉麵賣完了',
      } as Order,
    ];
    const api = {
      order: vi.fn(async () => ({ ok: true as const, status: 200, data: answers.shift() ?? order('declined') })),
    };
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: OrderingApi, useValue: api }],
    });
    const store = TestBed.inject(ShopStore);
    store.slug.set('wangji-noodles');
    store.shop.set({
      name: '王記麵店',
      phone: '06-123-4567',
      timeZone: 'Asia/Taipei',
      now: '2026-11-06T14:33:00Z',
    } as ShopInfo);
    const fixture = TestBed.createComponent(StatusPage);
    const root = fixture.nativeElement as HTMLElement;
    const settle = async () => {
      for (let i = 0; i < 5; i++) {
        await Promise.resolve();
        fixture.detectChanges();
      }
    };
    fixture.detectChanges();
    await settle();
    expect(root.querySelector('[data-testid="order-pickup"]')?.textContent).toContain(
      '明天 12:45（店家調整了取餐時間）',
    );
    expect(root.querySelector('[data-testid="order-reason"]')).toBeNull();

    await vi.advanceTimersByTimeAsync(POLL_MS);
    await settle();
    expect(root.querySelector('[data-testid="order-status"]')?.textContent?.trim()).toBe(
      '店家無法接這筆訂單',
    );
    expect(root.querySelector('[data-testid="order-reason"]')?.textContent?.trim()).toBe(
      '原因：有品項已經賣完。牛肉麵賣完了',
    );
    fixture.destroy();
  });

  it('shows an expired order and one whose details were cleared after 90 days (#400)', async () => {
    history.replaceState(null, '', `/order/wangji-noodles/o#${TOKEN}`);
    const api = {
      order: vi.fn(async () => ({
        ok: true as const,
        status: 200,
        data: { ...order('expired'), customerName: '（已刪除）', phoneLastDigits: '' } as Order,
      })),
    };
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: OrderingApi, useValue: api }],
    });
    const store = TestBed.inject(ShopStore);
    store.slug.set('wangji-noodles');
    store.shop.set({
      name: '王記麵店',
      phone: '06-123-4567',
      timeZone: 'Asia/Taipei',
      now: '2026-11-06T14:33:00Z',
    } as ShopInfo);
    const fixture = TestBed.createComponent(StatusPage);
    const root = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    for (let i = 0; i < 5; i++) {
      await Promise.resolve();
      fixture.detectChanges();
    }
    expect(root.querySelector('[data-testid="order-status"]')?.textContent?.trim()).toBe(
      '店家未能及時確認，訂單已自動取消',
    );
    expect(root.textContent).toContain('（已刪除）');
    expect(root.textContent).not.toContain('電話末三碼');
    fixture.destroy();
  });

  it('without a token it explains what to do and never calls the API', async () => {
    history.replaceState(null, '', '/order/wangji-noodles/o');
    const api = { order: vi.fn() };
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: OrderingApi, useValue: api }],
    });
    const store = TestBed.inject(ShopStore);
    store.slug.set('wangji-noodles');
    const fixture = TestBed.createComponent(StatusPage);
    await fixture.whenStable();
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[data-testid="order-not-found"]'),
    ).not.toBeNull();
    expect(api.order).not.toHaveBeenCalled();
  });
});

describe('StatusPage: delivery (#401)', () => {
  afterEach(() => vi.useRealTimers());

  it('shows the zone, the address, the fee, cash on delivery and 已出發 for a ready delivery', async () => {
    history.replaceState(null, '', `/order/wangji-noodles/o#${TOKEN}`);
    const ready = {
      ...order('ready'),
      serviceType: 'delivery-scheduled',
      promisedAt: '2026-11-07T05:00:00+00:00',
      pickupAt: '2026-11-07T05:00:00+00:00',
      subtotal: 340,
      deliveryFee: 30,
      total: 370,
      delivery: {
        zoneId: 'xinying',
        zoneName: '新營區其他里',
        fee: 30,
        address: '台南市新營區中山路 1 號 3 樓',
        note: '請按電鈴',
      },
    } as Order;
    const api = { order: vi.fn(async () => ({ ok: true as const, status: 200, data: ready })) };
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: OrderingApi, useValue: api }],
    });
    const store = TestBed.inject(ShopStore);
    store.slug.set('wangji-noodles');
    store.shop.set({
      name: '王記麵店',
      phone: '06-123-4567',
      timeZone: 'Asia/Taipei',
      now: '2026-11-07T04:20:00Z',
    } as ShopInfo);

    const fixture = TestBed.createComponent(StatusPage);
    const root = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    for (let i = 0; i < 5; i++) {
      await Promise.resolve();
      fixture.detectChanges();
    }
    const text = (id: string) => root.querySelector(`[data-testid="${id}"]`)?.textContent?.trim();
    expect(text('order-status')).toBe('外送已出發');
    expect(text('order-pickup')).toBe('預計 今天 13:00 送達');
    expect(text('order-zone')).toBe('新營區其他里');
    expect(text('order-address')).toBe('台南市新營區中山路 1 號 3 樓');
    expect(text('order-fee')).toBe('NT$30');
    expect(root.textContent).toContain('貨到付款');
    expect(root.textContent).toContain('請按電鈴');
    expect(root.querySelector('[aria-current="step"]')?.textContent).toContain('已出發');
    fixture.destroy();
  });
});
