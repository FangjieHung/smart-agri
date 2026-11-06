import type { Availability, MenuItem, ShopInfo, Slot } from '../api/types';

/**
 * 店家時區的牆上時間與顯示文字。所有「現在」都用伺服器回傳的 `now`（不是顧客手機的時鐘）：
 * 手機時間不準時，畫面上的「今天、明天」與分類供應時段仍和伺服器的判斷一致。
 */

export interface WallClock {
  /** `yyyy-MM-dd` */
  readonly date: string;
  /** `HH:mm` */
  readonly time: string;
  /** 0＝週日 … 6＝週六 */
  readonly weekday: number;
}

const formatters = new Map<string, Intl.DateTimeFormat>();

function formatter(timeZone: string): Intl.DateTimeFormat {
  let found = formatters.get(timeZone);
  if (!found) {
    found = new Intl.DateTimeFormat('en-US', {
      timeZone,
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      weekday: 'short',
      hourCycle: 'h23',
    });
    formatters.set(timeZone, found);
  }
  return found;
}

const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

/** 某個時刻在店家時區的日期、時刻與星期。 */
export function wallClock(instant: string | Date, timeZone: string): WallClock {
  const parts = formatter(timeZone).formatToParts(
    typeof instant === 'string' ? new Date(instant) : instant,
  );
  const part = (type: Intl.DateTimeFormatPartTypes) =>
    parts.find((p) => p.type === type)?.value ?? '';
  return {
    date: `${part('year')}-${part('month')}-${part('day')}`,
    time: `${part('hour')}:${part('minute')}`,
    weekday: WEEKDAYS.indexOf(part('weekday')),
  };
}

const WEEKDAY_NAMES = ['日', '一', '二', '三', '四', '五', '六'];
/** API 的星期名稱（`mon` … `sun`），週一在前。 */
export const DAY_KEYS = ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'] as const;
const DAY_KEY_TO_WEEKDAY: Record<string, number> = {
  sun: 0,
  mon: 1,
  tue: 2,
  wed: 3,
  thu: 4,
  fri: 5,
  sat: 6,
};

export function weekdayName(dayKey: string): string {
  return `週${WEEKDAY_NAMES[DAY_KEY_TO_WEEKDAY[dayKey] ?? 0]}`;
}

function daysBetween(from: string, to: string): number {
  return Math.round((Date.parse(`${to}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / 86_400_000);
}

/** 營業日（`yyyy-MM-dd`）相對於今天的說法：今天、明天、後天，或「11/17（二）」。 */
export function dayLabel(date: string, today: string): string {
  const diff = daysBetween(today, date);
  if (diff === 0) return '今天';
  if (diff === 1) return '明天';
  if (diff === 2) return '後天';
  return dateLabel(date);
}

/** 「11/17（二）」 */
export function dateLabel(date: string): string {
  const [, month, day] = date.split('-').map(Number);
  const weekday = new Date(`${date}T00:00:00Z`).getUTCDay();
  return `${month}/${day}（${WEEKDAY_NAMES[weekday]}）`;
}

/** 頁首的營業狀態：「營業中」與補充說明（「今天到 14:00」「明天 11:00 開始」）。 */
export function openStatus(shop: Pick<ShopInfo, 'open' | 'now' | 'timeZone' | 'paused'>): {
  open: boolean;
  label: string;
  detail: string;
} {
  const today = wallClock(shop.now, shop.timeZone).date;
  if (shop.open.isOpen && shop.open.currentPeriodCloses) {
    const closes = wallClock(shop.open.currentPeriodCloses, shop.timeZone);
    const day = closes.date === today ? '' : `${dayLabel(closes.date, today)} `;
    return { open: true, label: '營業中', detail: `接單到 ${day}${closes.time}` };
  }
  if (shop.open.nextOpening) {
    const next = wallClock(shop.open.nextOpening, shop.timeZone);
    return {
      open: false,
      label: '休息中',
      detail: `${dayLabel(next.date, today)} ${next.time} 開始接單`,
    };
  }
  return { open: false, label: '休息中', detail: '目前沒有設定接單時間' };
}

/** 儘快取餐的說明：「儘快，約 15–30 分鐘」。 */
export function asapText(asap: { minMinutes: number; maxMinutes: number }): string {
  return asap.minMinutes > 0
    ? `儘快，約 ${asap.minMinutes}–${asap.maxMinutes} 分鐘`
    : `儘快，約 ${asap.maxMinutes} 分鐘內`;
}

/** 預約時段的說明：「今天 12:30」「明天 00:30（隔天凌晨）」。 */
export function slotText(
  date: string,
  slot: Pick<Slot, 'time' | 'isNextDay'>,
  today: string,
): string {
  return `${dayLabel(date, today)} ${slot.time}${slot.isNextDay ? '（隔天凌晨）' : ''}`;
}

/** 某個取餐時刻在不在分類的供應時段內（同伺服器 `MenuAvailability.Includes`）。 */
export function isCategoryAvailable(
  availability: Availability | null,
  pickup: string | Date,
  timeZone: string,
): boolean {
  if (!availability) return true;
  const local = wallClock(pickup, timeZone);
  const onDay = (weekday: number) =>
    !availability.days || availability.days.some((day) => DAY_KEY_TO_WEEKDAY[day] === weekday);
  const { from, to } = availability;
  if (from && to && to < from) {
    if (local.time >= from) return onDay(local.weekday);
    return local.time < to && onDay((local.weekday + 6) % 7);
  }
  return (!from || local.time >= from) && (!to || local.time < to) && onDay(local.weekday);
}

/** 分類供應時段的說明：「週二至週五 11:00–14:00 供應」。 */
export function availabilityText(availability: Availability): string {
  const days = availability.days;
  let dayText = '';
  if (days && days.length > 0 && days.length < 7) {
    const ordered = DAY_KEYS.filter((day) => days.includes(day));
    const indexes = ordered.map((day) => DAY_KEYS.indexOf(day));
    const contiguous = indexes.every(
      (value, index) => index === 0 || value === indexes[index - 1] + 1,
    );
    dayText =
      contiguous && ordered.length > 2
        ? `${weekdayName(ordered[0])}至${weekdayName(ordered[ordered.length - 1])}`
        : ordered.map(weekdayName).join('、');
  }
  const time =
    availability.from || availability.to
      ? `${availability.from ?? '00:00'}–${availability.to ?? '24:00'}`
      : '';
  return time ? `${dayText ? `${dayText} ` : ''}${time} 供應` : `${dayText}供應`;
}

/** 品項在這個取餐時刻能不能點：停售一律不行，今日售完要到恢復時間之後。 */
export function isItemAvailable(item: Pick<MenuItem, 'state'>, pickup: string | Date): boolean {
  const state = item.state;
  if (!state) return true;
  if (state.kind === 'sold-out-today' && state.resetsAt) {
    return new Date(pickup).getTime() >= new Date(state.resetsAt).getTime();
  }
  return false;
}

/** 某個時刻加上幾分鐘。 */
export function addMinutes(instant: string, minutes: number): Date {
  return new Date(new Date(instant).getTime() + minutes * 60_000);
}

/** 訂單的取餐時間說明（狀態頁）：「11/17（二）12:30」或「今天 12:30」。 */
export function pickupText(pickupAt: string, timeZone: string, now: string): string {
  const pickup = wallClock(pickupAt, timeZone);
  return `${dayLabel(pickup.date, wallClock(now, timeZone).date)} ${pickup.time}`;
}
