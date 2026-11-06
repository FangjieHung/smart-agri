import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { Order } from '../api/types';
import { Nav, pagePath, parseRoute } from '../state/nav';
import {
  activeOrder,
  newSubmissionKey,
  orderBySubmissionKey,
  pendingSubmission,
  recentOrders,
  rememberOrder,
  savePendingSubmission,
  tokenFromHash,
  updateRecentStatus,
} from './orders';

const TOKEN = 'kq3Vd8u6m0yJxT2bA9cR4eLw7sZpN1fH5gUiOoE_-Qa';

const order = (status: Order['status'], pickupAt = '2026-11-07T12:30:00+08:00') =>
  ({ displayNumber: '#001', status, pickupAt, createdAt: '2026-11-06T22:32:00+08:00' }) as Order;

describe('order tokens and the recent-order list', () => {
  beforeEach(() => localStorage.clear());

  it('reads the token from the URL fragment only when it looks like one', () => {
    expect(tokenFromHash(`#${TOKEN}`)).toBe(TOKEN);
    expect(tokenFromHash('')).toBeNull();
    expect(tokenFromHash('#')).toBeNull();
    expect(tokenFromHash('#not a token')).toBeNull();
  });

  it('makes a UUID v4 submission key for every checkout', () => {
    const a = newSubmissionKey();
    expect(a).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    expect(newSubmissionKey()).not.toBe(a);
  });

  it('keeps the pending submission until it is cleared', () => {
    savePendingSubmission('wangji', {
      submissionKey: 'k1',
      request: { submissionKey: 'k1' } as never,
    });
    expect(pendingSubmission('wangji')?.submissionKey).toBe('k1');
    savePendingSubmission('wangji', null);
    expect(pendingSubmission('wangji')).toBeNull();
  });

  it('remembers sent orders by token and submission key, and shows only the active one in the banner', () => {
    rememberOrder('wangji', TOKEN, 'k1', order('pending'));
    expect(recentOrders('wangji')).toHaveLength(1);
    expect(orderBySubmissionKey('wangji', 'k1')?.token).toBe(TOKEN);
    expect(activeOrder('wangji', '2026-11-07T12:00:00+08:00')?.displayNumber).toBe('#001');
    // 取餐時間過了 3 小時就不再提醒。
    expect(activeOrder('wangji', '2026-11-07T16:00:00+08:00')).toBeNull();
    updateRecentStatus('wangji', TOKEN, order('cancelled'));
    expect(activeOrder('wangji', '2026-11-07T12:00:00+08:00')).toBeNull();
  });
});

describe('navigation keeps the token out of the path', () => {
  it('parses the pages under /order/', () => {
    expect(parseRoute('/order/WangJi-Noodles')).toEqual({ slug: 'wangji-noodles', page: 'menu' });
    expect(parseRoute('/order/wangji/cart')).toEqual({ slug: 'wangji', page: 'cart' });
    expect(parseRoute('/order/wangji/checkout/')).toEqual({ slug: 'wangji', page: 'checkout' });
    expect(parseRoute('/order/wangji/o')).toEqual({ slug: 'wangji', page: 'status' });
    expect(parseRoute('/order/wangji/o/abc')).toEqual({ slug: null, page: 'not-found' });
    expect(parseRoute('/order/')).toEqual({ slug: null, page: 'not-found' });
    expect(parseRoute('/order/wangji/menu')).toEqual({ slug: 'wangji', page: 'not-found' });
    expect(pagePath('wangji', 'status')).toBe('/order/wangji/o');
  });

  it('puts the status token in the fragment when going to the status page', () => {
    vi.spyOn(window, 'scrollTo').mockImplementation(() => undefined);
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const nav = TestBed.inject(Nav);
    nav.go('wangji', 'status', { hash: TOKEN });
    expect(location.pathname).toBe('/order/wangji/o');
    expect(location.pathname + location.search).not.toContain(TOKEN);
    expect(location.hash).toBe(`#${TOKEN}`);
    expect(nav.route()).toEqual({ slug: 'wangji', page: 'status' });
    expect(tokenFromHash(nav.hash())).toBe(TOKEN);
  });
});
