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
    actions: ['accept', 'decline'], expiresAt: '2026-11-07T03:50:00Z', personalDataClearedAt: null,
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
