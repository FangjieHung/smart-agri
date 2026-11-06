import type { Order } from '../api/types';
import {
  DELIVERY_STEPS,
  DINE_IN_STEPS,
  isDeliveryOrder,
  reasonText,
  serviceKind,
  statusWords,
} from './status';

const order = (fields: Partial<Order>): Order =>
  ({
    status: 'pending',
    declineReason: null,
    declineNote: null,
    cancelledBy: null,
    cancelReason: null,
    cancelNote: null,
    ...fields,
  }) as Order;

describe('status words (#399)', () => {
  it('tells a shop cancellation from the customer’s own', () => {
    expect(statusWords('cancelled').title).toBe('訂單已取消');
    expect(statusWords('cancelled', 'customer').title).toBe('訂單已取消');
    expect(statusWords('cancelled', 'shop').title).toBe('店家已取消這筆訂單');
  });

  it('gives the shop’s reason and note for a decline or a shop cancellation only', () => {
    expect(
      reasonText(
        order({ status: 'declined', declineReason: 'sold-out', declineNote: '牛肉麵賣完了' }),
      ),
    ).toBe('原因：有品項已經賣完。牛肉麵賣完了');
    expect(reasonText(order({ status: 'declined', declineReason: 'too-busy' }))).toBe(
      '原因：店家目前太忙。',
    );
    expect(
      reasonText(
        order({ status: 'cancelled', cancelledBy: 'shop', cancelReason: 'customer-no-show' }),
      ),
    ).toBe('原因：超過取餐時間沒有取餐。');
    expect(reasonText(order({ status: 'cancelled', cancelledBy: 'customer' }))).toBeNull();
    expect(reasonText(order({ status: 'accepted' }))).toBeNull();
  });

  it('treats fields missing from an older response as absent', () => {
    expect(reasonText({ status: 'declined' } as Order)).toBeNull();
    expect(reasonText({ status: 'cancelled' } as Order)).toBeNull();
  });
});

describe('status words for delivery (#401)', () => {
  it('a delivery order that is ready has set out and is paid on delivery', () => {
    expect(statusWords('ready', null, 'delivery')).toEqual({
      title: '外送已出發',
      detail: '餐點正在送過去，送達時付款（貨到付款）。',
    });
    expect(statusWords('completed', null, 'delivery').title).toBe('已送達');
    expect(statusWords('accepted', null, 'delivery').detail).toContain('送達時付款');
    expect(statusWords('pending', null, 'delivery').title).toBe('等待店家確認');
    expect(statusWords('ready').title).toBe('餐點好了，可以取餐');
    expect(DELIVERY_STEPS.at(-1)).toBe('已出發');
  });

  it('tells a delivery order by its service type', () => {
    expect(isDeliveryOrder({ serviceType: 'delivery-asap' })).toBe(true);
    expect(isDeliveryOrder({ serviceType: 'delivery-scheduled' })).toBe(true);
    expect(isDeliveryOrder({ serviceType: 'takeout-asap' })).toBe(false);
  });
});

describe('dine-in status words (#402)', () => {
  it('says what happens at the table', () => {
    expect(statusWords('pending', null, 'dine-in').title).toBe('等待店家確認');
    expect(statusWords('accepted', null, 'dine-in').title).toBe('店家已接單，餐點準備中');
    expect(statusWords('accepted', null, 'dine-in', true).title).toBe('已自動接單，餐點準備中');
    expect(statusWords('ready', null, 'dine-in').title).toBe('已出餐');
    expect(statusWords('completed', null, 'dine-in').title).toBe('已結帳，謝謝光臨');
    // 付款說明由訂單頁的「付款」一行顯示（店家可以改 dineIn.note），狀態說明不重複、也不提取餐。
    expect(statusWords('accepted', null, 'dine-in').detail).not.toContain('取餐');
    expect(statusWords('ready', null, 'dine-in').detail).toBe('請慢用。');
    expect(statusWords('cancelled', 'shop', 'dine-in').title).toBe('店家已取消這筆訂單');
    expect(DINE_IN_STEPS.at(-1)).toBe('已出餐');
  });

  it('tells dine-in, delivery and takeout apart, also for answers recorded before #402', () => {
    expect(serviceKind({ serviceType: 'dine-in', table: { name: 'A1' } })).toBe('dine-in');
    expect(serviceKind({ serviceType: 'dine-in' })).toBe('dine-in');
    expect(serviceKind({ serviceType: 'delivery-asap' })).toBe('delivery');
    expect(serviceKind({ serviceType: 'takeout-scheduled' })).toBe('takeout');
    expect(serviceKind({})).toBe('takeout');
  });
});
