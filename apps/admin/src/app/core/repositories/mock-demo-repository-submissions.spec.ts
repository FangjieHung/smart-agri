import type { AccountId } from '../domain/account.model';
import type { DatabaseSubmissionInput, DatabaseTrialAnswers } from '../domain/database.model';
import type { DemoKeyValueStorage } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';
import { syncValue } from './sync-value.testing';

const CUSTOMER: AccountId = 'account-external-customer';
const ADMIN: AccountId = 'account-smb-admin';
const ORDERS = 'database-orders';

const ANSWERS: DatabaseTrialAnswers = {
  'field-order-number': 'DEMO-2001',
  'field-issue-type': '配送延遲',
  'field-reported-on': '2026-09-21',
};

function repositoryFor(viewer: AccountId | null, storage: DemoKeyValueStorage = createMemoryStorage()) {
  return new MockDemoRepository(DEMO_SEED, {
    storage,
    now: () => new Date('2026-10-03T02:00:00.000Z'),
    viewer: () => viewer,
  });
}

function input(overrides: Partial<DatabaseSubmissionInput> = {}): DatabaseSubmissionInput {
  return { submissionId: 'key-1', formVersion: 1, consent: true, answers: ANSWERS, ...overrides };
}

function orderRecordCount(storage: DemoKeyValueStorage): number {
  const tracking = repositoryFor(ADMIN, storage).readDatabaseTracking(ADMIN, ORDERS);
  if (tracking.status !== 'ready') throw new Error(`expected ready, got ${tracking.status}`);
  return tracking.data.subjects.reduce((sum, subject) => sum + subject.records.length, 0);
}

