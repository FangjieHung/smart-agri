import type { components } from '../api/api-schema';

/**
 * 線上點餐的後台（issue #399，`docs/plans/2026-11-06-online-ordering.md` §4.4）：店員在「點餐」頁接單、婉拒、
 * 標示可取餐與完成，切換暫停接單、即時單準備時間與品項供應。型別都來自 OpenAPI（`/api/v1/ordering`）。
 */
export type OrderingSummaryView = components['schemas']['OrderingStaffSummaryView'];
export type OrderingOrdersView = components['schemas']['OrderingStaffOrdersView'];
export type OrderingOrderView = components['schemas']['OrderingStaffOrderView'];
export type OrderingConflictView = components['schemas']['OrderingStaffConflict'];
export type OrderingMenuView = components['schemas']['OrderingStaffMenuView'];
export type OrderingItemView = components['schemas']['OrderingStaffItemView'];
export type OrderingItemAvailability = components['schemas']['OrderingItemAvailability'];
export type OrderingOrderLine = components['schemas']['OrderingOrderLine'];
export type OrderingChosenOption = components['schemas']['ChosenOption'];
export type OrderingOrderAction = components['schemas']['OrderingOrderAction'];
export type OrderingServiceType = components['schemas']['OrderingServiceType'];
export type OrderingAsapBand = components['schemas']['AsapPrepBand'];
export type OrderingOrderStatus = NonNullable<components['schemas']['OrderingOrderStatus']>;
export type OrderingDeclineReason = NonNullable<components['schemas']['OrderingDeclineReason']>;
export type OrderingCancelReason = NonNullable<components['schemas']['OrderingCancelReason']>;

/** 清單的三種檢視（`GET orders?view=`）。 */
export type OrderingListView = 'pending' | 'active' | 'done';

export const ORDERING_LIST_VIEWS: readonly OrderingListView[] = ['pending', 'active', 'done'];

export const ORDERING_LIST_LABELS: Readonly<Record<OrderingListView, string>> = {
  pending: '待確認',
  active: '處理中',
  done: '今日完成',
};

/** 訂單現在屬於哪個清單（動作成功或 409 之後，用來把卡片搬到對的欄）。 */
export function listOf(status: OrderingOrderStatus | null): OrderingListView {
  if (status === 'pending') return 'pending';
  if (status === 'accepted' || status === 'ready') return 'active';
  return 'done';
}

export const ORDERING_SERVICE_LABELS: Readonly<Record<OrderingServiceType, string>> = {
  'takeout-scheduled': '外帶預約',
  'takeout-asap': '外帶即時',
};

export const ORDERING_STATUS_LABELS: Readonly<Record<OrderingOrderStatus, string>> = {
  pending: '待確認',
  accepted: '已接單',
  ready: '可取餐',
  completed: '已完成',
  declined: '已婉拒',
  cancelled: '已取消',
  expired: '逾時未處理',
};

/** 卡片上的狀態文字：取消分成顧客與店家。 */
export function statusLabel(order: Pick<OrderingOrderView, 'status' | 'cancelled'>): string {
  if (order.status === null) return '';
  if (order.status === 'cancelled') return order.cancelled?.by === 'shop' ? '店家已取消' : '顧客已取消';
  return ORDERING_STATUS_LABELS[order.status];
}

/** 按鈕文字；〔可取餐〕在外送單是〔已出發〕（#401 加入外送後才會出現）。 */
export function actionLabel(action: OrderingOrderAction, serviceType: string): string {
  switch (action) {
    case 'accept':
      return '接單';
    case 'decline':
      return '婉拒';
    case 'ready':
      return serviceType === 'delivery' ? '已出發' : '可取餐';
    case 'complete':
      return '完成';
    case 'cancel':
      return '取消訂單';
  }
}

export const ORDERING_DECLINE_REASONS: readonly (readonly [OrderingDeclineReason, string])[] = [
  ['too-busy', '太忙，無法接單'],
  ['sold-out', '有品項賣完了'],
  ['closed', '已經休息'],
  ['cannot-meet-request', '無法配合需求或時間'],
  ['other', '其他（請說明）'],
];

export const ORDERING_CANCEL_REASONS: readonly (readonly [OrderingCancelReason, string])[] = [
  ['customer-no-show', '顧客沒有來取餐'],
  ['customer-request', '顧客要求取消'],
  ['other', '其他（請說明）'],
];

export function declineReasonLabel(reason: OrderingDeclineReason | null): string {
  return ORDERING_DECLINE_REASONS.find(([value]) => value === reason)?.[1].replace('（請說明）', '') ?? '';
}

export function cancelReasonLabel(reason: OrderingCancelReason | null): string {
  return ORDERING_CANCEL_REASONS.find(([value]) => value === reason)?.[1].replace('（請說明）', '') ?? '';
}

/** 店家給顧客的說明：與後端（`OrderingOrder.StaffNoteProblem`）相同，去頭尾空白後最多 100 字、單行，選「其他」時必填。 */
export const ORDERING_NOTE_MAX_LENGTH = 100;

export function staffNoteProblem(note: string, required: boolean): string | null {
  const trimmed = note.trim();
  if (trimmed.length === 0) return required ? '選「其他」時請寫明原因。' : null;
  if (trimmed.length > ORDERING_NOTE_MAX_LENGTH) return `說明最多 ${ORDERING_NOTE_MAX_LENGTH} 個字（目前 ${trimmed.length} 個）。`;
  // 與後端的 char.IsControl 相同：換行、Tab 等控制字元（U+0000–001F、U+007F–009F）都不行。
  const control = Array.from(trimmed).some((char) => {
    const code = char.charCodeAt(0);
    return code <= 0x1f || (code >= 0x7f && code <= 0x9f);
  });
  return control ? '說明不能有換行或特殊字元。' : null;
}

