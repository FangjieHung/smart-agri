import { HttpClient, HttpEventType, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom, type Observable } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';
import type { components } from '../api/api-schema';
import type { AccountId, AccountPermission } from '../domain/account.model';
import { createEmptyAssistantDraft, type AssistantDraft } from '../domain/assistant-draft.model';
import type { TeamView } from '../domain/team.model';
import type { RepositoryView } from './demo-repository';
import { DEMO_SEED, type DemoSeed } from './demo-seed';
import {
  API_ASSISTANT_DRAFTS_PATH,
  API_ASSISTANTS_PATH,
  API_CONNECTABLE_SOURCES_PATH,
  API_CREATE_MEMBER_PATH,
  API_KNOWLEDGE_BASES_PATH,
  API_USABLE_ASSISTANTS_PATH,
  apiAssistantDraftPath,
  apiAssistantKnowledgeSourcePath,
  apiAssistantPath,
  apiAssistantPlatformPausedPath,
  apiAssistantPlatformSharingPath,
  apiAssistantPublishingPath,
  apiAssistantSettingsPath,
  apiTrialAnswersPath,
  API_RECENT_CONVERSATIONS_PATH,
  API_TEAM_PATH,
  apiAssistantChatConversationPath,
  apiAssistantChatConversationsPath,
  apiAssistantChatPath,
  apiKnowledgeApprovePath,
  apiKnowledgeBasePath,
  apiKnowledgeChunkExclusionPath,
  apiKnowledgeDisablePath,
  apiKnowledgeDocumentDetailPath,
  apiKnowledgeDocumentPath,
  apiKnowledgeDocumentsPath,
  apiKnowledgeDocumentVersionsPath,
  apiKnowledgeEnablePath,
  apiKnowledgeRetrievalPreviewPath,
  apiKnowledgeRetryPath,
  apiKnowledgeSharingPath,
  apiKnowledgeVersionPreviewPath,
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
type ApiKnowledgeDocumentDetail = components['schemas']['KnowledgeDocumentDetailView'];
type ApiKnowledgeVersion = components['schemas']['KnowledgeVersionView'];
type ApiKnowledgeActivity = components['schemas']['KnowledgeActivityView'];
type ApiKnowledgeAccount = components['schemas']['KnowledgeAccountView'];
type ApiKnowledgeVersionPreview = components['schemas']['KnowledgeVersionPreviewView'];
type ApiKnowledgeRetrievalPreview = components['schemas']['KnowledgeRetrievalPreviewView'];
type ApiChatThreadListView = components['schemas']['ChatThreadListView'];
type ApiChatThreadSummaryView = components['schemas']['ChatThreadSummaryView'];
type ApiAssistantChatView = components['schemas']['AssistantChatView'];
type ApiChatMessageView = components['schemas']['ChatMessageView'];
type ApiRecentConversationView = components['schemas']['RecentConversationView'];
type ApiAssistantConfiguration = components['schemas']['AssistantConfigurationView'];
type ApiAssistantSettings = components['schemas']['AssistantSettingsView'];
type ApiAssistantDraft = components['schemas']['AssistantDraftView'];
type ApiAssistantPublishing = components['schemas']['AssistantPublishingView'];
type ApiPublishingChannel = components['schemas']['PublishingChannelView'];

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
    expect(repository.listDatabaseSummaries('account-smb-admin').status).toBe('ready');
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

    // 仍是 mock 的功能區（資料庫）寫進 storage 的資料，要依真實組織／帳號隔開。
    const created = repository.createDatabaseFromTemplate('account-smb-admin', {
      templateId: 'template-satisfaction',
      name: '組織 A 的資料庫',
    });
    expect(created.status).toBe('ready');
    const names = () => {
      const result = repository.listDatabaseSummaries('account-smb-admin');
      return result.status === 'ready' ? result.data.map((database) => database.name) : [];
    };
    expect(names()).toContain('組織 A 的資料庫');

    // 換到組織 B：同一個 Demo 角色 id，但真實組織／帳號不同，看不到組織 A 的資料。
    me = { demoAccountId: 'account-smb-admin', permissions: ALL_ADMIN, organizationId: 'org-b', accountId: 'account-b-admin' };
    expect(names()).not.toContain('組織 A 的資料庫');

    // 換回組織 A：資料仍然看得到。
    me = { demoAccountId: 'account-smb-admin', permissions: ALL_ADMIN, organizationId: 'org-a', accountId: 'account-a-admin' };
    expect(names()).toContain('組織 A 的資料庫');
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

function apiAccount(overrides: Partial<ApiKnowledgeAccount> = {}): ApiKnowledgeAccount {
  return { id: ADMIN_ID, displayName: '安心商行管理者', ...overrides };
}

function apiVersion(overrides: Partial<ApiKnowledgeVersion> = {}): ApiKnowledgeVersion {
  return {
    id: VERSION_ID,
    documentId: DOC_ID,
    versionNumber: 1,
    fileName: '退換貨辦法 2026 版.pdf',
    contentType: 'application/pdf',
    sizeBytes: 1024,
    status: 'ready',
    issue: null,
    state: 'effective',
    effectiveFrom: '2026-09-27T01:00:00+00:00',
    uploadedBy: apiAccount(),
    uploadedAt: '2026-09-27T01:00:00+00:00',
    approvedBy: apiAccount(),
    approvedAt: '2026-09-27T01:00:00+00:00',
    updatedAt: '2026-09-27T01:00:00+00:00',
    ...overrides,
  };
}

function apiActivity(overrides: Partial<ApiKnowledgeActivity> = {}): ApiKnowledgeActivity {
  return {
    id: 'activity-1',
    action: 'version-approved',
    actor: apiAccount(),
    at: '2026-09-27T01:00:00+00:00',
    versionId: VERSION_ID,
    versionNumber: 1,
    reason: null,
    ...overrides,
  };
}

function apiDocumentDetail(overrides: Partial<ApiKnowledgeDocumentDetail> = {}): ApiKnowledgeDocumentDetail {
  return {
    document: apiDocument({ latestVersionState: 'effective', effectiveVersionNumber: 1, inEffect: true }),
    createdAt: '2026-09-27T01:00:00+00:00',
    disabledAt: null,
    disabledBy: null,
    disabledReason: null,
    versions: [apiVersion()],
    activities: [apiActivity()],
    ...overrides,
  };
}

function apiVersionPreview(overrides: Partial<ApiKnowledgeVersionPreview> = {}): ApiKnowledgeVersionPreview {
  return {
    documentId: DOC_ID,
    versionId: VERSION_ID,
    versionNumber: 1,
    fileName: '退換貨辦法 2026 版.pdf',
    status: 'ready',
    issue: null,
    units: [
      {
        ordinal: 1,
        locationKind: 'page',
        locationLabel: '第 1 頁',
        readable: true,
        issueCode: null,
        text: '封面',
        chunks: [{ id: 'chunk-1', locationLabel: '第 1 頁', text: '封面', excluded: false }],
      },
    ],
    ...overrides,
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
          inEffectCount: 0,
          awaitingApprovalCount: 0,
          disabledCount: 0,
          sharingScope: 'private',
          connectedAssistantNames: [],
          updatedAt: '2026-09-27T01:00:00+00:00',
          viewerCanManage: true,
        },
      ],
    });
  });

  it('no longer fills in connected assistants from the mock assistants, which API mode does not have (issue #81)', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.listKnowledgeBaseSummaries());

    // mock 的客服助理連接的是 `knowledge-product-guide`；API 模式不能再拿它當成真實紀錄。
    controller.expectOne(API_KNOWLEDGE_BASES_PATH).flush([apiSummary({ id: 'knowledge-product-guide' })]);

    expect(await result).toMatchObject({ status: 'ready', data: [{ connectedAssistantNames: [] }] });
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
            latestVersionNumber: 1,
            latestVersionState: 'pending-review',
            effectiveVersionNumber: null,
            disabled: false,
            inEffect: false,
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
    repository.uploadKnowledgeDocument(KB_ID, testFile());

    controller.expectNone(API_KNOWLEDGE_BASES_PATH);
    controller.expectNone(apiKnowledgeBasePath(KB_ID));
    controller.expectNone(apiKnowledgeDocumentsPath(KB_ID));
  });

  // ---------- 版本確認、抽取預覽與緊急停用（issue #47，M2 Slice 13） ----------

  it('reads a document’s detail: version history and activity log', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.getKnowledgeDocumentDetail(KB_ID, DOC_ID));

    controller.expectOne({ method: 'GET', url: apiKnowledgeDocumentDetailPath(KB_ID, DOC_ID) }).flush(apiDocumentDetail());

    expect(await result).toMatchObject({
      status: 'ready',
      data: {
        document: { id: DOC_ID, inEffect: true },
        versions: [{ id: VERSION_ID, versionNumber: 1, state: 'effective' }],
        activities: [{ action: 'version-approved' }],
      },
    });
  });

  it('reads a version’s extraction preview, including chunk exclusion', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.previewKnowledgeVersion(KB_ID, DOC_ID, VERSION_ID));

    controller
      .expectOne({ method: 'GET', url: apiKnowledgeVersionPreviewPath(KB_ID, DOC_ID, VERSION_ID) })
      .flush(apiVersionPreview());

    expect(await result).toMatchObject({
      status: 'ready',
      data: { versionId: VERSION_ID, units: [{ locationLabel: '第 1 頁', chunks: [{ excluded: false }] }] },
    });
  });

  it('toggles a chunk’s exclusion with a PUT of the new value', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(
      repository.updateKnowledgeChunkExclusion(KB_ID, DOC_ID, VERSION_ID, 'chunk-1', true),
    );

    const request = controller.expectOne({
      method: 'PUT',
      url: apiKnowledgeChunkExclusionPath(KB_ID, DOC_ID, VERSION_ID, 'chunk-1'),
    });
    expect(request.request.body).toEqual({ excluded: true });
    request.flush({ id: 'chunk-1', locationLabel: '第 1 頁', text: '封面', excluded: true });

    expect(await result).toEqual({
      status: 'ready',
      data: { id: 'chunk-1', locationLabel: '第 1 頁', text: '封面', excluded: true },
    });
  });

  it('approves versions in a batch, sending the optional effective date', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.approveKnowledgeVersions(KB_ID, [VERSION_ID], '2026-10-01T00:00:00Z'));

    const request = controller.expectOne({ method: 'POST', url: apiKnowledgeApprovePath(KB_ID) });
    expect(request.request.body).toEqual({ versionIds: [VERSION_ID], effectiveFrom: '2026-10-01T00:00:00Z' });
    request.flush([apiVersion({ state: 'scheduled', effectiveFrom: '2026-10-01T00:00:00Z' })]);

    expect(await result).toMatchObject({ status: 'ready', data: [{ id: VERSION_ID, state: 'scheduled' }] });
  });

  it('turns a 422 on batch approve (a version is not approvable) into validation-failed, naming which ones', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.approveKnowledgeVersions(KB_ID, [VERSION_ID, 'not-a-real-version']));

    controller.expectOne(apiKnowledgeApprovePath(KB_ID)).flush(
      {
        reason: 'versions-not-approvable',
        message: '這些版本剛剛有其他變更，請重新整理後再試一次。',
        errors: { 'versionIds[1]': ['這個版本不存在，或不是待確認的版本。'] },
      },
      { status: 422, statusText: 'Unprocessable Content' },
    );

    expect(await result).toEqual({
      status: 'validation-failed',
      message: '這些版本剛剛有其他變更，請重新整理後再試一次。',
    });
  });

  it('turns a 409 on batch approve (concurrent change) into validation-failed', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.approveKnowledgeVersions(KB_ID, [VERSION_ID]));

    controller.expectOne(apiKnowledgeApprovePath(KB_ID)).flush(
      { reason: 'approval-conflict', message: '這些版本剛剛有其他變更，請重新整理後再試一次。' },
      { status: 409, statusText: 'Conflict' },
    );

    expect(await result).toEqual({
      status: 'validation-failed',
      message: '這些版本剛剛有其他變更，請重新整理後再試一次。',
    });
  });

  it('disables a document with the required reason', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.disableKnowledgeDocument(KB_ID, DOC_ID, '疑似內容錯誤'));

    const request = controller.expectOne({ method: 'POST', url: apiKnowledgeDisablePath(KB_ID, DOC_ID) });
    expect(request.request.body).toEqual({ reason: '疑似內容錯誤' });
    request.flush(apiDocument({ disabled: true, inEffect: false }));

    expect(await result).toMatchObject({ status: 'ready', data: { disabled: true, inEffect: false } });
  });

  it('turns a 422 on disable (missing reason) into validation-failed', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.disableKnowledgeDocument(KB_ID, DOC_ID, ''));

    controller.expectOne(apiKnowledgeDisablePath(KB_ID, DOC_ID)).flush(
      { message: '請說明緊急停用的原因。', errors: { reason: ['請說明緊急停用的原因。'] } },
      { status: 422, statusText: 'Unprocessable Content' },
    );

    expect(await result).toEqual({ status: 'validation-failed', message: '請說明緊急停用的原因。' });
  });

  it('turns a 409 on disable (already disabled) into validation-failed', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.disableKnowledgeDocument(KB_ID, DOC_ID, '原因'));

    controller.expectOne(apiKnowledgeDisablePath(KB_ID, DOC_ID)).flush(
      { reason: 'document-already-disabled', message: '這份文件已經停用了。' },
      { status: 409, statusText: 'Conflict' },
    );

    expect(await result).toEqual({ status: 'validation-failed', message: '這份文件已經停用了。' });
  });

  it('enables a document', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.enableKnowledgeDocument(KB_ID, DOC_ID));

    controller
      .expectOne({ method: 'POST', url: apiKnowledgeEnablePath(KB_ID, DOC_ID) })
      .flush(apiDocument({ disabled: false, inEffect: true }));

    expect(await result).toMatchObject({ status: 'ready', data: { disabled: false, inEffect: true } });
  });

  it('turns a 409 on enable (not disabled) into validation-failed', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.enableKnowledgeDocument(KB_ID, DOC_ID));

    controller.expectOne(apiKnowledgeEnablePath(KB_ID, DOC_ID)).flush(
      { reason: 'document-not-disabled', message: '這份文件目前沒有停用，不需要恢復。' },
      { status: 409, statusText: 'Conflict' },
    );

    expect(await result).toEqual({ status: 'validation-failed', message: '這份文件目前沒有停用，不需要恢復。' });
  });

  // ---------- 檢索試查（issue #48，M2 Slice 14） ----------

  it('previews retrieval with a POST of the question and includePending', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.previewKnowledgeRetrieval(KB_ID, '收到商品幾天內可以退貨？', true));

    const request = controller.expectOne({ method: 'POST', url: apiKnowledgeRetrievalPreviewPath(KB_ID) });
    expect(request.request.body).toEqual({ question: '收到商品幾天內可以退貨？', includePending: true });
    // 一條真正的 JSON 字串 fixture（PR #103 的教訓：後端以 JsonIgnore(WhenWritingNull) 省略
    // null 欄位時，OpenAPI 仍把型別寫成必填，adapter 要同時處理 undefined）。
    request.flush(
      JSON.parse(`{
        "passages": [
          {
            "documentId": "${DOC_ID}",
            "documentName": "退換貨辦法 2026 版.pdf",
            "versionNumber": 1,
            "versionState": "effective",
            "locationLabel": "第 2 頁",
            "excerpt": "收到商品後 7 天內可以申請退貨。",
            "score": 0.82,
            "versionId": "${VERSION_ID}",
            "chunkId": "0199b000-0000-7000-8000-0000000000f1"
          }
        ],
        "threshold": 0.3,
        "belowThreshold": false
      }`) as ApiKnowledgeRetrievalPreview,
    );

    expect(await result).toEqual({
      status: 'ready',
      data: {
        passages: [
          {
            documentId: DOC_ID,
            documentName: '退換貨辦法 2026 版.pdf',
            versionNumber: 1,
            versionState: 'effective',
            locationLabel: '第 2 頁',
            excerpt: '收到商品後 7 天內可以申請退貨。',
            score: 0.82,
            versionId: VERSION_ID,
            chunkId: '0199b000-0000-7000-8000-0000000000f1',
          },
        ],
        threshold: 0.3,
        belowThreshold: false,
      },
    });
  });

  it('maps 403 to the knowledge-base permission-denied', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.previewKnowledgeRetrieval(KB_ID, '退貨期限', false));

    controller
      .expectOne(apiKnowledgeRetrievalPreviewPath(KB_ID))
      .flush(KNOWLEDGE_FORBIDDEN, { status: 403, statusText: 'Forbidden' });

    expect(await result).toEqual({
      status: 'permission-denied',
      reason: 'knowledge-base',
      message: '你沒有這個知識庫的存取權限，或它已不存在。',
    });
  });

  it('turns a 422 (blank or too-long question) into validation-failed', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.previewKnowledgeRetrieval(KB_ID, '', false));

    controller.expectOne(apiKnowledgeRetrievalPreviewPath(KB_ID)).flush(
      { message: '請輸入要試查的問題。', errors: { question: ['請輸入要試查的問題。'] } },
      { status: 422, statusText: 'Unprocessable Content' },
    );

    expect(await result).toEqual({ status: 'validation-failed', message: '請輸入要試查的問題。' });
  });

  it('turns a 503 embedding-not-configured into unavailable, keeping the reason', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.previewKnowledgeRetrieval(KB_ID, '退貨期限', false));

    controller.expectOne(apiKnowledgeRetrievalPreviewPath(KB_ID)).flush(
      { reason: 'embedding-not-configured', message: '這個部署尚未設定嵌入模型，請聯絡管理員。' },
      { status: 503, statusText: 'Service Unavailable' },
    );

    expect(await result).toEqual({
      status: 'unavailable',
      reason: 'embedding-not-configured',
      message: '這個部署尚未設定嵌入模型，請聯絡管理員。',
    });
  });

  it('turns a 503 embedding-unavailable into unavailable', async () => {
    const { repository } = setUpKnowledge();
    const result = pending(repository.previewKnowledgeRetrieval(KB_ID, '退貨期限', false));

    controller.expectOne(apiKnowledgeRetrievalPreviewPath(KB_ID)).flush(
      { reason: 'embedding-unavailable', message: '嵌入模型暫時無法使用，請稍後重試。' },
      { status: 503, statusText: 'Service Unavailable' },
    );

    expect(await result).toEqual({
      status: 'unavailable',
      reason: 'embedding-unavailable',
      message: '嵌入模型暫時無法使用，請稍後重試。',
    });
  });
});

