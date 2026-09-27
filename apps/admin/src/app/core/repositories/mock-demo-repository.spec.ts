import { firstValueFrom } from 'rxjs';
import { MockDemoRepository } from './mock-demo-repository';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import type { SubmissionConsentStatus } from '../domain/conversation.model';
import type { CreateMemberResult } from './demo-repository';

describe('MockDemoRepository', () => {
  let repository: MockDemoRepository;

  beforeEach(() => {
    // 助理清單方法改為由建構子的 `viewer` 選項推導身分（不再由呼叫端傳入），這裡固定
    // 用種子的 admin 帳號；需要別的身分的測試另外建立自己的 repository 實例。
    repository = new MockDemoRepository(DEMO_SEED, { viewer: () => 'account-smb-admin' });
  });

  it('keeps assistant configuration private while allowing a shared assistant to be used', async () => {
    const employeeRepository = new MockDemoRepository(DEMO_SEED, {
      viewer: () => 'account-internal-employee',
    });

    const adminConfigurations = await firstValueFrom(repository.listAssistantConfigurations());
    const employeeConfigurations = await firstValueFrom(employeeRepository.listAssistantConfigurations());
    const employeeAssistants = await firstValueFrom(employeeRepository.listUsableAssistants());

    expect(adminConfigurations).toMatchObject({
      status: 'ready',
      data: [
        {
          id: 'assistant-customer-service',
          ownerAccountId: 'account-smb-admin',
        },
        {
          id: 'assistant-internal-onboarding',
          ownerAccountId: 'account-smb-admin',
        },
      ],
    });
    expect(employeeConfigurations).toEqual({ status: 'ready', data: [] });
    expect(employeeAssistants).toMatchObject({
      status: 'ready',
      data: [{ id: 'assistant-customer-service' }],
    });
  });

  it('only exposes private conversations to the account that owns them', () => {
    const employeeView = repository.getConversation(
      'account-internal-employee',
      'conversation-employee-private',
    );
    const assistantOwnerView = repository.getConversation(
      'account-smb-admin',
      'conversation-employee-private',
    );
    const customerView = repository.listPrivateConversations(
      'account-external-customer',
    );

    expect(employeeView.status).toBe('ready');
    if (employeeView.status === 'ready') {
      expect(employeeView.data.id).toBe('conversation-employee-private');
      expect(employeeView.data.accountId).toBe('account-internal-employee');
      expect(employeeView.data.messages[0]?.text).toBe('如何處理退貨申請？');
    }
    expect(assistantOwnerView).toEqual({
      status: 'permission-denied',
      reason: 'private-conversation',
      message: expect.any(String),
    });
    expect(customerView).toMatchObject({
      status: 'ready',
      data: [{ accountId: 'account-external-customer' }],
    });
    if (customerView.status === 'ready') {
      expect(
        customerView.data.every(
          (conversation) =>
            conversation.accountId === 'account-external-customer',
        ),
      ).toBe(true);
    }
  });

  it('allows only the specified data manager to read consented structured records', () => {
    const managerView = repository.listManagedSubmissions('account-smb-admin');
    const employeeView = repository.listManagedSubmissions(
      'account-internal-employee',
    );

    expect(managerView).toMatchObject({
      status: 'ready',
      data: [
        {
          id: 'submission-customer-authorized',
          dataManagerAccountId: 'account-smb-admin',
          consentStatus: 'consented',
        },
      ],
    });
    expect(employeeView).toEqual({ status: 'ready', data: [] });
  });

  it('lets an external customer submit an authorized form and see only their own tracking data', () => {
    const submitted = repository.submitAuthorizedForm(
      'account-external-customer',
      {
        assistantId: 'assistant-customer-service',
        orderNumber: 'DEMO-1042',
        contactEmail: 'customer@example.test',
        issue: '尚未收到出貨通知',
        consent: true,
      },
    );
    const unauthorized = repository.submitAuthorizedForm(
      'account-internal-employee',
      {
        assistantId: 'assistant-customer-service',
        orderNumber: 'DEMO-1042',
        contactEmail: 'employee@example.test',
        issue: '尚未收到出貨通知',
        consent: true,
      },
    );
    const customerView = repository.listOwnSubmissions(
      'account-external-customer',
    );
    const employeeView = repository.listOwnSubmissions(
      'account-internal-employee',
    );

    expect(submitted).toMatchObject({
      status: 'ready',
      data: {
        submittedByAccountId: 'account-external-customer',
        consentStatus: 'consented',
      },
    });
    expect(unauthorized).toMatchObject({
      status: 'permission-denied',
      reason: 'authorized-form',
    });
    expect(customerView).toMatchObject({
      status: 'ready',
      data: [
        {
          id: 'submission-customer-authorized',
          submittedByAccountId: 'account-external-customer',
          trackingStatus: 'in-review',
        },
      ],
    });
    expect(employeeView).toEqual({ status: 'ready', data: [] });
  });

  it('rejects an external customer submitting to an assistant they cannot use', async () => {
    const privateAssistantRepository = new MockDemoRepository(
      {
        ...DEMO_SEED,
        assistants: DEMO_SEED.assistants.map((assistant) => ({
          ...assistant,
          audience: 'account-members',
          sharedWithAccountIds: [],
        })),
      },
      { viewer: () => 'account-external-customer' },
    );

    const usableAssistants = await firstValueFrom(privateAssistantRepository.listUsableAssistants());
    const submission = privateAssistantRepository.submitAuthorizedForm(
      'account-external-customer',
      {
        assistantId: 'assistant-customer-service',
        orderNumber: 'DEMO-1042',
        contactEmail: 'customer@example.test',
        issue: '尚未收到出貨通知',
        consent: true,
      },
    );

    expect(usableAssistants).toEqual({ status: 'ready', data: [] });
    expect(submission).toMatchObject({
      status: 'permission-denied',
      reason: 'authorized-form',
    });
  });

  it('rejects an authorized form when consent is false', () => {
    const result = repository.submitAuthorizedForm(
      'account-external-customer',
      {
        assistantId: 'assistant-customer-service',
        orderNumber: 'DEMO-1042',
        contactEmail: 'customer@example.test',
        issue: '尚未收到出貨通知',
        consent: false,
      },
    );

    expect(result).toMatchObject({
      status: 'permission-denied',
      reason: 'authorized-form',
    });
  });

  it.each(['withdrawn', 'not-consented'] as const)(
    'does not expose %s structured records to the data manager',
    (consentStatus: SubmissionConsentStatus) => {
      const restrictedRepository = new MockDemoRepository({
        ...DEMO_SEED,
        structuredSubmissions: DEMO_SEED.structuredSubmissions.map(
          (submission) => ({ ...submission, consentStatus }),
        ),
      });

      expect(
        restrictedRepository.listManagedSubmissions('account-smb-admin'),
      ).toEqual({ status: 'ready', data: [] });
    },
  );

  it('connects one assistant to multiple knowledge and database sources', () => {
    const result = repository.getAssistantSources(
      'account-smb-admin',
      'assistant-customer-service',
    );

    expect(result).toMatchObject({
      status: 'ready',
      data: expect.arrayContaining([
        { id: 'knowledge-product-guide', type: 'knowledge-base' },
        { id: 'knowledge-refund-policy', type: 'knowledge-base' },
        { id: 'database-orders', type: 'database' },
        { id: 'database-customer-records', type: 'database' },
      ]),
    });
    if (result.status === 'ready') {
      expect(new Set(result.data.map((source) => source.type))).toEqual(
        new Set(['knowledge-base', 'database']),
      );
    }
  });

  it('returns immutable view models instead of exposing mutable seed data', () => {
    const result = repository.listKnowledgeBases('account-smb-admin');

    expect(result.status).toBe('ready');
    if (result.status === 'ready') {
      expect(Object.isFrozen(result)).toBe(true);
      expect(Object.isFrozen(result.data)).toBe(true);
      expect(Object.isFrozen(result.data[0])).toBe(true);
    }
  });

  it.each([
    ['loading', 'loading'],
    ['permission-denied', 'permission-denied'],
  ] as const)('reproduces the %s scenario', async (scenario, expectedStatus) => {
    repository.setScenario(scenario);

    expect(await firstValueFrom(repository.listUsableAssistants())).toMatchObject({
      status: expectedStatus,
    });
    expect(repository.getScenario()).toBe(scenario);
  });

  it('reproduces partial failure with usable data and an explicit unavailable resource', () => {
    repository.setScenario('partial-failure');

    const result = repository.listKnowledgeBases('account-smb-admin');

    expect(result).toMatchObject({
      status: 'partial-failure',
      unavailable: ['knowledge-sync'],
      data: expect.any(Array),
    });
  });

  it('reproduces a disconnected publishing channel without changing the seed', async () => {
    repository.setScenario('disconnected-channel');

    const disconnected = await firstValueFrom(repository.listPublishingChannels());
    repository.resetScenario();
    const restored = await firstValueFrom(repository.listPublishingChannels());

    expect(disconnected.status).toBe('ready');
    expect(restored.status).toBe('ready');
    if (disconnected.status === 'ready' && restored.status === 'ready') {
      expect(
        disconnected.data.find(
          (channel) => channel.id === 'channel-website:assistant-customer-service',
        )?.status,
      ).toBe('needs-attention');
      expect(
        restored.data.find(
          (channel) => channel.id === 'channel-website:assistant-customer-service',
        )?.status,
      ).toBe('published');
    }
  });

  it('keeps anonymous analytics separate from conversation text and structured submissions', () => {
    const result = repository.getAssistantAnalytics(
      'account-smb-admin',
      'assistant-customer-service',
    );

    expect(result).toEqual({
      status: 'ready',
      data: {
        assistantId: 'assistant-customer-service',
        period: 'last-7-days',
        conversationCount: 18,
        resolvedCount: 12,
        helpfulRatingPercent: 89,
      },
    });
    expect(JSON.stringify(result)).not.toContain('messages');
    expect(JSON.stringify(result)).not.toContain('fields');
  });
});

