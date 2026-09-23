import { beforeEach, describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import type {
  DatabaseAccessView,
  DatabaseId,
  DatabaseTrackingView,
} from '../domain/database.model';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';

const ADMIN: AccountId = 'account-smb-admin';
const EMPLOYEE: AccountId = 'account-internal-employee';
const CUSTOMER: AccountId = 'account-external-customer';
const RECORDS = 'database-customer-records';
const CHECKINS = 'database-staff-checkins';

function dataOf<T>(result: { status: string }): T {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return (result as unknown as { data: T }).data;
}

function accessOf(repository: MockDemoRepository, accountId: AccountId, databaseId: string) {
  const detail = repository.getDatabaseDetail(accountId, databaseId);
  if (detail.status !== 'ready') throw new Error(`expected ready, got ${detail.status}`);
  return detail.data.access;
}

function subjectCount(repository: MockDemoRepository, accountId: AccountId, databaseId: string) {
  return dataOf<DatabaseTrackingView>(repository.getDatabaseTracking(accountId, databaseId)).subjects
    .length;
}

describe('MockDemoRepository data access', () => {
  let storage: ReturnType<typeof createMemoryStorage>;
  let repository: MockDemoRepository;

  beforeEach(() => {
    storage = createMemoryStorage();
    repository = new MockDemoRepository(DEMO_SEED, {
      storage,
      now: () => new Date('2026-09-23T02:00:00.000Z'),
    });
  });

  it('needs both the account permission and the per-database designation to read records', () => {
    // 種子狀態：管理者兩者都有。
    expect(accessOf(repository, ADMIN, RECORDS)).toMatchObject({
      viewerIsDataManager: true,
      viewerCanReadRecords: true,
      viewerCanManageAccess: true,
    });
    expect(subjectCount(repository, ADMIN, RECORDS)).toBe(3);

    // 只拿掉帳號層級的權限，資料管理者的指定不動。
    repository.updateMemberPermissions(ADMIN, ADMIN, [
      'manage-assistants',
      'manage-data-sources',
      'manage-publishing',
    ]);

    expect(accessOf(repository, ADMIN, RECORDS)).toMatchObject({
      viewerIsDataManager: true,
      viewerCanReadRecords: false,
    });
    const refused = repository.getDatabaseTracking(ADMIN, RECORDS);
    expect(refused).toMatchObject({ status: 'permission-denied', reason: 'database-records' });
    if (refused.status === 'permission-denied') {
      expect(refused.message).not.toContain('王小姐');
      expect(refused.message).not.toContain('客戶資料庫');
    }
  });

  it('hides the record and subject counts from a database summary when records are unreadable', () => {
    repository.updateDatabaseAccess(ADMIN, RECORDS, []);
    const summaries = dataOf<readonly { id: string; recordCount: number | null }[]>(
      repository.listDatabaseSummaries(ADMIN),
    );

    expect(summaries.find((summary) => summary.id === RECORDS)?.recordCount).toBeNull();
  });

  it('lets the owner change the designated data managers and keeps every record intact', () => {
    const before = subjectCount(repository, ADMIN, RECORDS);

    const removed = repository.updateDatabaseAccess(ADMIN, RECORDS, []);
    expect(removed.status).toBe('ready');
    expect(dataOf<DatabaseAccessView>(removed).dataManagers).toEqual([]);
    expect(repository.getDatabaseTracking(ADMIN, RECORDS)).toMatchObject({
      status: 'permission-denied',
      reason: 'database-records',
    });

    // 收回查看權限不會刪除任何紀錄：重新指定就原封不動回來。
    const restored = repository.updateDatabaseAccess(ADMIN, RECORDS, [ADMIN]);
    expect(restored.status).toBe('ready');
    expect(subjectCount(repository, ADMIN, RECORDS)).toBe(before);
  });

  it('persists the designation under the sme-demo: convention and reloads it', () => {
    repository.updateDatabaseAccess(ADMIN, RECORDS, [ADMIN, EMPLOYEE]);

    expect(storage.getItem(`sme-demo:database-access:${RECORDS}`)).toContain(EMPLOYEE);
    const reloaded = new MockDemoRepository(DEMO_SEED, { storage });
    expect(accessOf(reloaded, ADMIN, RECORDS).dataManagers.map((manager) => manager.id)).toEqual([
      ADMIN,
      EMPLOYEE,
    ]);
  });

  it('says on the access screen which candidates still lack the account permission', () => {
    const access = accessOf(repository, ADMIN, RECORDS);
    const customer = access.candidates.find((candidate) => candidate.id === CUSTOMER);

    expect(access.candidates.map((candidate) => candidate.id)).toEqual([
      ADMIN,
      EMPLOYEE,
      CUSTOMER,
    ]);
    expect(customer).toMatchObject({ roleLabel: '外部客戶', hasReadPermission: false });
  });

  it('refuses access changes from anyone but the owner, without leaking the database name', () => {
    const refused = repository.updateDatabaseAccess(EMPLOYEE, RECORDS, [EMPLOYEE]);
    const unknown = repository.updateDatabaseAccess(ADMIN, 'database-nope' as DatabaseId, [ADMIN]);

    expect(refused).toMatchObject({ status: 'permission-denied', reason: 'database' });
    if (refused.status === 'permission-denied' && unknown.status === 'permission-denied') {
      expect(refused.message).toBe(unknown.message);
      expect(refused.message).not.toContain('客戶資料庫');
    }
    expect(accessOf(repository, ADMIN, RECORDS).dataManagers.map((m) => m.id)).toEqual([ADMIN]);
  });

  it('rejects an unknown account id without writing anything', () => {
    const result = repository.updateDatabaseAccess(ADMIN, RECORDS, ['account-ghost' as AccountId]);

    expect(result).toMatchObject({ status: 'validation-failed' });
    expect(storage.getItem(`sme-demo:database-access:${RECORDS}`)).toBeNull();
  });

  it('lets the admin revoke an employee’s access to the employee’s own database', () => {
    // 同仁是自己資料庫的擁有者，也是指定的資料管理者，所以看得到收集紀錄（目前還沒有紀錄）。
    expect(repository.getDatabaseTracking(EMPLOYEE, CHECKINS).status).toBe('ready');

    repository.updateMemberPermissions(ADMIN, EMPLOYEE, ['use-shared-assistants']);

    expect(repository.getDatabaseTracking(EMPLOYEE, CHECKINS)).toMatchObject({
      status: 'permission-denied',
      reason: 'database-records',
    });
    // 表單設定仍然管得動：收回的只是「看紀錄」。
    expect(repository.getDatabaseDetail(EMPLOYEE, CHECKINS).status).toBe('ready');
  });
});