function testFile(name = 'x.pdf'): File {
  return new File([new Uint8Array(10)], name, { type: 'application/pdf' });
}

describe('HybridDemoRepository uploads (issue #46)', () => {
  let controller: HttpTestingController;

  function setUpUploads() {
    const setup = setUp();
    controller = setup.controller;
    return setup;
  }

  afterEach(() => controller.verify());

  it('uploads over HTTP as multipart form data, reporting progress before the final result', async () => {
    const { repository } = setUpUploads();
    const events: unknown[] = [];
    repository.uploadKnowledgeDocument(KB_ID, testFile('新規格書.pdf')).subscribe((event) => events.push(event));

    const request = controller.expectOne({ method: 'POST', url: apiKnowledgeDocumentsPath(KB_ID) });
    expect(request.request.body).toBeInstanceOf(FormData);
    expect((request.request.body as FormData).get('file')).toBeInstanceOf(File);

    request.event({ type: HttpEventType.UploadProgress, loaded: 5, total: 10 });
    request.flush(apiDocument({ name: '新規格書.pdf' }), { status: 201, statusText: 'Created' });

    expect(events).toEqual([
      { status: 'progress', percent: 50 },
      { status: 'ready', data: expect.objectContaining({ name: '新規格書.pdf' }) },
    ]);
  });

  it('turns a 413 into a rejected result', async () => {
    const { repository } = setUpUploads();
    const result = pending(repository.uploadKnowledgeDocument(KB_ID, testFile()));

    controller.expectOne(apiKnowledgeDocumentsPath(KB_ID)).flush(
      { reason: 'file-too-large', message: '檔案超過 20 MB 的上限，請分割或壓縮後再上傳。' },
      { status: 413, statusText: 'Content Too Large' },
    );

    expect(await result).toEqual({
      status: 'rejected',
      reason: 'file-too-large',
      message: '檔案超過 20 MB 的上限，請分割或壓縮後再上傳。',
    });
  });

  it('turns a 415 into a rejected result', async () => {
    const { repository } = setUpUploads();
    const result = pending(repository.uploadKnowledgeDocument(KB_ID, testFile()));

    controller.expectOne(apiKnowledgeDocumentsPath(KB_ID)).flush(
      { reason: 'unsupported-file-type', message: '只支援 PDF、Word（.docx）、Excel（.xlsx）、純文字（.txt）與 Markdown（.md）檔案。' },
      { status: 415, statusText: 'Unsupported Media Type' },
    );

    expect(await result).toEqual({
      status: 'rejected',
      reason: 'unsupported-file-type',
      message: '只支援 PDF、Word（.docx）、Excel（.xlsx）、純文字（.txt）與 Markdown（.md）檔案。',
    });
  });

  it('turns a 422 duplicate-content into a rejected result naming the existing document', async () => {
    const { repository } = setUpUploads();
    const result = pending(repository.uploadKnowledgeDocument(KB_ID, testFile()));

    controller.expectOne(apiKnowledgeDocumentsPath(KB_ID)).flush(
      {
        reason: 'duplicate-content',
        message: '這份檔案的內容與「開店檢查表.pdf」完全相同，不需要重複上傳。',
        existingDocumentName: '開店檢查表.pdf',
        errors: { file: ['這份檔案的內容與「開店檢查表.pdf」完全相同，不需要重複上傳。'] },
      },
      { status: 422, statusText: 'Unprocessable Content' },
    );

    expect(await result).toEqual({
      status: 'rejected',
      reason: 'duplicate-content',
      message: '這份檔案的內容與「開店檢查表.pdf」完全相同，不需要重複上傳。',
      existingDocumentName: '開店檢查表.pdf',
    });
  });

  it('turns a 403 into the knowledge-base permission-denied, like the other document endpoints', async () => {
    const { repository } = setUpUploads();
    const result = pending(repository.uploadKnowledgeDocument(KB_ID, testFile()));

    controller.expectOne(apiKnowledgeDocumentsPath(KB_ID)).flush(KNOWLEDGE_FORBIDDEN, { status: 403, statusText: 'Forbidden' });

    expect(await result).toMatchObject({ status: 'permission-denied', reason: 'knowledge-base' });
  });

  it('leaves server errors to the screen', async () => {
    const { repository } = setUpUploads();
    const result = pending(repository.uploadKnowledgeDocument(KB_ID, testFile())).catch((error: unknown) => error);

    controller.expectOne(apiKnowledgeDocumentsPath(KB_ID)).flush(null, { status: 500, statusText: 'Server Error' });

    expect(await result).toMatchObject({ status: 500 });
  });

  it('uploads a new version to the versions endpoint', async () => {
    const { repository } = setUpUploads();
    const result = pending(repository.uploadKnowledgeDocumentVersion(KB_ID, DOC_ID, testFile('開店檢查表-v2.pdf')));

    const request = controller.expectOne({ method: 'POST', url: apiKnowledgeDocumentVersionsPath(KB_ID, DOC_ID) });
    request.flush(apiDocument(), { status: 201, statusText: 'Created' });

    expect(await result).toMatchObject({ status: 'ready', data: { id: DOC_ID } });
  });

  it('turns a 409 on a new version into a rejected result', async () => {
    const { repository } = setUpUploads();
    const result = pending(repository.uploadKnowledgeDocumentVersion(KB_ID, DOC_ID, testFile()));

    controller.expectOne(apiKnowledgeDocumentVersionsPath(KB_ID, DOC_ID)).flush(
      { reason: 'concurrent-version-upload', message: '這份文件剛剛有另一個新版本上傳完成，請重新整理後再試一次。' },
      { status: 409, statusText: 'Conflict' },
    );

    // 409 目前不是逐檔的 rejected 原因之一（後端保留給併發競賽，見 KnowledgeDocumentEndpoints）；
    // 這裡沒有特別轉換，交給畫面的一般錯誤處理。
    expect(await result.catch((error: unknown) => error)).toMatchObject({ status: 409 });
  });
});

