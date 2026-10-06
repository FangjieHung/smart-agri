/**
 * 案件處理時限的換算（M7 計畫決定 H，issue #247）：以小時保存、以日曆時間計算，畫面可以用「天」或
 * 「小時」輸入。範圍 1–2,160 小時（90 天），與後端 `CaseType.MinDueHours`／`MaxDueHours` 相同。
 */
export type CaseDueUnit = 'days' | 'hours';

export const CASE_DUE_MIN_HOURS = 1;
export const CASE_DUE_MAX_HOURS = 2160;
export const HOURS_PER_DAY = 24;

/** 與後端 `CaseTypeRules.DefaultDueHoursMessage` 相同。 */
export const CASE_DUE_RANGE_MESSAGE = '預設處理時限請在 1 到 2,160 小時（90 天）之間。';
export const CASE_DUE_INTEGER_MESSAGE = '預設處理時限請輸入整數。';

export const CASE_DUE_UNIT_LABELS: Readonly<Record<CaseDueUnit, string>> = { days: '天', hours: '小時' };

/**
 * 畫面上的輸入換成小時：只接受正負號以外的整數（`3`、`72`），天數乘以 24。
 * 不是整數回傳 `integer`，超出 1–2,160 小時回傳 `range`。
 */
export function dueHoursFromInput(value: string, unit: CaseDueUnit): number | 'integer' | 'range' {
  const trimmed = value.trim();
  if (!/^\d+$/.test(trimmed)) return 'integer';
  const hours = Number(trimmed) * (unit === 'days' ? HOURS_PER_DAY : 1);
  return hours >= CASE_DUE_MIN_HOURS && hours <= CASE_DUE_MAX_HOURS ? hours : 'range';
}

/** 編輯既有類型時的初始輸入：整天數就用「天」，否則用「小時」。 */
export function dueInputFromHours(hours: number): { readonly value: string; readonly unit: CaseDueUnit } {
  return hours % HOURS_PER_DAY === 0
    ? { value: String(hours / HOURS_PER_DAY), unit: 'days' }
    : { value: String(hours), unit: 'hours' };
}

/** 顯示用：`72` → 「3 天」，`36` → 「36 小時」。 */
export function formatDueHours(hours: number): string {
  return hours % HOURS_PER_DAY === 0 ? `${hours / HOURS_PER_DAY} 天` : `${hours} 小時`;
}