export const ORDERING_ASAP_BANDS: readonly (readonly [OrderingAsapBand, string])[] = [
  ['under-15', '15 分鐘內'],
  ['15-30', '15–30 分鐘'],
  ['30-45', '30–45 分鐘'],
  ['45-60', '45–60 分鐘'],
];

/** 各檔位的分鐘數（與後端 `AsapPrepBand` 相同）；mock 模式用。 */
export const ORDERING_ASAP_MINUTES: Readonly<Record<OrderingAsapBand, { readonly minMinutes: number; readonly maxMinutes: number }>> = {
  'under-15': { minMinutes: 0, maxMinutes: 15 },
  '15-30': { minMinutes: 15, maxMinutes: 30 },
  '30-45': { minMinutes: 30, maxMinutes: 45 },
  '45-60': { minMinutes: 45, maxMinutes: 60 },
};

export const ORDERING_AVAILABILITY_LABELS: Readonly<Record<OrderingItemAvailability, string>> = {
  available: '供應中',
  'sold-out-today': '今日售完',
  unavailable: '停售',
};

/** 明細裡一行的選項：「大碗、寬麵、滷蛋 ×2、小菜飲料套餐（涼拌小黃瓜、冬瓜檸檬）」（同顧客端的狀態頁）。 */
export function optionsText(options: readonly OrderingChosenOption[]): string {
  return options
    .map((option) => {
      const inner = optionsText(option.options);
      return `${option.optionName}${option.count > 1 ? ` ×${option.count}` : ''}${inner ? `（${inner}）` : ''}`;
    })
    .join('、');
}

/** 新台幣整數元：「NT$1,245」。 */
export function priceText(amount: number): string {
  return `NT$${amount.toLocaleString('en-US')}`;
}

/** 卡片的排序：待確認新的在前；處理中依給顧客的時間（接單前是顧客選的時間）早的在前；完成依結束時間新的在前。 */
export function sortOrders(list: OrderingListView, orders: readonly OrderingOrderView[]): OrderingOrderView[] {
  const copy = [...orders];
  if (list === 'pending') return copy.sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt));
  if (list === 'active') {
    return copy.sort((a, b) => Date.parse(a.promisedAt ?? a.pickupAt) - Date.parse(b.promisedAt ?? b.pickupAt)
      || Date.parse(a.createdAt) - Date.parse(b.createdAt));
  }
  return copy.sort((a, b) => Date.parse(b.statusChangedAt) - Date.parse(a.statusChangedAt));
}

const clockFormatters = new Map<string, Intl.DateTimeFormat>();

function clockFormatter(timeZone: string): Intl.DateTimeFormat {
  let found = clockFormatters.get(timeZone);
  if (!found) {
    found = new Intl.DateTimeFormat('en-CA', {
      timeZone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
    });
    clockFormatters.set(timeZone, found);
  }
  return found;
}

/** 某個時刻在店家時區的日期（`yyyy-MM-dd`）與時刻（`HH:mm`）。 */
export function shopClock(instant: string | number, timeZone: string): { readonly date: string; readonly time: string } {
  const parts = clockFormatter(timeZone).formatToParts(new Date(instant));
  const part = (type: Intl.DateTimeFormatPartTypes) => parts.find((candidate) => candidate.type === type)?.value ?? '';
  return { date: `${part('year')}-${part('month')}-${part('day')}`, time: `${part('hour')}:${part('minute')}` };
}

/** 「12:30」，不是今天時加上「11/8 」。 */
export function clockText(instant: string | number, timeZone: string, now: number): string {
  const at = shopClock(instant, timeZone);
  const today = shopClock(now, timeZone);
  if (at.date === today.date) return at.time;
  const [, month, day] = at.date.split('-').map(Number);
  return `${month}/${day} ${at.time}`;
}

/** 店家時區某天的某個時刻（`yyyy-MM-dd`＋`HH:mm`）是哪個時刻；先猜 UTC 再用時差修正（日光節約時再修一次）。 */
export function shopInstant(date: string, time: string, timeZone: string): number {
  const [year, month, day] = date.split('-').map(Number);
  const [hour, minute] = time.split(':').map(Number);
  const wanted = Date.UTC(year, month - 1, day, hour, minute);
  let guess = wanted;
  for (let attempt = 0; attempt < 2; attempt++) {
    const seen = shopClock(guess, timeZone);
    const [y, m, d] = seen.date.split('-').map(Number);
    const [h, min] = seen.time.split(':').map(Number);
    guess += wanted - Date.UTC(y, m - 1, d, h, min);
  }
  return guess;
}

/** 「剩 12 分鐘」「超過 5 分鐘」「現在」：以伺服器的現在時間計算，分鐘無條件捨去。 */
export function remainingText(target: string, now: number): string {
  const minutes = Math.trunc((Date.parse(target) - now) / 60_000);
  if (minutes > 0) return `剩 ${minutes} 分鐘`;
  if (minutes < 0) return `超過 ${-minutes} 分鐘`;
  return '就是現在';
}

/** 「3 分鐘前」「1 小時 5 分鐘前」「剛剛」。 */
export function elapsedText(since: string, now: number): string {
  const minutes = Math.max(0, Math.floor((now - Date.parse(since)) / 60_000));
  if (minutes < 1) return '剛剛';
  if (minutes < 60) return `${minutes} 分鐘前`;
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  return rest === 0 ? `${hours} 小時前` : `${hours} 小時 ${rest} 分鐘前`;
}
