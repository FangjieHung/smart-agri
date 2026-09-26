import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom, type Observable } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';
import type { components } from '../api/api-schema';
import type { AccountId, AccountPermission } from '../domain/account.model';
import type { TeamView } from '../domain/team.model';
import type { RepositoryView } from './demo-repository';
import { DEMO_SEED, type DemoSeed } from './demo-seed';
import {
  API_TEAM_PATH,
  apiMemberPermissionsPath,
  HybridDemoRepository,
  type ApiViewerPermissions,
} from './hybrid-demo-repository';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository, type MockDemoRepositoryOptions } from './mock-demo-repository';
import { API_DEMO_REPOSITORY_FACTORY, DEMO_REPOSITORY } from './tokens';

type TeamResponse = components['schemas']['TeamResponse'];

const ADMIN_ID = '0199a000-0000-7000-8000-00000000000a';
const EMPLOYEE_ID = '0199a000-0000-7000-8000-000000000001';
const CUSTOMER_ID = '0199a000-0000-7000-8000-000000000002';
// 同組織第二位 internal-employee：直接插進團隊回應，id 與 EMPLOYEE_ID 不同，
// 用來驗證改權限不會透過角色轉換而互相覆蓋（issue #36）。
const EMPLOYEE_2_ID = '0199a000-0000-7000-8000-000000000003';
const CHECKINS = 'database-staff-checkins';

const ALL_ADMIN: AccountPermission[] = [
  'manage-assistants',
  'manage-data-sources',
  'manage-publishing',
  'read-consented-submissions',
  'submit-authorized-forms',
  'use-shared-assistants',
  'read-own-tracking',
];

/** API 依成員 id 排序，所以順序與 mock 不同：客戶、同仁、管理者。 */
function teamResponse(employeePermissions: AccountPermission[], savedAt: string | null = null): TeamResponse {
  return {
    members: [
      {
        id: EMPLOYEE_ID,
        displayName: '安心商行客服同仁',
        role: 'internal-employee',
        permissions: employeePermissions,
        lockedPermissions: [],
      },
      {
        id: CUSTOMER_ID,
        displayName: '安心商行客戶',
        role: 'external-customer',
        permissions: ['use-shared-assistants', 'submit-authorized-forms', 'read-own-tracking'],
        lockedPermissions: [],
      },
      {
        id: ADMIN_ID,
        displayName: '安心商行管理者',
        role: 'smb-admin',
        permissions: ALL_ADMIN,
        lockedPermissions: ['manage-assistants'],
      },
    ],
    savedAt,
  };
}

/** 兩位 internal-employee（同角色）＋一位 admin：驗收條件要求的「同角色多人」情境。 */
function teamResponseWithTwoSameRoleMembers(): TeamResponse {
  return {
    members: [
      {
        id: EMPLOYEE_ID,
        displayName: '安心商行客服同仁 A',
        role: 'internal-employee',
        permissions: ['use-shared-assistants'],
        lockedPermissions: [],
      },
      {
        id: EMPLOYEE_2_ID,
        displayName: '安心商行客服同仁 B',
        role: 'internal-employee',
        permissions: ['read-consented-submissions'],
        lockedPermissions: [],
      },
      {
        id: ADMIN_ID,
        displayName: '安心商行管理者',
        role: 'smb-admin',
        permissions: ALL_ADMIN,
        lockedPermissions: ['manage-assistants'],
      },
    ],
    savedAt: null,
  };
}

const SEED_EMPLOYEE: AccountPermission[] = [
  'read-consented-submissions',
  'use-shared-assistants',
];

function setUp(
  viewer: AccountId | null = 'account-smb-admin',
  me: ApiViewerPermissions | null = {
    accountId: ADMIN_ID,
    demoAccountId: 'account-smb-admin',
    permissions: ALL_ADMIN,
  },
) {
  TestBed.configureTestingModule({
    providers: [provideHttpClient(), provideHttpClientTesting()],
  });
  const repository = new HybridDemoRepository(
    DEMO_SEED,
    { storage: createMemoryStorage(), viewer: () => viewer },
    { http: TestBed.inject(HttpClient), viewerPermissions: () => me },
  );
  return { repository, controller: TestBed.inject(HttpTestingController) };
}

/** 訂閱並回傳結果的 Promise；HTTP 由測試自行 flush。 */
function pending<T>(source: Observable<T>): Promise<T> {
  return firstValueFrom(source);
}

function dataOf(result: RepositoryView<TeamView> | { status: string }): TeamView {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return (result as { data: TeamView }).data;
}

