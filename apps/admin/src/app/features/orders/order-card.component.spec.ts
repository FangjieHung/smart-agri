import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { OrderingOrderView } from '../../core/domain/ordering.model';
import { OrderCardComponent } from './order-card.component';

const NOW = Date.parse('2026-11-07T03:40:00Z'); // 11:40 in Taipei

function order(fields: Partial<OrderingOrderView> = {}): OrderingOrderView {
  return {
    id: 'order-1', number: 1, displayNumber: '#001', status: 'pending', version: 1, serviceType: 'takeout-scheduled',
    businessDate: '2026-11-07', pickupAt: '2026-11-07T04:30:00Z', estimate: null, promisedAt: null, customerName: '陳小姐',
    customerPhone: '0912345678', customerPhoneDisplay: '0912-345-678', customerEmail: null,
    lines: [{ itemId: 'zhajiang', itemName: '炸醬麵', itemPrice: 80, quantity: 1, unitPrice: 80, lineTotal: 80, comment: null, options: [] }],
    orderComment: null, tableware: null, subtotal: 80, total: 80, menuVersion: 1, createdAt: '2026-11-07T03:35:00Z',
    statusChangedAt: '2026-11-07T03:35:00Z', accepted: null, ready: null, completed: null, declined: null, cancelled: null,
    actions: ['accept', 'decline'], expiresAt: '2026-11-07T03:50:00Z', personalDataClearedAt: null, deliveryFee: 0, delivery: null,
    ...fields,
  };
}

