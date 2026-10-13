import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import {
  checkMemberDeactivation,
  checkSuccessor,
  MEMBER_DEACTIVATION_MESSAGES,
  requiredSuccessorPermissions,
  NO_HOLDINGS,
  SUCCESSOR_NOT_ELIGIBLE_MESSAGE,
  type DeactivatedMemberView,
  type MemberOffboardingView,
  type TeamView,
} from '../domain/team.model';
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

describe('member offboarding rules (same as the backend MemberOffboardingRules)', () => {
  it('needs manage-assistants for assistants or drafts and manage-data-sources for sources, nothing for work items', () => {
    expect(requiredSuccessorPermissions(NO_HOLDINGS)).toEqual([]);
    expect(requiredSuccessorPermissions({ ...NO_HOLDINGS, drafts: 1 })).toEqual(['manage-assistants']);
    expect(requiredSuccessorPermissions({ ...NO_HOLDINGS, databases: 1, assistants: 2 })).toEqual([
      'manage-assistants',
      'manage-data-sources',
    ]);
    expect(requiredSuccessorPermissions({ ...NO_HOLDINGS, openIssues: 3, openCases: 1, borrowedSources: 2 })).toEqual([]);
  });

  it('blocks self, an already deactivated member and the last manager in use, in that order', () => {
    expect(checkMemberDeactivation(ADMIN, { id: ADMIN, role: 'smb-admin', deactivated: true }, 0)).toBe('self');
    expect(checkMemberDeactivation(ADMIN, { id: EMPLOYEE, role: 'internal-employee', deactivated: true }, 0)).toBe('already-deactivated');
    expect(checkMemberDeactivation(EMPLOYEE, { id: ADMIN, role: 'smb-admin', deactivated: false }, 0)).toBe('last-admin');
    expect(checkMemberDeactivation(EMPLOYEE, { id: ADMIN, role: 'smb-admin', deactivated: false }, 1)).toBeNull();
  });

  it('accepts only internal members in use that hold every required permission', () => {
    const required = ['manage-assistants', 'manage-data-sources'] as const;
    expect(checkSuccessor({ role: 'internal-employee', deactivated: false, permissions: [...required] }, required).reason).toBeNull();
    expect(checkSuccessor({ role: 'external-customer', deactivated: false, permissions: [...required] }, required).reason).toBe('external-customer');
    expect(checkSuccessor({ role: 'smb-admin', deactivated: true, permissions: [...required] }, required).reason).toBe('deactivated');
    expect(checkSuccessor({ role: 'smb-admin', deactivated: false, permissions: ['manage-assistants'] }, required)).toEqual({
      reason: 'missing-permissions',
      missingPermissions: ['manage-data-sources'],
    });
  });
});

describe('MockDemoRepository member offboarding (issue #356)', () => {
  let repository: MockDemoRepository;
  let viewer: AccountId | null;

  const preview = async (member: AccountId) => firstValueFrom(repository.getMemberOffboarding(member));
  const deactivate = async (member: AccountId, successor: AccountId) =>
    firstValueFrom(repository.deactivateMember(member, successor));

  beforeEach(() => {
    viewer = ADMIN;
    repository = new MockDemoRepository(DEMO_SEED, {
      storage: createMemoryStorage(),
      now: () => new Date('2026-10-13T02:00:00.000Z'),
      viewer: () => viewer,
    });
  });

  it('previews what the employee holds and who may take it over', async () => {
    const view = dataOf<MemberOffboardingView>(await preview(EMPLOYEE));

    expect(view.block).toBeNull();
    expect(view.holdings).toMatchObject({ assistants: 0, knowledgeBases: 1, databases: 1 });
    expect(view.requiredPermissions).toEqual(['manage-data-sources']);
    expect(view.candidates.map((candidate) => [candidate.id, candidate.eligible, candidate.ineligibleReason])).toEqual([
      [ADMIN, true, null],
      [CUSTOMER, false, 'external-customer'],
    ]);
  });

  it('hands the knowledge base, the database and its data-manager seat to the successor', async () => {
    const result = dataOf<DeactivatedMemberView>(await deactivate(EMPLOYEE, ADMIN));

    expect(result.transferred).toMatchObject({ knowledgeBases: 1, databases: 1, assistants: 0 });
    expect(result.team.members.find((member) => member.id === EMPLOYEE)?.deactivatedAt).toBe('2026-10-13T02:00:00.000Z');

    // The successor now manages them through the ordinary methods.
    const detail = await firstValueFrom(repository.getKnowledgeBaseDetail('knowledge-staff-notes'));
    expect(detail.status).toBe('ready');
    const database = dataOf<{ access: { viewerCanManageAccess: boolean; dataManagers: readonly { id: string }[] } }>(
      await firstValueFrom(repository.getDatabaseDetail('database-staff-checkins')),
    );
    expect(database.access.viewerCanManageAccess).toBe(true);
    expect(database.access.dataManagers.map((manager) => manager.id)).toEqual([ADMIN]);

    // The deactivated persona can no longer be chosen at /login (listAccounts is who can sign in).
    expect(dataOf<readonly { id: string }[]>(repository.listAccounts()).map((account) => account.id)).toEqual([ADMIN, CUSTOMER]);
  });

  it('refuses self, an ineligible successor and a second deactivation, writing nothing', async () => {
    expect(await deactivate(ADMIN, EMPLOYEE)).toEqual({ status: 'validation-failed', message: MEMBER_DEACTIVATION_MESSAGES.self });
    expect(await deactivate(EMPLOYEE, CUSTOMER)).toEqual({ status: 'validation-failed', message: SUCCESSOR_NOT_ELIGIBLE_MESSAGE });
    expect(await deactivate(EMPLOYEE, EMPLOYEE)).toEqual({ status: 'validation-failed', message: SUCCESSOR_NOT_ELIGIBLE_MESSAGE });
    const team = dataOf<TeamView>(await firstValueFrom(repository.getTeam()));
    expect(team.members.every((member) => member.deactivatedAt === null)).toBe(true);

    await deactivate(EMPLOYEE, ADMIN);
    expect(await deactivate(EMPLOYEE, ADMIN)).toEqual({
      status: 'validation-failed',
      message: MEMBER_DEACTIVATION_MESSAGES['already-deactivated'],
    });
  });

  it('reactivates without bringing anything back', async () => {
    await deactivate(EMPLOYEE, ADMIN);

    const team = dataOf<TeamView>(await firstValueFrom(repository.reactivateMember(EMPLOYEE)));
    expect(team.members.find((member) => member.id === EMPLOYEE)?.deactivatedAt).toBeNull();
    expect(dataOf<readonly { id: string }[]>(repository.listAccounts()).map((account) => account.id)).toContain(EMPLOYEE);
    expect(dataOf<MemberOffboardingView>(await preview(EMPLOYEE)).holdings).toMatchObject({ knowledgeBases: 0, databases: 0 });

    expect((await firstValueFrom(repository.reactivateMember(EMPLOYEE))).status).toBe('validation-failed');
  });

  it('gives the same team permission-denied to accounts without manage-assistants and for unknown members', async () => {
    viewer = EMPLOYEE;
    const denied = await preview(CUSTOMER);
    expect(denied.status).toBe('permission-denied');
    expect(await deactivate(CUSTOMER, ADMIN)).toEqual(denied);

    viewer = ADMIN;
    expect(await preview('account-nobody')).toEqual(denied);
  });
});
