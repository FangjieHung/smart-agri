import { MockDemoRepository } from './mock-demo-repository';
import { DEMO_SEED } from './demo-seed';
import type { SubmissionConsentStatus } from '../domain/conversation.model';

describe('MockDemoRepository', () => {
  let repository: MockDemoRepository;

  beforeEach(() => {
    repository = new MockDemoRepository();
  });

  it('keeps assistant configuration private while allowing a shared assistant to be used', () => {
    const adminConfigurations =
      repository.listAssistantConfigurations('account-smb-admin');
    const employeeConfigurations = repository.listAssistantConfigurations(
      'account-internal-employee',
    );
    const employeeAssistants = repository.listUsableAssistants(
      'account-internal-employee',
    );

    expect(adminConfigurations).toMatchObject({
      status: 'ready',
      data: [
        {
          id: 'assistant-customer-service',
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

  it('rejects an external customer submitting to an assistant they cannot use', () => {
    const privateAssistantRepository = new MockDemoRepository({
      ...DEMO_SEED,
      assistants: DEMO_SEED.assistants.map((assistant) => ({
        ...assistant,
        audience: 'account-members',
        sharedWithAccountIds: [],
      })),
    });

    const usableAssistants = privateAssistantRepository.listUsableAssistants(
      'account-external-customer',
    );
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
  ] as const)('reproduces the %s scenario', (scenario, expectedStatus) => {
    repository.setScenario(scenario);

    expect(repository.listUsableAssistants('account-smb-admin')).toMatchObject({
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

  it('reproduces a disconnected publishing channel without changing the seed', () => {
    repository.setScenario('disconnected-channel');

    const disconnected = repository.listPublishingChannels('account-smb-admin');
    repository.resetScenario();
    const restored = repository.listPublishingChannels('account-smb-admin');

    expect(disconnected.status).toBe('ready');
    expect(restored.status).toBe('ready');
    if (disconnected.status === 'ready' && restored.status === 'ready') {
      expect(
        disconnected.data.find((channel) => channel.id === 'channel-website')
          ?.connectionStatus,
      ).toBe('disconnected');
      expect(
        restored.data.find((channel) => channel.id === 'channel-website')
          ?.connectionStatus,
      ).toBe('connected');
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
