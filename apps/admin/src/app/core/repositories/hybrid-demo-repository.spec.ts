import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom, type Observable } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';
import type { components } from '../api/api-schema';
import type { AccountId, AccountPermission } from '../domain/account.model';
import { createEmptyAssistantDraft } from '../domain/assistant-draft.model';
import type { TeamView } from '../domain/team.model';
import type { RepositoryView } from './demo-repository';
import { DEMO_SEED, type DemoSeed } from './demo-seed';
import {
  API_CREATE_MEMBER_PATH,
  API_KNOWLEDGE_BASES_PATH,
  API_TEAM_PATH,
  apiKnowledgeBasePath,
  apiKnowledgeDocumentPath,
  apiKnowledgeRetryPath,
  apiKnowledgeSharingPath,
  apiMemberPermissionsPath,
  HybridDemoRepository,
  type ApiViewerPermissions,
} from './hybrid-demo-repository';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository, type MockDemoRepositoryOptions } from './mock-demo-repository';
import { API_DEMO_REPOSITORY_FACTORY, DEMO_REPOSITORY } from './tokens';

type TeamResponse = components['schemas']['TeamResponse'];
type ApiKnowledgeBaseSummary = components['schemas']['KnowledgeBaseSummaryView'];
type ApiKnowledgeBaseDetail = components['schemas']['KnowledgeBaseDetailView'];
type ApiKnowledgeDocument = components['schemas']['KnowledgeDocumentView'];