function render(item: OrderingOrderView): HTMLElement {
  const fixture = TestBed.createComponent(OrderCardComponent);
  fixture.componentRef.setInput('order', item);
  fixture.componentRef.setInput('now', NOW);
  fixture.componentRef.setInput('timeZone', 'Asia/Taipei');
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('OrderCardComponent (#400)', () => {
  it('shows when a pending order cancels itself, quietly while there is time', () => {
    const expiry = render(order()).querySelector('.order__expiry');

    expect(expiry?.textContent?.trim()).toBe('11:50 前未確認將自動取消');
    expect(expiry?.classList.contains('order__expiry--soon')).toBe(false);
  });

  it('highlights it with less than five minutes left', () => {
    const expiry = render(order({ expiresAt: '2026-11-07T03:44:59Z' })).querySelector('.order__expiry');

    expect(expiry?.textContent?.trim()).toBe('11:44 前未確認將自動取消');
    expect(expiry?.classList.contains('order__expiry--soon')).toBe(true);
  });

  it('shows no expiry once the order is answered', () => {
    const accepted = order({ status: 'accepted', promisedAt: '2026-11-07T04:30:00Z', actions: ['ready', 'complete', 'cancel'] });

    expect(render(accepted).querySelector('.order__expiry')).toBeNull();
    expect(render(order({ status: 'expired', actions: [] })).querySelector('.order__expiry')).toBeNull();
  });

  it('shows a cleared order without a phone to call', () => {
    const card = render(order({
      status: 'completed', actions: [], customerName: '（已刪除）', customerPhone: '', customerPhoneDisplay: '',
      personalDataClearedAt: '2027-02-05T19:00:00Z',
    }));

    expect(card.querySelector('.order__name')?.textContent).toBe('（已刪除）');
    expect(card.querySelector('a[href^="tel:"]')).toBeNull();
    expect(card.querySelector('.order__total')?.textContent).toContain('80');
  });
});

describe('OrderCardComponent, delivery (#401)', () => {
  const delivery = { zoneId: 'xinying', zoneName: '新營區其他里', fee: 30, address: '台南市新營區中山路 1 號 3 樓', note: '請按電鈴' };
  const deliveryOrder = (fields: Partial<OrderingOrderView> = {}) =>
    order({ serviceType: 'delivery-scheduled', subtotal: 80, deliveryFee: 30, total: 110, delivery, ...fields });

  it('shows the 外送 badge, the zone, the address as a Google Maps link, the note and the fee', () => {
    const card = render(deliveryOrder());

    const badge = card.querySelector('.order__service');
    expect(badge?.textContent).toBe('外送預約');
    expect(badge?.classList.contains('order__service--delivery')).toBe(true);
    expect(card.querySelector('.order__zone')?.textContent).toBe('新營區其他里');
    const link = card.querySelector<HTMLAnchorElement>('a[data-delivery-address]');
    expect(link?.textContent).toBe('台南市新營區中山路 1 號 3 樓');
    expect(link?.getAttribute('href'))
      .toBe('https://www.google.com/maps/search/?api=1&query=%E5%8F%B0%E5%8D%97%E5%B8%82%E6%96%B0%E7%87%9F%E5%8D%80%E4%B8%AD%E5%B1%B1%E8%B7%AF%201%20%E8%99%9F%203%20%E6%A8%93');
    expect([link?.getAttribute('target'), link?.getAttribute('rel'), link?.getAttribute('aria-label')])
      .toEqual(['_blank', 'noopener noreferrer', '台南市新營區中山路 1 號 3 樓：在 Google 地圖開啟（另開視窗）']);
    expect(card.querySelector('[data-delivery-note]')?.textContent).toBe('給外送的備註：請按電鈴');
    expect(card.querySelector('[data-delivery-fee]')?.textContent?.trim()).toBe('運費 NT$30');
    expect(card.querySelector('.order__total')?.textContent).toContain('NT$110');
    expect(card.querySelector('.order__when')?.textContent).toContain('12:30 送達');
  });

  it('says 送達 and 〔已出發〕 once accepted, and 已出發 once ready', () => {
    const accepted = render(deliveryOrder({ status: 'accepted', promisedAt: '2026-11-07T04:30:00Z', actions: ['ready', 'complete', 'cancel'] }));
    expect(accepted.querySelector('.order__when strong')?.textContent).toBe('預計 12:30 送達');
    expect(accepted.querySelector('[data-action="ready"]')?.textContent).toBe('已出發');
    expect(accepted.querySelector('[data-action="ready"]')?.getAttribute('aria-label')).toBe('已出發 #001');

    const ready = render(deliveryOrder({
      status: 'ready', promisedAt: '2026-11-07T04:30:00Z', actions: ['complete', 'cancel'],
      accepted: { at: '2026-11-07T03:36:00Z', by: { id: 'a', displayName: '老闆' } }, ready: { at: '2026-11-07T03:39:00Z', by: null },
    }));
    expect(ready.querySelector('.order__status')?.textContent).toBe('已出發');
    expect(ready.querySelector('.order__outcome')?.textContent).toBe('11:36 接單，老闆；11:39 已出發');
  });

  it('keeps 可取餐 and shows no address or fee for takeout', () => {
    const card = render(order({ status: 'ready', promisedAt: '2026-11-07T04:30:00Z', actions: ['complete', 'cancel'] }));
    expect(card.querySelector('.order__status')?.textContent).toBe('可取餐');
    expect(card.querySelector('.order__when strong')?.textContent).toBe('預計 12:30 可取餐');
    expect(card.querySelector('.order__service--delivery')).toBeNull();
    expect(card.querySelector('[data-delivery]')).toBeNull();
    expect(card.querySelector('[data-delivery-fee]')).toBeNull();
    expect(render(order({ status: 'accepted', actions: ['ready'] })).querySelector('[data-action="ready"]')?.textContent).toBe('可取餐');
  });

  it('shows a cleared address as （已刪除） without a map link or note', () => {
    const card = render(deliveryOrder({
      status: 'completed', actions: [], customerName: '（已刪除）', customerPhone: '', customerPhoneDisplay: '',
      personalDataClearedAt: '2027-02-05T19:00:00Z', delivery: { ...delivery, address: '', note: null },
    }));

    expect(card.querySelector('.order__zone')?.textContent).toBe('新營區其他里');
    expect(card.querySelector('[data-delivery-address]')?.textContent).toBe('（已刪除）');
    expect(card.querySelector('a[href*="google.com/maps"]')).toBeNull();
    expect(card.querySelector('[data-delivery-note]')).toBeNull();
    expect(card.querySelector('[data-delivery-fee]')?.textContent?.trim()).toBe('運費 NT$30');
  });
});
