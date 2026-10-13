import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import { DemoSessionService } from '../session/demo-session.service';
import { CASE_GROUP_DEACTIVATED_MEMBER_MESSAGE, CaseSettingsRepository } from './case-settings.repository';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';
import { DEMO_REPOSITORY } from './tokens';

const ADMIN: AccountId = 'account-smb-admin';
const EMPLOYEE: AccountId = 'account-internal-employee';

/**
 * Issue #361 in Demo mode: a deactivated account (kept by the Demo repository) is not offered as a
 * case-group member and cannot be newly added, keeps the memberships it has, is not counted among the
 * members who handle a database's automatic cases, and is offered again once reactivated.
 */
function setUp() {
  const activeAccountId = signal<AccountId | null>(ADMIN);
  const demo = new MockDemoRepository(DEMO_SEED, { storage: createMemoryStorage(), viewer: () => activeAccountId() });
  TestBed.configureTestingModule({
    providers: [
      { provide: DemoSessionService, useValue: { activeAccountId } },
      { provide: DEMO_REPOSITORY, useValue: demo },
    ],
  });
  return { repository: TestBed.inject(CaseSettingsRepository), demo };
}

function ready<T>(result: { status: string; data?: T }): T {
  if (result.status !== 'ready' || result.data === undefined) throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

describe('CaseSettingsRepository with a deactivated member (mock, issue #361)', () => {
  it('leaves them out of the candidates and the auto-case count, keeps their membership, and brings them back', async () => {
    const { repository, demo } = setUp();
    const memberCount = async () =>
      ready(await firstValueFrom(repository.getDatabaseAutoCase('database-customer-records'))).options
        .find((option) => option.group.id === 'case-group-equipment')?.memberCount;
    expect(await memberCount()).toBe(1);

    expect((await firstValueFrom(demo.deactivateMember(EMPLOYEE, ADMIN))).status).toBe('ready');

    const list = ready(await firstValueFrom(repository.listCaseGroups()));
    expect(list.candidates.map((candidate) => candidate.id)).toEqual([ADMIN]);
    expect(list.groups.find((group) => group.id === 'case-group-equipment')?.members.map((member) => member.id)).toEqual([EMPLOYEE]);
    expect(await memberCount()).toBe(0);

    // Kept when the list is saved with them in it; refused as a new member elsewhere.
    expect((await firstValueFrom(repository.updateCaseGroupMembers('case-group-equipment', [EMPLOYEE, ADMIN]))).status).toBe('ready');
    expect(await firstValueFrom(repository.updateCaseGroupMembers('case-group-purchasing', [ADMIN, EMPLOYEE]))).toEqual({
      status: 'validation-failed',
      message: CASE_GROUP_DEACTIVATED_MEMBER_MESSAGE,
    });

    expect((await firstValueFrom(demo.reactivateMember(EMPLOYEE))).status).toBe('ready');
    expect(ready(await firstValueFrom(repository.listCaseGroups())).candidates.map((candidate) => candidate.id)).toEqual([ADMIN, EMPLOYEE]);
    expect(await memberCount()).toBe(2);
  });
});