const ADMIN_ID = '0199a000-0000-7000-8000-00000000000a';
const EMPLOYEE_ID = '0199a000-0000-7000-8000-000000000001';
const CUSTOMER_ID = '0199a000-0000-7000-8000-000000000002';
// 同組織第二位 internal-employee：直接插進團隊回應，id 與 EMPLOYEE_ID 不同，
// 用來驗證改權限不會透過角色轉換而互相覆蓋（issue #36）。
const EMPLOYEE_2_ID = '0199a000-0000-7000-8000-000000000003';
const CHECKINS = 'database-staff-checkins';
/** 管理者自己擁有的資料庫（`demo-seed.ts`）。 */
const ADMIN_ORDERS = 'database-orders';

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
  /** 需要在測試中途換身分時傳入；否則固定回傳 `me`。 */
  viewerPermissions: () => ApiViewerPermissions | null = () => me,
) {
  TestBed.configureTestingModule({
    providers: [provideHttpClient(), provideHttpClientTesting()],
  });
  const repository = new HybridDemoRepository(
    DEMO_SEED,
    { storage: createMemoryStorage(), viewer: () => viewer },
    { http: TestBed.inject(HttpClient), viewerPermissions },
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

  it('applies the viewer’s own permission change from the team API to the still-mock areas immediately', async () => {
    let me: ApiViewerPermissions | null = {
      accountId: ADMIN_ID,
      demoAccountId: 'account-smb-admin',
      permissions: ALL_ADMIN,
    };
    const { repository, controller } = setUp('account-smb-admin', null, () => me);
    expect(repository.getDatabaseTracking('account-smb-admin', ADMIN_ORDERS).status).toBe('ready');

    const withoutRecords = ALL_ADMIN.filter((permission) => permission !== 'read-consented-submissions');
    const saved = pending(repository.updateMemberPermissions(ADMIN_ID, withoutRecords));
    controller.expectOne({ method: 'PUT', url: apiMemberPermissionsPath(ADMIN_ID) }).flush({
      members: [
        {
          id: ADMIN_ID,
          displayName: '安心商行管理者',
          role: 'smb-admin',
          permissions: withoutRecords,
          lockedPermissions: ['manage-assistants'],
        },
      ],
      savedAt: '2026-09-25T02:00:00Z',
    } satisfies TeamResponse);
    await saved;

    expect(repository.getDatabaseTracking('account-smb-admin', ADMIN_ORDERS)).toMatchObject({
      status: 'permission-denied',
      reason: 'database-records',
    });

    // 同一分頁換成另一個帳號登入（同角色、不同 GUID）：不沿用上一位的團隊結果。
    me = { accountId: EMPLOYEE_2_ID, demoAccountId: 'account-smb-admin', permissions: ALL_ADMIN };
    expect(repository.getDatabaseTracking('account-smb-admin', ADMIN_ORDERS).status).toBe('ready');
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

  it('creates a member over HTTP and returns the one-time password exactly once', async () => {
    const { repository, controller } = setUp();
    const created = pending(
      repository.createMember({
        loginName: 'new-hire',
        displayName: '新進同仁',
        role: 'internal-employee',
        permissions: ['use-shared-assistants'],
      }),
    );

    const request = controller.expectOne({ method: 'POST', url: API_CREATE_MEMBER_PATH });
    expect(request.request.body).toEqual({
      loginName: 'new-hire',
      displayName: '新進同仁',
      role: 'internal-employee',
      permissions: ['use-shared-assistants'],
    });
    request.flush(
      {
        member: {
          id: '0199a000-0000-7000-8000-0000000000ff',
          displayName: '新進同仁',
          role: 'internal-employee',
          permissions: ['use-shared-assistants'],
          lockedPermissions: [],
        },
        oneTimePassword: 'One-Time-Pass-1!',
      },
      { status: 201, statusText: 'Created' },
    );

    expect(await created).toMatchObject({
      status: 'ready',
      data: {
        member: { displayName: '新進同仁', isViewer: false },
        oneTimePassword: 'One-Time-Pass-1!',
      },
    });
  });

  it('turns a 422 on create (duplicate login name) into validation-failed with the API’s message', async () => {
    const { repository, controller } = setUp();
    const result = pending(
      repository.createMember({
        loginName: 'admin',
        displayName: '重複的人',
        role: 'internal-employee',
        permissions: [],
      }),
    );

    controller.expectOne(API_CREATE_MEMBER_PATH).flush(
      { message: '這個登入名稱在目前組織已經有人使用，請改用其他名稱。', errors: { loginName: ['這個登入名稱在目前組織已經有人使用，請改用其他名稱。'] } },
      { status: 422, statusText: 'Unprocessable Content' },
    );

    expect(await result).toEqual({
      status: 'validation-failed',
      message: '這個登入名稱在目前組織已經有人使用，請改用其他名稱。',
    });
  });

  it('turns a 403 on create (no manage-assistants) into the team permission-denied', async () => {
    const { repository, controller } = setUp();
    const result = pending(
      repository.createMember({
        loginName: 'blocked',
        displayName: '被擋下的人',
        role: 'internal-employee',
        permissions: [],
      }),
    );

    controller.expectOne(API_CREATE_MEMBER_PATH).flush(
      { reason: 'team', message: '只有可管理助理與團隊的帳號可以查看或變更團隊成員權限。' },
      { status: 403, statusText: 'Forbidden' },
    );

    expect(await result).toEqual({
      status: 'permission-denied',
      reason: 'team',
      message: '只有可管理助理與團隊的帳號可以查看或變更團隊成員權限。',
    });
  });

  it('delegates every other method to the mock', () => {
    const { repository } = setUp();

    expect(repository.listAssistantTemplates().status).toBe('ready');
    expect(repository.listUsableAssistants('account-smb-admin').status).toBe('ready');
  });

  it('scopes storage by the real organization and account id from /me, read fresh on every access', () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const storage = createMemoryStorage();
    let me: ApiViewerPermissions | null = {
      demoAccountId: 'account-smb-admin',
      permissions: ALL_ADMIN,
      organizationId: 'org-a',
      accountId: 'account-a-admin',
    };
    // 同一個 repository 實例：identity 靠 `deps.viewerPermissions()` 每次呼叫時才讀，
    // 不是建構當下固定，這裡直接改 `me` 模擬「同一次頁面生命週期換了身分」。
    const repository = new HybridDemoRepository(
      DEMO_SEED,
      { storage, viewer: () => 'account-smb-admin' },
      { http: TestBed.inject(HttpClient), viewerPermissions: () => me },
    );

    const draft = { ...createEmptyAssistantDraft(), name: '組織 A 的草稿' };
    repository.saveAssistantDraft('account-smb-admin', draft);
    expect(repository.getAssistantDraft('account-smb-admin')).toMatchObject({
      status: 'ready',
      data: { draft: { name: '組織 A 的草稿' } },
    });

    // 換到組織 B：同一個 Demo 角色 id，但真實組織／帳號不同，看不到組織 A 的草稿。
    me = { demoAccountId: 'account-smb-admin', permissions: ALL_ADMIN, organizationId: 'org-b', accountId: 'account-b-admin' };
    expect(repository.getAssistantDraft('account-smb-admin')).toEqual({ status: 'ready', data: null });

    // 換回組織 A：草稿仍然看得到。
    me = { demoAccountId: 'account-smb-admin', permissions: ALL_ADMIN, organizationId: 'org-a', accountId: 'account-a-admin' };
    expect(repository.getAssistantDraft('account-smb-admin')).toMatchObject({
      status: 'ready',
      data: { draft: { name: '組織 A 的草稿' } },
    });
  });
});

const KB_ID = '0199b000-0000-7000-8000-0000000000b1';
const DOC_ID = '0199b000-0000-7000-8000-0000000000d1';
const VERSION_ID = '0199b000-0000-7000-8000-0000000000e1';

/** 與後端 `ForbiddenReason.KnowledgeBase` 位元組相同的內容（ProblemDetails + reason + message）。 */
const KNOWLEDGE_FORBIDDEN = {
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.4',
  title: 'Forbidden',
  status: 403,
  reason: 'knowledge-base',
  message: '你沒有這個知識庫的存取權限，或它已不存在。',
};

function apiSummary(overrides: Partial<ApiKnowledgeBaseSummary> = {}): ApiKnowledgeBaseSummary {
  return {
    id: KB_ID,
    name: '門市作業手冊',
    purpose: '開店與結帳流程',
    documentCount: 1,
    faqCount: 0,
    statusCounts: { queued: 0, processing: 0, ready: 0, 'partially-readable': 0, failed: 1 },
    inEffectCount: 0,
    awaitingApprovalCount: 0,
    disabledCount: 0,
    sharingScope: 'private',
    updatedAt: '2026-09-27T01:00:00+00:00',
    viewerCanManage: true,
    ...overrides,
  };
}

function apiDocument(overrides: Partial<ApiKnowledgeDocument> = {}): ApiKnowledgeDocument {
  return {
    id: DOC_ID,
    kind: 'document',
    name: '開店檢查表.pdf',
    status: 'failed',
    issue: '檔案設有開啟密碼，無法讀取內容。',
    updatedAt: '2026-09-27T01:00:00+00:00',
    latestVersionId: VERSION_ID,
    latestVersionNumber: 1,
    latestVersionState: 'pending-review',
    effectiveVersionNumber: null,
    disabled: false,
    inEffect: false,
    ...overrides,
  };
}

function apiDetail(): ApiKnowledgeBaseDetail {
  return {
    summary: apiSummary(),
    documents: [apiDocument()],
    sharing: { scope: 'private', sharedWithAccountIds: [], allowOriginalDownload: false },
    shareTargets: [
      { id: EMPLOYEE_ID, displayName: '安心商行客服同仁' },
      { id: CUSTOMER_ID, displayName: '安心商行客戶' },
    ],
  };
}

describe('HybridDemoRepository knowledge bases', () => {
  let controller: HttpTestingController;

  function setUpKnowledge() {
    const setup = setUp();
    controller = setup.controller;
    return setup;
  }

  afterEach(() => controller.verify());

  it('lists knowledge bases over HTTP, mapped to the frontend view', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.listKnowledgeBaseSummaries());

    controller.expectOne({ method: 'GET', url: API_KNOWLEDGE_BASES_PATH }).flush([apiSummary()]);

    expect(await result).toEqual({
      status: 'ready',
      data: [
        {
          id: KB_ID,
          name: '門市作業手冊',
          purpose: '開店與結帳流程',
          documentCount: 1,
          faqCount: 0,
          statusCounts: { queued: 0, processing: 0, ready: 0, 'partially-readable': 0, failed: 1 },
          sharingScope: 'private',
          connectedAssistantNames: [],
          updatedAt: '2026-09-27T01:00:00+00:00',
          viewerCanManage: true,
        },
      ],
    });
  });

  it('fills in connected assistants from the still-mock assistants, matched by knowledge base id', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.listKnowledgeBaseSummaries());

    // 後端沒有「已連接助理」；mock 的客服助理連接的是 `knowledge-product-guide` 這個 id。
    controller.expectOne(API_KNOWLEDGE_BASES_PATH).flush([apiSummary({ id: 'knowledge-product-guide' })]);

    expect(await result).toMatchObject({
      status: 'ready',
      data: [{ connectedAssistantNames: ['客服助理', '內部教育訓練助理'] }],
    });
  });

  it('reads the detail, keeping the version id the retry needs and the API’s share targets', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.getKnowledgeBaseDetail(KB_ID));

    controller.expectOne({ method: 'GET', url: apiKnowledgeBasePath(KB_ID) }).flush(apiDetail());

    expect(await result).toEqual({
      status: 'ready',
      data: {
        summary: expect.objectContaining({ id: KB_ID, viewerCanManage: true }),
        documents: [
          {
            id: DOC_ID,
            kind: 'document',
            name: '開店檢查表.pdf',
            status: 'failed',
            issue: '檔案設有開啟密碼，無法讀取內容。',
            updatedAt: '2026-09-27T01:00:00+00:00',
            latestVersionId: VERSION_ID,
          },
        ],
        connectedAssistants: [],
        sharing: { scope: 'private', sharedWithAccountIds: [], allowOriginalDownload: false },
        shareTargets: [
          { id: EMPLOYEE_ID, displayName: '安心商行客服同仁' },
          { id: CUSTOMER_ID, displayName: '安心商行客戶' },
        ],
      },
    });
  });

  it('maps 403 knowledge-base to the knowledge-base permission-denied, not team', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.getKnowledgeBaseDetail(KB_ID));

    controller.expectOne(apiKnowledgeBasePath(KB_ID)).flush(KNOWLEDGE_FORBIDDEN, { status: 403, statusText: 'Forbidden' });

    expect(await result).toEqual({
      status: 'permission-denied',
      reason: 'knowledge-base',
      message: '你沒有這個知識庫的存取權限，或它已不存在。',
    });
  });

  it('keeps the reason the API actually sent (password-change-required)', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.listKnowledgeBaseSummaries());

    controller.expectOne(API_KNOWLEDGE_BASES_PATH).flush(
      { reason: 'password-change-required', message: '請先設定新密碼，才能使用其他功能。' },
      { status: 403, statusText: 'Forbidden' },
    );

    expect(await result).toMatchObject({ status: 'permission-denied', reason: 'password-change-required' });
  });

  it('treats a 404 (an id that is not a GUID never reaches the endpoint) like 403 knowledge-base', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.getKnowledgeBaseDetail('knowledge-product-guide'));

    controller.expectOne(apiKnowledgeBasePath('knowledge-product-guide')).flush(null, { status: 404, statusText: 'Not Found' });

    expect(await result).toMatchObject({ status: 'permission-denied', reason: 'knowledge-base' });
  });

  it('leaves server errors to the screen instead of pretending the list is empty', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.listKnowledgeBaseSummaries()).catch((error: unknown) => error);

    controller.expectOne(API_KNOWLEDGE_BASES_PATH).flush(null, { status: 500, statusText: 'Server Error' });

    expect(await result).toMatchObject({ status: 500 });
  });

  it('creates a knowledge base, and a reload of the list shows it', async () => {
    const { repository } = setUpKnowledge();
    const created = pending(repository.createKnowledgeBase({ name: '門市作業手冊', purpose: '開店與結帳流程' }));

    const request = controller.expectOne({ method: 'POST', url: API_KNOWLEDGE_BASES_PATH });
    expect(request.request.body).toEqual({ name: '門市作業手冊', purpose: '開店與結帳流程' });
    request.flush(apiSummary({ documentCount: 0, statusCounts: { queued: 0, processing: 0, ready: 0, 'partially-readable': 0, failed: 0 } }), {
      status: 201,
      statusText: 'Created',
    });
    expect(await created).toMatchObject({ status: 'ready', data: { id: KB_ID, name: '門市作業手冊' } });

    const reloaded = pending(repository.listKnowledgeBaseSummaries());
    controller.expectOne({ method: 'GET', url: API_KNOWLEDGE_BASES_PATH }).flush([apiSummary()]);
    expect(await reloaded).toMatchObject({ status: 'ready', data: [{ id: KB_ID, name: '門市作業手冊' }] });
  });

  it('turns a 422 on create into validation-failed with the API’s message', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.createKnowledgeBase({ name: ' ', purpose: '' }));

    controller.expectOne(API_KNOWLEDGE_BASES_PATH).flush(
      { message: '請輸入知識庫名稱。', errors: { name: ['請輸入知識庫名稱。'] } },
      { status: 422, statusText: 'Unprocessable Content' },
    );

    expect(await result).toEqual({ status: 'validation-failed', message: '請輸入知識庫名稱。' });
  });

  it('turns a 403 on create (no manage-data-sources) into the knowledge-base permission-denied', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.createKnowledgeBase({ name: '名稱', purpose: '' }));

    controller.expectOne(API_KNOWLEDGE_BASES_PATH).flush(
      { reason: 'knowledge-base', message: '只有可管理資料來源的帳號可以建立知識庫。' },
      { status: 403, statusText: 'Forbidden' },
    );

    expect(await result).toEqual({
      status: 'permission-denied',
      reason: 'knowledge-base',
      message: '只有可管理資料來源的帳號可以建立知識庫。',
    });
  });

  it('saves sharing with a PUT of the whole setting', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(
      repository.updateKnowledgeSharing(KB_ID, {
        scope: 'specific-accounts',
        sharedWithAccountIds: [EMPLOYEE_ID],
        allowOriginalDownload: false,
      }),
    );

    const request = controller.expectOne({ method: 'PUT', url: apiKnowledgeSharingPath(KB_ID) });
    expect(request.request.body).toEqual({
      scope: 'specific-accounts',
      sharedWithAccountIds: [EMPLOYEE_ID],
      allowOriginalDownload: false,
    });
    request.flush({ scope: 'specific-accounts', sharedWithAccountIds: [EMPLOYEE_ID], allowOriginalDownload: false });

    expect(await result).toEqual({
      status: 'ready',
      data: { scope: 'specific-accounts', sharedWithAccountIds: [EMPLOYEE_ID], allowOriginalDownload: false },
    });
  });

  it('turns a 422 on sharing (no share target) into validation-failed', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(
      repository.updateKnowledgeSharing(KB_ID, {
        scope: 'specific-accounts',
        sharedWithAccountIds: ['11111111-1111-4111-8111-111111111111'],
        allowOriginalDownload: false,
      }),
    );

    controller.expectOne(apiKnowledgeSharingPath(KB_ID)).flush(
      { message: '請至少選擇一個帳號或團隊。', errors: { sharedWithAccountIds: ['請至少選擇一個帳號或團隊。'] } },
      { status: 422, statusText: 'Unprocessable Content' },
    );

    expect(await result).toEqual({ status: 'validation-failed', message: '請至少選擇一個帳號或團隊。' });
  });

  it('retries the latest version and returns the requeued document', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.retryKnowledgeDocument(KB_ID, DOC_ID, VERSION_ID));

    controller
      .expectOne({ method: 'POST', url: apiKnowledgeRetryPath(KB_ID, DOC_ID, VERSION_ID) })
      .flush(apiDocument({ status: 'queued', issue: null }));

    expect(await result).toMatchObject({ status: 'ready', data: { id: DOC_ID, status: 'queued', issue: null } });
  });

  it('turns a 409 on retry into validation-failed with the API’s message', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.retryKnowledgeDocument(KB_ID, DOC_ID, VERSION_ID));

    controller.expectOne(apiKnowledgeRetryPath(KB_ID, DOC_ID, VERSION_ID)).flush(
      { reason: 'version-not-retryable', message: '只有處理失敗的版本可以重試。' },
      { status: 409, statusText: 'Conflict' },
    );

    expect(await result).toEqual({ status: 'validation-failed', message: '只有處理失敗的版本可以重試。' });
  });

  it('deletes a knowledge base and a document with 204', async () => {
    const { repository } = setUpKnowledge();
    const knowledgeBase = pending(repository.deleteKnowledgeBase(KB_ID));
    controller.expectOne({ method: 'DELETE', url: apiKnowledgeBasePath(KB_ID) }).flush(null, { status: 204, statusText: 'No Content' });
    expect(await knowledgeBase).toEqual({ status: 'ready', data: null });

    const document = pending(repository.deleteKnowledgeDocument(KB_ID, DOC_ID));
    controller
      .expectOne({ method: 'DELETE', url: apiKnowledgeDocumentPath(KB_ID, DOC_ID) })
      .flush(null, { status: 204, statusText: 'No Content' });
    expect(await document).toEqual({ status: 'ready', data: null });
  });

  it('turns a 403 on delete into the knowledge-base permission-denied', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.deleteKnowledgeBase(KB_ID));

    controller.expectOne(apiKnowledgeBasePath(KB_ID)).flush(KNOWLEDGE_FORBIDDEN, { status: 403, statusText: 'Forbidden' });

    expect(await result).toMatchObject({ status: 'permission-denied', reason: 'knowledge-base' });
  });

  it('is cold: nothing is sent until subscribed', () => {
    const { repository } = setUpKnowledge();
    repository.createKnowledgeBase({ name: '未訂閱', purpose: '' });
    repository.deleteKnowledgeBase(KB_ID);

    controller.expectNone(API_KNOWLEDGE_BASES_PATH);
    controller.expectNone(apiKnowledgeBasePath(KB_ID));
  });
});

