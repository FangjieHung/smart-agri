import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import { CASE_DUE_RANGE_MESSAGE } from '../domain/case-due-time';
import type { CaseGroupView, CaseTypeInput } from '../domain/case-settings.model';
import { DemoSessionService } from '../session/demo-session.service';
import {
  CASE_FEATURE_DENIED_MESSAGE,
  CASE_GROUP_MEMBER_NOT_ELIGIBLE_MESSAGE,
  CASE_GROUP_NAME_REQUIRED_MESSAGE,
  CASE_GROUP_NAME_TAKEN_MESSAGE,
  CASE_GROUP_NAME_TOO_LONG_MESSAGE,
  CASE_SETTINGS_ADMIN_DENIED_MESSAGE,
  CASE_TYPE_DESCRIPTION_TOO_LONG_MESSAGE,
  CASE_TYPE_GROUP_ARCHIVED_MESSAGE,
  CASE_TYPE_GROUP_NOT_FOUND_MESSAGE,
  CASE_TYPE_GROUP_REQUIRED_MESSAGE,
  CASE_TYPE_NAME_REQUIRED_MESSAGE,
  CASE_TYPE_NAME_TAKEN_MESSAGE,
  CASE_TYPE_NAME_TOO_LONG_MESSAGE,
  caseGroupInUseMessage,
  CaseSettingsRepository,
} from './case-settings.repository';

/** Demo 模式（沒有 API）：與後端相同的契約與檢查（issue #246）。Hybrid 見 `case-settings.repository-hybrid.spec.ts`。 */
function mockRepository(accountId: AccountId | null = 'account-smb-admin') {
  const activeAccountId = signal<AccountId | null>(accountId);
  TestBed.configureTestingModule({ providers: [{ provide: DemoSessionService, useValue: { activeAccountId } }] });
  return { repository: TestBed.inject(CaseSettingsRepository), activeAccountId };
}

function input(defaultGroupId = 'case-group-equipment', defaultDueHours = 48): CaseTypeInput {
  return { name: '冷藏庫異常', description: '', defaultGroupId, defaultDueHours, isActive: true };
}

