import { firstValueFrom } from 'rxjs';
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
  /** 非同步契約不再傳 viewer，改由工作階段提供；測試直接改這個值切換身分。 */
  let viewer: AccountId | null;

  const getTeam = (target: MockDemoRepository = repository) => firstValueFrom(target.getTeam());
  const update = (member: AccountId, permissions: readonly AccountPermission[]) =>
    firstValueFrom(repository.updateMemberPermissions(member, permissions));

  beforeEach(() => {
    storage = createMemoryStorage();
    viewer = ADMIN;
    repository = new MockDemoRepository(DEMO_SEED, {
      storage,
      now: () => new Date('2026-09-23T02:00:00.000Z'),
      viewer: () => viewer,
    });
  });

  it('lists every demo persona with its role and what each permission actually does', async () => {
    const team = dataOf<TeamView>(await getTeam());

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

  it('refuses the team screen to accounts without manage-assistants and leaks no names', async () => {
    for (const accountId of [EMPLOYEE, CUSTOMER, null]) {
      viewer = accountId;
      const refused = await getTeam();
      expect(refused).toMatchObject({ status: 'permission-denied', reason: 'team' });
      if (refused.status === 'permission-denied') {
        expect(refused.message).not.toContain('安心商行管理者');
      }
      expect(await update(ADMIN, [])).toMatchObject({
        status: 'permission-denied',
        reason: 'team',
      });
    }
  });

  it('does nothing until subscribed, like the HTTP adapter', async () => {
    const pending = repository.updateMemberPermissions(EMPLOYEE, []);
    expect(storage.getItem('sme-demo:team-permissions')).toBeNull();

    await firstValueFrom(pending);
    expect(storage.getItem('sme-demo:team-permissions')).not.toBeNull();
  });

  it('persists a member’s permissions under the sme-demo: convention and reloads them', async () => {
    const updated = await update(EMPLOYEE, [
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

    const reloaded = new MockDemoRepository(DEMO_SEED, { storage, viewer: () => ADMIN });
    const team = dataOf<TeamView>(await getTeam(reloaded));
    expect(permissionsOf(team, EMPLOYEE)).toEqual([
      'manage-publishing',
      'use-shared-assistants',
    ]);
    expect(team.savedAt).not.toBeNull();
  });

  it('refuses to let the acting admin remove their own manage-assistants', async () => {
    const result = await update(ADMIN, ['manage-data-sources']);

    expect(result).toMatchObject({ status: 'validation-failed' });
    if (result.status === 'validation-failed') {
      expect(result.message).toContain('不能移除自己');
    }
    const team = dataOf<TeamView>(await getTeam());
    expect(permissionsOf(team, ADMIN)).toContain('manage-assistants');
    expect(team.members[0].lockedPermissions).toEqual(['manage-assistants']);
  });

  it('rejects unknown permission values without writing anything', async () => {
    const result = await update(EMPLOYEE, ['not-a-permission' as AccountPermission]);

    expect(result).toMatchObject({ status: 'validation-failed' });
    expect(storage.getItem('sme-demo:team-permissions')).toBeNull();
  });

  it('refuses an unknown member with the same message it uses for no permission', async () => {
    const unknown = await update('account-ghost' as AccountId, []);
    viewer = EMPLOYEE;
    const noPermission = await update(ADMIN, []);

    expect(unknown).toMatchObject({ status: 'permission-denied', reason: 'team' });
    expect(noPermission).toMatchObject({ status: 'permission-denied', reason: 'team' });
    if (unknown.status === 'permission-denied' && noPermission.status === 'permission-denied') {
      expect(unknown.message).toBe(noPermission.message);
    }
  });

  it('lists accounts with the edited permissions everywhere, not just on the team screen', async () => {
    await update(EMPLOYEE, []);

    const accounts = dataOf<readonly { id: AccountId; permissions: readonly AccountPermission[] }[]>(
      repository.listAccounts(),
    );
    expect(accounts.find((account) => account.id === EMPLOYEE)?.permissions).toEqual([]);
  });

  it('uses the API permissions in API mode and ignores team edits made in mock mode', async () => {
    await update(EMPLOYEE, ['read-consented-submissions']);
    const apiMode = new MockDemoRepository(DEMO_SEED, {
      storage,
      viewer: () => EMPLOYEE,
      accountsSource: () => ({ [EMPLOYEE]: ['use-shared-assistants'] }),
    });

    const accounts = dataOf<readonly { id: AccountId; permissions: readonly AccountPermission[] }[]>(
      apiMode.listAccounts(),
    );
    expect(accounts.find((account) => account.id === EMPLOYEE)?.permissions).toEqual([
      'use-shared-assistants',
    ]);
    // 沒有列出的帳號沿用 seed，而不是這台瀏覽器改過的值。
    expect(accounts.find((account) => account.id === ADMIN)?.permissions).toEqual(
      DEMO_SEED.accounts.find((account) => account.id === ADMIN)?.permissions,
    );
  });
});
