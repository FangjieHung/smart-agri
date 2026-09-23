import { beforeEach, describe, expect, it } from 'vitest';
import type { AccountId, AccountPermission } from '../domain/account.model';
import type { TeamView } from '../domain/team.model';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';

const ADMIN: AccountId = 'account-smb-admin';
const EMPLOYEE: AccountId = 'account-internal-employee';
const CUSTOMER: AccountId = 'account-external-customer';

function dataOf<T>(result: { status: string }): T {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return (result as unknown as { data: T }).data;
}

function permissionsOf(team: TeamView, accountId: AccountId): readonly AccountPermission[] {
  const member = team.members.find((candidate) => candidate.id === accountId);
  if (member === undefined) throw new Error(`missing member ${accountId}`);
  return member.permissions;
}

describe('MockDemoRepository team management', () => {
  let storage: ReturnType<typeof createMemoryStorage>;
  let repository: MockDemoRepository;

  beforeEach(() => {
    storage = createMemoryStorage();
    repository = new MockDemoRepository(DEMO_SEED, {
      storage,
      now: () => new Date('2026-09-23T02:00:00.000Z'),
    });
  });

  it('lists every demo persona with its role and what each permission actually does', () => {
    const team = dataOf<TeamView>(repository.getTeam(ADMIN));

    expect(team.members.map((member) => member.id)).toEqual([ADMIN, EMPLOYEE, CUSTOMER]);
    expect(team.members[0]).toMatchObject({ roleLabel: '管理者', isViewer: true });
    expect(team.members[1]).toMatchObject({ roleLabel: '內部同仁', isViewer: false });
    // 每個權限都要說明它是不是真的被檢查，畫面才不會假裝。
    expect(team.permissions.map((permission) => permission.id)).toEqual([
      'manage-assistants',
      'manage-data-sources',
      'manage-publishing',
      'read-consented-submissions',
      'submit-authorized-forms',
      'use-shared-assistants',
      'read-own-tracking',
    ]);
    expect(team.permissions.filter((permission) => !permission.enforced).map((p) => p.id)).toEqual([
      'use-shared-assistants',
      'read-own-tracking',
    ]);
    expect(team.savedAt).toBeNull();
  });

  it('refuses the team screen to accounts without manage-assistants and leaks no names', () => {
    for (const accountId of [EMPLOYEE, CUSTOMER]) {
      const refused = repository.getTeam(accountId);
      expect(refused).toMatchObject({ status: 'permission-denied', reason: 'team' });
      if (refused.status === 'permission-denied') {
        expect(refused.message).not.toContain('安心商行管理者');
      }
      expect(repository.updateMemberPermissions(accountId, ADMIN, [])).toMatchObject({
        status: 'permission-denied',
        reason: 'team',
      });
    }
  });

  it('persists a member’s permissions under the sme-demo: convention and reloads them', () => {
    const updated = repository.updateMemberPermissions(ADMIN, EMPLOYEE, [
      'use-shared-assistants',
      'manage-publishing',
    ]);

    expect(updated.status).toBe('ready');
    expect(permissionsOf(dataOf<TeamView>(updated), EMPLOYEE)).toEqual([
      // 依 ACCOUNT_PERMISSIONS 的順序正規化，而不是送進來的順序。
      'manage-publishing',
      'use-shared-assistants',
    ]);
    expect(storage.getItem('sme-demo:team-permissions')).toContain('manage-publishing');

    const reloaded = new MockDemoRepository(DEMO_SEED, { storage });
    expect(permissionsOf(dataOf<TeamView>(reloaded.getTeam(ADMIN)), EMPLOYEE)).toEqual([
      'manage-publishing',
      'use-shared-assistants',
    ]);
    expect(dataOf<TeamView>(reloaded.getTeam(ADMIN)).savedAt).not.toBeNull();
  });

  it('refuses to let the acting admin remove their own manage-assistants', () => {
    const result = repository.updateMemberPermissions(ADMIN, ADMIN, ['manage-data-sources']);

    expect(result).toMatchObject({ status: 'validation-failed' });
    if (result.status === 'validation-failed') {
      expect(result.message).toContain('不能移除自己');
    }
    expect(permissionsOf(dataOf<TeamView>(repository.getTeam(ADMIN)), ADMIN)).toContain(
      'manage-assistants',
    );
    expect(dataOf<TeamView>(repository.getTeam(ADMIN)).members[0].lockedPermissions).toEqual([
      'manage-assistants',
    ]);
  });

  it('rejects unknown permission values without writing anything', () => {
    const result = repository.updateMemberPermissions(ADMIN, EMPLOYEE, [
      'not-a-permission' as AccountPermission,
    ]);

    expect(result).toMatchObject({ status: 'validation-failed' });
    expect(storage.getItem('sme-demo:team-permissions')).toBeNull();
  });

  it('refuses an unknown member with the same message it uses for no permission', () => {
    const unknown = repository.updateMemberPermissions(ADMIN, 'account-ghost' as AccountId, []);
    const noPermission = repository.updateMemberPermissions(EMPLOYEE, ADMIN, []);

    expect(unknown).toMatchObject({ status: 'permission-denied', reason: 'team' });
    if (unknown.status === 'permission-denied' && noPermission.status === 'permission-denied') {
      expect(unknown.message).toBe(noPermission.message);
    }
  });

  it('lists accounts with the edited permissions everywhere, not just on the team screen', () => {
    repository.updateMemberPermissions(ADMIN, EMPLOYEE, []);

    const accounts = dataOf<readonly { id: AccountId; permissions: readonly AccountPermission[] }[]>(
      repository.listAccounts(),
    );
    expect(accounts.find((account) => account.id === EMPLOYEE)?.permissions).toEqual([]);
  });
});
