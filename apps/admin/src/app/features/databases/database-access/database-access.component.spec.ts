import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../../../core/domain/account.model';
import type { DatabaseAccessView, DatabaseId } from '../../../core/domain/database.model';
import type { MockDemoRepository } from '../../../core/repositories/mock-demo-repository';
import { provideDatabaseTesting } from '../databases.testing';
import { DatabaseAccessComponent } from './database-access.component';

const RECORDS = 'database-customer-records' as DatabaseId;

function accessOf(
  repository: MockDemoRepository,
  accountId: AccountId,
  databaseId: DatabaseId,
): DatabaseAccessView {
  const detail = repository.getDatabaseDetail(accountId, databaseId);
  if (detail.status !== 'ready') throw new Error(`expected ready, got ${detail.status}`);
  return detail.data.access;
}

function render(accountId: AccountId = 'account-smb-admin', databaseId = RECORDS) {
  const { providers, repository } = provideDatabaseTesting(accountId);
  TestBed.configureTestingModule({ imports: [DatabaseAccessComponent], providers });
  const fixture = TestBed.createComponent(DatabaseAccessComponent);
  fixture.componentRef.setInput('databaseId', databaseId);
  fixture.componentRef.setInput('access', accessOf(repository, accountId, databaseId));
  fixture.detectChanges();
  let changed = 0;
  fixture.componentInstance.changed.subscribe(() => (changed += 1));
  return {
    fixture,
    host: fixture.nativeElement as HTMLElement,
    repository,
    changes: () => changed,
  };
}

describe('DatabaseAccessComponent', () => {
  it('lists every account as a labelled checkbox and marks who cannot read anyway', () => {
    const { host } = render();
    const boxes = Array.from(host.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'));

    expect(host.querySelector('fieldset legend')?.textContent).toContain('指定資料管理者');
    expect(boxes).toHaveLength(3);
    expect(boxes[0].checked).toBe(true);
    expect(boxes[1].checked).toBe(false);
    expect(host.querySelector('label[for="data-manager-account-smb-admin"]')?.textContent).toContain(
      '擁有者',
    );
    // 外部客戶沒有帳號層級的權限：畫面直說指定了也看不到。
    expect(
      host.querySelector('label[for="data-manager-account-external-customer"]')?.textContent,
    ).toContain('指定了也看不到');
    expect(host.textContent).toContain('不會刪除任何紀錄');
  });

  it('saves the designation, announces it, and says the records were not deleted', () => {
    const { fixture, host, repository, changes } = render();
    host.querySelector<HTMLInputElement>('#data-manager-account-smb-admin')?.click();
    fixture.detectChanges();
    host.querySelector<HTMLButtonElement>('button[type="submit"]')?.click();
    fixture.detectChanges();

    const feedback = host.querySelector('[aria-live="polite"]')?.textContent ?? '';
    expect(feedback).toContain('已更新資料管理者');
    expect(feedback).toContain('沒有被刪除');
    expect(changes()).toBe(1);
    expect(accessOf(repository, 'account-smb-admin', RECORDS).dataManagers).toEqual([]);
    expect(repository.getDatabaseTracking('account-smb-admin', RECORDS).status).toBe(
      'permission-denied',
    );
  });

  it('warns when a designated account still lacks the account-level permission', () => {
    const { fixture, host } = render();
    host.querySelector<HTMLInputElement>('#data-manager-account-external-customer')?.click();
    fixture.detectChanges();
    host.querySelector<HTMLButtonElement>('button[type="submit"]')?.click();
    fixture.detectChanges();

    expect(host.querySelector('[aria-live="polite"]')?.textContent).toContain(
      '還沒有「查看同意提交的紀錄」權限',
    );
  });

  it('explains the two layers instead of blaming the designation alone', () => {
    const { repository } = render();
    // 拿掉帳號層級的權限，但保留資料管理者的指定。
    repository.updateMemberPermissions('account-smb-admin', 'account-smb-admin', [
      'manage-assistants',
      'manage-data-sources',
      'manage-publishing',
    ]);

    TestBed.resetTestingModule();
    const { providers } = provideDatabaseTesting('account-smb-admin');
    TestBed.configureTestingModule({ imports: [DatabaseAccessComponent], providers });
    const fixture = TestBed.createComponent(DatabaseAccessComponent);
    fixture.componentRef.setInput('databaseId', RECORDS);
    fixture.componentRef.setInput('access', {
      owner: { id: 'account-smb-admin', displayName: '安心商行管理者' },
      dataManagers: [{ id: 'account-smb-admin', displayName: '安心商行管理者' }],
      viewerIsDataManager: true,
      viewerCanReadRecords: false,
      viewerCanManageAccess: true,
      candidates: [],
      savedAt: null,
    } satisfies DatabaseAccessView);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain(
      '還沒有「查看同意提交的紀錄」權限',
    );
  });

  it('shows the designation read-only to someone who is not the owner', () => {
    const { host } = render('account-internal-employee', 'database-staff-checkins' as DatabaseId);
    // 同仁是自己資料庫的擁有者，所以改得動；這裡改用明確的唯讀輸入驗證。
    expect(host.querySelector('button[type="submit"]')).not.toBeNull();

    TestBed.resetTestingModule();
    const { providers } = provideDatabaseTesting('account-internal-employee');
    TestBed.configureTestingModule({ imports: [DatabaseAccessComponent], providers });
    const fixture = TestBed.createComponent(DatabaseAccessComponent);
    fixture.componentRef.setInput('databaseId', RECORDS);
    fixture.componentRef.setInput('access', {
      owner: { id: 'account-smb-admin', displayName: '安心商行管理者' },
      dataManagers: [],
      viewerIsDataManager: false,
      viewerCanReadRecords: false,
      viewerCanManageAccess: false,
      candidates: [],
      savedAt: null,
    } satisfies DatabaseAccessView);
    fixture.detectChanges();
    const readOnly = fixture.nativeElement as HTMLElement;

    expect(readOnly.querySelector('input[type="checkbox"]')).toBeNull();
    expect(readOnly.querySelector('button[type="submit"]')).toBeNull();
    expect(readOnly.textContent).toContain('只有這個資料庫的擁有者可以變更資料管理者');
  });
});
