import type { AccountId, AccountView } from '../domain/account.model';
import type {
  AssistantConfigurationView,
  AssistantId,
  AssistantSourceReference,
  AssistantSummaryView,
} from '../domain/assistant.model';
import type {
  AssistantDraft,
  AssistantDraftFieldError,
  AssistantTemplateView,
  ConnectableSourceView,
  SavedAssistantDraftView,
  TrialAnswerRequest,
  TrialAnswerView,
  TrialQuestionView,
} from '../domain/assistant-draft.model';
import type {
  CreateDatabaseInput,
  DatabaseDetailView,
  DatabaseFieldError,
  DatabaseFieldView,
  DatabaseId,
  DatabaseSummaryView,
  DatabaseTemplateView,
  DatabaseTrackingView,
  DatabaseTrialAnswers,
  DatabaseTrialPreviewView,
  DatabaseView,
} from '../domain/database.model';
import type {
  AssistantAnalyticsView,
  AssistantChatView,
  AuthorizedFormInput,
  ChatFormReviewView,
  ChatFormSubmission,
  ConversationId,
  PrivateConversationView,
  StructuredSubmissionView,
} from '../domain/conversation.model';
import type {
  KnowledgeBaseDetailView,
  KnowledgeBaseId,
  KnowledgeBaseSummaryView,
  KnowledgeBaseView,
  KnowledgeDocumentId,
  KnowledgeDocumentView,
  KnowledgeSharingView,
} from '../domain/knowledge-base.model';
import type {
  AssistantChannelsView,
  AssistantPublishingView,
  LineSettingsInput,
  LineSetupView,
  PlatformSharingView,
  PublishingChannelType,
  PublishingChannelView,
  PublishingFieldError,
  WebsiteEmbedSettings,
  WebsiteEmbedView,
} from '../domain/publishing.model';

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
  | 'authorized-form'
  | 'assistant-draft'
  | 'knowledge-base'
  | 'database'
  | 'database-records'
  | 'assistant-use'
  | 'publishing';

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

export interface ValidationFailedRepositoryView {
  readonly status: 'validation-failed';
  readonly errors: readonly AssistantDraftFieldError[];
  readonly message: string;
}

export type CreateAssistantResult =
  | RepositoryView<AssistantConfigurationView>
  | ValidationFailedRepositoryView;

export interface SharingValidationFailedView {
  readonly status: 'validation-failed';
  readonly message: string;
}

export type UpdateKnowledgeSharingResult =
  | RepositoryView<KnowledgeSharingView>
  | SharingValidationFailedView;

export interface CreateDatabaseValidationFailedView {
  readonly status: 'validation-failed';
  readonly message: string;
}

export type CreateDatabaseResult =
  | RepositoryView<DatabaseSummaryView>
  | CreateDatabaseValidationFailedView;

export interface DatabaseFieldsValidationFailedView {
  readonly status: 'validation-failed';
  readonly errors: readonly DatabaseFieldError[];
  readonly message: string;
}

export type UpdateDatabaseFieldsResult =
  | RepositoryView<readonly DatabaseFieldView[]>
  | DatabaseFieldsValidationFailedView;

export type PreviewDatabaseEntryResult =
  | RepositoryView<DatabaseTrialPreviewView>
  | DatabaseFieldsValidationFailedView;

export interface ChatValidationFailedView {
  readonly status: 'validation-failed';
  readonly message: string;
}

export type SendChatMessageResult =
  | RepositoryView<AssistantChatView>
  | ChatValidationFailedView;

export type ReviewChatFormResult =
  | RepositoryView<ChatFormReviewView>
  | DatabaseFieldsValidationFailedView;

export type SubmitChatFormResult =
  | RepositoryView<AssistantChatView>
  | DatabaseFieldsValidationFailedView;

export interface PublishingValidationFailedView {
  readonly status: 'validation-failed';
  readonly errors: readonly PublishingFieldError[];
  readonly message: string;
}

export type UpdatePlatformSharingResult =
  | RepositoryView<PlatformSharingView>
  | PublishingValidationFailedView;

export type UpdateWebsiteEmbedResult =
  | RepositoryView<WebsiteEmbedView>
  | PublishingValidationFailedView;

export type ActivateLineChannelResult =
  | RepositoryView<LineSetupView>
  | PublishingValidationFailedView;

/** 只需要 Web Storage 的讀寫子集，方便測試替換成記憶體實作。 */
export type DemoKeyValueStorage = Pick<
  Storage,
  'getItem' | 'setItem' | 'removeItem'
