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
import { syncValue } from './sync-value.testing';

const ADMIN: AccountId = 'account-smb-admin';
const EMPLOYEE: AccountId = 'account-internal-employee';
const CUSTOMER: AccountId = 'account-external-customer';
const RECORDS = 'database-customer-records';
const CHECKINS = 'database-staff-checkins';

function dataOf<T>(result: { status: string }): T {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return (result as unknown as { data: T }).data;
}

/** 詳情是非同步契約、以 repository 的 `viewer()` 為準；這裡暫時切換身分讀一次。 */
let currentViewer: AccountId = ADMIN;

function detailAs(repository: MockDemoRepository, accountId: AccountId, databaseId: string) {
  const previous = currentViewer;
  currentViewer = accountId;
  try {
    return syncValue(repository.getDatabaseDetail(databaseId));
  } finally {
    currentViewer = previous;
  }
}

/** 寫入以 repository 的 `viewer()` 為準；暫時切換身分寫一次（mock 的 Observable 是同步的）。 */
function updateAccess(
  repository: MockDemoRepository,
  accountId: AccountId,
  databaseId: string,
  accountIds: readonly AccountId[],
) {
  const previous = currentViewer;
  currentViewer = accountId;
  try {
    return syncValue(repository.updateDatabaseAccess(databaseId, accountIds));
  } finally {
    currentViewer = previous;
  }
}

function accessOf(repository: MockDemoRepository, accountId: AccountId, databaseId: string) {
  const detail = detailAs(repository, accountId, databaseId);
  if (detail.status !== 'ready') throw new Error(`expected ready, got ${detail.status}`);
  return detail.data.access;
}

function subjectCount(repository: MockDemoRepository, accountId: AccountId, databaseId: string) {
  return dataOf<DatabaseTrackingView>(repository.readDatabaseTracking(accountId, databaseId)).subjects
    .length;
}

