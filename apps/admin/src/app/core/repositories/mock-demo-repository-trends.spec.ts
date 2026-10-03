import { describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import type { DatabasePeriodName, TrackedSubjectId } from '../domain/database.model';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';
import { syncValue } from './sync-value.testing';

/** 「今天」固定為 2026-09-22（星期二，UTC）。 */
function createRepository(viewer: AccountId | null = 'account-smb-admin') {
  return new MockDemoRepository(DEMO_SEED, {
    storage: createMemoryStorage(),
    now: () => new Date('2026-09-22T02:00:00.000Z'),
    viewer: () => viewer,
  });
}

function summary(
  repository: MockDemoRepository,
  period: DatabasePeriodName,
  subjectId: TrackedSubjectId | null,
  databaseId = 'database-customer-records',
) {
  return syncValue(repository.getDatabasePeriodSummary(databaseId, { period, subjectId }));
}

describe('MockDemoRepository period statistics (issue #147)', () => {
  it('counts and sums one subject over a month and compares with the month before', () => {
    const result = summary(createRepository(), 'this-month', 'subject-wang');

    expect(result.status).toBe('ready');
    if (result.status !== 'ready') return;
    expect(result.data.period.label).toBe('本月（2026-09-01 至 2026-09-30）');
    expect(result.data.previousPeriod.label).toBe('2026-08-01 至 2026-08-31');
    expect(result.data.recordCount).toBe(1);
    expect(result.data.previousRecordCount).toBe(1);
    expect(result.data.recordCountChangeLabel).toBe('持平');
    expect(result.data.sums).toHaveLength(1);
    expect(result.data.sums[0]).toMatchObject({
      fieldId: 'field-monthly-spend',
      label: '本月消費金額',
      sum: 3200,
      display: '3,200 元',
      previousDisplay: '2,100 元',
      change: 1100,
      changeLabel: '+1,100 元',
    });
  });

  it('covers the whole database without a subject and leaves out the withdrawn record', () => {
    const result = summary(createRepository(), 'this-month', null);

    if (result.status !== 'ready') throw new Error('expected ready');
    // 王 09-15、林 09-10、陳 09-12；林 09-18 已撤回，不計入。
    expect(result.data.recordCount).toBe(3);
    expect(result.data.previousRecordCount).toBe(2);
    expect(result.data.recordCountChangeLabel).toBe('+1 筆');
    expect(result.data.sums[0]).toMatchObject({ sum: 5300, previousSum: 3600, changeLabel: '+1,700 元' });
  });

  it('answers zero, not an error, for a period without records', () => {
    const result = summary(createRepository(), 'this-week', 'subject-wang');

    if (result.status !== 'ready') throw new Error('expected ready');
    expect(result.data.recordCount).toBe(0);
    expect(result.data.sums[0]).toMatchObject({ sum: 0, display: '0 元', recordCount: 0 });
  });

  it('keeps the same refusals as the timeline: not visible, and visible but not readable', () => {
    const outsider = syncValue(
      createRepository('account-external-customer').getDatabasePeriodSummary('database-customer-records', {
        period: 'this-month',
        subjectId: null,
      }),
    );
    const missing = summary(createRepository(), 'this-month', null, 'database-nope');

    expect(outsider).toMatchObject({ status: 'permission-denied' });
    expect(missing).toMatchObject({ status: 'permission-denied', reason: 'database' });
  });

  it('gives a subject with one record "not enough records" and no metrics', () => {
    const tracking = createRepository().readDatabaseTracking('account-smb-admin', 'database-customer-records');
    if (tracking.status !== 'ready') throw new Error('expected ready');
    const chen = tracking.data.subjects.find((subject) => subject.id === 'subject-chen');

    expect(chen?.comparison).toEqual({
      status: 'insufficient-records',
      recordCount: 1,
      message: '目前只有 1 筆紀錄，累積 2 筆以上才會顯示比較與趨勢。',
    });
  });
});