describe('HybridDemoRepository connectable sources (issue #49)', () => {
  let controller: HttpTestingController;

  function setUpSources(viewer: AccountId | null = 'account-smb-admin') {
    const setup = setUp(viewer);
    controller = setup.controller;
    return setup;
  }

  afterEach(() => controller.verify());

  it('mixes real knowledge bases from the API with the still-mock databases', async () => {
    const { repository } = setUpSources();
    const result = pending(repository.listConnectableSources());

    controller.expectOne(API_KNOWLEDGE_BASES_PATH).flush([apiSummary()]);

    expect(await result).toMatchObject({
      status: 'ready',
      data: [
        expect.objectContaining({ id: KB_ID, type: 'knowledge-base', name: '門市作業手冊' }),
        expect.objectContaining({ id: 'database-orders', type: 'database' }),
        expect.objectContaining({ id: 'database-customer-records', type: 'database' }),
      ],
    });
  });

  it('drops knowledge bases the viewer cannot manage, matching the mock’s owner-only rule', async () => {
    const { repository } = setUpSources();
    const pendingResult = pending(repository.listConnectableSources());

    controller
      .expectOne(API_KNOWLEDGE_BASES_PATH)
      .flush([apiSummary({ viewerCanManage: false }), apiSummary({ id: 'knowledge-product-guide' })]);

    const result = await pendingResult;
    expect(result.status).toBe('ready');
    if (result.status !== 'ready') return;
    expect(result.data.map((source) => source.id)).toEqual(
      expect.arrayContaining(['knowledge-product-guide', 'database-orders', 'database-customer-records']),
    );
    expect(result.data.map((source) => source.id)).not.toContain(KB_ID);
  });

  it('returns an empty ready list without calling the API when the viewer cannot manage assistants', async () => {
    const { repository } = setUp('account-external-customer', {
      accountId: CUSTOMER_ID,
      demoAccountId: 'account-external-customer',
      permissions: ['use-shared-assistants'],
    });
    controller = TestBed.inject(HttpTestingController);

    expect(await pending(repository.listConnectableSources())).toEqual({ status: 'ready', data: [] });
    controller.expectNone(API_KNOWLEDGE_BASES_PATH);
  });

  it('returns an empty ready list without calling the API when signed out', async () => {
    const { repository } = setUpSources(null);

    expect(await pending(repository.listConnectableSources())).toEqual({ status: 'ready', data: [] });
    controller.expectNone(API_KNOWLEDGE_BASES_PATH);
  });

  it('maps a 403 to the knowledge-base permission-denied', async () => {
    const { repository } = setUpSources();
    const result = pending(repository.listConnectableSources());

    controller.expectOne(API_KNOWLEDGE_BASES_PATH).flush(KNOWLEDGE_FORBIDDEN, { status: 403, statusText: 'Forbidden' });

    expect(await result).toMatchObject({ status: 'permission-denied', reason: 'knowledge-base' });
  });

  /** 寫入 mock storage 的測試需要組織 id：沒有它時 scoped storage 不落地（見 `scoped-storage.ts`）。 */
  function setUpWithOrganization() {
    const setup = setUp('account-smb-admin', {
      accountId: ADMIN_ID,
      demoAccountId: 'account-smb-admin',
      permissions: ALL_ADMIN,
      organizationId: 'org-a',
    });
    controller = setup.controller;
    return setup;
  }

  it('keeps a real knowledge base picked in the wizard when the mock assistant is created', async () => {
    const { repository } = setUpWithOrganization();
    const listed = pending(repository.listConnectableSources());
    controller.expectOne(API_KNOWLEDGE_BASES_PATH).flush([apiSummary()]);
    await listed;

    const created = repository.createAssistantFromDraft('account-smb-admin', {
      ...createEmptyAssistantDraft(),
      templateId: 'answer-customer-questions',
      name: '門市問答助理',
      purpose: '回答門市作業問題',
      audience: 'account-members',
      sources: [{ id: KB_ID, type: 'knowledge-base' }],
      testedQuestionIds: ['trial-refund-window'],
      currentStep: 'test',
    });

    expect(created).toMatchObject({ status: 'ready', data: { knowledgeBaseIds: [KB_ID] } });
  });

  it('lets the settings page connect a real knowledge base to a mock assistant', async () => {
    const { repository } = setUpWithOrganization();
    const listed = pending(repository.listConnectableSources());
    controller.expectOne(API_KNOWLEDGE_BASES_PATH).flush([apiSummary()]);
    await listed;

    const result = repository.setAssistantSourceConnection(
      'account-smb-admin',
      'assistant-customer-service',
      { id: KB_ID, type: 'knowledge-base' },
      true,
    );

    expect(result.status).toBe('ready');
    if (result.status !== 'ready') return;
    expect(result.data.sources).toContainEqual({ id: KB_ID, type: 'knowledge-base' });
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