function ready<T>(result: { status: string; data?: T }): T {
  if (result.status !== 'ready' || result.data === undefined) throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

describe('CaseSettingsRepository (mock)', () => {
  it('lists the groups for every internal account and refuses an external customer with 403 case', async () => {
    const { repository, activeAccountId } = mockRepository();

    const admin = ready(await firstValueFrom(repository.listCaseGroups({ includeArchived: true })));
    expect(admin.canManage).toBe(true);
    expect(admin.groups.map((group) => [group.name, group.archived])).toEqual([
      ['設備組', false], ['採購組', false], ['舊倉儲組', true],
    ]);
    expect(admin.candidates.map((candidate) => candidate.role)).toEqual(['smb-admin', 'internal-employee']);

    activeAccountId.set('account-internal-employee');
    const member = ready(await firstValueFrom(repository.listCaseGroups({ includeArchived: true })));
    expect(member.canManage).toBe(false);
    expect(member.candidates).toEqual([]);
    expect(member.groups.map((group) => group.name)).toEqual(['設備組', '採購組']);
    expect(member.groups[0].members).toEqual([{ id: 'account-internal-employee', displayName: '安心商行客服同仁' }]);

    activeAccountId.set('account-external-customer');
    expect(await firstValueFrom(repository.listCaseGroups())).toEqual({
      status: 'permission-denied', reason: 'case', message: CASE_FEATURE_DENIED_MESSAGE,
    });
  });

  it('leaves archived groups out of the default list, even for the manager', async () => {
    const { repository } = mockRepository();

    const names = ready(await firstValueFrom(repository.listCaseGroups())).groups.map((group) => group.name);

    expect(names).toEqual(['設備組', '採購組']);
  });

  it('refuses every write by a non-manager and an unknown id with the same 403 organization-settings', async () => {
    const { repository, activeAccountId } = mockRepository('account-internal-employee');
    const denied = { status: 'permission-denied', reason: 'organization-settings', message: CASE_SETTINGS_ADMIN_DENIED_MESSAGE };

    expect(await firstValueFrom(repository.createCaseGroup('品保組'))).toEqual(denied);
    expect(await firstValueFrom(repository.renameCaseGroup('case-group-equipment', '改名'))).toEqual(denied);
    expect(await firstValueFrom(repository.setCaseGroupArchived('case-group-equipment', true))).toEqual(denied);
    expect(await firstValueFrom(repository.updateCaseGroupMembers('case-group-equipment', []))).toEqual(denied);
    expect(await firstValueFrom(repository.listCaseGroupMemberChanges('case-group-equipment'))).toEqual(denied);

    activeAccountId.set('account-smb-admin');
    expect(await firstValueFrom(repository.renameCaseGroup('no-such-group', '改名'))).toEqual(denied);
    expect(await firstValueFrom(repository.setCaseGroupArchived('no-such-group', true))).toEqual(denied);
  });

  it('checks a name the way the API does: trimmed, 1–40 characters, unique', async () => {
    const { repository } = mockRepository();

    expect(await firstValueFrom(repository.createCaseGroup('  '))).toEqual({ status: 'validation-failed', message: CASE_GROUP_NAME_REQUIRED_MESSAGE });
    expect(await firstValueFrom(repository.createCaseGroup('組'.repeat(41)))).toEqual({ status: 'validation-failed', message: CASE_GROUP_NAME_TOO_LONG_MESSAGE });
    expect(await firstValueFrom(repository.createCaseGroup('設備組'))).toEqual({ status: 'validation-failed', message: CASE_GROUP_NAME_TAKEN_MESSAGE });
    expect(await firstValueFrom(repository.renameCaseGroup('case-group-purchasing', '設備組'))).toEqual({ status: 'validation-failed', message: CASE_GROUP_NAME_TAKEN_MESSAGE });

    const created = ready<CaseGroupView>(await firstValueFrom(repository.createCaseGroup(' 品保組 ')));
    expect([created.name, created.archived, created.members]).toEqual(['品保組', false, []]);
    expect(ready<CaseGroupView>(await firstValueFrom(repository.createCaseGroup('組'.repeat(40)))).name).toHaveLength(40);
  });

  it('archives and unarchives back and forth', async () => {
    const { repository } = mockRepository();

    const archived = ready(await firstValueFrom(repository.setCaseGroupArchived('case-group-purchasing', true)));
    expect([archived.archived, archived.archivedAt !== null]).toEqual([true, true]);
    expect(ready(await firstValueFrom(repository.listCaseGroups())).groups.map((group) => group.name)).toEqual(['設備組']);

    const back = ready(await firstValueFrom(repository.setCaseGroupArchived('case-group-purchasing', false)));
    expect([back.archived, back.archivedAt]).toEqual([false, null]);
  });

  it('will not archive the default group of an active case type until that type is deactivated (issue #247)', async () => {
    const { repository } = mockRepository();

    expect(await firstValueFrom(repository.setCaseGroupArchived('case-group-equipment', true))).toEqual({
      status: 'validation-failed',
      message: caseGroupInUseMessage('設備組', ['設備故障報修']),
    });
    expect(caseGroupInUseMessage('設備組', ['設備故障報修', '冷藏庫異常'])).toBe(
      '「設備組」是啟用中的案件類型「設備故障報修」、「冷藏庫異常」的預設承辦組。請先替這些類型換一個承辦組，或停用它們，再封存。');

    const repair = ready(await firstValueFrom(repository.listCaseTypes())).types[0];
    ready(await firstValueFrom(repository.updateCaseType(repair.id, { ...input(repair.defaultGroup.id), name: repair.name, isActive: false })));
    expect(ready(await firstValueFrom(repository.setCaseGroupArchived('case-group-equipment', true))).archived).toBe(true);
  });

  it('replaces the members as a whole, records each change, and refuses an external customer', async () => {
    const { repository } = mockRepository();

    expect(await firstValueFrom(repository.updateCaseGroupMembers('case-group-equipment', ['account-smb-admin', 'account-external-customer'])))
      .toEqual({ status: 'validation-failed', message: CASE_GROUP_MEMBER_NOT_ELIGIBLE_MESSAGE });
    expect(await firstValueFrom(repository.updateCaseGroupMembers('case-group-equipment', ['account-smb-admin', 'someone-else'])))
      .toEqual({ status: 'validation-failed', message: CASE_GROUP_MEMBER_NOT_ELIGIBLE_MESSAGE });

    const saved = ready(await firstValueFrom(repository.updateCaseGroupMembers('case-group-equipment', ['account-smb-admin'])));
    expect(saved.members.map((member) => member.id)).toEqual(['account-smb-admin']);

    const changes = ready(await firstValueFrom(repository.listCaseGroupMemberChanges('case-group-equipment')));
    expect(changes.map((change) => [change.account.displayName, change.added])).toEqual([
      ['安心商行管理者', true], ['安心商行客服同仁', false], ['安心商行客服同仁', true],
    ]);
    expect(changes.every((change) => change.changedBy.displayName === '安心商行管理者')).toBe(true);

    // The same list again writes nothing.
    await firstValueFrom(repository.updateCaseGroupMembers('case-group-equipment', ['account-smb-admin']));
    expect(ready(await firstValueFrom(repository.listCaseGroupMemberChanges('case-group-equipment')))).toHaveLength(3);
  });

  it('lists the active case types for every internal account; only the manager sees inactive ones (issue #247)', async () => {
    const { repository, activeAccountId } = mockRepository();

    const admin = ready(await firstValueFrom(repository.listCaseTypes({ includeInactive: true })));
    expect(admin.canManage).toBe(true);
    expect(admin.types.map((type) => [type.name, type.isActive, type.defaultGroup.name, type.defaultGroup.archived, type.defaultDueHours]))
      .toEqual([['設備故障報修', true, '設備組', false, 72], ['倉儲盤點差異', false, '舊倉儲組', true, 36]]);
    expect(ready(await firstValueFrom(repository.listCaseTypes())).types.map((type) => type.name)).toEqual(['設備故障報修']);

    activeAccountId.set('account-internal-employee');
    const member = ready(await firstValueFrom(repository.listCaseTypes({ includeInactive: true })));
    expect([member.canManage, member.types.map((type) => type.name)]).toEqual([false, ['設備故障報修']]);

    activeAccountId.set('account-external-customer');
    expect(await firstValueFrom(repository.listCaseTypes())).toEqual({
      status: 'permission-denied', reason: 'case', message: CASE_FEATURE_DENIED_MESSAGE,
    });
  });

  it('checks a case type the way the API does: every field at once, 1–2,160 hours, a unique name and a group in use', async () => {
    const { repository } = mockRepository();

    expect(await firstValueFrom(repository.createCaseType({ name: ' ', description: '說'.repeat(501), defaultGroupId: '', defaultDueHours: 0, isActive: true })))
      .toEqual({
        status: 'validation-failed',
        message: CASE_TYPE_NAME_REQUIRED_MESSAGE,
        fieldErrors: {
          name: CASE_TYPE_NAME_REQUIRED_MESSAGE,
          description: CASE_TYPE_DESCRIPTION_TOO_LONG_MESSAGE,
          defaultGroupId: CASE_TYPE_GROUP_REQUIRED_MESSAGE,
          defaultDueHours: CASE_DUE_RANGE_MESSAGE,
        },
      });
    for (const [hours, accepted] of [[0, false], [1, true], [2160, true], [2161, false]] as const) {
      const result = await firstValueFrom(repository.createCaseType({ ...input(), name: `時限 ${hours}`, defaultDueHours: hours }));
      expect(result.status, String(hours)).toBe(accepted ? 'ready' : 'validation-failed');
    }
    const fieldOf = async (candidate: CaseTypeInput) => {
      const result = await firstValueFrom(repository.createCaseType(candidate));
      return result.status === 'validation-failed' ? result.fieldErrors : result.status;
    };
    expect(await fieldOf({ ...input(), name: '類'.repeat(41) })).toEqual({ name: CASE_TYPE_NAME_TOO_LONG_MESSAGE });
    expect(await fieldOf({ ...input(), name: '設備故障報修' })).toEqual({ name: CASE_TYPE_NAME_TAKEN_MESSAGE });
    expect(await fieldOf({ ...input(), name: '倉儲盤點差異' })).toEqual({ name: CASE_TYPE_NAME_TAKEN_MESSAGE });
    expect(await fieldOf(input('case-group-old-warehouse'))).toEqual({ defaultGroupId: CASE_TYPE_GROUP_ARCHIVED_MESSAGE });
    expect(await fieldOf(input('no-such-group'))).toEqual({ defaultGroupId: CASE_TYPE_GROUP_NOT_FOUND_MESSAGE });

    const created = ready(await firstValueFrom(repository.createCaseType({ ...input(), name: ' 冷藏庫異常 ', description: ' 溫度過高 ' })));
    expect([created.name, created.description, created.defaultGroup.name, created.defaultDueHours, created.isActive])
      .toEqual(['冷藏庫異常', '溫度過高', '設備組', 48, true]);
  });

  it('updates a type, keeps an archived group only while the type stays inactive, and refuses a non-manager', async () => {
    const { repository, activeAccountId } = mockRepository();

    const stocktake = 'case-type-stocktake';
    const kept = ready(await firstValueFrom(repository.updateCaseType(stocktake, {
      name: '倉儲盤點差異', description: '每季盤點', defaultGroupId: 'case-group-old-warehouse', defaultDueHours: 36, isActive: false,
    })));
    expect([kept.description, kept.defaultGroup.archived]).toEqual(['每季盤點', true]);
    const reactivated = await firstValueFrom(repository.updateCaseType(stocktake, {
      name: '倉儲盤點差異', description: '每季盤點', defaultGroupId: 'case-group-old-warehouse', defaultDueHours: 36, isActive: true,
    }));
    expect(reactivated).toMatchObject({ status: 'validation-failed', fieldErrors: { defaultGroupId: CASE_TYPE_GROUP_ARCHIVED_MESSAGE } });
    const moved = ready(await firstValueFrom(repository.updateCaseType(stocktake, {
      name: '倉儲盤點差異', description: '每季盤點', defaultGroupId: 'case-group-purchasing', defaultDueHours: 36, isActive: true,
    })));
    expect([moved.isActive, moved.defaultGroup.name]).toEqual([true, '採購組']);

    activeAccountId.set('account-internal-employee');
    const denied = { status: 'permission-denied', reason: 'organization-settings', message: CASE_SETTINGS_ADMIN_DENIED_MESSAGE };
    expect(await firstValueFrom(repository.createCaseType(input()))).toEqual(denied);
    expect(await firstValueFrom(repository.updateCaseType(stocktake, input()))).toEqual(denied);
    activeAccountId.set('account-smb-admin');
    expect(await firstValueFrom(repository.updateCaseType('no-such-type', input()))).toEqual(denied);
  });
});
