import { describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import type { DatabaseSubmissionInput, DatabaseTrackingView } from '../domain/database.model';
import type { DemoKeyValueStorage, RepositoryView } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';
import { syncValue } from './sync-value.testing';

const CUSTOMER: AccountId = 'account-external-customer';
const ADMIN: AccountId = 'account-smb-admin';
const EMPLOYEE: AccountId = 'account-internal-employee';
const ORDERS = 'database-orders';

function repositoryFor(viewer: AccountId | null, storage: DemoKeyValueStorage, at = '2026-10-03T02:00:00.000Z') {
  return new MockDemoRepository(DEMO_SEED, { storage, now: () => new Date(at), viewer: () => viewer });
}

function input(orderNumber: string, overrides: Partial<DatabaseSubmissionInput> = {}): DatabaseSubmissionInput {
  return {
    // 編號不含填寫內容，才能檢查撤回後儲存裡完全找不到內容。
    submissionId: `key-${orderNumber.slice(-4)}`,
    formVersion: 1,
    consent: true,
    answers: { 'field-order-number': orderNumber, 'field-issue-type': '配送延遲', 'field-reported-on': '2026-09-21' },
    ...overrides,
  };
}

function submit(storage: DemoKeyValueStorage, orderNumber: string): string {
  const result = syncValue(repositoryFor(CUSTOMER, storage).submitDatabaseEntry(ORDERS, input(orderNumber)));
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data.id;
}

function tracking(storage: DemoKeyValueStorage): DatabaseTrackingView {
  const result: RepositoryView<DatabaseTrackingView> = syncValue(repositoryFor(ADMIN, storage).getDatabaseTracking(ORDERS));
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

function customerSubject(storage: DemoKeyValueStorage) {
  return tracking(storage).subjects.find((subject) => subject.id === `subject-${CUSTOMER}`);
}

describe('MockDemoRepository records and withdrawal (issue #146)', () => {
  it('lists only the viewer’s own submissions, newest first, including withdrawn trails', () => {
    const storage = createMemoryStorage();
    const first = submit(storage, 'DEMO-3001');
    const second = syncValue(
      repositoryFor(CUSTOMER, storage, '2026-10-03T03:00:00.000Z').submitDatabaseEntry(ORDERS, input('DEMO-3002')),
    );
    if (second.status !== 'ready') throw new Error('expected ready');
    syncValue(repositoryFor(CUSTOMER, storage).withdrawDatabaseSubmission(first));

    const own = syncValue(repositoryFor(CUSTOMER, storage).listOwnDatabaseSubmissions());
    expect(own.status).toBe('ready');
    if (own.status !== 'ready') return;
    expect(own.data.map((item) => [item.id, item.withdrawnAt])).toEqual([
      [second.data.id, null],
      [first, '2026-10-03T02:00:00.000Z'],
    ]);
    expect(JSON.stringify(own.data)).not.toContain('DEMO-300');

    expect(syncValue(repositoryFor(ADMIN, storage).listOwnDatabaseSubmissions())).toEqual({ status: 'ready', data: [] });
    expect(syncValue(repositoryFor(null, storage).listOwnDatabaseSubmissions())).toEqual({ status: 'ready', data: [] });
  });

  it('withdrawal deletes the content everywhere and leaves a content-free trail in the timeline', () => {
    const storage = createMemoryStorage();
    const kept = submit(storage, 'DEMO-3101');
    const withdrawn = submit(storage, 'DEMO-3102');
    expect(customerSubject(storage)?.records).toHaveLength(2);

    const result = syncValue(repositoryFor(CUSTOMER, storage, '2026-10-04T05:00:00.000Z').withdrawDatabaseSubmission(withdrawn));

    expect(result).toMatchObject({ status: 'ready', data: { id: withdrawn, entries: [], withdrawnAt: '2026-10-04T05:00:00.000Z' } });
    const subject = customerSubject(storage);
    expect(subject?.records).toHaveLength(1);
    expect(JSON.stringify(subject?.records)).toContain('DEMO-3101');
    expect(subject?.withdrawals).toEqual([
      expect.objectContaining({ submittedDateLabel: '2026-10-03', withdrawnDateLabel: '2026-10-04', source: 'form-link' }),
    ]);
    // 收集紀錄與回執快取都不再有這筆的內容。
    const everything = JSON.stringify(
      Object.fromEntries(['sme-demo:chat-records', 'sme-demo:database-submissions'].map((key) => [key, storage.getItem(key)])),
    );
    expect(everything).not.toContain('DEMO-3102');
    expect(everything).toContain('DEMO-3101');
    expect(syncValue(repositoryFor(CUSTOMER, storage).getDatabaseSubmissionReceipt(withdrawn))).toEqual(result);
    expect(syncValue(repositoryFor(CUSTOMER, storage).getDatabaseSubmissionReceipt(kept))).toMatchObject({
      status: 'ready',
      data: { withdrawnAt: null },
    });
  });

  it('withdrawing again returns the same withdrawn receipt and changes nothing', () => {
    const storage = createMemoryStorage();
    const id = submit(storage, 'DEMO-3201');
    const first = syncValue(repositoryFor(CUSTOMER, storage).withdrawDatabaseSubmission(id));
    const again = syncValue(repositoryFor(CUSTOMER, storage, '2026-10-09T00:00:00.000Z').withdrawDatabaseSubmission(id));

    expect(again).toEqual(first);
    expect(customerSubject(storage)?.withdrawals).toHaveLength(1);
  });

  it('only the submitter can withdraw: a data manager, another member and a missing id get the same refusal', () => {
    const storage = createMemoryStorage();
    const id = submit(storage, 'DEMO-3301');
    const refusal = {
      status: 'permission-denied',
      reason: 'submission-withdrawal',
      message: '找不到這筆紀錄，或你沒有撤回它的權限。',
    };

    expect(syncValue(repositoryFor(ADMIN, storage).withdrawDatabaseSubmission(id))).toEqual(refusal);
    expect(syncValue(repositoryFor(EMPLOYEE, storage).withdrawDatabaseSubmission(id))).toEqual(refusal);
    expect(syncValue(repositoryFor(null, storage).withdrawDatabaseSubmission(id))).toEqual(refusal);
    expect(syncValue(repositoryFor(CUSTOMER, storage).withdrawDatabaseSubmission('submission-form-99'))).toEqual(refusal);
    expect(customerSubject(storage)?.records).toHaveLength(1);
  });

  it('resending a withdrawn fill’s key returns the withdrawn receipt and records nothing again', () => {
    const storage = createMemoryStorage();
    const id = submit(storage, 'DEMO-3401');
    const withdrawn = syncValue(repositoryFor(CUSTOMER, storage).withdrawDatabaseSubmission(id));

    const replay = syncValue(repositoryFor(CUSTOMER, storage).submitDatabaseEntry(ORDERS, input('DEMO-3401')));

    expect(replay).toEqual(withdrawn);
    expect(customerSubject(storage)?.records ?? []).toHaveLength(0);
    expect(customerSubject(storage)?.withdrawals).toHaveLength(1);
  });

  it('reads the timeline for the signed-in viewer only: no session is the same refusal as a missing database', () => {
    const storage = createMemoryStorage();
    expect(syncValue(repositoryFor(null, storage).getDatabaseTracking(ORDERS))).toMatchObject({
      status: 'permission-denied',
      reason: 'database',
    });
    expect(syncValue(repositoryFor(ADMIN, storage).getDatabaseTracking(ORDERS))).toEqual(
      repositoryFor(ADMIN, storage).readDatabaseTracking(ADMIN, ORDERS),
    );
  });
});