describe('HybridDemoRepository', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('reads the team over HTTP and keeps the API’s real account ids, in mock role order', async () => {
    const { repository, controller } = setUp();
    const result = pending(repository.getTeam());

    controller
      .expectOne({ method: 'GET', url: API_TEAM_PATH })
      .flush(teamResponse(['use-shared-assistants', 'read-consented-submissions'], '2026-09-25T01:00:00Z'));

    const team = dataOf(await result);
    // id 就是 API 回傳的帳號 GUID，不再換回 Demo 身分。
    expect(team.members.map((member) => member.id)).toEqual([ADMIN_ID, EMPLOYEE_ID, CUSTOMER_ID]);
    expect(team.members[0]).toMatchObject({
      displayName: '安心商行管理者',
      roleLabel: '管理者',
      isViewer: true,
      lockedPermissions: ['manage-assistants'],
    });
    // 權限依 ACCOUNT_PERMISSIONS 的順序呈現，與 mock 相同。
    expect(team.members[1]).toMatchObject({
      isViewer: false,
      permissions: ['read-consented-submissions', 'use-shared-assistants'],
    });
    expect(team.permissions).toHaveLength(7);
    expect(team.savedAt).toBe('2026-09-25T01:00:00Z');
  });

  it('renders two members with the same role as two separate rows, each keeping its own id', async () => {
    const { repository, controller } = setUp();
    const result = pending(repository.getTeam());

    controller.expectOne(API_TEAM_PATH).flush(teamResponseWithTwoSameRoleMembers());

    const team = dataOf(await result);
    const employees = team.members.filter((member) => member.role === 'internal-employee');
    expect(employees).toHaveLength(2);
    expect(employees.map((member) => member.id)).toEqual([EMPLOYEE_ID, EMPLOYEE_2_ID]);
    expect(employees[0].permissions).toEqual(['use-shared-assistants']);
    expect(employees[1].permissions).toEqual(['read-consented-submissions']);
  });

  it('sends the edited member’s own GUID in the PUT url, leaving a same-role member untouched', async () => {
    const { repository, controller } = setUp();
    const first = pending(repository.getTeam());
    controller.expectOne(API_TEAM_PATH).flush(teamResponseWithTwoSameRoleMembers());
    await first;

    const saved = pending(repository.updateMemberPermissions(EMPLOYEE_ID, ['manage-data-sources']));
    const request = controller.expectOne({ method: 'PUT', url: apiMemberPermissionsPath(EMPLOYEE_ID) });
    expect(request.request.url).toBe(apiMemberPermissionsPath(EMPLOYEE_ID));
    expect(request.request.url).not.toContain(EMPLOYEE_2_ID);
    request.flush({
      members: [
        {
          id: EMPLOYEE_ID,
          displayName: '安心商行客服同仁 A',
          role: 'internal-employee',
          permissions: ['manage-data-sources'],
          lockedPermissions: [],
        },
        {
          id: EMPLOYEE_2_ID,
          displayName: '安心商行客服同仁 B',
          role: 'internal-employee',
          permissions: ['read-consented-submissions'],
          lockedPermissions: [],
        },
      ],
      savedAt: '2026-09-26T00:00:00Z',
    });

    const team = dataOf(await saved);
    const memberA = team.members.find((member) => member.id === EMPLOYEE_ID);
    const memberB = team.members.find((member) => member.id === EMPLOYEE_2_ID);
    expect(memberA?.permissions).toEqual(['manage-data-sources']);
    // B 從未被送進任何請求，權限維持原樣。
    expect(memberB?.permissions).toEqual(['read-consented-submissions']);
  });

  it('turns 403 team into the same permission-denied the mock returns', async () => {
    const { repository, controller } = setUp('account-internal-employee');
    const result = pending(repository.getTeam());

    controller.expectOne(API_TEAM_PATH).flush(
      { reason: 'team', message: '只有可管理助理與團隊的帳號可以查看或變更團隊成員權限。' },
      { status: 403, statusText: 'Forbidden' },
    );

    expect(await result).toEqual({
      status: 'permission-denied',
      reason: 'team',
      message: '只有可管理助理與團隊的帳號可以查看或變更團隊成員權限。',
    });
  });

  it('keeps password-change-required as its own reason', async () => {
    const { repository, controller } = setUp();
    const result = pending(repository.getTeam());

    controller.expectOne(API_TEAM_PATH).flush(
      { reason: 'password-change-required', message: '請先設定新密碼，才能使用其他功能。' },
      { status: 403, statusText: 'Forbidden' },
    );

    expect(await result).toMatchObject({
      status: 'permission-denied',
      reason: 'password-change-required',
    });
  });

  it('turns 422 self-lock into validation-failed and does not reload', async () => {
    const { repository, controller } = setUp();
    const team = pending(repository.getTeam());
    controller.expectOne(API_TEAM_PATH).flush(teamResponse(SEED_EMPLOYEE));
    await team;

    const result = pending(repository.updateMemberPermissions(ADMIN_ID, ['manage-data-sources']));
    const request = controller.expectOne({
      method: 'PUT',
      url: apiMemberPermissionsPath(ADMIN_ID),
    });
    expect(request.request.body).toEqual({ permissions: ['manage-data-sources'] });
    const message =
      '不能移除自己的「管理助理與團隊」權限：移除後就打不開團隊設定，也沒有別的入口可以加回來。';
    request.flush(
      { message, errors: { permissions: [message] } },
      { status: 422, statusText: 'Unprocessable Entity' },
    );

    expect(await result).toEqual({ status: 'validation-failed', message });
    controller.expectNone(API_TEAM_PATH);
  });

  it('saves over HTTP and a subsequent read reflects the update', async () => {
    const { repository, controller } = setUp();
    const first = pending(repository.getTeam());
    controller.expectOne(API_TEAM_PATH).flush(teamResponse(SEED_EMPLOYEE));
    await first;

    const saved = pending(repository.updateMemberPermissions(EMPLOYEE_ID, ['use-shared-assistants']));
    controller
      .expectOne({ method: 'PUT', url: apiMemberPermissionsPath(EMPLOYEE_ID) })
      .flush(teamResponse(['use-shared-assistants'], '2026-09-25T02:00:00Z'));
    expect(dataOf(await saved).members.find((member) => member.id === EMPLOYEE_ID)?.permissions).toEqual([
      'use-shared-assistants',
    ]);

    // 團隊面板儲存後會重新讀取。
    const reloaded = pending(repository.getTeam());
    controller
      .expectOne({ method: 'GET', url: API_TEAM_PATH })
      .flush(teamResponse(['use-shared-assistants'], '2026-09-25T02:00:00Z'));
    expect(dataOf(await reloaded).savedAt).toBe('2026-09-25T02:00:00Z');
  });

  it('updating a member does not change what the still-mock feature areas see for other Demo identities', async () => {
    // 這個 repository 不再把「其他成員」的權限經由角色換算成 Demo 身分（issue #36），
    // 所以編輯 internal-employee 的權限不會影響 mock 區 `account-internal-employee` 的檢查結果，
    // 那一段行為改由 seed 的固定權限決定，直到該功能區也換成 API 為止。
    const { repository, controller } = setUp();
    const first = pending(repository.getTeam());
    controller.expectOne(API_TEAM_PATH).flush(teamResponse(SEED_EMPLOYEE));
    await first;
    expect(repository.getDatabaseTracking('account-internal-employee', CHECKINS).status).toBe('ready');

    const saved = pending(repository.updateMemberPermissions(EMPLOYEE_ID, ['use-shared-assistants']));
    controller
      .expectOne({ method: 'PUT', url: apiMemberPermissionsPath(EMPLOYEE_ID) })
      .flush(teamResponse(['use-shared-assistants'], '2026-09-25T02:00:00Z'));
    await saved;

    expect(repository.getDatabaseTracking('account-internal-employee', CHECKINS).status).toBe('ready');
  });

  it('uses /me for the viewer’s own permissions before any team read', () => {
    const { repository } = setUp('account-internal-employee', {
      accountId: EMPLOYEE_ID,
      demoAccountId: 'account-internal-employee',
      permissions: ['use-shared-assistants'],
    });

    expect(repository.getDatabaseTracking('account-internal-employee', CHECKINS)).toMatchObject({
      status: 'permission-denied',
      reason: 'database-records',
    });
  });

  it('turns a PUT to an unknown or foreign member id into the same permission-denied as no permission', async () => {
    const { repository, controller } = setUp();
    const result = pending(repository.updateMemberPermissions('11111111-1111-4111-8111-111111111111', []));

    controller.expectOne({ method: 'PUT', url: apiMemberPermissionsPath('11111111-1111-4111-8111-111111111111') }).flush(
      { reason: 'team', message: '只有可管理助理與團隊的帳號可以查看或變更團隊成員權限。' },
      { status: 403, statusText: 'Forbidden' },
    );

    expect(await result).toMatchObject({ status: 'permission-denied', reason: 'team' });
  });

  it('leaves server errors to the screen instead of pretending to be a result', async () => {
    const { repository, controller } = setUp();
    const result = pending(repository.getTeam()).catch((error: unknown) => error);

    controller.expectOne(API_TEAM_PATH).flush(null, { status: 503, statusText: 'Unavailable' });

    expect(await result).toMatchObject({ status: 503 });
  });

  it('delegates every other method to the mock', () => {
    const { repository } = setUp();

    expect(repository.listAssistantTemplates().status).toBe('ready');
    expect(repository.listUsableAssistants('account-smb-admin').status).toBe('ready');
  });
});

describe('DEMO_REPOSITORY factory', () => {
  it('is the plain mock when API mode provides nothing', () => {
    TestBed.configureTestingModule({});
    expect(TestBed.inject(DEMO_REPOSITORY)).toBeInstanceOf(MockDemoRepository);
    expect(TestBed.inject(DEMO_REPOSITORY)).not.toBeInstanceOf(HybridDemoRepository);
  });

  it('uses the API-mode repository when provideApiMode registers one', () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: API_DEMO_REPOSITORY_FACTORY,
          useFactory: () => {
            const http = inject(HttpClient);
            return (seed: DemoSeed, options: MockDemoRepositoryOptions) =>
              new HybridDemoRepository(seed, options, { http, viewerPermissions: () => null });
          },
        },
      ],
    });
    expect(TestBed.inject(DEMO_REPOSITORY)).toBeInstanceOf(HybridDemoRepository);
  });
});