describe('MockDemoRepository data access', () => {
  let storage: ReturnType<typeof createMemoryStorage>;
  let repository: MockDemoRepository;

  beforeEach(() => {
    storage = createMemoryStorage();
    currentViewer = ADMIN;
    repository = new MockDemoRepository(DEMO_SEED, {
      storage,
      now: () => new Date('2026-09-23T02:00:00.000Z'),
      viewer: () => currentViewer,
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
    // mock 的 Observable 是同步的，訂閱當下就寫入。
    repository
      .updateMemberPermissions(ADMIN, ['manage-assistants', 'manage-data-sources', 'manage-publishing'])
      .subscribe();

    expect(accessOf(repository, ADMIN, RECORDS)).toMatchObject({
      viewerIsDataManager: true,
      viewerCanReadRecords: false,
    });
    const refused = repository.readDatabaseTracking(ADMIN, RECORDS);
    expect(refused).toMatchObject({ status: 'permission-denied', reason: 'database-records' });
    if (refused.status === 'permission-denied') {
      expect(refused.message).not.toContain('王小姐');
      expect(refused.message).not.toContain('客戶資料庫');
    }
  });

  it('hides the record and subject counts from a database summary when records are unreadable', () => {
    updateAccess(repository, ADMIN, RECORDS, []);
    const summaries = dataOf<readonly { id: string; recordCount: number | null }[]>(
      syncValue(repository.listDatabaseSummaries()),
    );

    expect(summaries.find((summary) => summary.id === RECORDS)?.recordCount).toBeNull();
  });

  it('lets the owner change the designated data managers and keeps every record intact', () => {
    const before = subjectCount(repository, ADMIN, RECORDS);

    const removed = updateAccess(repository, ADMIN, RECORDS, []);
    expect(removed.status).toBe('ready');
    expect(dataOf<DatabaseAccessView>(removed).dataManagers).toEqual([]);
    expect(repository.readDatabaseTracking(ADMIN, RECORDS)).toMatchObject({
      status: 'permission-denied',
      reason: 'database-records',
    });

    // 收回查看權限不會刪除任何紀錄：重新指定就原封不動回來。
    const restored = updateAccess(repository, ADMIN, RECORDS, [ADMIN]);
    expect(restored.status).toBe('ready');
    expect(subjectCount(repository, ADMIN, RECORDS)).toBe(before);
  });

  it('persists the designation under the sme-demo: convention and reloads it', () => {
    updateAccess(repository, ADMIN, RECORDS, [ADMIN, EMPLOYEE]);

    expect(storage.getItem(`sme-demo:database-access:${RECORDS}`)).toContain(EMPLOYEE);
    const reloaded = new MockDemoRepository(DEMO_SEED, { storage, viewer: () => currentViewer });
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
    const refused = updateAccess(repository, EMPLOYEE, RECORDS, [EMPLOYEE]);
    const unknown = updateAccess(repository, ADMIN, 'database-nope' as DatabaseId, [ADMIN]);

    expect(refused).toMatchObject({ status: 'permission-denied', reason: 'database' });
    if (refused.status === 'permission-denied' && unknown.status === 'permission-denied') {
      expect(refused.message).toBe(unknown.message);
      expect(refused.message).not.toContain('客戶資料庫');
    }
    expect(accessOf(repository, ADMIN, RECORDS).dataManagers.map((m) => m.id)).toEqual([ADMIN]);
  });

  it('rejects an unknown account id without writing anything', () => {
    const result = updateAccess(repository, ADMIN, RECORDS, ['account-ghost' as AccountId]);

    expect(result).toMatchObject({ status: 'validation-failed' });
    expect(storage.getItem(`sme-demo:database-access:${RECORDS}`)).toBeNull();
  });

  it('lets the admin revoke an employee’s access to the employee’s own database', () => {
    // 同仁是自己資料庫的擁有者，也是指定的資料管理者，所以看得到收集紀錄（目前還沒有紀錄）。
    expect(repository.readDatabaseTracking(EMPLOYEE, CHECKINS).status).toBe('ready');

    repository.updateMemberPermissions(EMPLOYEE, ['use-shared-assistants']).subscribe();

    expect(repository.readDatabaseTracking(EMPLOYEE, CHECKINS)).toMatchObject({
      status: 'permission-denied',
      reason: 'database-records',
    });
    // 表單設定仍然管得動：收回的只是「看紀錄」。
    expect(detailAs(repository, EMPLOYEE, CHECKINS).status).toBe('ready');
  });
  it('shows a designated data manager the database read-only, and only while both layers hold', () => {
    const asEmployee = () => {
      currentViewer = EMPLOYEE;
      try {
        return syncValue(repository.listDatabaseSummaries());
      } finally {
        currentViewer = ADMIN;
      }
    };
    // 還沒被指定：只有自己的資料庫，看不到客戶資料庫。
    expect(dataOf<readonly { id: string }[]>(asEmployee()).map((summary) => summary.id)).not.toContain(RECORDS);
    expect(detailAs(repository, EMPLOYEE, RECORDS).status).toBe('permission-denied');

    // 擁有者指定同仁（同仁具備帳號層級權限）。
    updateAccess(repository, ADMIN, RECORDS, [ADMIN, EMPLOYEE]);
    const listed = dataOf<readonly { id: string; viewerCanManage: boolean }[]>(asEmployee());
    expect(listed.find((summary) => summary.id === RECORDS)?.viewerCanManage).toBe(false);
    const access = accessOf(repository, EMPLOYEE, RECORDS);
    expect(access).toMatchObject({
      viewerIsDataManager: true,
      viewerCanReadRecords: true,
      viewerCanManageAccess: false,
      candidates: [],
    });
    expect(detailAs(repository, ADMIN, RECORDS).status).toBe('ready');

    // 唯讀：不能改指定，也和不存在的 id 同一句話。
    const refused = updateAccess(repository, EMPLOYEE, RECORDS, [EMPLOYEE]);
    expect(refused).toMatchObject({ status: 'permission-denied', reason: 'database' });

    // 撤銷任一層都立刻失效：先拿掉帳號層級權限。
    repository.updateMemberPermissions(EMPLOYEE, ['use-shared-assistants']).subscribe();
    expect(detailAs(repository, EMPLOYEE, RECORDS).status).toBe('permission-denied');
    expect(dataOf<readonly { id: string }[]>(asEmployee()).map((summary) => summary.id)).not.toContain(RECORDS);

    // 還原權限、再撤銷指定。
    repository.updateMemberPermissions(EMPLOYEE, ['use-shared-assistants', 'read-consented-submissions']).subscribe();
    expect(detailAs(repository, EMPLOYEE, RECORDS).status).toBe('ready');
    updateAccess(repository, ADMIN, RECORDS, [ADMIN]);
    expect(detailAs(repository, EMPLOYEE, RECORDS).status).toBe('permission-denied');
  });

  it('separates who is designated from who can actually read right now, and records who changed it', () => {
    updateAccess(repository, ADMIN, RECORDS, [ADMIN, EMPLOYEE, CUSTOMER]);
    const access = accessOf(repository, ADMIN, RECORDS);

    // 外部客戶已指定，但帳號層級沒有權限：在「已指定」，不在「目前可讀」。
    expect(access.dataManagers.map((manager) => manager.id)).toEqual([ADMIN, EMPLOYEE, CUSTOMER]);
    expect(access.effectiveReaders.map((reader) => reader.id)).toEqual([ADMIN, EMPLOYEE]);
    expect(access.savedAt).toBe('2026-09-23T02:00:00.000Z');
    expect(access.savedBy).toEqual({ id: ADMIN, displayName: expect.any(String) });
  });
});
