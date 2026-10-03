import { TestBed } from '@angular/core/testing';
import { Subject, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { AccountId } from '../../../core/domain/account.model';
import type { DatabaseAccessView, DatabaseId } from '../../../core/domain/database.model';
import type { MockDemoRepository } from '../../../core/repositories/mock-demo-repository';
import { syncValue } from '../../../core/repositories/sync-value.testing';
import { provideDatabaseTesting } from '../databases.testing';
import { DatabaseAccessComponent } from './database-access.component';

const RECORDS = 'database-customer-records' as DatabaseId;

/** 以 repository 目前的 viewer（`provideDatabaseTesting` 的帳號）讀取權限頁籤的資料。 */
function accessOf(repository: MockDemoRepository, databaseId: DatabaseId): DatabaseAccessView {
  const detail = syncValue(repository.getDatabaseDetail(databaseId));
  if (detail.status !== 'ready') throw new Error(`expected ready, got ${detail.status}`);
  return detail.data.access;
}

function render(accountId: AccountId = 'account-smb-admin', databaseId = RECORDS) {
  const { providers, repository } = provideDatabaseTesting(accountId);
  TestBed.configureTestingModule({ imports: [DatabaseAccessComponent], providers });
  const fixture = TestBed.createComponent(DatabaseAccessComponent);
  fixture.componentRef.setInput('databaseId', databaseId);
  fixture.componentRef.setInput('access', accessOf(repository, databaseId));
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
    expect(accessOf(repository, RECORDS).dataManagers).toEqual([]);
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
    repository
      .updateMemberPermissions('account-smb-admin', [
        'manage-assistants',
        'manage-data-sources',
        'manage-publishing',
      ])
      .subscribe();

    TestBed.resetTestingModule();
    const { providers } = provideDatabaseTesting('account-smb-admin');
    TestBed.configureTestingModule({ imports: [DatabaseAccessComponent], providers });
    const fixture = TestBed.createComponent(DatabaseAccessComponent);
    fixture.componentRef.setInput('databaseId', RECORDS);
    fixture.componentRef.setInput('access', {
      owner: { id: 'account-smb-admin', displayName: '安心商行管理者' },
      dataManagers: [{ id: 'account-smb-admin', displayName: '安心商行管理者' }],
      effectiveReaders: [],
      viewerIsDataManager: true,
      viewerCanReadRecords: false,
      viewerCanManageAccess: true,
      candidates: [],
      savedAt: null,
      savedBy: null,
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
      effectiveReaders: [],
      viewerIsDataManager: false,
      viewerCanReadRecords: false,
      viewerCanManageAccess: false,
      candidates: [],
      savedAt: null,
      savedBy: null,
    } satisfies DatabaseAccessView);
    fixture.detectChanges();
    const readOnly = fixture.nativeElement as HTMLElement;

    expect(readOnly.querySelector('input[type="checkbox"]')).toBeNull();
    expect(readOnly.querySelector('button[type="submit"]')).toBeNull();
    expect(readOnly.textContent).toContain('只有這個資料庫的擁有者可以變更資料管理者');
  });
  it('shows who is designated and who can actually read right now as two separate lists', () => {
    const { repository } = render();
    const access = accessOf(repository, RECORDS);

    const rendered = TestBed.createComponent(DatabaseAccessComponent);
    rendered.componentRef.setInput('databaseId', RECORDS);
    rendered.componentRef.setInput('access', {
      ...access,
      dataManagers: [...access.dataManagers, { id: 'account-external-customer', displayName: '外部客戶' }],
      effectiveReaders: access.effectiveReaders,
      savedAt: '2026-10-03T02:00:00.000Z',
      savedBy: access.owner,
    } satisfies DatabaseAccessView);
    rendered.detectChanges();
    const rows = Array.from((rendered.nativeElement as HTMLElement).querySelectorAll('.access-list > div')).map(
      (row) => [row.querySelector('dt')?.textContent?.trim(), row.querySelector('dd')?.textContent ?? ''] as const,
    );

    expect(rows.find(([label]) => label === '已指定資料管理者')?.[1]).toContain('外部客戶');
    expect(rows.find(([label]) => label === '目前可讀紀錄')?.[1]).not.toContain('外部客戶');
    expect(rows.find(([label]) => label === '最後變更')?.[1]).toContain('由 安心商行管理者 變更');
  });

  it('keeps the selection and says so when saving fails, and ignores a second submit while saving', () => {
    const { fixture, host, repository } = render();
    const pending = new Subject<never>();
    const update = vi.spyOn(repository, 'updateDatabaseAccess').mockReturnValueOnce(pending);
    host.querySelector<HTMLInputElement>('#data-manager-account-internal-employee')?.click();
    fixture.detectChanges();
    const submit = () => host.querySelector<HTMLButtonElement>('button[type="submit"]')?.click();

    submit();
    fixture.detectChanges();
    expect(host.querySelector<HTMLButtonElement>('button[type="submit"]')?.disabled).toBe(true);
    submit();
    expect(update).toHaveBeenCalledTimes(1);

    pending.error(new Error('500'));
    fixture.detectChanges();
    expect(host.querySelector('[role="alert"]')?.textContent).toContain('勾選仍保留');
    expect(host.querySelector<HTMLInputElement>('#data-manager-account-internal-employee')?.checked).toBe(true);
    expect(host.querySelector<HTMLButtonElement>('button[type="submit"]')?.disabled).toBe(false);

    update.mockReturnValueOnce(throwError(() => new Error('500')));
    submit();
    fixture.detectChanges();
    expect(update).toHaveBeenCalledTimes(2);
  });
});
