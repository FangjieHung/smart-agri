import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom, of } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import type { DatabaseDetailView } from '../domain/database.model';
import { DemoSessionService } from '../session/demo-session.service';
import {
  CASE_SETTINGS_ADMIN_DENIED_MESSAGE,
  CaseSettingsRepository,
  caseTypeInUseMessage,
  DATABASE_AUTO_CASE_TYPE_INACTIVE_MESSAGE,
} from './case-settings.repository';
import type { DemoRepository } from './demo-repository';
import { DEMO_REPOSITORY } from './tokens';

/**
 * 數據庫「送出後自動開案」的 Demo 模式（沒有 API，issue #255）：與後端相同的契約與檢查——只有管理者、
 * 只能選啟用中的類型、正在用的類型不能停用；「無法讀取紀錄的人數」用 mock 數據庫自己的「目前可讀取的人」
 * 計算。Hybrid（實際錄下的 API 回應）見 `case-settings.repository-hybrid-auto-case.spec.ts`。
 */
function mockRepository(accountId: AccountId | null = 'account-smb-admin') {
  const activeAccountId = signal<AccountId | null>(accountId);
  // 只有「客戶資料庫」打得開；目前能讀紀錄的人只有管理者（設備組的客服同仁讀不到）。
  const detail = {
    summary: { id: 'database-customer-records', name: '客戶資料庫' },
    access: { effectiveReaders: [{ id: 'account-smb-admin', displayName: '安心商行管理者' }] },
  } as unknown as DatabaseDetailView;
  const demo: Pick<DemoRepository, 'getDatabaseDetail'> = {
    getDatabaseDetail: (databaseId: string) => of(databaseId === 'database-customer-records'
      ? { status: 'ready', data: detail }
      : { status: 'permission-denied', reason: 'database', message: '你沒有這個資料庫的存取權限。' }),
  };
  TestBed.configureTestingModule({
    providers: [
      { provide: DemoSessionService, useValue: { activeAccountId } },
      { provide: DEMO_REPOSITORY, useValue: demo },
    ],
  });
  return { repository: TestBed.inject(CaseSettingsRepository), activeAccountId };
}

function ready<T>(result: { status: string; data?: T }): T {
  if (result.status !== 'ready' || result.data === undefined) throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

const ADMIN_DENIED = { status: 'permission-denied', reason: 'organization-settings', message: CASE_SETTINGS_ADMIN_DENIED_MESSAGE };

describe('CaseSettingsRepository — database auto case (mock)', () => {
  it('shows the manager the setting and every active type with how many group members cannot read the records', async () => {
    const { repository } = mockRepository();

    expect(ready(await firstValueFrom(repository.getDatabaseAutoCase('database-customer-records')))).toEqual({
      databaseId: 'database-customer-records',
      caseTypeId: null,
      options: [{
        id: 'case-type-equipment-repair',
        name: '設備故障報修',
        group: { id: 'case-group-equipment', name: '設備組', archived: false },
        defaultDueHours: 72,
        memberCount: 1,
        unreadableMemberCount: 1,
      }],
    });
  });

  it('refuses everyone but the manager, and a database that cannot be opened, with the same 403 organization-settings', async () => {
    const { repository, activeAccountId } = mockRepository();

    expect(await firstValueFrom(repository.getDatabaseAutoCase('database-somewhere-else'))).toEqual(ADMIN_DENIED);
    expect(await firstValueFrom(repository.setDatabaseAutoCase('database-somewhere-else', 'case-type-equipment-repair'))).toEqual(ADMIN_DENIED);
    for (const accountId of ['account-internal-employee', 'account-external-customer'] as const) {
      activeAccountId.set(accountId);
      expect(await firstValueFrom(repository.getDatabaseAutoCase('database-customer-records'))).toEqual(ADMIN_DENIED);
      expect(await firstValueFrom(repository.setDatabaseAutoCase('database-customer-records', 'case-type-equipment-repair')))
        .toEqual(ADMIN_DENIED);
    }
  });

  it('sets only an active type, and a type in use cannot be deactivated until it is turned off', async () => {
    const { repository } = mockRepository();

    for (const typeId of ['case-type-stocktake', 'case-type-that-does-not-exist']) {
      expect(await firstValueFrom(repository.setDatabaseAutoCase('database-customer-records', typeId)))
        .toEqual({ status: 'validation-failed', message: DATABASE_AUTO_CASE_TYPE_INACTIVE_MESSAGE });
    }
    expect(ready(await firstValueFrom(repository.getDatabaseAutoCase('database-customer-records'))).caseTypeId).toBeNull();

    const on = ready(await firstValueFrom(repository.setDatabaseAutoCase('database-customer-records', 'case-type-equipment-repair')));
    expect(on.caseTypeId).toBe('case-type-equipment-repair');
    expect(ready(await firstValueFrom(repository.getDatabaseAutoCase('database-customer-records'))).caseTypeId)
      .toBe('case-type-equipment-repair');

    const repair = ready(await firstValueFrom(repository.listCaseTypes())).types[0];
    const deactivate = {
      name: repair.name, description: repair.description, defaultGroupId: repair.defaultGroup.id,
      defaultDueHours: repair.defaultDueHours, isActive: false,
    };
    expect(await firstValueFrom(repository.updateCaseType(repair.id, deactivate))).toEqual({
      status: 'validation-failed', message: caseTypeInUseMessage('設備故障報修', ['客戶資料庫']), fieldErrors: {},
    });
    expect(caseTypeInUseMessage('設備故障報修', ['客戶資料庫', '門市回報'])).toBe(
      '「設備故障報修」是數據庫「客戶資料庫」、「門市回報」送出後自動開案的類型。請先在這些數據庫改選其他類型或關閉自動開案，再停用。');

    expect(ready(await firstValueFrom(repository.setDatabaseAutoCase('database-customer-records', null))).caseTypeId).toBeNull();
    expect(ready(await firstValueFrom(repository.updateCaseType(repair.id, deactivate))).isActive).toBe(false);
  });
});
