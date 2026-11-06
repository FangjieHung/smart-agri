import type { CancelReason, Canceller, DeclineReason, Order, OrderStatus } from '../api/types';

/** 訂單狀態的白話說法（標題與補充）。`cancelledBy` 是 `shop` 時是店家取消（#399）。 */
export function statusWords(
  status: OrderStatus,
  cancelledBy: Canceller | null = null,
): { title: string; detail: string } {
  switch (status) {
    case 'pending':
      return { title: '等待店家確認', detail: '店家確認後，這個頁面會自動更新。' };
    case 'accepted':
      return { title: '店家已接單，正在準備', detail: '請在取餐時間到店取餐，取餐時付款。' };
    case 'ready':
      return { title: '餐點好了，可以取餐', detail: '請到店取餐，取餐時付款。' };
    case 'completed':
      return { title: '已完成取餐', detail: '謝謝光臨，歡迎再來。' };
    case 'declined':
      return {
        title: '店家無法接這筆訂單',
        detail: '這筆訂單不會製作，也不需要付款。有問題請來電店家。',
      };
    case 'cancelled':
      return cancelledBy === 'shop'
        ? { title: '店家已取消這筆訂單', detail: '這筆訂單不會製作，也不需要付款。有問題請來電店家。' }
        : { title: '訂單已取消', detail: '這筆訂單不會製作，也不需要付款。' };
    case 'expired':
      return {
        title: '店家未能及時確認，訂單已自動取消',
        detail: '這筆訂單不會製作。需要的話請重新點餐或來電店家。',
      };
  }
}

/** 進度條的步驟。 */
export const PROGRESS_STEPS = ['已送出', '店家確認', '準備中', '可以取餐'] as const;

/** 目前走到第幾步（從 0 起）；不會製作的訂單是 `null`。 */
export function progressIndex(status: OrderStatus): number | null {
  switch (status) {
    case 'pending':
      return 1;
    case 'accepted':
      return 2;
    case 'ready':
      return 3;
    case 'completed':
      return 4;
    default:
      return null;
  }
}

const DECLINE_REASONS: Readonly<Record<DeclineReason, string>> = {
  'too-busy': '店家目前太忙',
  'sold-out': '有品項已經賣完',
  closed: '店家已經休息',
  'cannot-meet-request': '無法配合訂單的需求或時間',
  other: '其他原因',
};

const CANCEL_REASONS: Readonly<Record<CancelReason, string>> = {
  'customer-no-show': '超過取餐時間沒有取餐',
  'customer-request': '依您的要求取消',
  other: '其他原因',
};

/**
 * 店家婉拒或取消時給顧客的原因（#399），例如「原因：有品項已經賣完。牛肉麵賣完了」；其他狀態是 `null`。
 * 舊的回應（或錄下的回應）可能沒有這些欄位，一律當成沒有。
 */
export function reasonText(order: Order): string | null {
  const reason =
    order.status === 'declined'
      ? (order.declineReason ?? null)
      : order.status === 'cancelled' && order.cancelledBy === 'shop'
        ? (order.cancelReason ?? null)
        : null;
  if (reason === null) return null;
  const label =
    order.status === 'declined'
      ? DECLINE_REASONS[reason as DeclineReason]
      : CANCEL_REASONS[reason as CancelReason];
  const note = (order.status === 'declined' ? order.declineNote : order.cancelNote) ?? null;
  return note ? `原因：${label}。${note}` : `原因：${label}。`;
}
