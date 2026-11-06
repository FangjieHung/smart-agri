import {
  asapText,
  availabilityText,
  dayLabel,
  isCategoryAvailable,
  isItemAvailable,
  openStatus,
  slotText,
  wallClock,
} from './time';

const TZ = 'Asia/Taipei';

describe('shop clock texts', () => {
  it('reads the wall clock in the shop time zone, whatever the browser zone', () => {
    expect(wallClock('2026-11-06T16:30:00Z', TZ)).toEqual({
      date: '2026-11-07',
      time: '00:30',
      weekday: 6,
    });
  });

  it('says today / tomorrow / the day after, then a date', () => {
    expect(dayLabel('2026-11-06', '2026-11-06')).toBe('今天');
    expect(dayLabel('2026-11-07', '2026-11-06')).toBe('明天');
    expect(dayLabel('2026-11-08', '2026-11-06')).toBe('後天');
    expect(dayLabel('2026-11-10', '2026-11-06')).toBe('11/10（二）');
  });

  it('open: until when; closed: when it opens next (server time, not the phone clock)', () => {
    const base = { timeZone: TZ, paused: false, now: '2026-11-06T04:00:00Z' };
    expect(
      openStatus({
        ...base,
        open: {
          isOpen: true,
          currentPeriodOpens: '2026-11-06T11:00:00+08:00',
          currentPeriodCloses: '2026-11-06T14:00:00+08:00',
          nextOpening: null,
        },
      }),
    ).toEqual({ open: true, label: '營業中', detail: '接單到 14:00' });
    expect(
      openStatus({
        ...base,
        now: '2026-11-06T14:30:00Z',
        open: {
          isOpen: false,
          currentPeriodOpens: null,
          currentPeriodCloses: null,
          nextOpening: '2026-11-07T11:00:00+08:00',
        },
      }),
    ).toEqual({ open: false, label: '休息中', detail: '明天 11:00 開始接單' });
    // 跨午夜的營業時段：結束在隔天。
    expect(
      openStatus({
        ...base,
        now: '2026-11-07T15:00:00Z',
        open: {
          isOpen: true,
          currentPeriodOpens: '2026-11-07T17:00:00+08:00',
          currentPeriodCloses: '2026-11-08T01:00:00+08:00',
          nextOpening: null,
        },
      }).detail,
    ).toBe('接單到 明天 01:00');
  });

  it('describes ASAP and scheduled pickups', () => {
    expect(asapText({ minMinutes: 15, maxMinutes: 30 })).toBe('儘快，約 15–30 分鐘');
    expect(asapText({ minMinutes: 0, maxMinutes: 15 })).toBe('儘快，約 15 分鐘內');
    expect(slotText('2026-11-07', { time: '12:30', isNextDay: false }, '2026-11-06')).toBe(
      '明天 12:30',
    );
    expect(slotText('2026-11-07', { time: '00:30', isNextDay: true }, '2026-11-07')).toBe(
      '今天 00:30（隔天凌晨）',
    );
  });

  it('category windows follow the server rule, overnight included', () => {
    const lunch = { days: ['tue', 'wed', 'thu', 'fri'], from: '11:00', to: '14:00' };
    expect(isCategoryAvailable(lunch, '2026-11-06T12:30:00+08:00', TZ)).toBe(true); // 週五
    expect(isCategoryAvailable(lunch, '2026-11-06T14:00:00+08:00', TZ)).toBe(false); // to 不含
    expect(isCategoryAvailable(lunch, '2026-11-07T12:30:00+08:00', TZ)).toBe(false); // 週六
    const lateFriday = { days: ['fri'], from: '22:00', to: '02:00' };
    expect(isCategoryAvailable(lateFriday, '2026-11-07T01:00:00+08:00', TZ)).toBe(true); // 週六凌晨屬於週五
    expect(isCategoryAvailable(lateFriday, '2026-11-08T01:00:00+08:00', TZ)).toBe(false);
    expect(isCategoryAvailable(null, '2026-11-08T01:00:00+08:00', TZ)).toBe(true);
    expect(availabilityText(lunch)).toBe('週二至週五 11:00–14:00 供應');
    expect(availabilityText({ days: ['sat', 'mon'], from: null, to: null })).toBe('週一、週六供應');
  });

  it('sold out today comes back at resetsAt; unavailable never', () => {
    const soldOut = {
      state: { kind: 'sold-out-today' as const, resetsAt: '2026-11-07T11:00:00+08:00' },
    };
    expect(isItemAvailable(soldOut, '2026-11-07T10:30:00+08:00')).toBe(false);
    expect(isItemAvailable(soldOut, '2026-11-07T11:00:00+08:00')).toBe(true);
    expect(
      isItemAvailable({ state: { kind: 'unavailable', resetsAt: null } }, '2030-01-01T00:00:00Z'),
    ).toBe(false);
    expect(isItemAvailable({ state: null }, '2026-11-07T10:30:00+08:00')).toBe(true);
  });
});