describe('MockDemoRepository consented submission (issue #145)', () => {
  it('shows purpose, recipient, the actual readers and the form version before consent', () => {
    const result = syncValue(repositoryFor(CUSTOMER).getDatabaseSubmissionForm(ORDERS));

    expect(result.status).toBe('ready');
    if (result.status !== 'ready') return;
    expect(result.data).toMatchObject({
      databaseId: ORDERS,
      databaseName: '訂單資料庫',
      purpose: '收集訂單問題回報，讓客服追蹤處理進度。',
      recipient: '安心商行（訂單資料庫）',
      viewers: ['安心商行管理者'],
      formVersion: 1,
    });
    expect(result.data.sensitiveNotice).toContain('敏感');
    expect(result.data.fields.map((field) => field.id)).toEqual(['field-order-number', 'field-issue-type', 'field-reported-on']);
  });

  it('refuses the form, review and submission to an account without submit-authorized-forms, like a missing database', () => {
    const storage = createMemoryStorage();
    const before = orderRecordCount(storage);
    const admin = repositoryFor(ADMIN, storage);
    const missing = syncValue(repositoryFor(CUSTOMER, storage).getDatabaseSubmissionForm('database-missing'));

    expect(syncValue(admin.getDatabaseSubmissionForm(ORDERS))).toEqual(missing);
    expect(syncValue(admin.submitDatabaseEntry(ORDERS, input()))).toEqual(missing);
    expect(syncValue(admin.reviewDatabaseSubmission(ORDERS, 1, ANSWERS))).toEqual(missing);
    expect(missing).toMatchObject({ status: 'permission-denied', reason: 'authorized-form' });
    expect(orderRecordCount(storage)).toBe(before);
  });

  it('reviews answers with the submission rule without recording anything', () => {
    const storage = createMemoryStorage();
    const before = orderRecordCount(storage);
    const repository = repositoryFor(CUSTOMER, storage);

    const reviewed = syncValue(repository.reviewDatabaseSubmission(ORDERS, 1, ANSWERS));
    const invalid = syncValue(repository.reviewDatabaseSubmission(ORDERS, 1, {}));
    const stale = syncValue(repository.reviewDatabaseSubmission(ORDERS, 2, ANSWERS));

    expect(reviewed).toMatchObject({ status: 'ready', data: { saved: false, formVersion: 1 } });
    expect(invalid).toMatchObject({ status: 'validation-failed', message: '「訂單編號」為必填。' });
    expect(stale).toMatchObject({ status: 'conflict', reason: 'form-version-changed' });
    expect(orderRecordCount(storage)).toBe(before);
  });

  it('records nothing without consent, with invalid answers (reported first) or for a stale form version', () => {
    const storage = createMemoryStorage();
    const before = orderRecordCount(storage);
    const repository = repositoryFor(CUSTOMER, storage);

    expect(syncValue(repository.submitDatabaseEntry(ORDERS, input({ consent: false })))).toEqual({
      status: 'validation-failed',
      errors: [{ fieldId: null, message: '請先勾選同意，才能送出資料。' }],
      message: '尚未同意，資料沒有送出。',
    });
    const invalid = syncValue(repository.submitDatabaseEntry(ORDERS, input({ submissionId: 'key-2', consent: false, answers: {} })));
    expect(invalid.status).toBe('validation-failed');
    if (invalid.status === 'validation-failed') {
      expect(invalid.errors.map((error) => error.fieldId)).toEqual(['field-order-number', 'field-issue-type', 'field-reported-on']);
    }
    expect(syncValue(repository.submitDatabaseEntry(ORDERS, input({ submissionId: 'key-3', formVersion: 2 })))).toMatchObject({
      status: 'conflict',
      reason: 'form-version-changed',
    });
    expect(orderRecordCount(storage)).toBe(before);
  });

  it('records a consented submission once, returns a receipt, and a retry with the same key gets the same receipt', () => {
    const storage = createMemoryStorage();
    const before = orderRecordCount(storage);
    const repository = repositoryFor(CUSTOMER, storage);

    const first = syncValue(repository.submitDatabaseEntry(ORDERS, input()));
    const retry = syncValue(repository.submitDatabaseEntry(ORDERS, input()));

    expect(first.status).toBe('ready');
    if (first.status !== 'ready') return;
    expect(first.data).toMatchObject({
      databaseName: '訂單資料庫',
      recipient: '安心商行（訂單資料庫）',
      viewers: ['安心商行管理者'],
      formVersion: 1,
      source: 'form-link',
    });
    expect(first.data.receiptNumber).toMatch(/^R-20261003-\d{10}$/);
    expect(first.data.entries.map((entry) => entry.display)).toEqual(['DEMO-2001', '配送延遲', '2026-09-21']);
    expect(retry).toEqual(first);
    expect(orderRecordCount(storage)).toBe(before + 1);

    // The data manager sees it in the timeline, from 表單連結.
    const tracking = repositoryFor(ADMIN, storage).readDatabaseTracking(ADMIN, ORDERS);
    if (tracking.status !== 'ready') throw new Error(tracking.status);
    const customer = tracking.data.subjects.find((subject) => subject.displayName === '外部客戶');
    expect(customer?.records[0]).toMatchObject({ source: 'form-link' });

    // Same key, other content: refused and nothing more recorded.
    expect(syncValue(repository.submitDatabaseEntry(ORDERS, input({ answers: { ...ANSWERS, 'field-order-number': 'DEMO-9' } })))).toMatchObject({
      status: 'conflict',
      reason: 'submission-key-reused',
    });
    expect(orderRecordCount(storage)).toBe(before + 1);

    // Only the submitter reopens the receipt.
    expect(syncValue(repository.getDatabaseSubmissionReceipt(first.data.id))).toEqual(first);
    expect(syncValue(repositoryFor(ADMIN, storage).getDatabaseSubmissionReceipt(first.data.id))).toMatchObject({
      status: 'permission-denied',
      reason: 'authorized-form',
    });
  });

  it('keeps the receipt unchanged after the form changes', async () => {
    const storage = createMemoryStorage();
    const receipt = syncValue(repositoryFor(CUSTOMER, storage).submitDatabaseEntry(ORDERS, input()));
    if (receipt.status !== 'ready') throw new Error(receipt.status);

    const admin = repositoryFor(ADMIN, storage);
    const detail = syncValue(admin.getDatabaseDetail(ORDERS));
    if (detail.status !== 'ready') throw new Error(detail.status);
    const renamed = detail.data.fields.map((field, index) => (index === 0 ? { ...field, label: '單號' } : field));
    expect(syncValue(admin.updateDatabaseFields(ORDERS, renamed, detail.data.formVersion)).status).toBe('ready');

    expect(syncValue(repositoryFor(CUSTOMER, storage).getDatabaseSubmissionReceipt(receipt.data.id))).toEqual(receipt);
    expect(receipt.data.entries[0].label).toBe('訂單編號');
  });
});
