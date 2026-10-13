import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import {
  checkMemberDeactivation,
  checkSuccessor,
  isSuccessorRequired,
  mayManageMemberRole,
  MEMBER_DEACTIVATION_MESSAGES,
  requiredSuccessorPermissions,
  NO_HOLDINGS,
  NO_TRANSFERS,
  SUCCESSOR_NOT_ELIGIBLE_MESSAGE,
  SUCCESSOR_REQUIRED_MESSAGE,
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

  it('blocks self, an already deactivated member, a manager for a non-manager and the last manager in use, in that order', () => {
    const admin = { id: ADMIN, role: 'smb-admin' } as const;
    const employee = { id: EMPLOYEE, role: 'internal-employee' } as const;
    expect(checkMemberDeactivation(admin, { id: ADMIN, role: 'smb-admin', deactivated: true }, 0)).toBe('self');
    expect(checkMemberDeactivation(admin, { id: EMPLOYEE, role: 'internal-employee', deactivated: true }, 0)).toBe('already-deactivated');
    expect(checkMemberDeactivation(employee, { id: ADMIN, role: 'smb-admin', deactivated: true }, 0)).toBe('already-deactivated');
    // Issue #361: only a manager deactivates a manager.
    expect(checkMemberDeactivation(employee, { id: ADMIN, role: 'smb-admin', deactivated: false }, 1)).toBe('admin-required');
    expect(checkMemberDeactivation({ id: 'account-x', role: 'smb-admin' }, { id: ADMIN, role: 'smb-admin', deactivated: false }, 0)).toBe('last-admin');
    expect(checkMemberDeactivation({ id: 'account-x', role: 'smb-admin' }, { id: ADMIN, role: 'smb-admin', deactivated: false }, 1)).toBeNull();
    expect(checkMemberDeactivation(employee, { id: CUSTOMER, role: 'external-customer', deactivated: false }, 0)).toBeNull();

    expect(mayManageMemberRole('smb-admin', 'smb-admin')).toBe(true);
    expect(mayManageMemberRole('internal-employee', 'smb-admin')).toBe(false);
    expect(mayManageMemberRole('internal-employee', 'internal-employee')).toBe(true);
    expect(mayManageMemberRole('external-customer', 'external-customer')).toBe(true);
  });

  it('requires a successor only when there is something to hand over (issue #361)', () => {
    expect(isSuccessorRequired(NO_HOLDINGS)).toBe(false);
    expect(isSuccessorRequired({ ...NO_HOLDINGS, borrowedSources: 2 })).toBe(false);
    for (const key of ['assistants', 'drafts', 'knowledgeBases', 'databases', 'openIssues', 'openCases'] as const) {
      expect(isSuccessorRequired({ ...NO_HOLDINGS, [key]: 1 }), key).toBe(true);
    }
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
  const deactivate = async (member: AccountId, successor: AccountId | null) =>
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

interface Listed {
  readonly id: string;
  readonly deactivated: boolean;
}

/** What each picker offers (accounts in use), what is chosen, and who is listed flagged deactivated (issue #363). */
async function readPicks(repository: MockDemoRepository) {
  const platform = dataOf<{ platform: { candidates: readonly Listed[]; allowedAccountIds: readonly string[] } }>(
    await firstValueFrom(repository.getAssistantPublishing('assistant-customer-service')),
  ).platform;
  const detail = dataOf<{ shareTargets: readonly Listed[]; sharing: { sharedWithAccountIds: readonly string[] } }>(
    await firstValueFrom(repository.getKnowledgeBaseDetail('knowledge-product-guide')),
  );
  const access = dataOf<{ access: { candidates: readonly Listed[]; dataManagers: readonly { id: string }[] } }>(
    await firstValueFrom(repository.getDatabaseDetail('database-orders')),
  ).access;
  const offered = (listed: readonly Listed[]) => listed.filter((entry) => !entry.deactivated).map((entry) => entry.id);
  const flagged = (listed: readonly Listed[]) => listed.filter((entry) => entry.deactivated).map((entry) => entry.id);
  return {
    assistant: offered(platform.candidates),
    assistantShared: platform.allowedAccountIds,
    knowledge: offered(detail.shareTargets),
    knowledgeShared: detail.sharing.sharedWithAccountIds,
    dataManagerCandidates: offered(access.candidates),
    dataManagers: access.dataManagers.map((manager) => manager.id),
    flagged: { assistant: flagged(platform.candidates), knowledge: flagged(detail.shareTargets), dataManagers: flagged(access.candidates) },
  };
}

describe('MockDemoRepository offboarding rules of issue #361', () => {
  let repository: MockDemoRepository;
  let viewer: AccountId | null;

  const preview = async (member: AccountId) => dataOf<MemberOffboardingView>(await firstValueFrom(repository.getMemberOffboarding(member)));
  const deactivate = async (member: AccountId, successor: AccountId | null) =>
    firstValueFrom(repository.deactivateMember(member, successor));

  beforeEach(async () => {
    viewer = ADMIN;
    repository = new MockDemoRepository(DEMO_SEED, {
      storage: createMemoryStorage(),
      now: () => new Date('2026-10-14T02:00:00.000Z'),
      viewer: () => viewer,
    });
    // The employee may open the team settings, without being a manager.
    const team = dataOf<TeamView>(await firstValueFrom(repository.getTeam()));
    const employee = team.members.find((member) => member.id === EMPLOYEE);
    await firstValueFrom(repository.updateMemberPermissions(EMPLOYEE, [...(employee?.permissions ?? []), 'manage-assistants']));
  });

  it('lets only a manager deactivate or reactivate a manager', async () => {
    viewer = EMPLOYEE;
    expect((await preview(ADMIN)).block).toBe('admin-required');
    expect((await preview(ADMIN)).blockMessage).toBe(MEMBER_DEACTIVATION_MESSAGES['admin-required']);
    expect(await deactivate(ADMIN, EMPLOYEE)).toEqual({ status: 'validation-failed', message: MEMBER_DEACTIVATION_MESSAGES['admin-required'] });
    expect((await preview(CUSTOMER)).block).toBeNull();

    // A second manager, deactivated by the first: the employee cannot bring them back, the manager can.
    viewer = ADMIN;
    const created = await firstValueFrom(
      repository.createMember({ loginName: 'admin2', displayName: '第二位管理者', role: 'smb-admin', permissions: ['manage-assistants'] }),
    );
    const admin2 = dataOf<{ member: { id: AccountId } }>(created).member.id;
    expect((await deactivate(admin2, null)).status).toBe('ready');
    viewer = EMPLOYEE;
    expect(await firstValueFrom(repository.reactivateMember(admin2))).toEqual({
      status: 'validation-failed',
      message: MEMBER_DEACTIVATION_MESSAGES['admin-required'],
    });
    viewer = ADMIN;
    expect((await firstValueFrom(repository.reactivateMember(admin2))).status).toBe('ready');
  });

  it('deactivates a member holding nothing without a successor and refuses that for one holding something', async () => {
    expect((await preview(CUSTOMER)).successorRequired).toBe(false);
    expect((await preview(EMPLOYEE)).successorRequired).toBe(true);

    expect(await deactivate(EMPLOYEE, null)).toEqual({ status: 'validation-failed', message: SUCCESSOR_REQUIRED_MESSAGE });
    const result = dataOf<DeactivatedMemberView>(await deactivate(CUSTOMER, null));
    expect(result.transferred).toEqual(NO_TRANSFERS);
    expect(result.team.members.find((member) => member.id === CUSTOMER)?.deactivatedAt).toBe('2026-10-14T02:00:00.000Z');
    expect(result.team.members.find((member) => member.id === EMPLOYEE)?.deactivatedAt).toBeNull();
  });

  it('leaves deactivated accounts out of every picker, keeps what was chosen, and offers them again once reactivated', async () => {
    // Chosen for the customer before deactivation: the assistant and knowledge-base shares, a data-manager seat.
    await firstValueFrom(repository.updatePlatformSharing('assistant-customer-service', [CUSTOMER]));
    await firstValueFrom(repository.updateKnowledgeSharing('knowledge-product-guide', {
      scope: 'specific-accounts', sharedWithAccountIds: [CUSTOMER], allowOriginalDownload: false,
    }));
    await firstValueFrom(repository.updateDatabaseAccess('database-orders', [ADMIN, CUSTOMER]));
    expect((await deactivate(CUSTOMER, null)).status).toBe('ready');

    const picks = () => readPicks(repository);

    expect(await picks()).toEqual({
      assistant: [EMPLOYEE],
      assistantShared: [CUSTOMER],
      knowledge: [EMPLOYEE],
      knowledgeShared: [CUSTOMER],
      dataManagerCandidates: [ADMIN, EMPLOYEE],
      dataManagers: [ADMIN, CUSTOMER],
      // Listed, flagged, where still chosen (issue #363).
      flagged: { assistant: [CUSTOMER], knowledge: [CUSTOMER], dataManagers: [CUSTOMER] },
    });

    // Saving each picker with the existing choice sent back keeps it.
    expect((await firstValueFrom(repository.updatePlatformSharing('assistant-customer-service', [CUSTOMER, EMPLOYEE]))).status).toBe('ready');
    expect((await firstValueFrom(repository.updateDatabaseAccess('database-orders', [ADMIN, CUSTOMER]))).status).toBe('ready');
    expect((await picks()).assistantShared).toEqual([EMPLOYEE, CUSTOMER]);
    expect((await picks()).dataManagers).toEqual([ADMIN, CUSTOMER]);

    viewer = ADMIN;
    expect((await firstValueFrom(repository.reactivateMember(CUSTOMER))).status).toBe('ready');
    const again = await picks();
    expect(again.assistant).toContain(CUSTOMER);
    expect(again.knowledge).toContain(CUSTOMER);
    expect(again.dataManagerCandidates).toContain(CUSTOMER);
    expect(again.flagged).toEqual({ assistant: [], knowledge: [], dataManagers: [] });
  });

  it('shows the owner a deactivated share target flagged until removed, and nobody else (issue #363)', async () => {
    await firstValueFrom(repository.updatePlatformSharing('assistant-customer-service', [EMPLOYEE, CUSTOMER]));
    await firstValueFrom(repository.updateKnowledgeSharing('knowledge-product-guide', {
      scope: 'specific-accounts', sharedWithAccountIds: [EMPLOYEE, CUSTOMER], allowOriginalDownload: false,
    }));
    await firstValueFrom(repository.updateDatabaseAccess('database-orders', [ADMIN, EMPLOYEE, CUSTOMER]));
    expect((await readPicks(repository)).flagged).toEqual({ assistant: [], knowledge: [], dataManagers: [] });

    expect((await deactivate(CUSTOMER, null)).status).toBe('ready');
    expect((await readPicks(repository)).flagged).toEqual({ assistant: [CUSTOMER], knowledge: [CUSTOMER], dataManagers: [CUSTOMER] });

    // Anybody else: the publishing and knowledge-base pages are the owner's only; a data manager gets no candidates.
    viewer = EMPLOYEE;
    expect((await firstValueFrom(repository.getAssistantPublishing('assistant-customer-service'))).status).toBe('permission-denied');
    expect((await firstValueFrom(repository.getKnowledgeBaseDetail('knowledge-product-guide'))).status).toBe('permission-denied');
    const seen = dataOf<{ access: { viewerIsDataManager: boolean; candidates: readonly unknown[] } }>(
      await firstValueFrom(repository.getDatabaseDetail('database-orders')),
    ).access;
    expect(seen.viewerIsDataManager).toBe(true);
    expect(seen.candidates).toEqual([]);

    // The owner removes it in each picker; it is listed no more.
    viewer = ADMIN;
    expect((await firstValueFrom(repository.updatePlatformSharing('assistant-customer-service', [EMPLOYEE]))).status).toBe('ready');
    expect((await firstValueFrom(repository.updateKnowledgeSharing('knowledge-product-guide', {
      scope: 'specific-accounts', sharedWithAccountIds: [EMPLOYEE], allowOriginalDownload: false,
    }))).status).toBe('ready');
    expect((await firstValueFrom(repository.updateDatabaseAccess('database-orders', [ADMIN, EMPLOYEE]))).status).toBe('ready');
    expect(await readPicks(repository)).toEqual({
      assistant: [EMPLOYEE],
      assistantShared: [EMPLOYEE],
      knowledge: [EMPLOYEE],
      knowledgeShared: [EMPLOYEE],
      dataManagerCandidates: [ADMIN, EMPLOYEE],
      dataManagers: [ADMIN, EMPLOYEE],
      flagged: { assistant: [], knowledge: [], dataManagers: [] },
    });
  });

  it('refuses a deactivated account as a new data manager or platform share', async () => {
    expect((await deactivate(CUSTOMER, null)).status).toBe('ready');
    expect(await firstValueFrom(repository.updateDatabaseAccess('database-orders', [ADMIN, CUSTOMER]))).toMatchObject({
      status: 'validation-failed',
    });
    // 客服助理 was already shared with the customer (seed); this one never was.
    expect((await firstValueFrom(repository.updatePlatformSharing('assistant-internal-onboarding', [EMPLOYEE, CUSTOMER]))).status)
      .toBe('validation-failed');
    expect((await firstValueFrom(repository.updatePlatformSharing('assistant-customer-service', [CUSTOMER]))).status).toBe('ready');
  });
});
