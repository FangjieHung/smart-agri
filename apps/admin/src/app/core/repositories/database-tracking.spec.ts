import { describe, expect, it } from 'vitest';
import type { DatabaseFieldView, TrackedSubjectId } from '../domain/database.model';
import { previousPeriod, resolvePeriod, statisticsDay, summarizePeriod } from './database-tracking';
import type { DatabaseRecordFixture } from './demo-seed-databases';

/*
 * 期間與統計的算法要與後端 `DatabaseFixedQueries`／`DatabaseQueryResults` 一致：這裡的案例刻意與
 * `DatabaseFixedQueriesTests`、`DatabaseQueryResultsTests` 相同（週從週一、統計時區（台北）曆日、前一期是完整的前一期）。
 */

// 2026-10-03 是星期六。
const TODAY = '2026-10-03';
const TAIPEI = 'Asia/Taipei';

function range(name: Parameters<typeof resolvePeriod>[0], today = TODAY): [string, string] {
  const period = resolvePeriod(name, today);
  return [period.from, period.to];
}

const spend: DatabaseFieldView = {
  id: 'field-spend',
  label: '消費金額',
  type: 'number',
  required: false,
  options: [],
  scale: null,
  unit: '元',
};

function record(id: string, subject: TrackedSubjectId, at: string, value: number | null, unit = '元'): DatabaseRecordFixture {
  return {
    id: `record-${id}`,
    databaseId: 'database-customer-records',
    subjectId: subject,
    recordedAt: at,
    source: 'form-link',
    consentStatus: 'consented',
    values:
      value === null
        ? []
        : [{ fieldId: 'field-spend', label: '消費金額', type: 'number', value, unit }],
  };
}

describe('statisticsDay', () => {
  it('is the Taipei calendar day, as the receipts show it, not the UTC day', () => {
    expect(statisticsDay('2026-10-02T23:30:00Z', TAIPEI)).toBe('2026-10-03');
    expect(statisticsDay('2026-10-03T15:59:59Z', TAIPEI)).toBe('2026-10-03');
    expect(statisticsDay('2026-10-03T16:00:00Z', TAIPEI)).toBe('2026-10-04');
    expect(statisticsDay('2026-10-03T16:30:00Z', TAIPEI)).toBe('2026-10-04');
    expect(statisticsDay('2026-10-04T00:30:00+08:00', TAIPEI)).toBe('2026-10-04');
  });

  it('follows the configured zone instead of a fixed one (#177)', () => {
    // 台北已是 10/03、柏林（夏令時間 +02）與紐約（-04）仍是 10/02。
    expect(statisticsDay('2026-10-02T23:30:00Z', 'Europe/Berlin')).toBe('2026-10-03');
    expect(statisticsDay('2026-10-02T20:30:00Z', TAIPEI)).toBe('2026-10-03');
    expect(statisticsDay('2026-10-02T20:30:00Z', 'Europe/Berlin')).toBe('2026-10-02');
    expect(statisticsDay('2026-10-02T20:30:00Z', 'America/New_York')).toBe('2026-10-02');
  });
});

describe('periods', () => {
  it('are whole Taipei days with Monday-based weeks', () => {
    expect(range('this-week')).toEqual(['2026-09-28', '2026-10-04']);
    expect(range('last-week')).toEqual(['2026-09-21', '2026-09-27']);
    expect(range('this-month')).toEqual(['2026-10-01', '2026-10-31']);
    expect(range('last-month')).toEqual(['2026-09-01', '2026-09-30']);
    expect(range('last-7-days')).toEqual(['2026-09-27', '2026-10-03']);
    expect(range('last-30-days')).toEqual(['2026-09-04', '2026-10-03']);
    expect(range('this-week', '2026-10-04')[0]).toBe('2026-09-28');
    expect(range('this-week', '2026-10-05')[0]).toBe('2026-10-05');
    expect(range('last-month', '2026-01-15')).toEqual(['2025-12-01', '2025-12-31']);
  });

  it('have the full period before them as the previous one', () => {
    const before = (name: Parameters<typeof resolvePeriod>[0], today = TODAY) => {
      const period = previousPeriod(resolvePeriod(name, today));
      return [period.from, period.to, period.name];
    };

    expect(before('this-month', '2026-03-31')).toEqual(['2026-02-01', '2026-02-28', null]);
    expect(before('last-month')).toEqual(['2026-08-01', '2026-08-31', null]);
    expect(before('this-week')).toEqual(['2026-09-21', '2026-09-27', null]);
    expect(before('last-30-days')).toEqual(['2026-08-05', '2026-09-03', null]);
  });

  it('are labelled with their name and range, and the previous period with the range only', () => {
    const period = resolvePeriod('last-month', TODAY);

    expect(period.label).toBe('上月（2026-09-01 至 2026-09-30）');
    expect(previousPeriod(period).label).toBe('2026-08-01 至 2026-08-31');
  });
});

