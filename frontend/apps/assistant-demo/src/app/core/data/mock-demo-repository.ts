import type { AccountId } from '../domain/account.model';
import type {
  AssistantConfigurationView,
  AssistantId,
  AssistantSourceReference,
  AssistantSummaryView,
} from '../domain/assistant.model';
import type {
  AuthorizedFormInput,
  ConversationId,
  StructuredSubmissionView,
} from '../domain/conversation.model';
import type { PublishingChannelView } from '../domain/publishing.model';
import { DEMO_SEED, type DemoSeed } from './demo-seed';
import type {
  DemoRepository,
  DemoScenario,
  PermissionDeniedRepositoryView,
  RepositoryPermissionDeniedReason,
  RepositoryView,
} from './demo-repository';

function freezeDeep<T>(value: T): T {
  if (value !== null && typeof value === 'object' && !Object.isFrozen(value)) {
    Object.values(value as Record<string, unknown>).forEach((entry) => {
      freezeDeep(entry);
    });
    Object.freeze(value);
  }

  return value;
}

function clonePlainValue<T>(value: T): T {
  if (Array.isArray(value)) {
    return value.map((entry) => clonePlainValue(entry)) as T;
  }

  if (value !== null && typeof value === 'object') {
    return Object.fromEntries(
      Object.entries(value).map(([key, entry]) => [
        key,
        clonePlainValue(entry),
      ]),
    ) as T;
  }

  return value;
}

function immutableCopy<T>(value: T): T {
  return freezeDeep(clonePlainValue(value));
}

export class MockDemoRepository implements DemoRepository {
  private scenario: DemoScenario = 'ready';

  constructor(private readonly seed: DemoSeed = DEMO_SEED) {}

  setScenario(scenario: DemoScenario): void {
    this.scenario = scenario;
  }

  getScenario(): DemoScenario {
    return this.scenario;
  }

  resetScenario(): void {
    this.scenario = 'ready';
  }

  listAccounts(): ReturnType<DemoRepository['listAccounts']> {
    return this.applyScenario(this.seed.accounts);
  }

  listAssistantConfigurations(
    viewerAccountId: AccountId,
  ): ReturnType<DemoRepository['listAssistantConfigurations']> {
    const configurations = this.seed.assistants.filter(
      (assistant) => assistant.ownerAccountId === viewerAccountId,
    );

    return this.applyScenario(configurations);
  }

  listUsableAssistants(
    viewerAccountId: AccountId,
  ): ReturnType<DemoRepository['listUsableAssistants']> {
    const assistants = this.seed.assistants
      .filter((assistant) => this.canUseAssistant(assistant, viewerAccountId))
      .map((assistant) => this.toAssistantSummary(assistant, viewerAccountId));

    return this.applyScenario(assistants);
  }

  getAssistantSources(
    viewerAccountId: AccountId,
    assistantId: AssistantId,
  ): ReturnType<DemoRepository['getAssistantSources']> {
    const assistant = this.seed.assistants.find(
      (candidate) => candidate.id === assistantId,
    );

    if (assistant?.ownerAccountId !== viewerAccountId) {
      return this.permissionDenied(
        'assistant-configuration',
        '只有助理擁有者可查看資料來源設定。',
      );
    }

    const sources: readonly AssistantSourceReference[] = [
      ...assistant.knowledgeBaseIds.map((id): AssistantSourceReference => ({
        id,
        type: 'knowledge-base',
      })),
      ...assistant.databaseIds.map((id): AssistantSourceReference => ({
        id,
        type: 'database',
      })),
    ];

    return this.applyScenario(sources);
  }

  listKnowledgeBases(
    viewerAccountId: AccountId,
  ): ReturnType<DemoRepository['listKnowledgeBases']> {
    const knowledgeBases = this.seed.knowledgeBases.filter(
      (knowledgeBase) => knowledgeBase.ownerAccountId === viewerAccountId,
    );

    return this.applyScenario(knowledgeBases);
  }

  listDatabases(
    viewerAccountId: AccountId,
  ): ReturnType<DemoRepository['listDatabases']> {
    const databases = this.seed.databases.filter(
      (database) => database.ownerAccountId === viewerAccountId,
    );

    return this.applyScenario(databases);
  }

  listPrivateConversations(
    viewerAccountId: AccountId,
  ): ReturnType<DemoRepository['listPrivateConversations']> {
    const conversations = this.seed.privateConversations.filter(
      (conversation) => conversation.accountId === viewerAccountId,
    );

    return this.applyScenario(conversations);
  }

  getConversation(
    viewerAccountId: AccountId,
    conversationId: ConversationId,
  ): ReturnType<DemoRepository['getConversation']> {
    const conversation = this.seed.privateConversations.find(
      (candidate) => candidate.id === conversationId,
    );

    if (conversation?.accountId !== viewerAccountId) {
      return this.permissionDenied(
        'private-conversation',
        '私人對話內容僅限對話所屬帳號查看。',
      );
    }

    return this.applyScenario(conversation);
  }

