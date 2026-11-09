import type { OrderStatus } from '../api/types';

/** 訂單狀態的白話說法（標題與補充）。 */
export function statusWords(status: OrderStatus): { title: string; detail: string } {
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
      return { title: '訂單已取消', detail: '這筆訂單不會製作，也不需要付款。' };
    case 'expired':
      return {
        title: '店家沒有及時確認，訂單已取消',
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