describe('summarizePeriod', () => {
  const records = [
    record('a', 'subject-wang', '2026-08-31T23:59:59+08:00', 1000),
    record('b', 'subject-wang', '2026-09-01T00:00:00+08:00', 2000),
    record('c', 'subject-wang', '2026-09-30T23:59:59.999+08:00', 3500),
    record('d', 'subject-wang', '2026-10-01T00:00:00+08:00', 4000),
    record('e', 'subject-lin', '2026-09-10T10:00:00.000Z', 10),
    record('f', 'subject-lin', '2026-09-11T10:00:00.000Z', null),
  ];
  const input = { records, fields: [spend], subjectId: null, period: 'last-month' as const, today: TODAY, timeZone: TAIPEI };

  it('counts and sums what is inside the period, with the day boundaries in Taipei', () => {
    const summary = summarizePeriod(input);

    expect(summary.recordCount).toBe(4);
    expect(summary.previousRecordCount).toBe(1);
    expect(summary.recordCountChange).toBe(3);
    expect(summary.recordCountChangeLabel).toBe('+3 筆');
    expect(summary.sums).toEqual([
      {
        fieldId: 'field-spend',
        label: '消費金額',
        unit: '元',
        sum: 5510,
        display: '5,510 元',
        recordCount: 3,
        previousSum: 1000,
        previousDisplay: '1,000 元',
        previousRecordCount: 1,
        change: 4510,
        changeLabel: '+4,510 元',
      },
    ]);
  });

  it('puts records into the periods by the calendar days of the given zone (#177)', () => {
    // UTC 下，台北 09/01 00:00 的 b 還是 08/31（前一期），台北 10/01 00:00 的 d 是 09/30（本期）。
    const summary = summarizePeriod({ ...input, timeZone: 'UTC' });

    expect(summary.recordCount).toBe(4);
    expect(summary.previousRecordCount).toBe(2);
    expect(summary.sums[0]).toMatchObject({ sum: 7510, previousSum: 3000 });
  });

  it('can be limited to one subject', () => {
    const summary = summarizePeriod({ ...input, subjectId: 'subject-lin' });

    expect(summary.subjectId).toBe('subject-lin');
    expect(summary.recordCount).toBe(2);
    expect(summary.sums[0]).toMatchObject({ sum: 10, recordCount: 1 });
  });

  it('answers zero, flat and an empty total for a period without records', () => {
    const summary = summarizePeriod({ ...input, records: [] });

    expect(summary.recordCount).toBe(0);
    expect(summary.recordCountChangeLabel).toBe('持平');
    expect(summary.sums[0]).toMatchObject({ sum: 0, display: '0 元', changeLabel: '持平', recordCount: 0 });
  });

  it('never adds a value stored under another unit and lists a removed field only while it has values', () => {
    const mixed = [
      record('g', 'subject-wang', '2026-09-05T10:00:00.000Z', 100),
      record('h', 'subject-wang', '2026-09-06T10:00:00.000Z', 7, '公斤'),
    ];
    const removed: DatabaseRecordFixture = {
      ...record('i', 'subject-wang', '2026-09-07T10:00:00.000Z', null),
      values: [{ fieldId: 'field-old', label: '舊欄位', type: 'number', value: 3, unit: '件' }],
    };

    const summary = summarizePeriod({ ...input, records: [...mixed, removed] });

    expect(summary.sums.map((line) => [line.fieldId, line.sum, line.recordCount])).toEqual([
      ['field-spend', 100, 1],
      ['field-old', 3, 1],
    ]);
    const quiet = summarizePeriod({ ...input, records: [], fields: [spend] });
    expect(quiet.sums.map((line) => line.fieldId)).toEqual(['field-spend']);
  });
});