const CHAT_ASSISTANT_ID = '0199a000-0000-7000-8000-0000000000c1';
const CHAT_THREAD_ID = '0199a000-0000-7000-8000-0000000000c2';

function apiThreadSummary(overrides: Partial<ApiChatThreadSummaryView> = {}): ApiChatThreadSummaryView {
  return {
    id: CHAT_THREAD_ID,
    title: '退貨問題',
    messageCount: 2,
    updatedAt: '2026-09-27T01:00:00+00:00',
    ...overrides,
  };
}

function apiThreadList(overrides: Partial<ApiChatThreadListView> = {}): ApiChatThreadListView {
  return {
    assistantId: CHAT_ASSISTANT_ID,
    assistantName: '客服助理',
    historyMode: 'saved',
    threads: [apiThreadSummary()],
    historyNotice: '對話只留在你的帳號，助理擁有者看不到內容。',
    ...overrides,
  };
}

function apiAccountMessage(overrides: Partial<ApiChatMessageView> = {}): ApiChatMessageView {
  return {
    id: '0199a000-0000-7000-8000-0000000000d1',
    author: 'account',
    text: '收到商品後幾天內可以退貨？',
    reply: null,
    createdAt: '2026-09-27T01:00:00+00:00',
    ...overrides,
  };
}

function apiAssistantMessage(reply: ApiChatMessageView['reply']): ApiChatMessageView {
  return {
    id: '0199a000-0000-7000-8000-0000000000d2',
    author: 'assistant',
    text: null,
    reply,
    createdAt: '2026-09-27T01:00:01+00:00',
  };
}

function apiAssistantChat(overrides: Partial<ApiAssistantChatView> = {}): ApiAssistantChatView {
  return {
    assistantId: CHAT_ASSISTANT_ID,
    assistantName: '客服助理',
    purpose: '回答退換貨、保養與訂單問題',
    threadId: CHAT_THREAD_ID,
    title: '退貨問題',
    historyMode: 'saved',
    welcome: '哈囉，我可以幫你查退換貨、保養與訂單問題。',
    privacyNotice: '這段對話只屬於你的帳號，助理建立者看不到內容。',
    suggestedPrompts: [],
    messages: [],
    ...overrides,
  };
}

const CHAT_FORBIDDEN = { reason: 'assistant-use', message: '你沒有使用這個助理的權限，或它已不存在。' };

