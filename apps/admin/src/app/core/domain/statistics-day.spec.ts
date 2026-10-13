import { describe, expect, it } from 'vitest';
import { resolveAnswerAnalyticsRange } from './case.model';
import { addCalendarDays, statisticsDay, withinStatisticsDays } from './statistics-day';

/**
 * 統計時區的曆日（#345）：與後端 `AnswerAnalyticsRange`／`DatabaseFixedQueries.DayOf` 相同的換日。
 * 每個呼叫都明確帶時區，所以結果與執行測試的機器／瀏覽器時區無關。
 */
describe('statistics days (#345)', () => {
  it('changes the Taipei day at local midnight, not at 08:00', () => {
    expect(statisticsDay('2026-10-02T15:59:00Z', 'Asia/Taipei')).toBe('2026-10-02');
    expect(statisticsDay('2026-10-02T16:00:00Z', 'Asia/Taipei')).toBe('2026-10-03');
    // Same UTC date, different Taipei days: 08:00 on the 3rd and 07:59 on the 4th.
    expect(statisticsDay('2026-10-03T00:00:00Z', 'Asia/Taipei')).toBe('2026-10-03');
    expect(statisticsDay('2026-10-03T23:59:00Z', 'Asia/Taipei')).toBe('2026-10-04');
    expect(withinStatisticsDays('2026-10-03T23:59:00Z', { from: '2026-10-03', to: '2026-10-03' }, 'Asia/Taipei')).toBe(false);
  });

  it('follows the DST rules of the zone', () => {
    // New York: 2026-03-08 is 23 hours (05:00Z .. 04:00Z next day), 2026-11-01 is 25 hours.
    const spring = { from: '2026-03-08', to: '2026-03-08' };
    expect(withinStatisticsDays('2026-03-08T04:59:59Z', spring, 'America/New_York')).toBe(false);
    expect(withinStatisticsDays('2026-03-08T05:00:00Z', spring, 'America/New_York')).toBe(true);
    expect(withinStatisticsDays('2026-03-09T03:59:59Z', spring, 'America/New_York')).toBe(true);
    expect(withinStatisticsDays('2026-03-09T04:00:00Z', spring, 'America/New_York')).toBe(false);
    const fall = { from: '2026-11-01', to: '2026-11-01' };
    expect(withinStatisticsDays('2026-11-01T03:59:59Z', fall, 'America/New_York')).toBe(false);
    expect(withinStatisticsDays('2026-11-02T04:59:59Z', fall, 'America/New_York')).toBe(true);
    expect(withinStatisticsDays('2026-11-02T05:00:00Z', fall, 'America/New_York')).toBe(false);
    expect(addCalendarDays('2026-03-08', 1)).toBe('2026-03-09');
    expect(addCalendarDays('2026-11-01', -1)).toBe('2026-10-31');
  });

  it('resolves today and the newest 30 days in the given zone, at most 180 local days', () => {
    const now = new Date('2026-10-12T23:30:00Z');
    expect(resolveAnswerAnalyticsRange(null, null, now, 'Asia/Taipei')).toEqual({ from: '2026-09-14', to: '2026-10-13' });
    expect(resolveAnswerAnalyticsRange(null, null, now, 'America/New_York')).toEqual({ from: '2026-09-13', to: '2026-10-12' });
    expect(resolveAnswerAnalyticsRange('2026-04-17', null, now, 'Asia/Taipei')).toEqual({ from: '2026-04-17', to: '2026-10-13' });
    expect(resolveAnswerAnalyticsRange('2026-04-16', null, now, 'Asia/Taipei')).toEqual({ message: '日期範圍最長 180 天。' });
  });
});
