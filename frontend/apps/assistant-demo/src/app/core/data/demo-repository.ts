import type { AccountId, AccountView } from '../domain/account.model';
import type {
  AssistantConfigurationView,
  AssistantId,
  AssistantSourceReference,
  AssistantSummaryView,
} from '../domain/assistant.model';
import type { DatabaseView } from '../domain/database.model';
import type {
  AssistantAnalyticsView,
  AuthorizedFormInput,
  ConversationId,
  PrivateConversationView,
  StructuredSubmissionView,
} from '../domain/conversation.model';
import type { KnowledgeBaseView } from '../domain/knowledge-base.model';
import type { PublishingChannelView } from '../domain/publishing.model';

export type DemoScenario =
  | 'ready'
  | 'loading'
  | 'partial-failure'
  | 'permission-denied'
  | 'disconnected-channel';

export type RepositoryUnavailableResource = 'knowledge-sync';

export type RepositoryPermissionDeniedReason =
  | 'scenario'
  | 'private-conversation'
  | 'assistant-configuration'
  | 'authorized-form';

export interface ReadyRepositoryView<T> {
  readonly status: 'ready';
  readonly data: T;
}

export interface LoadingRepositoryView {
  readonly status: 'loading';
}

export interface PartialFailureRepositoryView<T> {
  readonly status: 'partial-failure';
  readonly data: T;
  readonly unavailable: readonly RepositoryUnavailableResource[];
  readonly message: string;
}

export interface PermissionDeniedRepositoryView {
  readonly status: 'permission-denied';
  readonly reason: RepositoryPermissionDeniedReason;
  readonly message: string;
}

export type RepositoryView<T> =
  | ReadyRepositoryView<T>
  | LoadingRepositoryView
  | PartialFailureRepositoryView<T>
  | PermissionDeniedRepositoryView;

export interface DemoScenarioController {
  setScenario(scenario: DemoScenario): void;
  getScenario(): DemoScenario;
  resetScenario(): void;
}

export interface DemoRepository extends DemoScenarioController {
  listAccounts(): RepositoryView<readonly AccountView[]>;
  listAssistantConfigurations(
    viewerAccountId: AccountId,
  ): RepositoryView<readonly AssistantConfigurationView[]>;
  listUsableAssistants(
    viewerAccountId: AccountId,
  ): RepositoryView<readonly AssistantSummaryView[]>;
  getAssistantSources(
    viewerAccountId: AccountId,
    assistantId: AssistantId,
  ): RepositoryView<readonly AssistantSourceReference[]>;
  listKnowledgeBases(
    viewerAccountId: AccountId,
  ): RepositoryView<readonly KnowledgeBaseView[]>;
  listDatabases(
    viewerAccountId: AccountId,
  ): RepositoryView<readonly DatabaseView[]>;
  listPrivateConversations(
    viewerAccountId: AccountId,
  ): RepositoryView<readonly PrivateConversationView[]>;
  getConversation(
    viewerAccountId: AccountId,
    conversationId: ConversationId,
  ): RepositoryView<PrivateConversationView>;
  listManagedSubmissions(
    viewerAccountId: AccountId,
  ): RepositoryView<readonly StructuredSubmissionView[]>;
  listOwnSubmissions(
    viewerAccountId: AccountId,
  ): RepositoryView<readonly StructuredSubmissionView[]>;
  submitAuthorizedForm(
    viewerAccountId: AccountId,
    input: AuthorizedFormInput,
  ): RepositoryView<StructuredSubmissionView>;
  getAssistantAnalytics(
    viewerAccountId: AccountId,
    assistantId: AssistantId,
  ): RepositoryView<AssistantAnalyticsView>;
  listPublishingChannels(
    viewerAccountId: AccountId,
  ): RepositoryView<readonly PublishingChannelView[]>;
}

export const DEMO_SECURITY_NOTICE =
  '此 mock 僅用於視覺 Demo，不提供真實驗證與資料安全邊界，不使用真實資料，也不連接真實 AI。';