describe('HybridDemoRepository chat (issue #79)', () => {
  let controller: HttpTestingController;

  function setUpChat() {
    const setup = setUp();
    controller = setup.controller;
    return setup;
  }

  afterEach(() => controller.verify());

  it('lists chat threads over HTTP, mapped to the frontend view', async () => {
    const { repository } = setUpChat();
    const result = pending(repository.listChatThreads(CHAT_ASSISTANT_ID));

    controller.expectOne({ method: 'GET', url: apiAssistantChatConversationsPath(CHAT_ASSISTANT_ID) }).flush(apiThreadList());

    expect(await result).toEqual({
      status: 'ready',
      data: {
        assistantId: CHAT_ASSISTANT_ID,
        assistantName: '客服助理',
        historyMode: 'saved',
        threads: [{ id: CHAT_THREAD_ID, title: '退貨問題', messageCount: 2, updatedAt: '2026-09-27T01:00:00+00:00' }],
        historyNotice: '對話只留在你的帳號，助理擁有者看不到內容。',
      },
    });
  });

  it('turns 403 assistant-use on listing threads into the matching permission-denied', async () => {
    const { repository } = setUpChat();
    const result = pending(repository.listChatThreads(CHAT_ASSISTANT_ID));

    controller
      .expectOne(apiAssistantChatConversationsPath(CHAT_ASSISTANT_ID))
      .flush(CHAT_FORBIDDEN, { status: 403, statusText: 'Forbidden' });

    expect(await result).toEqual({
      status: 'permission-denied',
      reason: 'assistant-use',
      message: CHAT_FORBIDDEN.message,
    });
  });

  it('creates a chat thread with a 201 and returns the empty conversation', async () => {
    const { repository } = setUpChat();
    const result = pending(repository.createChatThread(CHAT_ASSISTANT_ID));

    const request = controller.expectOne({ method: 'POST', url: apiAssistantChatConversationsPath(CHAT_ASSISTANT_ID) });
    request.flush(apiAssistantChat({ messages: [] }), { status: 201, statusText: 'Created' });

    expect(await result).toEqual({
      status: 'ready',
      data: expect.objectContaining({ threadId: CHAT_THREAD_ID, messages: [] }),
    });
  });

  it('renames a thread with a PATCH and turns a blank-or-long-title 422 into validation-failed', async () => {
    const { repository } = setUpChat();
    const renamed = pending(repository.renameChatThread(CHAT_ASSISTANT_ID, CHAT_THREAD_ID, '退貨與換貨'));

    const request = controller.expectOne({
      method: 'PATCH',
      url: apiAssistantChatConversationPath(CHAT_ASSISTANT_ID, CHAT_THREAD_ID),
    });
    expect(request.request.body).toEqual({ title: '退貨與換貨' });
    request.flush(apiThreadSummary({ title: '退貨與換貨' }));

    expect(await renamed).toEqual({
      status: 'ready',
      data: { id: CHAT_THREAD_ID, title: '退貨與換貨', messageCount: 2, updatedAt: '2026-09-27T01:00:00+00:00' },
    });

    const rejected = pending(repository.renameChatThread(CHAT_ASSISTANT_ID, CHAT_THREAD_ID, ''));
    controller
      .expectOne({ method: 'PATCH', url: apiAssistantChatConversationPath(CHAT_ASSISTANT_ID, CHAT_THREAD_ID) })
      .flush({ message: '請輸入對話名稱。' }, { status: 422, statusText: 'Unprocessable Entity' });

    expect(await rejected).toEqual({ status: 'validation-failed', message: '請輸入對話名稱。' });
  });

  it('deletes a thread and returns the remaining list; 403 chat-thread maps to the thread permission-denied', async () => {
    const { repository } = setUpChat();
    const deleted = pending(repository.deleteChatThread(CHAT_ASSISTANT_ID, CHAT_THREAD_ID));

    controller
      .expectOne({ method: 'DELETE', url: apiAssistantChatConversationPath(CHAT_ASSISTANT_ID, CHAT_THREAD_ID) })
      .flush(apiThreadList({ threads: [] }));

    expect(await deleted).toEqual({
      status: 'ready',
      data: expect.objectContaining({ threads: [] }),
    });

    const forbidden = pending(repository.deleteChatThread(CHAT_ASSISTANT_ID, 'not-mine'));
    controller
      .expectOne({ method: 'DELETE', url: apiAssistantChatConversationPath(CHAT_ASSISTANT_ID, 'not-mine') })
      .flush(
        { reason: 'chat-thread', message: '找不到這段對話，或它不屬於你的帳號。' },
        { status: 403, statusText: 'Forbidden' },
      );

    expect(await forbidden).toEqual({
      status: 'permission-denied',
      reason: 'chat-thread',
      message: '找不到這段對話，或它不屬於你的帳號。',
    });
  });

  it('reads a conversation whose account message has no reply key at all, as the backend really sends it', async () => {
    // 後端省略 null 欄位（`JsonIgnore(WhenWritingNull)`）：使用者訊息沒有 `reply`、助理訊息沒有 `text`／`notice`。
    // 這是 `GET chat` 的實際 JSON；曾因 `reply !== null` 把 `undefined` 當成助理訊息而讓整頁顯示「目前無法開啟對話」。
    const wire = JSON.parse(`{
      "assistantId": "${CHAT_ASSISTANT_ID}", "assistantName": "客服助理", "purpose": "回答退換貨問題",
      "threadId": "${CHAT_THREAD_ID}", "title": "退貨", "historyMode": "saved", "welcome": "你好", "privacyNotice": "只有你看得到",
      "suggestedPrompts": [],
      "messages": [
        { "id": "0199a000-0000-7000-8000-0000000000d1", "author": "account", "text": "收到商品後幾天內可以退貨？", "createdAt": "2026-09-27T01:00:00+00:00" },
        { "id": "0199a000-0000-7000-8000-0000000000d2", "author": "assistant", "createdAt": "2026-09-27T01:00:01+00:00",
          "reply": { "kind": "company-data", "text": "7 天內可以退貨 [1]。", "nextSteps": [],
            "citations": [ { "id": "citation-1", "knowledgeBaseName": "退換貨政策", "documentName": "退換貨辦法.pdf", "excerpt": "7 天內", "updatedLabel": "2026-09-27" } ] } }
      ]
    }`) as ApiAssistantChatView;
    const { repository } = setUpChat();
    const result = pending(repository.getAssistantChat(CHAT_ASSISTANT_ID, CHAT_THREAD_ID));

    controller.expectOne({ method: 'GET', url: apiAssistantChatPath(CHAT_ASSISTANT_ID, CHAT_THREAD_ID) }).flush(wire);

    const view = await result;
    if (view.status !== 'ready') throw new Error(`expected ready, got ${view.status}`);
    expect(view.data.messages[0]).toEqual({
      id: '0199a000-0000-7000-8000-0000000000d1',
      author: 'account',
      text: '收到商品後幾天內可以退貨？',
      createdAt: '2026-09-27T01:00:00+00:00',
    });
    expect(view.data.messages[1]).toMatchObject({ author: 'assistant', reply: { kind: 'company-data', citationNotice: null } });
  });

  it('reads a conversation and adapts the flat backend reply into the frontend union for each kind', async () => {
    const { repository } = setUpChat();
    const result = pending(repository.getAssistantChat(CHAT_ASSISTANT_ID, CHAT_THREAD_ID));

    controller
      .expectOne({ method: 'GET', url: apiAssistantChatPath(CHAT_ASSISTANT_ID, CHAT_THREAD_ID) })
      .flush(
        apiAssistantChat({
          messages: [
            apiAccountMessage(),
            apiAssistantMessage({
              kind: 'company-data',
              text: '7 天內可以退貨。',
              citations: [
                {
                  id: 'citation-1',
                  knowledgeBaseName: '退換貨政策',
                  documentName: '退換貨辦法 2026 版.pdf',
                  excerpt: '商品到貨 7 天內可申請退貨。',
                  updatedLabel: '2026-01-01',
                },
              ],
              notice: null,
              nextSteps: [],
            }),
          ],
        }),
      );

    const view = await result;
    if (view.status !== 'ready') throw new Error(`expected ready, got ${view.status}`);
    expect(view.data.messages).toEqual([
      { id: apiAccountMessage().id, author: 'account', text: '收到商品後幾天內可以退貨？', createdAt: '2026-09-27T01:00:00+00:00' },
      {
        id: '0199a000-0000-7000-8000-0000000000d2',
        author: 'assistant',
        createdAt: '2026-09-27T01:00:01+00:00',
        reply: {
          kind: 'company-data',
          text: '7 天內可以退貨。',
          citations: [
            {
              id: 'citation-1',
              knowledgeBaseName: '退換貨政策',
              documentName: '退換貨辦法 2026 版.pdf',
              excerpt: '商品到貨 7 天內可申請退貨。',
              updatedLabel: '2026-01-01',
            },
          ],
          citationNotice: null,
        },
      },
    ]);
  });

  it('adapts a general-knowledge reply, keeping the notice and dropping citations', async () => {
    const { repository } = setUpChat();
    const result = pending(repository.getAssistantChat(CHAT_ASSISTANT_ID));

    controller.expectOne(apiAssistantChatPath(CHAT_ASSISTANT_ID)).flush(
      apiAssistantChat({
        messages: [
          apiAssistantMessage({
            kind: 'general-knowledge',
            text: '皮革建議乾布擦拭。',
            citations: [],
            notice: '以下是一般知識，並非貴公司資料。',
            nextSteps: [],
          }),
        ],
      }),
    );

    const view = await result;
    if (view.status !== 'ready') throw new Error(`expected ready, got ${view.status}`);
    const last = view.data.messages.at(-1);
    if (last?.author !== 'assistant') throw new Error('expected an assistant reply');
    expect(last.reply).toEqual({
      kind: 'general-knowledge',
      text: '皮革建議乾布擦拭。',
      notice: '以下是一般知識，並非貴公司資料。',
    });
  });

  it('adapts a no-result reply, keeping the next steps', async () => {
    const { repository } = setUpChat();
    const result = pending(repository.getAssistantChat(CHAT_ASSISTANT_ID));

    controller.expectOne(apiAssistantChatPath(CHAT_ASSISTANT_ID)).flush(
      apiAssistantChat({
        messages: [
          apiAssistantMessage({
            kind: 'no-result',
            text: '這題我查不到相關資料。',
            citations: [],
            notice: null,
            nextSteps: ['請改用其他關鍵字再試一次', '或聯絡客服人員'],
          }),
        ],
      }),
    );

    const view = await result;
    if (view.status !== 'ready') throw new Error(`expected ready, got ${view.status}`);
    const last = view.data.messages.at(-1);
    if (last?.author !== 'assistant') throw new Error('expected an assistant reply');
    expect(last.reply).toEqual({
      kind: 'no-result',
      text: '這題我查不到相關資料。',
      nextSteps: ['請改用其他關鍵字再試一次', '或聯絡客服人員'],
    });
  });

  it('turns 403 assistant-use on reading a conversation into the matching permission-denied', async () => {
    const { repository } = setUpChat();
    const result = pending(repository.getAssistantChat(CHAT_ASSISTANT_ID));

    controller
      .expectOne(apiAssistantChatPath(CHAT_ASSISTANT_ID))
      .flush(CHAT_FORBIDDEN, { status: 403, statusText: 'Forbidden' });

    expect(await result).toEqual({
      status: 'permission-denied',
      reason: 'assistant-use',
      message: CHAT_FORBIDDEN.message,
    });
  });

  it('lists the most recent conversations across assistants', async () => {
    const { repository } = setUpChat();
    const result = pending(repository.listRecentChatThreads());

    const recent: ApiRecentConversationView = {
      assistantId: CHAT_ASSISTANT_ID,
      assistantName: '客服助理',
      threadId: CHAT_THREAD_ID,
      title: '退貨問題',
      messageCount: 2,
      updatedAt: '2026-09-27T01:00:00+00:00',
    };
    controller.expectOne({ method: 'GET', url: API_RECENT_CONVERSATIONS_PATH }).flush([recent]);

    expect(await result).toEqual({ status: 'ready', data: [recent] });
  });

  it('falls back to an empty ready list instead of an error when the recent-conversations request fails', async () => {
    const { repository } = setUpChat();
    const result = pending(repository.listRecentChatThreads());

    controller.expectOne(API_RECENT_CONVERSATIONS_PATH).flush(null, { status: 500, statusText: 'Server Error' });

    expect(await result).toEqual({ status: 'ready', data: [] });
  });

  it('is cold: nothing is sent until subscribed', () => {
    const { repository } = setUpChat();
    repository.createChatThread(CHAT_ASSISTANT_ID);
    repository.listRecentChatThreads();

    controller.expectNone(apiAssistantChatConversationsPath(CHAT_ASSISTANT_ID));
    controller.expectNone(API_RECENT_CONVERSATIONS_PATH);
  });
});