  listManagedSubmissions(
    viewerAccountId: AccountId,
  ): ReturnType<DemoRepository['listManagedSubmissions']> {
    const submissions = this.seed.structuredSubmissions.filter(
      (submission) =>
        submission.dataManagerAccountId === viewerAccountId &&
        submission.consentStatus === 'consented',
    );

    return this.applyScenario(submissions);
  }

  listOwnSubmissions(
    viewerAccountId: AccountId,
  ): ReturnType<DemoRepository['listOwnSubmissions']> {
    const submissions = this.seed.structuredSubmissions.filter(
      (submission) => submission.submittedByAccountId === viewerAccountId,
    );

    return this.applyScenario(submissions);
  }

  submitAuthorizedForm(
    viewerAccountId: AccountId,
    input: AuthorizedFormInput,
  ): ReturnType<DemoRepository['submitAuthorizedForm']> {
    const assistant = this.seed.assistants.find(
      (candidate) => candidate.id === input.assistantId,
    );

    if (
      viewerAccountId !== 'account-external-customer' ||
      input.consent !== true ||
      assistant === undefined ||
      !this.canUseAssistant(assistant, viewerAccountId)
    ) {
      return this.permissionDenied(
        'authorized-form',
        '只有已同意授權的外部客戶可提交這份表單。',
      );
    }

    const seededSubmission = this.seed.structuredSubmissions[0];

    if (seededSubmission === undefined) {
      return this.permissionDenied(
        'authorized-form',
        '目前沒有可用的授權表單。',
      );
    }

    const submission: StructuredSubmissionView = {
      id: seededSubmission.id,
      assistantId: input.assistantId,
      submittedByAccountId: viewerAccountId,
      dataManagerAccountId: assistant.ownerAccountId,
      consentStatus: 'consented',
      trackingStatus: 'received',
      fields: [
        { id: 'order-number', label: '訂單編號', value: input.orderNumber },
        {
          id: 'contact-email',
          label: '聯絡信箱',
          value: input.contactEmail,
        },
        { id: 'issue', label: '問題', value: input.issue },
      ],
      submittedAt: seededSubmission.submittedAt,
    };

    return this.applyScenario(submission);
  }

  getAssistantAnalytics(
    viewerAccountId: AccountId,
    assistantId: AssistantId,
  ): ReturnType<DemoRepository['getAssistantAnalytics']> {
    const assistant = this.seed.assistants.find(
      (candidate) => candidate.id === assistantId,
    );

    if (assistant?.ownerAccountId !== viewerAccountId) {
      return this.permissionDenied(
        'assistant-configuration',
        '只有助理擁有者可查看匿名使用摘要。',
      );
    }

    const analytics = this.seed.analytics.find(
      (candidate) => candidate.assistantId === assistantId,
    );

    if (analytics === undefined) {
      return this.permissionDenied(
        'assistant-configuration',
        '找不到這個助理的匿名使用摘要。',
      );
    }

    return this.applyScenario(analytics);
  }

  listPublishingChannels(
    viewerAccountId: AccountId,
  ): ReturnType<DemoRepository['listPublishingChannels']> {
    const channels = this.seed.publishingChannels
      .filter((channel) => channel.ownerAccountId === viewerAccountId)
      .map((channel) => this.applyChannelScenario(channel));

    return this.applyScenario(channels);
  }

  private applyScenario<T>(data: T): RepositoryView<T> {
    if (this.scenario === 'loading') {
      return immutableCopy({ status: 'loading' });
    }

    if (this.scenario === 'permission-denied') {
      return this.permissionDenied(
        'scenario',
        '此情境用於預覽權限不足的畫面狀態。',
      );
    }

    if (this.scenario === 'partial-failure') {
      return immutableCopy({
        status: 'partial-failure',
        data,
        unavailable: ['knowledge-sync'],
        message: '部分知識庫同步暫時無法讀取。',
      });
    }

    return immutableCopy({ status: 'ready', data });
  }

  private permissionDenied(
    reason: RepositoryPermissionDeniedReason,
    message: string,
  ): PermissionDeniedRepositoryView {
    return immutableCopy({ status: 'permission-denied', reason, message });
  }

  private toAssistantSummary(
    assistant: AssistantConfigurationView,
    viewerAccountId: AccountId,
  ): AssistantSummaryView {
    return {
      id: assistant.id,
      name: assistant.name,
      purpose: assistant.purpose,
      status: assistant.status,
      audience: assistant.audience,
      permission:
        assistant.ownerAccountId === viewerAccountId ? 'configure' : 'use',
    };
  }

  private canUseAssistant(
    assistant: AssistantConfigurationView,
    viewerAccountId: AccountId,
  ): boolean {
    return (
      assistant.ownerAccountId === viewerAccountId ||
      assistant.sharedWithAccountIds.includes(viewerAccountId) ||
      (viewerAccountId === 'account-external-customer' &&
        assistant.audience === 'authorized-external-customers')
    );
  }

  private applyChannelScenario(
    channel: PublishingChannelView,
  ): PublishingChannelView {
    if (
      this.scenario === 'disconnected-channel' &&
      channel.id === 'channel-website'
    ) {
      return { ...channel, connectionStatus: 'disconnected' };
    }

    return channel;
  }
}
