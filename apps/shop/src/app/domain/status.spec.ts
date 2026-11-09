import type { Order } from '../api/types';
import { reasonText, statusWords } from './status';

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
    expect(reasonText(order({ status: 'declined', declineReason: 'sold-out', declineNote: '牛肉麵賣完了' }))).toBe(
      '原因：有品項已經賣完。牛肉麵賣完了',
    );
    expect(reasonText(order({ status: 'declined', declineReason: 'too-busy' }))).toBe('原因：店家目前太忙。');
    expect(
      reasonText(order({ status: 'cancelled', cancelledBy: 'shop', cancelReason: 'customer-no-show' })),
    ).toBe('原因：超過取餐時間沒有取餐。');
    expect(reasonText(order({ status: 'cancelled', cancelledBy: 'customer' }))).toBeNull();
    expect(reasonText(order({ status: 'accepted' }))).toBeNull();
  });

  it('treats fields missing from an older response as absent', () => {
    expect(reasonText({ status: 'declined' } as Order)).toBeNull();
    expect(reasonText({ status: 'cancelled' } as Order)).toBeNull();
  });
});