const ASSISTANT_ID = '0199c000-0000-7000-8000-0000000000a1';
const OTHER_ASSISTANT_ID = '0199c000-0000-7000-8000-0000000000a2';
const DRAFT_ID = '0199c000-0000-7000-8000-0000000000f1';
const SHARED_KB_ID = '0199b000-0000-7000-8000-0000000000b2';

function forbidden(reason: string, message: string) {
  return { type: 'https://tools.ietf.org/html/rfc9110#section-15.5.4', title: 'Forbidden', status: 403, reason, message };
}

const ASSISTANT_CONFIGURATION_FORBIDDEN = forbidden('assistant-configuration', '你沒有這個助理的設定權限，或它已不存在。');
const DRAFT_FORBIDDEN = forbidden('assistant-draft', '你沒有這份草稿的權限，或它已不存在。');
const PUBLISHING_FORBIDDEN = forbidden('publishing', '你沒有這個助理的發布設定權限，或它已不存在。');

/** 與後端 `ApiErrors.ValidationFailed` 相同的 422：`errors` 是「欄位 → 訊息陣列」物件。 */
function validationFailed(message: string, errors: Record<string, string[]>) {
  return { type: 'https://tools.ietf.org/html/rfc4918#section-11.2', title: 'Unprocessable Entity', status: 422, message, errors };
}

function apiConfiguration(overrides: Partial<ApiAssistantConfiguration> = {}): ApiAssistantConfiguration {
  return {
    id: ASSISTANT_ID,
    ownerAccountId: ADMIN_ID,
    name: '門市問答助理',
    purpose: '回答門市作業問題',
    status: 'ready',
    viewerCanManage: true,
    createdAt: '2026-09-28T01:00:00+00:00',
    updatedAt: '2026-09-28T01:00:00+00:00',
    ...overrides,
  };
}

function apiSettings(overrides: Partial<ApiAssistantSettings> = {}): ApiAssistantSettings {
  return {
    configuration: apiConfiguration(),
    knowledgeBaseIds: [KB_ID],
    tone: 'professional',
    roleInstructions: '請用門市用語回答。',
    rules: {
      knowledgeScope: 'company-data-only',
      refusalMessage: '目前的資料中找不到答案。',
      showCitations: true,
      keepConversations: false,
    },
    ...overrides,
  };
}

function apiDraft(payload: unknown, overrides: Partial<ApiAssistantDraft> = {}): ApiAssistantDraft {
  return {
    id: DRAFT_ID,
    payload: payload as ApiAssistantDraft['payload'],
    schemaVersion: 1,
    revision: 1,
    savedAt: '2026-09-28T02:00:00+00:00',
    ...overrides,
  };
}

function apiChannel(overrides: Partial<ApiPublishingChannel> = {}): ApiPublishingChannel {
  return {
    id: `channel-platform:${ASSISTANT_ID}`,
    assistantId: ASSISTANT_ID,
    ownerAccountId: ADMIN_ID,
    // 後端的管道 name 是助理名稱。
    name: '門市問答助理',
    type: 'platform',
    status: 'published',
    statusDetail: '已分享給組織內 1 位成員。',
    updatedAt: '2026-09-28T03:00:00+00:00',
    ...overrides,
  };
}

function apiPublishing(overrides: Partial<ApiAssistantPublishing> = {}): ApiAssistantPublishing {
  const notAvailable = { status: 'not-available', message: '官網嵌入與 LINE 對外發布將於後續版本開放。' };
  return {
    assistantId: ASSISTANT_ID,
    assistantName: '門市問答助理',
    platform: {
      channel: apiChannel(),
      usagePath: `/app/chat/${ASSISTANT_ID}`,
      allowedAccountIds: [EMPLOYEE_ID],
      candidates: [
        { id: EMPLOYEE_ID, displayName: '安心商行客服同仁' },
        { id: CUSTOMER_ID, displayName: '安心商行客戶' },
      ],
    },
    website: notAvailable,
    line: notAvailable,
    ...overrides,
  };
}

function completeDraft(sources: AssistantDraft['sources']): AssistantDraft {
  return {
    ...createEmptyAssistantDraft(),
    templateId: 'answer-customer-questions',
    name: '門市問答助理',
    purpose: '回答門市作業問題',
    audience: 'account-members',
    sources,
    hasTrialAnswer: true,
    currentStep: 'test',
  };
}