>;

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
  /** 目前帳號擁有的助理的所有管道（每個助理固定三個）。 */
  listPublishingChannels(
    viewerAccountId: AccountId,
  ): RepositoryView<readonly PublishingChannelView[]>;
  /** 發布管道總覽：依助理分組，每個助理固定平台內、官網與 LINE 三個管道。 */
  listChannelOverview(
    viewerAccountId: AccountId,
  ): RepositoryView<readonly AssistantChannelsView[]>;
  /**
   * 單一助理的三個管道設定。id 來自網址、未經驗證；不存在或非擁有者時一律回傳
   * 相同的 permission-denied，訊息不包含資源名稱。
   */
  getAssistantPublishing(
    viewerAccountId: AccountId,
    assistantId: string,
  ): RepositoryView<AssistantPublishingView>;
  /** 指定可在平台內使用助理的帳號；空清單代表尚未設定。 */
  updatePlatformSharing(
    viewerAccountId: AccountId,
    assistantId: string,
    accountIds: readonly AccountId[],
  ): UpdatePlatformSharingResult;
  /** 儲存官網外觀與允許網域；網域變更後需重新檢查安裝狀態。 */
  updateWebsiteEmbed(
    viewerAccountId: AccountId,
    assistantId: string,
    settings: WebsiteEmbedSettings,
  ): UpdateWebsiteEmbedResult;
  /** Demo：模擬檢查允許網域上是否已安裝嵌入碼，不會連線到任何網站。 */
  checkWebsiteInstallation(
    viewerAccountId: AccountId,
    assistantId: string,
  ): RepositoryView<WebsiteEmbedView>;
  /** 儲存 LINE 連接資訊並逐欄檢查；儲存後需重新傳送測試訊息才能啟用。 */
  saveLineSettings(
    viewerAccountId: AccountId,
    assistantId: string,
    input: LineSettingsInput,
  ): RepositoryView<LineSetupView>;
  /** Demo：模擬傳送 LINE 測試訊息，結果寫在 lastTest，不會連接 LINE。 */
  sendLineTestMessage(
    viewerAccountId: AccountId,
    assistantId: string,
  ): RepositoryView<LineSetupView>;
  /** 測試訊息送達後才可啟用；否則回傳 validation-failed。 */
  activateLineChannel(
    viewerAccountId: AccountId,
    assistantId: string,
  ): ActivateLineChannelResult;
  /** 暫停或恢復單一管道；不影響同一助理的其他管道。 */
  setPublishingChannelPaused(
    viewerAccountId: AccountId,
    assistantId: string,
    channelType: PublishingChannelType,
    paused: boolean,
  ): RepositoryView<PublishingChannelView>;
  listAssistantTemplates(): RepositoryView<readonly AssistantTemplateView[]>;
  /** 知識庫與資料庫混合的可連接來源清單。 */
  listConnectableSources(
    viewerAccountId: AccountId,
  ): RepositoryView<readonly ConnectableSourceView[]>;
  listTrialQuestions(): RepositoryView<readonly TrialQuestionView[]>;
  /** 以固定 fixture 模擬試問回答，不連接真實 AI。 */
  previewTrialAnswer(
    viewerAccountId: AccountId,
    request: TrialAnswerRequest,
  ): RepositoryView<TrialAnswerView>;
  /** 草稿依帳號隔離保存；沒有草稿時 data 為 null。 */
  getAssistantDraft(
    viewerAccountId: AccountId,
  ): RepositoryView<SavedAssistantDraftView | null>;
  saveAssistantDraft(
    viewerAccountId: AccountId,
    draft: AssistantDraft,
  ): RepositoryView<SavedAssistantDraftView>;
  discardAssistantDraft(viewerAccountId: AccountId): void;
  /** 驗證完整草稿後建立助理，成功後清除該帳號的草稿。 */
  createAssistantFromDraft(
    viewerAccountId: AccountId,
    draft: AssistantDraft,
  ): CreateAssistantResult;
  /** 目前帳號擁有的知識庫摘要：文件／FAQ 數量、狀態統計、分享範圍與已連接助理。 */
  listKnowledgeBaseSummaries(
    viewerAccountId: AccountId,
  ): RepositoryView<readonly KnowledgeBaseSummaryView[]>;
  /**
   * 知識庫詳情。id 來自網址、未經驗證；不存在或無權限時一律回傳相同的
   * permission-denied，訊息不包含資源名稱。
   */
  getKnowledgeBaseDetail(
    viewerAccountId: AccountId,
    knowledgeBaseId: string,
  ): RepositoryView<KnowledgeBaseDetailView>;
  /** Demo：只新增一筆等待處理的文件紀錄，不會真正上傳檔案。 */
  addDemoKnowledgeDocument(
    viewerAccountId: AccountId,
    knowledgeBaseId: KnowledgeBaseId,
  ): RepositoryView<KnowledgeDocumentView>;
  /** Demo：把文件往下一個處理狀態推進一步（等待處理 → 處理中 → 可使用）。 */
  advanceKnowledgeDocument(
    viewerAccountId: AccountId,
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
  ): RepositoryView<KnowledgeDocumentView>;
  /** 把處理失敗或部分無法讀取的文件重新排入處理。 */
  retryKnowledgeDocument(
    viewerAccountId: AccountId,
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
  ): RepositoryView<KnowledgeDocumentView>;
  updateKnowledgeSharing(
    viewerAccountId: AccountId,
    knowledgeBaseId: KnowledgeBaseId,
    sharing: KnowledgeSharingView,
  ): UpdateKnowledgeSharingResult;
  /** 資料庫入口先問「你要收集什麼」；只有可管理資料來源的帳號可以取得模板。 */
  listDatabaseTemplates(
    viewerAccountId: AccountId,
  ): RepositoryView<readonly DatabaseTemplateView[]>;
  /** 目前帳號擁有的資料庫摘要；非指定資料管理者看不到紀錄數量。 */
  listDatabaseSummaries(
    viewerAccountId: AccountId,
  ): RepositoryView<readonly DatabaseSummaryView[]>;
  createDatabaseFromTemplate(
    viewerAccountId: AccountId,
    input: CreateDatabaseInput,
  ): CreateDatabaseResult;
  /**
   * 資料庫詳情。id 來自網址、未經驗證；不存在或無權限時一律回傳相同的
   * permission-denied，訊息不包含資源名稱。
   */
  getDatabaseDetail(
    viewerAccountId: AccountId,
    databaseId: string,
  ): RepositoryView<DatabaseDetailView>;
  /** 儲存表單欄位；只接受六種欄位類型，不支援條件跳題或公式。 */
  updateDatabaseFields(
    viewerAccountId: AccountId,
    databaseId: DatabaseId,
    fields: readonly DatabaseFieldView[],
  ): UpdateDatabaseFieldsResult;
  /** 試填：以目前已儲存的表單驗證答案並回傳預覽，不會建立紀錄。 */
  previewDatabaseEntry(
    viewerAccountId: AccountId,
    databaseId: DatabaseId,
    answers: DatabaseTrialAnswers,
  ): PreviewDatabaseEntryResult;
  /**
   * 收集紀錄與比較。只有指定資料管理者可查看，且只包含使用者明確同意提交的紀錄；
   * 本次／上次／首次差異與文字摘要皆在此預先算好。
   */
  getDatabaseTracking(
    viewerAccountId: AccountId,
    databaseId: string,
  ): RepositoryView<DatabaseTrackingView>;
  /**
   * 目前帳號與助理的私人對話。id 來自網址、未經驗證；不存在或無使用權限時一律回傳
   * 相同的 permission-denied。對話只屬於發起的帳號，助理擁有者也看不到其他帳號的內容。
   */
  getAssistantChat(
    viewerAccountId: AccountId,
    assistantId: string,
  ): RepositoryView<AssistantChatView>;
  /** 以預先準備的 response map 回覆；對應不到時回覆查無資料與下一步，不模擬 LLM。 */
  sendChatMessage(
    viewerAccountId: AccountId,
    assistantId: string,
    text: string,
  ): SendChatMessageResult;
  /** 對話中表單的送出前確認：只驗證並整理填寫值，不會建立紀錄。 */
  reviewChatForm(
    viewerAccountId: AccountId,
    assistantId: string,
    formId: DatabaseId,
    answers: DatabaseTrialAnswers,
  ): ReviewChatFormResult;
  /**
   * 使用者明確同意後才會建立結構化紀錄，並寫入該資料庫的收集紀錄；
   * 只有指定資料管理者可在收集紀錄中看到。未同意時回傳 validation-failed。
   */
  submitChatForm(
    viewerAccountId: AccountId,
    assistantId: string,
    submission: ChatFormSubmission,
  ): SubmitChatFormResult;
}

export const DEMO_SECURITY_NOTICE =
  '此 mock 僅用於視覺 Demo，不提供真實驗證與資料安全邊界，不使用真實資料，也不連接真實 AI。';
