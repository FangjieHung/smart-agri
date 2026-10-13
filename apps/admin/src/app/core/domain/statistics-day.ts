/*
 * 統計時區的曆日（#177、#345）：數據庫統計、回答分析、營運追蹤與瓶頸統計都以後端設定
 * `Statistics:TimeZone` 的曆日換日（台灣是午夜，不是 UTC 的早上 8 點）。API 模式的時區來自
 * `GET /api/v1/me` 的 `statisticsTimeZone`（`ApiSessionService.statisticsTimeZone`），mock 模式是
 * `MOCK_STATISTICS_TIME_ZONE`。這個檔案很小、不依賴其他模組，畫面與 repository 都可以直接用。
 */

/** mock 模式的統計時區：扮演後端 `Statistics:TimeZone` 的預設值（`StatisticsOptions.DefaultTimeZone`）。 */
export const MOCK_STATISTICS_TIME_ZONE = 'Asia/Taipei';

/** 每個統計時區一個 formatter（建立 `Intl.DateTimeFormat` 不便宜，時區通常整個 session 只有一個）。 */
const dayFormats = new Map<string, Intl.DateTimeFormat>();

/**
 * 一個時間點在統計時區（IANA，例如 `Asia/Taipei`）的曆日（`YYYY-MM-DD`）；與回執、時間軸顯示的日期同一天。
 * 紀錄屬於它在這個時區落在的曆日，所以台灣 08:00 前送出的資料算當天，不是 UTC 的前一天。時區不寫死在
 * 前端（#177）：API 模式來自 `GET /api/v1/me` 的 `statisticsTimeZone`（後端設定 `Statistics:TimeZone`），
 * mock 模式來自 `MockDemoRepositoryOptions.statisticsTimeZone`（規則見 M4 設計文件 §11.3、§16）。
 * 與後端 `DatabaseFixedQueries.DayOf` 相同；夏令時間由 `Intl` 依時區規則處理。
 */
export function statisticsDay(iso: string, timeZone: string): string {
  let format = dayFormats.get(timeZone);
  if (format === undefined) {
    format = new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' });
    dayFormats.set(timeZone, format);
  }
  return format.format(new Date(iso));
}

const DAY_MS = 86_400_000;

/** 曆日（`YYYY-MM-DD`）加減天數。只是日曆運算，與時區無關（曆日沒有長短）。 */
export function addCalendarDays(day: string, days: number): string {
  return new Date(Date.parse(`${day}T00:00:00Z`) + days * DAY_MS).toISOString().slice(0, 10);
}

/** 兩個曆日之間的天數，含頭尾。 */
export function calendarDaysInclusive(from: string, to: string): number {
  return (Date.parse(`${to}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / DAY_MS + 1;
}

/** `at` 的統計曆日落在 `[from, to]`（含頭尾）：與後端半開區間 `[from 當地 00:00, to 隔天當地 00:00)` 相同。 */
export function withinStatisticsDays(at: string, range: { readonly from: string; readonly to: string }, timeZone: string): boolean {
  const day = statisticsDay(at, timeZone);
  return day >= range.from && day <= range.to;
}