describe('HybridDemoRepository assistants (issue #81)', () => {
  let controller: HttpTestingController;

  function setUpAssistants(
    viewer: AccountId | null = 'account-smb-admin',
    me: ApiViewerPermissions | null = { accountId: ADMIN_ID, demoAccountId: 'account-smb-admin', permissions: ALL_ADMIN },
  ) {
    const setup = setUp(viewer, me);
    controller = setup.controller;
    return setup;
  }

  /** 寫入 mock storage 的地方需要組織 id：沒有它時 scoped storage 不落地（見 `scoped-storage.ts`）。 */
  function setUpWithOrganization() {
    return setUpAssistants('account-smb-admin', {
      accountId: ADMIN_ID,
      demoAccountId: 'account-smb-admin',
      permissions: ALL_ADMIN,
      organizationId: 'org-a',
    });
  }

  const employee: ApiViewerPermissions = {
    accountId: EMPLOYEE_ID,
    demoAccountId: 'account-internal-employee',
    permissions: ['use-shared-assistants'],
  };

  afterEach(() => controller.verify());

  // ---------- 清單 ----------

  it('lists the owner’s assistants over HTTP with their real GUIDs', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.listAssistantConfigurations());

    controller.expectOne({ method: 'GET', url: API_ASSISTANTS_PATH }).flush([apiConfiguration()]);

    expect(await result).toEqual({
      status: 'ready',
      data: [
        {
          id: ASSISTANT_ID,
          ownerAccountId: ADMIN_ID,
          name: '門市問答助理',
          purpose: '回答門市作業問題',
          status: 'ready',
          audience: 'account-members',
          sharedWithAccountIds: [],
          knowledgeBaseIds: [],
          databaseIds: [],
          keepOwnConversations: true,
        },
      ],
    });
  });

  it('returns an empty management list without calling the API when the viewer cannot manage assistants', async () => {
    const { repository } = setUpAssistants('account-internal-employee', employee);

    expect(await pending(repository.listAssistantConfigurations())).toEqual({ status: 'ready', data: [] });
    controller.expectNone(API_ASSISTANTS_PATH);
  });

  it('lists usable assistants with ?usable=true and derives permission from viewerIsOwner', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.listUsableAssistants());

    controller.expectOne({ method: 'GET', url: API_USABLE_ASSISTANTS_PATH }).flush([
      { id: ASSISTANT_ID, name: '門市問答助理', purpose: '回答門市作業問題', status: 'ready', viewerIsOwner: true },
      { id: OTHER_ASSISTANT_ID, name: '同仁分享的助理', purpose: '內部問答', status: 'paused', viewerIsOwner: false },
    ]);

    expect(await result).toEqual({
      status: 'ready',
      data: [
        { id: ASSISTANT_ID, name: '門市問答助理', purpose: '回答門市作業問題', status: 'ready', audience: 'account-members', permission: 'configure' },
        { id: OTHER_ASSISTANT_ID, name: '同仁分享的助理', purpose: '內部問答', status: 'paused', audience: 'account-members', permission: 'use' },
      ],
    });
  });

  it('marks an owner who can only manage publishing as publish, and a shared-with viewer as use', async () => {
    const { repository } = setUpAssistants('account-internal-employee', {
      ...employee,
      permissions: ['use-shared-assistants', 'manage-publishing'],
    });
    const result = pending(repository.listUsableAssistants());

    controller.expectOne(API_USABLE_ASSISTANTS_PATH).flush([
      { id: ASSISTANT_ID, name: 'A', purpose: 'a', status: 'ready', viewerIsOwner: true },
      { id: OTHER_ASSISTANT_ID, name: 'B', purpose: 'b', status: 'ready', viewerIsOwner: false },
    ]);

    const view = await result;
    expect(view.status === 'ready' ? view.data.map((assistant) => assistant.permission) : []).toEqual(['publish', 'use']);
  });

  it('leaves server errors on the usable list to the screen', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.listUsableAssistants());

    controller.expectOne(API_USABLE_ASSISTANTS_PATH).flush(null, { status: 500, statusText: 'Server Error' });

    await expect(result).rejects.toMatchObject({ status: 500 });
  });

  // ---------- 設定 ----------

  it('reads settings, turning the connected knowledge base ids into sources and keepConversations into keepOwnConversations', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.getAssistantSettings(ASSISTANT_ID));

    controller.expectOne({ method: 'GET', url: apiAssistantSettingsPath(ASSISTANT_ID) }).flush(apiSettings());

    const view = await result;
    expect(view.status).toBe('ready');
    if (view.status !== 'ready') return;
    expect(view.data.sources).toEqual([{ id: KB_ID, type: 'knowledge-base' }]);
    expect(view.data.configuration).toMatchObject({ id: ASSISTANT_ID, knowledgeBaseIds: [KB_ID], keepOwnConversations: false });
    expect(view.data.tone).toBe('professional');
    expect(view.data.rules).toEqual({
      knowledgeScope: 'company-data-only',
      refusalMessage: '目前的資料中找不到答案。',
      showCitations: true,
      keepOwnConversations: false,
      dataWriteDatabaseId: null,
      dataWritePurpose: '',
      periodicReport: 'off',
    });
    // 建立後從未修改過：與 mock 的「尚未編輯」相同。
    expect(view.data.savedAt).toBeNull();
  });

  it('maps 403 assistant-configuration and a 404 (a non-GUID id such as a mock id) to the same permission-denied', async () => {
    const { repository } = setUpAssistants();
    const denied = pending(repository.getAssistantSettings(ASSISTANT_ID));
    controller
      .expectOne(apiAssistantSettingsPath(ASSISTANT_ID))
      .flush(ASSISTANT_CONFIGURATION_FORBIDDEN, { status: 403, statusText: 'Forbidden' });
    const missing = pending(repository.getAssistantSettings('assistant-customer-service'));
    controller
      .expectOne(apiAssistantSettingsPath('assistant-customer-service'))
      .flush(null, { status: 404, statusText: 'Not Found' });

    const expected = {
      status: 'permission-denied',
      reason: 'assistant-configuration',
      message: '你沒有這個助理的設定權限，或它已不存在。',
    };
    expect(await denied).toEqual(expected);
    expect(await missing).toEqual(expected);
  });

  it('PATCHes only the fields the backend knows, renaming keepOwnConversations', async () => {
    const { repository } = setUpAssistants();
    const result = pending(
      repository.updateAssistantSettings(ASSISTANT_ID, {
        name: '新名稱',
        audience: 'members-and-external-customers',
        rules: { keepOwnConversations: true, periodicReport: 'weekly', dataWritePurpose: '收集' },
      }),
    );

    const request = controller.expectOne({ method: 'PATCH', url: apiAssistantSettingsPath(ASSISTANT_ID) });
    expect(request.request.body).toEqual({ name: '新名稱', rules: { keepConversations: true } });
    request.flush(apiSettings({ configuration: apiConfiguration({ name: '新名稱', updatedAt: '2026-09-28T04:00:00+00:00' }) }));

    expect(await result).toMatchObject({
      status: 'ready',
      data: { configuration: { name: '新名稱' }, savedAt: '2026-09-28T04:00:00+00:00' },
    });
  });

  it('turns a 422 errors object on PATCH into per-field errors, keeping every message', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.updateAssistantSettings(ASSISTANT_ID, { name: '', rules: { refusalMessage: '' } }));

    controller.expectOne(apiAssistantSettingsPath(ASSISTANT_ID)).flush(
      validationFailed('請輸入助理名稱。', {
        name: ['請輸入助理名稱。'],
        refusalMessage: ['請填寫找不到資料時的回覆內容。'],
        mystery: ['後端新加、前端還不認得的欄位。'],
      }),
      { status: 422, statusText: 'Unprocessable Entity' },
    );

    expect(await result).toEqual({
      status: 'validation-failed',
      message: '請輸入助理名稱。',
      errors: [
        { field: 'name', message: '請輸入助理名稱。' },
        { field: 'refusalMessage', message: '請填寫找不到資料時的回覆內容。' },
        { field: 'name', message: '後端新加、前端還不認得的欄位。' },
      ],
    });
  });

  it('connects a real knowledge base on the settings page with a PUT to its GUID and shows it as a source', async () => {
    const { repository } = setUpWithOrganization();
    const result = pending(
      repository.setAssistantSourceConnection(ASSISTANT_ID, { id: SHARED_KB_ID, type: 'knowledge-base' }, true),
    );

    const request = controller.expectOne({
      method: 'PUT',
      url: apiAssistantKnowledgeSourcePath(ASSISTANT_ID, SHARED_KB_ID),
    });
    request.flush(apiSettings({ knowledgeBaseIds: [KB_ID, SHARED_KB_ID] }));

    const view = await result;
    expect(view.status).toBe('ready');
    if (view.status !== 'ready') return;
    expect(view.data.sources).toContainEqual({ id: SHARED_KB_ID, type: 'knowledge-base' });
  });

  it('disconnects with a DELETE and turns 422 last-source into a sources field error', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.setAssistantSourceConnection(ASSISTANT_ID, { id: KB_ID, type: 'knowledge-base' }, false));

    controller.expectOne({ method: 'DELETE', url: apiAssistantKnowledgeSourcePath(ASSISTANT_ID, KB_ID) }).flush(
      {
        status: 422,
        reason: 'last-source',
        message: '助理至少要連接一個知識庫或資料庫才能回答問題，無法解除最後一個來源。',
        errors: { sources: ['助理至少要連接一個知識庫或資料庫才能回答問題，無法解除最後一個來源。'] },
      },
      { status: 422, statusText: 'Unprocessable Entity' },
    );

    expect(await result).toEqual({
      status: 'validation-failed',
      message: '助理至少要連接一個知識庫或資料庫才能回答問題，無法解除最後一個來源。',
      errors: [{ field: 'sources', message: '助理至少要連接一個知識庫或資料庫才能回答問題，無法解除最後一個來源。' }],
    });
  });

  it('refuses a database source in API mode without calling the API', async () => {
    const { repository } = setUpAssistants();

    expect(
      await pending(repository.setAssistantSourceConnection(ASSISTANT_ID, { id: 'database-orders', type: 'database' }, true)),
    ).toMatchObject({ status: 'validation-failed', errors: [{ field: 'sources', message: expect.stringContaining('後續版本開放') }] });
    controller.expectNone(() => true);
  });

  it('deletes an assistant with a DELETE (204) and maps 403 to the configuration permission-denied', async () => {
    const { repository } = setUpAssistants();
    const deleted = pending(repository.deleteAssistant(ASSISTANT_ID));
    controller.expectOne({ method: 'DELETE', url: apiAssistantPath(ASSISTANT_ID) }).flush(null, { status: 204, statusText: 'No Content' });
    expect(await deleted).toEqual({ status: 'ready', data: null });

    const denied = pending(repository.deleteAssistant(OTHER_ASSISTANT_ID));
    controller
      .expectOne(apiAssistantPath(OTHER_ASSISTANT_ID))
      .flush(ASSISTANT_CONFIGURATION_FORBIDDEN, { status: 403, statusText: 'Forbidden' });
    expect(await denied).toMatchObject({ status: 'permission-denied', reason: 'assistant-configuration' });
  });

  // ---------- 草稿 ----------

  it('lists drafts, restoring the payload and keeping the revision', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.listNamedAssistantDrafts());

    const draft = { ...createEmptyAssistantDraft(), name: '門市問答助理', currentStep: 'sources' };
    controller.expectOne({ method: 'GET', url: API_ASSISTANT_DRAFTS_PATH }).flush([apiDraft(draft, { revision: 3 })]);

    expect(await result).toEqual({
      status: 'ready',
      data: [{ id: DRAFT_ID, draft, savedAt: '2026-09-28T02:00:00+00:00', revision: 3 }],
    });
  });

  it('answers the draft list with assistant-draft without calling the API when the viewer cannot manage assistants', async () => {
    const { repository } = setUpAssistants('account-internal-employee', employee);

    expect(await pending(repository.listNamedAssistantDrafts())).toMatchObject({
      status: 'permission-denied',
      reason: 'assistant-draft',
    });
    controller.expectNone(API_ASSISTANT_DRAFTS_PATH);
  });

  it('creates an empty draft with a POST of the empty payload and schema version 1', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.createNamedAssistantDraft());

    const request = controller.expectOne({ method: 'POST', url: API_ASSISTANT_DRAFTS_PATH });
    expect(request.request.body).toEqual({ payload: createEmptyAssistantDraft(), schemaVersion: 1 });
    request.flush(apiDraft(createEmptyAssistantDraft()), { status: 201, statusText: 'Created' });

    expect(await result).toMatchObject({ status: 'ready', data: { id: DRAFT_ID, revision: 1 } });
  });

  it('maps 403 assistant-draft (another account’s or a missing draft) on reading a draft', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.getNamedAssistantDraft(DRAFT_ID));

    controller.expectOne(apiAssistantDraftPath(DRAFT_ID)).flush(DRAFT_FORBIDDEN, { status: 403, statusText: 'Forbidden' });

    expect(await result).toEqual({
      status: 'permission-denied',
      reason: 'assistant-draft',
      message: '你沒有這份草稿的權限，或它已不存在。',
    });
  });

  it('saves a draft with a PUT carrying the revision, and returns the next revision', async () => {
    const { repository } = setUpAssistants();
    const draft = completeDraft([{ id: KB_ID, type: 'knowledge-base' }]);
    const result = pending(repository.saveNamedAssistantDraft(DRAFT_ID, draft, 4));

    const request = controller.expectOne({ method: 'PUT', url: apiAssistantDraftPath(DRAFT_ID) });
    expect(request.request.body).toEqual({ payload: draft, revision: 4, schemaVersion: 1 });
    request.flush(apiDraft(draft, { revision: 5, savedAt: '2026-09-28T05:00:00+00:00' }));

    expect(await result).toMatchObject({ status: 'ready', data: { revision: 5, savedAt: '2026-09-28T05:00:00+00:00' } });
  });

  it('turns a 409 draft-revision-conflict into conflict with the API’s message', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.saveNamedAssistantDraft(DRAFT_ID, createEmptyAssistantDraft(), 1));

    controller.expectOne(apiAssistantDraftPath(DRAFT_ID)).flush(
      { status: 409, reason: 'draft-revision-conflict', message: '這份草稿已在其他分頁被更新過，請重新載入後再修改。' },
      { status: 409, statusText: 'Conflict' },
    );

    expect(await result).toEqual({ status: 'conflict', message: '這份草稿已在其他分頁被更新過，請重新載入後再修改。' });
  });

  it('discards a draft with a DELETE', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.discardNamedAssistantDraft(DRAFT_ID));

    controller.expectOne({ method: 'DELETE', url: apiAssistantDraftPath(DRAFT_ID) }).flush(null, { status: 204, statusText: 'No Content' });

    expect(await result).toEqual({ status: 'ready', data: null });
  });

  it('creates an assistant from a draft with POST { draftId } and returns the new assistant', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.createAssistantFromDraft(DRAFT_ID));

    const request = controller.expectOne({ method: 'POST', url: API_ASSISTANTS_PATH });
    expect(request.request.body).toEqual({ draftId: DRAFT_ID });
    request.flush(apiConfiguration(), { status: 201, statusText: 'Created' });

    expect(await result).toMatchObject({ status: 'ready', data: { id: ASSISTANT_ID, name: '門市問答助理' } });
  });

  it('turns the 422 errors object of creating from a draft into the wizard’s per-field errors', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.createAssistantFromDraft(DRAFT_ID));

    controller.expectOne(API_ASSISTANTS_PATH).flush(
      validationFailed('草稿裡的知識庫無法連接，可能已不存在、屬於其他組織，或已被收回分享。', {
        sources: ['草稿裡的知識庫無法連接，可能已不存在、屬於其他組織，或已被收回分享。'],
        audience: ['對外發布將於後續版本開放，目前僅支援組織內使用。'],
        tone: ['語氣設定不正確。'],
      }),
      { status: 422, statusText: 'Unprocessable Entity' },
    );

    expect(await result).toEqual({
      status: 'validation-failed',
      message: '草稿裡的知識庫無法連接，可能已不存在、屬於其他組織，或已被收回分享。',
      errors: [
        { field: 'sources', message: '草稿裡的知識庫無法連接，可能已不存在、屬於其他組織，或已被收回分享。' },
        { field: 'audience', message: '對外發布將於後續版本開放，目前僅支援組織內使用。' },
        { field: 'tone', message: '語氣設定不正確。' },
      ],
    });
  });

  it('maps a 403 assistant-draft on creating from a missing draft', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.createAssistantFromDraft(DRAFT_ID));

    controller.expectOne(API_ASSISTANTS_PATH).flush(DRAFT_FORBIDDEN, { status: 403, statusText: 'Forbidden' });

    expect(await result).toMatchObject({ status: 'permission-denied', reason: 'assistant-draft' });
  });

  // ---------- 試問（issue #82，M3 計畫 Slice 8、12） ----------

  it('calls trial-answers with the free-text question and maps the reply, passages and threshold', async () => {
    const { repository } = setUpAssistants();
    const draft = completeDraft([{ id: KB_ID, type: 'knowledge-base' }]);
    const result = pending(
      repository.previewTrialAnswer(DRAFT_ID, {
        question: '收到商品後幾天內可以申請退貨？',
        sources: draft.sources,
        rules: draft.rules,
      }),
    );

    const request = controller.expectOne({ method: 'POST', url: apiTrialAnswersPath(DRAFT_ID) });
    expect(request.request.body).toEqual({ question: '收到商品後幾天內可以申請退貨？' });
    request.flush({
      reply: {
        kind: 'company-data',
        text: '收到商品後 7 天內可以申請退貨。',
        citations: [
          {
            ordinal: 1,
            knowledgeBaseId: KB_ID,
            knowledgeBaseName: '退換貨政策',
            documentId: '0199c000-0000-7000-8000-0000000000d1',
            documentName: '退換貨政策.pdf',
            locationLabel: '第 1 頁',
            excerpt: '消費者於收受商品後七日內，得申請退貨。',
            score: 0.82,
          },
        ],
        notice: null,
        nextSteps: [],
      },
      passages: [
        {
          knowledgeBaseId: KB_ID,
          knowledgeBaseName: '退換貨政策',
          documentId: '0199c000-0000-7000-8000-0000000000d1',
          documentName: '退換貨政策.pdf',
          locationLabel: '第 1 頁',
          excerpt: '消費者於收受商品後七日內，得申請退貨。',
          score: 0.82,
        },
      ],
      threshold: 0.3,
    });

    expect(await result).toEqual({
      status: 'ready',
      data: {
        question: '收到商品後幾天內可以申請退貨？',
        reply: {
          kind: 'company-data',
          text: '收到商品後 7 天內可以申請退貨。',
          citations: [
            {
              knowledgeBaseName: '退換貨政策',
              documentName: '退換貨政策.pdf',
              locationLabel: '第 1 頁',
              excerpt: '消費者於收受商品後七日內，得申請退貨。',
              score: 0.82,
            },
          ],
          citationNotice: null,
        },
        passages: [
          {
            knowledgeBaseName: '退換貨政策',
            documentName: '退換貨政策.pdf',
            locationLabel: '第 1 頁',
            excerpt: '消費者於收受商品後七日內，得申請退貨。',
            score: 0.82,
          },
        ],
        threshold: 0.3,
      },
    });
  });

  it('maps a general-knowledge reply from the actual JSON the backend sends (nullable notice omitted, issue #103’s lesson)', async () => {
    const { repository } = setUpAssistants();
    const draft = completeDraft([]);
    const result = pending(
      repository.previewTrialAnswer(DRAFT_ID, {
        question: '皮革商品平常要怎麼保養？',
        sources: draft.sources,
        rules: { ...draft.rules, knowledgeScope: 'allow-general-knowledge' },
      }),
    );

    const request = controller.expectOne({ method: 'POST', url: apiTrialAnswersPath(DRAFT_ID) });
    // 用實際的 JSON 字串驗證：PR #103 發現後端會用 `JsonIgnore(WhenWritingNull)` 省略掉
    // nullable 的欄位，而 OpenAPI 仍標成必填——這裡故意不送 `notice`，確認 adapter 不會炸掉。
    const body = JSON.parse(
      '{"reply":{"kind":"general-knowledge","text":"一般建議避免長時間日曬與潮濕。","citations":[],"nextSteps":[]},"passages":[],"threshold":0.3}',
    ) as unknown;
    request.flush(body as never);

    expect(await result).toEqual({
      status: 'ready',
      data: {
        question: '皮革商品平常要怎麼保養？',
        reply: { kind: 'general-knowledge', text: '一般建議避免長時間日曬與潮濕。', notice: '' },
        passages: [],
        threshold: 0.3,
      },
    });
  });

  it('is validation-failed (422) when the question is blank or over 2,000 characters', async () => {
    const { repository } = setUpAssistants();
    const draft = completeDraft([]);
    const result = pending(
      repository.previewTrialAnswer(DRAFT_ID, { question: '', sources: draft.sources, rules: draft.rules }),
    );

    controller
      .expectOne({ method: 'POST', url: apiTrialAnswersPath(DRAFT_ID) })
      .flush({ message: '請先輸入問題。' }, { status: 422, statusText: 'Unprocessable Entity' });

    expect(await result).toEqual({ status: 'validation-failed', message: '請先輸入問題。' });
  });

  it('maps a 403 assistant-draft on a missing or someone else’s draft', async () => {
    const { repository } = setUpAssistants();
    const draft = completeDraft([]);
    const result = pending(
      repository.previewTrialAnswer(DRAFT_ID, { question: '問題', sources: draft.sources, rules: draft.rules }),
    );

    controller
      .expectOne({ method: 'POST', url: apiTrialAnswersPath(DRAFT_ID) })
      .flush(DRAFT_FORBIDDEN, { status: 403, statusText: 'Forbidden' });

    expect(await result).toMatchObject({ status: 'permission-denied', reason: 'assistant-draft' });
  });

  it('is unavailable (503) when the embedding or chat model is not configured', async () => {
    const { repository } = setUpAssistants();
    const draft = completeDraft([]);
    const result = pending(
      repository.previewTrialAnswer(DRAFT_ID, { question: '問題', sources: draft.sources, rules: draft.rules }),
    );

    controller
      .expectOne({ method: 'POST', url: apiTrialAnswersPath(DRAFT_ID) })
      .flush({ message: '尚未設定對話模型，請先完成設定。' }, { status: 503, statusText: 'Service Unavailable' });

    expect(await result).toEqual({ status: 'unavailable', message: '尚未設定對話模型，請先完成設定。' });
  });

  // ---------- 可連接來源 ----------

  it('reads connectable sources from /connectable-sources: own, public and shared-with-me knowledge bases, no databases', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.listConnectableSources());

    controller.expectOne({ method: 'GET', url: API_CONNECTABLE_SOURCES_PATH }).flush([
      { id: KB_ID, type: 'knowledge-base', name: '門市作業手冊', summary: '1 份文件、0 則 FAQ', permission: 'owner', status: 'needs-attention', updatedAt: '2026-09-27T01:00:00+00:00' },
      { id: SHARED_KB_ID, type: 'knowledge-base', name: '同仁分享的手冊', summary: '2 份文件、1 則 FAQ', permission: 'read-only', status: 'ready', updatedAt: '2026-09-27T02:00:00+00:00' },
    ]);

    expect(await result).toEqual({
      status: 'ready',
      data: [
        { id: KB_ID, type: 'knowledge-base', name: '門市作業手冊', summary: '1 份文件、0 則 FAQ', permission: 'owner', status: 'needs-attention', updatedAt: '2026-09-27T01:00:00+00:00' },
        { id: SHARED_KB_ID, type: 'knowledge-base', name: '同仁分享的手冊', summary: '2 份文件、1 則 FAQ', permission: 'read-only', status: 'ready', updatedAt: '2026-09-27T02:00:00+00:00' },
      ],
    });
    controller.expectNone(API_KNOWLEDGE_BASES_PATH);
  });

  it('returns an empty ready list of sources without calling the API when the viewer cannot manage assistants or is signed out', async () => {
    const { repository } = setUpAssistants('account-internal-employee', employee);
    expect(await pending(repository.listConnectableSources())).toEqual({ status: 'ready', data: [] });

    const signedOut = new HybridDemoRepository(
      DEMO_SEED,
      { storage: createMemoryStorage(), viewer: () => null },
      { http: TestBed.inject(HttpClient), viewerPermissions: () => null },
    );
    expect(await pending(signedOut.listConnectableSources())).toEqual({ status: 'ready', data: [] });
    controller.expectNone(API_CONNECTABLE_SOURCES_PATH);
  });

  it('maps a 403 on connectable sources to the reason the API sent', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.listConnectableSources());

    controller
      .expectOne(API_CONNECTABLE_SOURCES_PATH)
      .flush(ASSISTANT_CONFIGURATION_FORBIDDEN, { status: 403, statusText: 'Forbidden' });

    expect(await result).toMatchObject({ status: 'permission-denied', reason: 'assistant-configuration' });
  });

  /**
   * PR #91 的教訓：可連接清單改讀 API 後，建立助理若仍用 mock 的種子清單驗證，真實 GUID 會被
   * 默默濾掉。這裡整條走一次：從 API 讀到的知識庫 GUID → 建立草稿 → 保存（payload 帶著 GUID）
   * → 由草稿建立 → 讀設定，來源仍是同一個 GUID。
   */
  it('keeps a knowledge base GUID read from the API all the way from the wizard to the created assistant’s sources', async () => {
    const { repository } = setUpWithOrganization();

    const sources = pending(repository.listConnectableSources());
    controller.expectOne(API_CONNECTABLE_SOURCES_PATH).flush([
      { id: SHARED_KB_ID, type: 'knowledge-base', name: '同仁分享的手冊', summary: '', permission: 'read-only', status: 'ready', updatedAt: '2026-09-27T02:00:00+00:00' },
    ]);
    const listed = await sources;
    const picked = listed.status === 'ready' ? listed.data[0] : null;
    expect(picked?.id).toBe(SHARED_KB_ID);

    const created = pending(repository.createNamedAssistantDraft());
    controller.expectOne({ method: 'POST', url: API_ASSISTANT_DRAFTS_PATH }).flush(apiDraft(createEmptyAssistantDraft()), { status: 201, statusText: 'Created' });
    const draftView = await created;
    if (draftView.status !== 'ready' || picked === null) throw new Error('expected a draft and a source');

    const draft = completeDraft([{ id: picked.id, type: 'knowledge-base' }]);
    const saved = pending(repository.saveNamedAssistantDraft(draftView.data.id, draft, draftView.data.revision));
    const put = controller.expectOne({ method: 'PUT', url: apiAssistantDraftPath(DRAFT_ID) });
    expect((put.request.body as { payload: AssistantDraft }).payload.sources).toEqual([{ id: SHARED_KB_ID, type: 'knowledge-base' }]);
    put.flush(apiDraft(draft, { revision: 2 }));
    expect(await saved).toMatchObject({ status: 'ready', data: { revision: 2 } });

    const assistant = pending(repository.createAssistantFromDraft(DRAFT_ID));
    controller.expectOne({ method: 'POST', url: API_ASSISTANTS_PATH }).flush(apiConfiguration(), { status: 201, statusText: 'Created' });
    const result = await assistant;
    if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);

    const settings = pending(repository.getAssistantSettings(result.data.id));
    controller.expectOne(apiAssistantSettingsPath(ASSISTANT_ID)).flush(apiSettings({ knowledgeBaseIds: [SHARED_KB_ID] }));
    expect(await settings).toMatchObject({ status: 'ready', data: { sources: [{ id: SHARED_KB_ID, type: 'knowledge-base' }] } });
  });

  // ---------- 發布 ----------

  it('reads publishing: the platform channel is real, website and LINE are not available yet', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.getAssistantPublishing(ASSISTANT_ID));

    controller.expectOne({ method: 'GET', url: apiAssistantPublishingPath(ASSISTANT_ID) }).flush(apiPublishing());

    const view = await result;
    expect(view.status).toBe('ready');
    if (view.status !== 'ready') return;
    expect(view.data.platform).toMatchObject({
      usagePath: `/app/chat/${ASSISTANT_ID}`,
      allowedAccountIds: [EMPLOYEE_ID],
      candidates: [
        { id: EMPLOYEE_ID, displayName: '安心商行客服同仁' },
        { id: CUSTOMER_ID, displayName: '安心商行客戶' },
      ],
      channel: { name: '組織內部分享', type: 'platform', status: 'published' },
    });
    expect(view.data.website).toMatchObject({
      availability: 'not-available',
      message: '官網嵌入與 LINE 對外發布將於後續版本開放。',
      channel: { type: 'website', name: '官網嵌入', status: 'not-configured' },
    });
    expect(view.data.line).toMatchObject({ availability: 'not-available', channel: { type: 'line', name: 'LINE' } });
  });

  it('maps 403 publishing (not the owner, no manage-publishing, or missing)', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.getAssistantPublishing(ASSISTANT_ID));

    controller.expectOne(apiAssistantPublishingPath(ASSISTANT_ID)).flush(PUBLISHING_FORBIDDEN, { status: 403, statusText: 'Forbidden' });

    expect(await result).toMatchObject({ status: 'permission-denied', reason: 'publishing' });
  });

  it('shares on the platform with a PUT of the whole account list', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.updatePlatformSharing(ASSISTANT_ID, [EMPLOYEE_ID as AccountId]));

    const request = controller.expectOne({ method: 'PUT', url: apiAssistantPlatformSharingPath(ASSISTANT_ID) });
    expect(request.request.body).toEqual({ accountIds: [EMPLOYEE_ID] });
    request.flush(apiPublishing().platform);

    expect(await result).toMatchObject({ status: 'ready', data: { allowedAccountIds: [EMPLOYEE_ID] } });
  });

  it('maps 403 publishing on sharing', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.updatePlatformSharing(ASSISTANT_ID, []));

    controller.expectOne(apiAssistantPlatformSharingPath(ASSISTANT_ID)).flush(PUBLISHING_FORBIDDEN, { status: 403, statusText: 'Forbidden' });

    expect(await result).toMatchObject({ status: 'permission-denied', reason: 'publishing' });
  });

  it('pauses the platform channel with a PUT and refuses website/LINE without calling the API', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.setPublishingChannelPaused(ASSISTANT_ID, 'platform', true));

    const request = controller.expectOne({ method: 'PUT', url: apiAssistantPlatformPausedPath(ASSISTANT_ID) });
    expect(request.request.body).toEqual({ paused: true });
    request.flush(apiChannel({ status: 'paused', statusDetail: '已暫停，除了你自己以外沒有人可以使用這個助理。' }));

    expect(await result).toMatchObject({ status: 'ready', data: { type: 'platform', status: 'paused' } });
    expect(await pending(repository.setPublishingChannelPaused(ASSISTANT_ID, 'line', true))).toMatchObject({
      status: 'permission-denied',
      reason: 'publishing',
    });
  });

  it('builds the channel overview from the owner’s assistants, skipping one whose publishing is refused', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.listChannelOverview());

    controller
      .expectOne(API_ASSISTANTS_PATH)
      .flush([apiConfiguration(), apiConfiguration({ id: OTHER_ASSISTANT_ID, name: '另一個助理' })]);
    controller.expectOne(apiAssistantPublishingPath(ASSISTANT_ID)).flush(apiPublishing());
    controller
      .expectOne(apiAssistantPublishingPath(OTHER_ASSISTANT_ID))
      .flush(PUBLISHING_FORBIDDEN, { status: 403, statusText: 'Forbidden' });

    const view = await result;
    expect(view.status).toBe('ready');
    if (view.status !== 'ready') return;
    expect(view.data).toHaveLength(1);
    expect(view.data[0].assistantId).toBe(ASSISTANT_ID);
    expect(view.data[0].channels.map((channel) => [channel.type, channel.status])).toEqual([
      ['platform', 'published'],
      ['website', 'not-configured'],
      ['line', 'not-configured'],
    ]);
  });

  it('lists publishing channels as the flattened overview, and skips the API entirely without manage-assistants', async () => {
    const { repository } = setUpAssistants();
    const result = pending(repository.listPublishingChannels());
    controller.expectOne(API_ASSISTANTS_PATH).flush([apiConfiguration()]);
    controller.expectOne(apiAssistantPublishingPath(ASSISTANT_ID)).flush(apiPublishing());
    const view = await result;
    expect(view.status === 'ready' ? view.data.map((channel) => channel.name) : []).toEqual(['組織內部分享', '官網嵌入', 'LINE']);

    const employeeRepository = new HybridDemoRepository(
      DEMO_SEED,
      { storage: createMemoryStorage(), viewer: () => 'account-internal-employee' },
      { http: TestBed.inject(HttpClient), viewerPermissions: () => employee },
    );
    expect(await pending(employeeRepository.listPublishingChannels())).toEqual({ status: 'ready', data: [] });
    controller.expectNone(API_ASSISTANTS_PATH);
  });

  it('is cold: nothing is sent until subscribed', () => {
    const { repository } = setUpAssistants();

    repository.deleteAssistant(ASSISTANT_ID);
    repository.saveNamedAssistantDraft(DRAFT_ID, createEmptyAssistantDraft(), 1);
    repository.updatePlatformSharing(ASSISTANT_ID, []);

    controller.expectNone(() => true);
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
