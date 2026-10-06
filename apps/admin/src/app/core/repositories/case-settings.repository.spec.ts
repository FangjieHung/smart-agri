import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import type { CaseGroupView } from '../domain/case-settings.model';
import { DemoSessionService } from '../session/demo-session.service';
import {
  CASE_FEATURE_DENIED_MESSAGE,
  CASE_GROUP_MEMBER_NOT_ELIGIBLE_MESSAGE,
  CASE_GROUP_NAME_REQUIRED_MESSAGE,
  CASE_GROUP_NAME_TAKEN_MESSAGE,
  CASE_GROUP_NAME_TOO_LONG_MESSAGE,
  CASE_SETTINGS_ADMIN_DENIED_MESSAGE,
  CaseSettingsRepository,
} from './case-settings.repository';

/** Demo 模式（沒有 API）：與後端相同的契約與檢查（issue #246）。Hybrid 見 `case-settings.repository-hybrid.spec.ts`。 */
function mockRepository(accountId: AccountId | null = 'account-smb-admin') {
  const activeAccountId = signal<AccountId | null>(accountId);
  TestBed.configureTestingModule({ providers: [{ provide: DemoSessionService, useValue: { activeAccountId } }] });
  return { repository: TestBed.inject(CaseSettingsRepository), activeAccountId };
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

    const archived = ready(await firstValueFrom(repository.setCaseGroupArchived('case-group-equipment', true)));
    expect([archived.archived, archived.archivedAt !== null]).toEqual([true, true]);
    expect(ready(await firstValueFrom(repository.listCaseGroups())).groups.map((group) => group.name)).toEqual(['採購組']);

    const back = ready(await firstValueFrom(repository.setCaseGroupArchived('case-group-equipment', false)));
    expect([back.archived, back.archivedAt]).toEqual([false, null]);
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
});