/** `createMember` (issue #52，M2 Slice 18)：mock 版本的新增成員。 */
describe('MockDemoRepository createMember', () => {
  function setUp(viewer: string | null = 'account-smb-admin') {
    return new MockDemoRepository(DEMO_SEED, {
      storage: createMemoryStorage(),
      viewer: () => viewer,
    });
  }

  async function create(
    repository: MockDemoRepository,
    overrides: Partial<Parameters<MockDemoRepository['createMember']>[0]> = {},
  ): Promise<CreateMemberResult> {
    return firstValueFrom(
      repository.createMember({
        loginName: 'new-hire',
        displayName: '新進同仁',
        role: 'internal-employee',
        permissions: ['use-shared-assistants'],
        ...overrides,
      }),
    );
  }

  it('denies an account without manage-assistants, without naming any existing member', async () => {
    const repository = setUp('account-internal-employee');
    const result = await create(repository);

    expect(result).toMatchObject({ status: 'permission-denied', reason: 'team' });
    expect(JSON.stringify(result)).not.toContain('安心商行');
  });

  it('creates a member with a separate id from the seed accounts and lists it in the team', async () => {
    const repository = setUp();
    const result = await create(repository);

    expect(result.status).toBe('ready');
    if (result.status !== 'ready') throw new Error('expected ready');
    expect(result.data.member.displayName).toBe('新進同仁');
    expect(result.data.member.role).toBe('internal-employee');
    expect(result.data.member.permissions).toEqual(['use-shared-assistants']);
    expect(result.data.member.lockedPermissions).toEqual([]);
    // 清楚標示為示範用，不是真的密碼。
    expect(result.data.oneTimePassword).toMatch(/^Demo-/);

    const team = await firstValueFrom(repository.getTeam());
    if (team.status !== 'ready') throw new Error('expected ready');
    expect(team.data.members.map((member) => member.id)).toContain(result.data.member.id);
  });

  it('rejects a duplicate login name and does not add a second member', async () => {
    const repository = setUp();
    const first = await create(repository, { loginName: 'duplicate-name' });
    expect(first.status).toBe('ready');

    const second = await create(repository, { loginName: 'duplicate-name', displayName: '另一個人' });
    expect(second).toEqual({
      status: 'validation-failed',
      message: '這個登入名稱在目前組織已經有人使用，請改用其他名稱。',
    });

    const team = await firstValueFrom(repository.getTeam());
    if (team.status !== 'ready') throw new Error('expected ready');
    expect(team.data.members.filter((member) => member.displayName === '另一個人')).toHaveLength(0);
  });

  it('creates two same-role members with independent ids and permissions', async () => {
    const repository = setUp();
    const first = await create(repository, {
      loginName: 'internal-a',
      displayName: '客服同仁 A',
      permissions: ['use-shared-assistants'],
    });
    const second = await create(repository, {
      loginName: 'internal-b',
      displayName: '客服同仁 B',
      permissions: ['read-consented-submissions'],
    });
    if (first.status !== 'ready' || second.status !== 'ready') throw new Error('expected ready');

    expect(first.data.member.id).not.toBe(second.data.member.id);

    const updated = await firstValueFrom(
      repository.updateMemberPermissions(first.data.member.id, ['manage-data-sources']),
    );
    expect(updated.status).toBe('ready');
    if (updated.status !== 'ready') throw new Error('expected ready');
    const updatedFirst = updated.data.members.find((member) => member.id === first.data.member.id);
    const untouchedSecond = updated.data.members.find((member) => member.id === second.data.member.id);
    expect(updatedFirst?.permissions).toEqual(['manage-data-sources']);
    expect(untouchedSecond?.permissions).toEqual(['read-consented-submissions']);
  });

  it('rejects an empty login name or display name', async () => {
    const repository = setUp();
    expect(await create(repository, { loginName: '  ' })).toMatchObject({ status: 'validation-failed' });
    expect(await create(repository, { displayName: '  ' })).toMatchObject({ status: 'validation-failed' });
  });
});
