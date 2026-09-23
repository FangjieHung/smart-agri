import { isVisitorId, type AccountId, type ChatViewerId } from '../domain/account.model';
import {
  ASSISTANT_WIZARD_STEPS,
  createEmptyAssistantDraft,
  validateAssistantDraft,
  type AssistantAnswerRules,
  type AssistantDraft,
  type AssistantTone,
  type ConnectableSourceStatus,
  type ConnectableSourceView,
  type SavedAssistantDraftView,
  type TrialAnswerRequest,
  type TrialAnswerView,
} from '../domain/assistant-draft.model';
import type {
  AssistantAudience,
  AssistantConfigurationView,
  CreatedAssistantId,
  AssistantId,
  AssistantSourceReference,
  AssistantSummaryView,
} from '../domain/assistant.model';
import {
  validateAssistantSettings,
  type AssistantSettingsPatch,
  type AssistantSettingsView,
} from '../domain/assistant-settings.model';
import type {
  AssistantChatView,
  AuthorizedFormInput,
  ChatFormSubmission,
  ChatFormView,
  ChatHistoryMode,
  ChatMessageView,
  ChatReplyView,
  ChatThreadId,
  ChatThreadListView,
  ChatThreadSummaryView,
  ConversationId,
  StructuredSubmissionView,
} from '../domain/conversation.model';
import type {
  CreatedDatabaseId,
  DatabaseDetailView,
  DatabaseFieldView,
  DatabaseId,
  DatabaseSummaryView,
  DatabaseTrackingView,
  DatabaseTrialAnswers,
  DatabaseView,
  TrackedSubjectId,
} from '../domain/database.model';
import {
  KNOWLEDGE_DOCUMENT_STATUSES,
  needsKnowledgeAttention,
  type KnowledgeBaseDetailView,
  type KnowledgeBaseId,
  type KnowledgeBaseSummaryView,
  type KnowledgeBaseView,
  type KnowledgeDocumentId,
  type KnowledgeDocumentStatus,
  type KnowledgeDocumentStatusCounts,
  type KnowledgeDocumentView,
  type KnowledgeSharingScope,
  type KnowledgeSharingView,
} from '../domain/knowledge-base.model';
import {
  PUBLISHING_CHANNEL_TYPES,
  type AssistantChannelsView,
  type AssistantPublishingView,
  type LineSettingsInput,
  type PublishingChannelType,
  type WebsiteEmbedSettings,
} from '../domain/publishing.model';
import {
  compareRecords,
  evaluateTrial,
  normalizeField,
  toRecordValues,
  toRecordView,
  validateFields,
} from './database-tracking';
import {
  ANONYMOUS_VISITOR_SUBJECT_NAME,
  CHAT_DEFAULT_THREAD_TITLE,
  CHAT_GENERAL_KNOWLEDGE_NOTICE,
  CHAT_HISTORY_OFF_NOTICE,
  CHAT_HISTORY_SAVED_NOTICE,
  CHAT_NO_RESULT_TEXT,
  CHAT_PRIVACY_NOTICE,
  CHAT_SENSITIVE_NOTICE,
  CHAT_THREAD_TITLE_MAX_LENGTH,
  CHAT_VISITOR_PRIVACY_NOTICE,
  CHAT_WITHDRAWAL_NOTICE,
  DEFAULT_CHAT_PROFILE,
  type ChatResponseFixture,
} from './demo-seed-chat';
import { DEMO_SEED, type DemoSeed } from './demo-seed';
import type {
  DatabaseCollectionFixture,
  DatabaseRecordFixture,
  TrackedSubjectFixture,
} from './demo-seed-databases';
import { createMemoryStorage } from './memory-storage';
import type { PublishingRecord } from './demo-seed-publishing';
import {
  canActivateLine,
  defaultPublishingRecord,
  isExternallyPublished,
  isPublishingRecord,
  lineTestResult,
  normalizePlatformAccounts,
  toAssistantPublishingView,
  trimLineSettings,
  validateWebsiteSettings,
} from './publishing-channels';
import type {
  ActivateLineChannelResult,
  CreateAssistantResult,
  CreateDatabaseResult,
  DemoKeyValueStorage,
  DemoRepository,
  DemoScenario,
  PermissionDeniedRepositoryView,
  RepositoryPermissionDeniedReason,
  RepositoryView,
  PreviewDatabaseEntryResult,
  RenameChatThreadResult,
  ReviewChatFormResult,
  SendChatMessageResult,
  SubmitChatFormResult,
  UpdateDatabaseFieldsResult,
  UpdateAssistantSettingsResult,
  UpdateKnowledgeSharingResult,
  UpdatePlatformSharingResult,
  UpdateWebsiteEmbedResult,
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

export interface MockDemoRepositoryOptions {
  /** 草稿與新建助理的保存位置；正式注入時使用 localStorage。 */
  readonly storage?: DemoKeyValueStorage;
  /**
   * 未登入訪客的對話保存位置；正式注入時使用 **sessionStorage**，
   * 所以關閉分頁就結束，也不會和任何帳號共用同一份儲存。
   */
  readonly visitorStorage?: DemoKeyValueStorage;
  readonly now?: () => Date;
}

const DRAFT_KEY_PREFIX = 'sme-demo:assistant-draft:';
const CREATED_ASSISTANTS_KEY = 'sme-demo:created-assistants';

interface StoredDraftRecord {
  readonly version: 1;
  readonly savedAt: string;
  readonly draft: AssistantDraft;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function parseJson(raw: string | null): unknown {
  if (raw === null) return null;
  try {
    return JSON.parse(raw) as unknown;
  } catch {
    return null;
  }
}

/** 只接受結構正確的草稿；欄位缺漏時以預設值補齊，毀損時視為沒有草稿。 */
function normalizeStoredDraft(value: unknown): SavedAssistantDraftView | null {
  if (
    !isRecord(value) ||
    value['version'] !== 1 ||
    typeof value['savedAt'] !== 'string' ||
    !isRecord(value['draft'])
  ) {
    return null;
  }

  const stored = value['draft'];
  const empty = createEmptyAssistantDraft();
  const step = ASSISTANT_WIZARD_STEPS.find((candidate) => candidate === stored['currentStep']);

  if (
    typeof stored['name'] !== 'string' ||
    typeof stored['purpose'] !== 'string' ||
    !Array.isArray(stored['sources']) ||
    !Array.isArray(stored['testedQuestionIds']) ||
    !isRecord(stored['rules']) ||
    step === undefined
  ) {
    return null;
  }

  const draft = {
    ...empty,
    ...stored,
    rules: { ...empty.rules, ...stored['rules'] },
    currentStep: step,
  } as AssistantDraft;

  return { draft, savedAt: value['savedAt'] };
}

const ASSISTANT_SETTINGS_KEY_PREFIX = 'sme-demo:assistant-settings:';

/**
 * 建立後編輯的設定。seed 的助理本身是唯讀 fixture，所以編輯結果一律寫在這裡，
 * 讀取時再疊回助理上——種子助理與精靈建立的助理因此走同一條保存路徑。
 */
interface StoredAssistantSettings {
  readonly version: 1;
  readonly savedAt: string;
  readonly name: string;
  readonly purpose: string;
  readonly audience: AssistantAudience;
  readonly knowledgeBaseIds: readonly KnowledgeBaseId[];
  readonly databaseIds: readonly DatabaseId[];
  readonly tone: AssistantTone;
  readonly roleInstructions: string;
  readonly rules: AssistantAnswerRules;
}

const ASSISTANT_AUDIENCES: readonly AssistantAudience[] = [
  'account-members',
  'authorized-external-customers',
  'members-and-external-customers',
];

/** 結構不完整時視為沒有存過設定，畫面退回助理本身的值，不會拿半筆資料覆蓋。 */
function normalizeStoredAssistantSettings(value: unknown): StoredAssistantSettings | null {
  if (
    !isRecord(value) ||
    value['version'] !== 1 ||
    typeof value['savedAt'] !== 'string' ||
    typeof value['name'] !== 'string' ||
    typeof value['purpose'] !== 'string' ||
    !ASSISTANT_AUDIENCES.includes(value['audience'] as AssistantAudience) ||
    !Array.isArray(value['knowledgeBaseIds']) ||
    !Array.isArray(value['databaseIds']) ||
    !isRecord(value['rules'])
  ) {
    return null;
  }

  const empty = createEmptyAssistantDraft();
  return {
    version: 1,
    savedAt: value['savedAt'],
    name: value['name'],
    purpose: value['purpose'],
    audience: value['audience'] as AssistantAudience,
    knowledgeBaseIds: value['knowledgeBaseIds'] as readonly KnowledgeBaseId[],
    databaseIds: value['databaseIds'] as readonly DatabaseId[],
    tone: (typeof value['tone'] === 'string' ? value['tone'] : empty.tone) as AssistantTone,
    roleInstructions:
      typeof value['roleInstructions'] === 'string' ? value['roleInstructions'] : '',
    rules: { ...empty.rules, ...value['rules'] } as AssistantAnswerRules,
  };
}

const KNOWLEDGE_KEY_PREFIX = 'sme-demo:knowledge:';

interface StoredKnowledgeRecord {
  readonly version: 1;
  readonly documents: readonly KnowledgeDocumentView[];
  readonly sharing: KnowledgeSharingView;
}

/** Demo 加入文件時輪流使用的示範檔名；不讀取任何真實檔案。 */
const DEMO_DOCUMENT_NAMES: readonly string[] = [
  '新品規格補充說明.pdf',
  '門市常見問答整理.docx',
  '包裝與配件清單.pdf',
];

const NEXT_DOCUMENT_STATUS: Partial<Record<KnowledgeDocumentStatus, KnowledgeDocumentStatus>> = {
  queued: 'processing',
  processing: 'ready',
};

const SHARING_SCOPES: readonly KnowledgeSharingScope[] = ['private', 'specific-accounts', 'public'];

function isStoredKnowledgeRecord(value: unknown): value is StoredKnowledgeRecord {
  if (!isRecord(value) || value['version'] !== 1) return false;
  const sharing = value['sharing'];
  return (
    Array.isArray(value['documents']) &&
    value['documents'].every(
      (document) =>
        isRecord(document) &&
        typeof document['id'] === 'string' &&
        typeof document['name'] === 'string' &&
        KNOWLEDGE_DOCUMENT_STATUSES.includes(document['status'] as KnowledgeDocumentStatus),
    ) &&
    isRecord(sharing) &&
    SHARING_SCOPES.includes(sharing['scope'] as KnowledgeSharingScope) &&
    Array.isArray(sharing['sharedWithAccountIds'])
  );
}

function countStatuses(
  documents: readonly KnowledgeDocumentView[],
): KnowledgeDocumentStatusCounts {
  const counts: Record<KnowledgeDocumentStatus, number> = {
    queued: 0,
    processing: 0,
    ready: 0,
    'partially-readable': 0,
    failed: 0,
  };
  documents.forEach((document) => {
    counts[document.status] += 1;
  });
  return counts;
}

function connectableKnowledgeStatus(
  counts: KnowledgeDocumentStatusCounts,
): ConnectableSourceStatus {
  if (counts.queued + counts.processing > 0) return 'processing';
  if (counts['partially-readable'] + counts.failed > 0) return 'needs-attention';
  return 'ready';
}

const CREATED_DATABASES_KEY = 'sme-demo:created-databases';
const DATABASE_FIELDS_KEY_PREFIX = 'sme-demo:database-fields:';

interface StoredCreatedDatabase {
  readonly view: DatabaseView;
  readonly collection: DatabaseCollectionFixture;
}

interface StoredDatabaseFields {
  readonly version: 1;
  readonly savedAt: string;
  readonly fields: readonly DatabaseFieldView[];
}

function isStoredCreatedDatabase(value: unknown): value is StoredCreatedDatabase {
  if (!isRecord(value) || !isRecord(value['view']) || !isRecord(value['collection'])) return false;
  const view = value['view'];
  const collection = value['collection'];
  return (
    typeof view['id'] === 'string' &&
    view['id'].startsWith('database-created-') &&
    typeof view['ownerAccountId'] === 'string' &&
    typeof view['name'] === 'string' &&
    Array.isArray(collection['fields']) &&
    Array.isArray(collection['dataManagerAccountIds'])
  );
}

function isStoredDatabaseFields(value: unknown): value is StoredDatabaseFields {
  return (
    isRecord(value) &&
    value['version'] === 1 &&
    typeof value['savedAt'] === 'string' &&
    Array.isArray(value['fields']) &&
    value['fields'].every((field) => isRecord(field) && typeof field['id'] === 'string')
  );
}

const CHAT_KEY_PREFIX = 'sme-demo:chat:';
const PUBLISHING_KEY_PREFIX = 'sme-demo:publishing:';
const CHAT_RECORDS_KEY = 'sme-demo:chat-records';
const MAX_QUESTION_LENGTH = 500;

const MAX_THREAD_TITLE_LENGTH = 60;

/** 版本 1：一個 (帳號, 助理) 只有一段對話。讀到時會就地升級成單一 thread。 */
interface StoredChatRecordV1 {
  readonly version: 1;
  readonly messages: readonly ChatMessageView[];
}

interface StoredChatThread {
  readonly id: ChatThreadId;
  readonly title: string;
  /** derived：仍會跟著第一則提問更新；manual：使用者改過名字，不再自動變動。 */
  readonly titleSource: 'derived' | 'manual';
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly messages: readonly ChatMessageView[];
}

/** 版本 2：同一個 (帳號, 助理) 的多段對話。 */
interface StoredChatRecord {
  readonly version: 2;
  readonly threads: readonly StoredChatThread[];
}

function isStoredChatMessages(value: unknown): value is readonly ChatMessageView[] {
  return (
    Array.isArray(value) &&
    value.every(
      (message) =>
        isRecord(message) &&
        typeof message['id'] === 'string' &&
        (message['author'] === 'account' || message['author'] === 'assistant'),
    )
  );
}

function isStoredChatThread(value: unknown): value is StoredChatThread {
  return (
    isRecord(value) &&
    typeof value['id'] === 'string' &&
    value['id'].startsWith('chat-thread-') &&
    typeof value['title'] === 'string' &&
    typeof value['createdAt'] === 'string' &&
    typeof value['updatedAt'] === 'string' &&
    isStoredChatMessages(value['messages'])
  );
}

function isStoredChatRecordV1(value: unknown): value is StoredChatRecordV1 {
  return isRecord(value) && value['version'] === 1 && isStoredChatMessages(value['messages']);
}

/** 讀取時就把舊版單一對話包成一段 thread；不回寫，等下一次寫入才落地成版本 2。 */
function normalizeStoredThreads(value: unknown): readonly StoredChatThread[] {
  if (isRecord(value) && value['version'] === 2 && Array.isArray(value['threads'])) {
    return value['threads'].filter(isStoredChatThread);
  }

  if (isStoredChatRecordV1(value) && value.messages.length > 0) {
    const createdAt = firstMessageTime(value.messages);
    return [
      {
        id: 'chat-thread-1',
        title: deriveThreadTitle(value.messages),
        titleSource: 'derived',
        createdAt,
        updatedAt: lastMessageTime(value.messages, createdAt),
        messages: value.messages,
      },
    ];
  }

  return [];
}

function firstMessageTime(messages: readonly ChatMessageView[]): string {
  return messages[0]?.createdAt ?? '';
}

function lastMessageTime(messages: readonly ChatMessageView[], fallback: string): string {
  return messages[messages.length - 1]?.createdAt ?? fallback;
}

/** 標題取第一則提問；沒有提問時用預設名稱。不含任何助理回覆內容。 */
function deriveThreadTitle(messages: readonly ChatMessageView[]): string {
  const asked = messages.find((message) => message.author === 'account');
  if (asked === undefined || asked.author !== 'account') return CHAT_DEFAULT_THREAD_TITLE;
  const text = asked.text.replace(/\s+/g, ' ').trim();
  if (text === '') return CHAT_DEFAULT_THREAD_TITLE;

  return text.length > CHAT_THREAD_TITLE_MAX_LENGTH
    ? `${text.slice(0, CHAT_THREAD_TITLE_MAX_LENGTH)}…`
    : text;
}

/** 未登入訪客在收集紀錄中的名稱；加上 id 末段，讓兩位訪客分得開，且不含任何個人資料。 */
function anonymousSubjectName(subjectId: string): string {
  const visitorId = subjectId.slice('subject-'.length);
  if (!visitorId.startsWith('visitor-')) return '已停用的帳號';
  const suffix = visitorId.slice('visitor-'.length).slice(-4);
  return `${ANONYMOUS_VISITOR_SUBJECT_NAME}（${suffix}）`;
}

function threadSequence(id: string): number {
  const parsed = Number.parseInt(id.slice('chat-thread-'.length), 10);
  return Number.isNaN(parsed) ? 0 : parsed;
}

/** 由新到舊：先比最後活動時間，同時間時用序號，保證順序穩定。 */
function byRecentActivity(a: StoredChatThread, b: StoredChatThread): number {
  if (a.updatedAt !== b.updatedAt) return a.updatedAt < b.updatedAt ? 1 : -1;
  return threadSequence(b.id) - threadSequence(a.id);
}

function toThreadSummary(thread: StoredChatThread): ChatThreadSummaryView {
  return {
    id: thread.id,
    title: thread.title,
    messageCount: thread.messages.length,
    updatedAt: thread.updatedAt,
  };
}

function isStoredChatDatabaseRecord(value: unknown): value is DatabaseRecordFixture {
  return (
    isRecord(value) &&
    typeof value['id'] === 'string' &&
    value['id'].startsWith('record-chat-') &&
    typeof value['databaseId'] === 'string' &&
    typeof value['subjectId'] === 'string' &&
    typeof value['recordedAt'] === 'string' &&
    Array.isArray(value['values'])
  );
}

interface ChatFormTarget {
  readonly assistant: AssistantConfigurationView;
  readonly form: ChatFormView;
}

const DATABASE_STATUS: Record<
  DemoSeed['databases'][number]['status'],
  ConnectableSourceStatus
> = {
  connected: 'ready',
  disconnected: 'needs-attention',
  'sync-error': 'needs-attention',
};

export class MockDemoRepository implements DemoRepository {
  private scenario: DemoScenario = 'ready';
  private readonly storage: DemoKeyValueStorage;
  /** 未登入訪客的對話只寫在這裡，和帳號的儲存完全分開。 */
  private readonly visitorStorage: DemoKeyValueStorage;
  private readonly now: () => Date;
  /**
   * 助理關閉「保存自己的對話」時，對話只留在這個 repository 實例的記憶體裡：
   * 不寫入 storage、不列在對話紀錄中，重新整理（重新建立實例）就消失。
   */
  private readonly ephemeralChats = new Map<string, readonly ChatMessageView[]>();

  constructor(
    private readonly seed: DemoSeed = DEMO_SEED,
    options: MockDemoRepositoryOptions = {},
  ) {
    this.storage = options.storage ?? createMemoryStorage();
    this.visitorStorage = options.visitorStorage ?? createMemoryStorage();
    this.now = options.now ?? (() => new Date());
  }

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
    const configurations = this.assistants().filter(
      (assistant) => assistant.ownerAccountId === viewerAccountId,
    );

    return this.applyScenario(configurations);
  }

  listUsableAssistants(
    viewerAccountId: AccountId,
  ): ReturnType<DemoRepository['listUsableAssistants']> {
    const assistants = this.assistants()
      .filter((assistant) => this.canUseAssistant(assistant, viewerAccountId))
      .map((assistant) => this.toAssistantSummary(assistant, viewerAccountId));

    return this.applyScenario(assistants);
  }

  getAssistantSources(
    viewerAccountId: AccountId,
    assistantId: AssistantId,
  ): ReturnType<DemoRepository['getAssistantSources']> {
    const assistant = this.assistants().find(
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

  getAssistantSettings(
    viewerAccountId: AccountId,
    assistantId: string,
  ): ReturnType<DemoRepository['getAssistantSettings']> {
    const assistant = this.settingsTarget(viewerAccountId, assistantId);
    if (assistant === undefined) return this.assistantSettingsPermissionDenied();

    return this.applyScenario(this.assistantSettings(assistant));
  }

  updateAssistantSettings(
    viewerAccountId: AccountId,
    assistantId: string,
    patch: AssistantSettingsPatch,
  ): UpdateAssistantSettingsResult {
    const assistant = this.settingsTarget(viewerAccountId, assistantId);
    if (assistant === undefined) return this.assistantSettingsPermissionDenied();

    const current = this.assistantSettings(assistant);
    return this.commitAssistantSettings({
      ...current,
      configuration: {
        ...current.configuration,
        name: patch.name ?? current.configuration.name,
        purpose: patch.purpose ?? current.configuration.purpose,
        audience: patch.audience ?? current.configuration.audience,
      },
      tone: patch.tone ?? current.tone,
      roleInstructions: patch.roleInstructions ?? current.roleInstructions,
      rules: { ...current.rules, ...patch.rules },
    });
  }

  setAssistantSourceConnection(
    viewerAccountId: AccountId,
    assistantId: string,
    source: AssistantSourceReference,
    connected: boolean,
  ): UpdateAssistantSettingsResult {
    const assistant = this.settingsTarget(viewerAccountId, assistantId);
    if (assistant === undefined) return this.assistantSettingsPermissionDenied();

    const visible = this.connectableSources(viewerAccountId).some(
      (candidate) => candidate.id === source.id && candidate.type === source.type,
    );
    if (connected && !visible) {
      return immutableCopy({
        status: 'validation-failed',
        errors: [
          { field: 'sources', message: '找不到這個資料來源，或你沒有它的存取權限。' },
        ],
        message: '找不到這個資料來源，或你沒有它的存取權限。',
      });
    }

    const current = this.assistantSettings(assistant);
    const matches = (candidate: AssistantSourceReference) =>
      candidate.id === source.id && candidate.type === source.type;
    const sources = connected
      ? current.sources.some(matches)
        ? current.sources
        : [...current.sources, source]
      : current.sources.filter((candidate) => !matches(candidate));
    // 解除連接的資料庫若正是寫入對象，一併清掉，避免助理寫進已斷線的資料庫。
    const dropsWriteTarget =
      !connected &&
      source.type === 'database' &&
      current.rules.dataWriteDatabaseId === source.id;

    return this.commitAssistantSettings({
      ...current,
      sources,
      rules: dropsWriteTarget
        ? { ...current.rules, dataWriteDatabaseId: null, dataWritePurpose: '' }
        : current.rules,
    });
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
    const databases = this.databases().filter(
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
    const assistant = this.assistants().find(
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
    const assistant = this.assistants().find(
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

    return this.applyScenario({
      ...analytics,
      conversationCount: analytics.conversationCount + this.countChatConversations(assistantId),
    });
  }

  listPublishingChannels(
    viewerAccountId: AccountId,
  ): ReturnType<DemoRepository['listPublishingChannels']> {
    return this.applyScenario(
      this.channelOverview(viewerAccountId).flatMap((entry) => entry.channels),
    );
  }

  listChannelOverview(
    viewerAccountId: AccountId,
  ): ReturnType<DemoRepository['listChannelOverview']> {
    return this.applyScenario(this.channelOverview(viewerAccountId));
  }

  getAssistantPublishing(
    viewerAccountId: AccountId,
    assistantId: string,
  ): ReturnType<DemoRepository['getAssistantPublishing']> {
    const assistant = this.ownedAssistant(viewerAccountId, assistantId);
    if (assistant === undefined) return this.publishingPermissionDenied();

    return this.applyScenario(this.toPublishingView(assistant));
  }

  updatePlatformSharing(
    viewerAccountId: AccountId,
    assistantId: string,
    accountIds: readonly AccountId[],
  ): UpdatePlatformSharingResult {
    const assistant = this.ownedAssistant(viewerAccountId, assistantId);
    if (assistant === undefined) return this.publishingPermissionDenied();

    const allowed = normalizePlatformAccounts(assistant, this.seed.accounts, accountIds);
    if (allowed === null) {
      return immutableCopy({
        status: 'validation-failed',
        errors: [{ field: 'accounts', message: '只能選擇清單中的帳號。' }],
        message: '可使用的帳號有誤，請重新選擇。',
      });
    }
    const record = this.publishingRecord(assistant);
    this.savePublishingRecord(assistant.id, {
      ...record,
      platform: { ...record.platform, allowedAccountIds: allowed, updatedAt: this.now().toISOString() },
    });

    return this.applyScenario(this.toPublishingView(assistant).platform);
  }

  updateWebsiteEmbed(
    viewerAccountId: AccountId,
    assistantId: string,
    settings: WebsiteEmbedSettings,
  ): UpdateWebsiteEmbedResult {
    const assistant = this.ownedAssistant(viewerAccountId, assistantId);
    if (assistant === undefined) return this.publishingPermissionDenied();

    const { errors, normalized } = validateWebsiteSettings(settings);
    if (errors.length > 0) {
      return immutableCopy({ status: 'validation-failed', errors, message: '還有設定需要修正。' });
    }
    const record = this.publishingRecord(assistant);
    const domainsChanged =
      normalized.allowedDomains.join('\n') !== record.website.allowedDomains.join('\n');
    this.savePublishingRecord(assistant.id, {
      ...record,
      website: {
        ...record.website,
        ...normalized,
        installCheck: domainsChanged ? 'not-checked' : record.website.installCheck,
        installCheckedAt: domainsChanged ? null : record.website.installCheckedAt,
        updatedAt: this.now().toISOString(),
      },
    });

    return this.applyScenario(this.toPublishingView(assistant).website);
  }

  checkWebsiteInstallation(
    viewerAccountId: AccountId,
    assistantId: string,
  ): ReturnType<DemoRepository['checkWebsiteInstallation']> {
    const assistant = this.ownedAssistant(viewerAccountId, assistantId);
    if (assistant === undefined) return this.publishingPermissionDenied();

    const record = this.publishingRecord(assistant);
    if (record.website.allowedDomains.length > 0) {
      const now = this.now().toISOString();
      this.savePublishingRecord(assistant.id, {
        ...record,
        website: {
          ...record.website,
          installCheck: this.scenario === 'disconnected-channel' ? 'not-detected' : 'detected',
          installCheckedAt: now,
          updatedAt: now,
        },
      });
    }

    return this.applyScenario(this.toPublishingView(assistant).website);
  }

  saveLineSettings(
    viewerAccountId: AccountId,
    assistantId: string,
    input: LineSettingsInput,
  ): ReturnType<DemoRepository['saveLineSettings']> {
    const assistant = this.ownedAssistant(viewerAccountId, assistantId);
    if (assistant === undefined) return this.publishingPermissionDenied();

    const record = this.publishingRecord(assistant);
    this.savePublishingRecord(assistant.id, {
      ...record,
      line: {
        ...record.line,
        ...trimLineSettings(input),
        checked: true,
        enabled: false,
        lastTest: null,
        updatedAt: this.now().toISOString(),
      },
    });

    return this.applyScenario(this.toPublishingView(assistant).line);
  }

  sendLineTestMessage(
    viewerAccountId: AccountId,
    assistantId: string,
  ): ReturnType<DemoRepository['sendLineTestMessage']> {
    const assistant = this.ownedAssistant(viewerAccountId, assistantId);
    if (assistant === undefined) return this.publishingPermissionDenied();

    const record = this.publishingRecord(assistant);
    const now = this.now().toISOString();
    this.savePublishingRecord(assistant.id, {
      ...record,
      line: { ...record.line, lastTest: lineTestResult(record.line, now), updatedAt: now },
    });

    return this.applyScenario(this.toPublishingView(assistant).line);
  }

  activateLineChannel(viewerAccountId: AccountId, assistantId: string): ActivateLineChannelResult {
    const assistant = this.ownedAssistant(viewerAccountId, assistantId);
    if (assistant === undefined) return this.publishingPermissionDenied();

    const record = this.publishingRecord(assistant);
    if (!canActivateLine(record.line)) {
      return immutableCopy({
        status: 'validation-failed',
        errors: [{ field: 'line', message: '請先讓所有欄位通過檢查並確認測試訊息送達，再啟用。' }],
        message: 'LINE 管道尚未完成測試。',
      });
    }
    this.savePublishingRecord(assistant.id, {
      ...record,
      line: { ...record.line, enabled: true, updatedAt: this.now().toISOString() },
    });

    return this.applyScenario(this.toPublishingView(assistant).line);
  }

  setPublishingChannelPaused(
    viewerAccountId: AccountId,
    assistantId: string,
    channelType: PublishingChannelType,
    paused: boolean,
  ): ReturnType<DemoRepository['setPublishingChannelPaused']> {
    const assistant = this.ownedAssistant(viewerAccountId, assistantId);
    if (assistant === undefined || !PUBLISHING_CHANNEL_TYPES.includes(channelType)) {
      return this.publishingPermissionDenied();
    }

    const record = this.publishingRecord(assistant);
    const updatedAt = this.now().toISOString();
    this.savePublishingRecord(assistant.id, {
      ...record,
      [channelType]: { ...record[channelType], paused, updatedAt },
    });

    return this.applyScenario(this.toPublishingView(assistant)[channelType].channel);
  }

  listAssistantTemplates(): ReturnType<DemoRepository['listAssistantTemplates']> {
    return this.applyScenario(this.seed.assistantTemplates);
  }

  listConnectableSources(
    viewerAccountId: AccountId,
  ): ReturnType<DemoRepository['listConnectableSources']> {
    return this.applyScenario(this.connectableSources(viewerAccountId));
  }

  listTrialQuestions(): ReturnType<DemoRepository['listTrialQuestions']> {
    return this.applyScenario(
      this.seed.trialQuestions.map(({ id, text }) => ({ id, text })),
    );
  }

  previewTrialAnswer(
    viewerAccountId: AccountId,
    request: TrialAnswerRequest,
  ): ReturnType<DemoRepository['previewTrialAnswer']> {
    if (!this.canManageAssistants(viewerAccountId)) {
      return this.draftPermissionDenied();
    }

    const question = this.seed.trialQuestions.find(
      (candidate) => candidate.id === request.questionId,
    );
    const companyAnswer = question?.companyAnswer ?? null;
    const knowledgeBase =
      companyAnswer === null
        ? undefined
        : this.seed.knowledgeBases.find(
            (candidate) =>
              candidate.id === companyAnswer.sourceId &&
              candidate.ownerAccountId === viewerAccountId &&
              request.sources.some(
                (source) =>
                  source.type === 'knowledge-base' && source.id === candidate.id,
              ),
          );

    let answer: TrialAnswerView;
    if (companyAnswer !== null && knowledgeBase !== undefined) {
      answer = {
        kind: 'company-data',
        questionId: request.questionId,
        text: companyAnswer.text,
        citation: request.rules.showCitations
          ? {
              sourceId: knowledgeBase.id,
              sourceName: knowledgeBase.name,
              excerpt: companyAnswer.excerpt,
            }
          : null,
      };
    } else if (
      question?.generalAnswer != null &&
      request.rules.knowledgeScope === 'allow-general-knowledge'
    ) {
      answer = {
        kind: 'general-knowledge',
        questionId: request.questionId,
        text: question.generalAnswer,
      };
    } else {
      answer = {
        kind: 'no-answer',
        questionId: request.questionId,
        text: request.rules.refusalMessage,
      };
    }

    return this.applyScenario(answer);
  }

  getAssistantDraft(
    viewerAccountId: AccountId,
  ): ReturnType<DemoRepository['getAssistantDraft']> {
    if (!this.canManageAssistants(viewerAccountId)) {
      return this.draftPermissionDenied();
    }

    const stored = normalizeStoredDraft(
      parseJson(this.storage.getItem(DRAFT_KEY_PREFIX + viewerAccountId)),
    );

    return this.applyScenario(stored);
  }

  saveAssistantDraft(
    viewerAccountId: AccountId,
    draft: AssistantDraft,
  ): ReturnType<DemoRepository['saveAssistantDraft']> {
    if (!this.canManageAssistants(viewerAccountId)) {
      return this.draftPermissionDenied();
    }

    const record: StoredDraftRecord = {
      version: 1,
      savedAt: this.now().toISOString(),
      draft,
    };
    this.storage.setItem(
      DRAFT_KEY_PREFIX + viewerAccountId,
      JSON.stringify(record),
    );

    return this.applyScenario({ draft: record.draft, savedAt: record.savedAt });
  }

  discardAssistantDraft(viewerAccountId: AccountId): void {
    this.storage.removeItem(DRAFT_KEY_PREFIX + viewerAccountId);
  }

  createAssistantFromDraft(
    viewerAccountId: AccountId,
    draft: AssistantDraft,
  ): CreateAssistantResult {
    if (!this.canManageAssistants(viewerAccountId)) {
      return this.draftPermissionDenied();
    }

    const errors = validateAssistantDraft(draft);
    if (errors.length > 0 || draft.audience === null) {
      return immutableCopy({
        status: 'validation-failed',
        errors,
        message: '還有必要設定尚未完成。',
      });
    }

    const connectable = this.connectableSources(viewerAccountId);
    const isConnectable = (source: AssistantSourceReference) =>
      connectable.some(
        (candidate) =>
          candidate.id === source.id && candidate.type === source.type,
      );
    const sources = draft.sources.filter(isConnectable);

    const configuration: AssistantConfigurationView = {
      id: this.nextCreatedAssistantId(),
      ownerAccountId: viewerAccountId,
      name: draft.name.trim(),
      purpose: draft.purpose.trim(),
      status: 'ready',
      audience: draft.audience,
      sharedWithAccountIds: [],
      knowledgeBaseIds: sources.flatMap((source) =>
        source.type === 'knowledge-base' ? [source.id] : [],
      ),
      databaseIds: sources.flatMap((source) =>
        source.type === 'database' ? [source.id] : [],
      ),
      keepOwnConversations: draft.rules.keepOwnConversations,
    };

    this.storage.setItem(
      CREATED_ASSISTANTS_KEY,
      JSON.stringify([...this.createdAssistants(), configuration]),
    );
    // 精靈填的語氣、角色說明與回答規則在 configuration 裡沒有位置，
    // 一併寫進設定紀錄，建立後的三個編輯頁籤才看得到使用者剛剛填的內容。
    this.saveAssistantSettings({
      configuration,
      sources,
      tone: draft.tone,
      roleInstructions: draft.roleInstructions,
      rules: draft.rules,
      savedAt: null,
    });
    this.discardAssistantDraft(viewerAccountId);

    return this.applyScenario(configuration);
  }

  listKnowledgeBaseSummaries(
    viewerAccountId: AccountId,
  ): ReturnType<DemoRepository['listKnowledgeBaseSummaries']> {
    return this.applyScenario(
      this.seed.knowledgeBases
        .filter((knowledgeBase) => knowledgeBase.ownerAccountId === viewerAccountId)
        .map((knowledgeBase) => this.toKnowledgeSummary(knowledgeBase, viewerAccountId)),
    );
  }

  getKnowledgeBaseDetail(
    viewerAccountId: AccountId,
    knowledgeBaseId: string,
  ): ReturnType<DemoRepository['getKnowledgeBaseDetail']> {
    const knowledgeBase = this.ownedKnowledgeBase(viewerAccountId, knowledgeBaseId);
    if (knowledgeBase === undefined) return this.knowledgePermissionDenied();

    const record = this.knowledgeRecord(knowledgeBase.id);
    const detail: KnowledgeBaseDetailView = {
      summary: this.toKnowledgeSummary(knowledgeBase, viewerAccountId),
      documents: record.documents,
      connectedAssistants: this.assistants()
        .filter(
          (assistant) =>
            assistant.ownerAccountId === viewerAccountId &&
            assistant.knowledgeBaseIds.includes(knowledgeBase.id),
        )
        .map(({ id, name, status }) => ({ id, name, status })),
      sharing: record.sharing,
      shareTargets: this.seed.accounts
        .filter((account) => account.id !== viewerAccountId)
        .map(({ id, displayName }) => ({ id, displayName })),
    };

    return this.applyScenario(detail);
  }

  addDemoKnowledgeDocument(
    viewerAccountId: AccountId,
    knowledgeBaseId: KnowledgeBaseId,
  ): ReturnType<DemoRepository['addDemoKnowledgeDocument']> {
    const knowledgeBase = this.ownedKnowledgeBase(viewerAccountId, knowledgeBaseId);
    if (knowledgeBase === undefined) return this.knowledgePermissionDenied();

    const record = this.knowledgeRecord(knowledgeBase.id);
    const existing = new Set<string>(record.documents.map((document) => document.id));
    let sequence = this.now().getTime();
    while (existing.has(`document-demo-${sequence}`)) sequence += 1;
    const demoCount = record.documents.filter((document) =>
      document.id.startsWith('document-demo-'),
    ).length;

    const document: KnowledgeDocumentView = {
      id: `document-demo-${sequence}`,
      kind: 'document',
      name: DEMO_DOCUMENT_NAMES[demoCount % DEMO_DOCUMENT_NAMES.length],
      status: 'queued',
      issue: null,
      updatedAt: this.now().toISOString(),
    };
    this.saveKnowledgeRecord(knowledgeBase.id, {
      ...record,
      documents: [...record.documents, document],
    });

    return this.applyScenario(document);
  }

  advanceKnowledgeDocument(
    viewerAccountId: AccountId,
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
  ): ReturnType<DemoRepository['advanceKnowledgeDocument']> {
    return this.updateKnowledgeDocument(viewerAccountId, knowledgeBaseId, documentId, (document) => {
      const next = NEXT_DOCUMENT_STATUS[document.status];
      return next === undefined ? document : { ...document, status: next };
    });
  }

  retryKnowledgeDocument(
    viewerAccountId: AccountId,
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
  ): ReturnType<DemoRepository['retryKnowledgeDocument']> {
    return this.updateKnowledgeDocument(viewerAccountId, knowledgeBaseId, documentId, (document) =>
      needsKnowledgeAttention(document.status)
        ? { ...document, status: 'queued', issue: null }
        : document,
    );
  }

  updateKnowledgeSharing(
    viewerAccountId: AccountId,
    knowledgeBaseId: KnowledgeBaseId,
    sharing: KnowledgeSharingView,
  ): UpdateKnowledgeSharingResult {
    const knowledgeBase = this.ownedKnowledgeBase(viewerAccountId, knowledgeBaseId);
    if (knowledgeBase === undefined) return this.knowledgePermissionDenied();

    const validTargets = new Set<string>(
      this.seed.accounts
        .filter((account) => account.id !== viewerAccountId)
        .map((account) => account.id),
    );
    const sharedWithAccountIds =
      sharing.scope === 'specific-accounts'
        ? sharing.sharedWithAccountIds.filter((id) => validTargets.has(id))
        : [];
    if (sharing.scope === 'specific-accounts' && sharedWithAccountIds.length === 0) {
      return immutableCopy({
        status: 'validation-failed',
        message: '請至少選擇一個帳號或團隊。',
      });
    }

    const saved: KnowledgeSharingView = {
      scope: sharing.scope,
      sharedWithAccountIds,
      allowOriginalDownload: sharing.scope === 'public' && sharing.allowOriginalDownload,
    };
    this.saveKnowledgeRecord(knowledgeBase.id, {
      ...this.knowledgeRecord(knowledgeBase.id),
      sharing: saved,
    });

    return this.applyScenario(saved);
  }

  listDatabaseTemplates(
    viewerAccountId: AccountId,
  ): ReturnType<DemoRepository['listDatabaseTemplates']> {
    if (!this.canManageDataSources(viewerAccountId)) return this.createDatabasePermissionDenied();
    return this.applyScenario(this.seed.databaseTemplates);
  }

  listDatabaseSummaries(
    viewerAccountId: AccountId,
  ): ReturnType<DemoRepository['listDatabaseSummaries']> {
    return this.applyScenario(
      this.databases()
        .filter((database) => database.ownerAccountId === viewerAccountId)
        .map((database) => this.toDatabaseSummary(database, viewerAccountId)),
    );
  }

  createDatabaseFromTemplate(
    viewerAccountId: AccountId,
    input: Parameters<DemoRepository['createDatabaseFromTemplate']>[1],
  ): CreateDatabaseResult {
    if (!this.canManageDataSources(viewerAccountId)) return this.createDatabasePermissionDenied();

    const template = this.seed.databaseTemplates.find((candidate) => candidate.id === input.templateId);
    if (template === undefined) {
      return immutableCopy({ status: 'validation-failed', message: '請選擇一個模板。' });
    }
    const name = input.name.trim();
    if (name.length === 0) {
      return immutableCopy({ status: 'validation-failed', message: '請輸入資料庫名稱。' });
    }
    if (name.length > 40) {
      return immutableCopy({ status: 'validation-failed', message: '資料庫名稱請在 40 個字以內。' });
    }

    const view: DatabaseView = {
      id: this.nextCreatedDatabaseId(),
      ownerAccountId: viewerAccountId,
      name,
      status: 'connected',
      accessMode: 'read-only',
      tableCount: 1,
      lastSyncedAt: this.now().toISOString(),
    };
    const collection: DatabaseCollectionFixture = {
      purpose: template.description,
      templateName: template.name,
      dataManagerAccountIds: [viewerAccountId],
      fields: template.fields,
    };
    this.storage.setItem(
      CREATED_DATABASES_KEY,
      JSON.stringify([...this.createdDatabases(), { view, collection }]),
    );

    return this.applyScenario(this.toDatabaseSummary(view, viewerAccountId));
  }

  getDatabaseDetail(
    viewerAccountId: AccountId,
    databaseId: string,
  ): ReturnType<DemoRepository['getDatabaseDetail']> {
    const database = this.ownedDatabase(viewerAccountId, databaseId);
    if (database === undefined) return this.databasePermissionDenied();

    const collection = this.databaseCollection(database.id);
    const displayName = (id: AccountId) => ({
      id,
      displayName: this.seed.accounts.find((account) => account.id === id)?.displayName ?? '已停用的帳號',
    });
    const detail: DatabaseDetailView = {
      summary: this.toDatabaseSummary(database, viewerAccountId),
      fields: collection.fields,
      connectedAssistants: this.assistants()
        .filter(
          (assistant) =>
            assistant.ownerAccountId === viewerAccountId &&
            assistant.databaseIds.includes(database.id),
        )
        .map(({ id, name, status }) => ({ id, name, status })),
      access: {
        owner: displayName(database.ownerAccountId),
        dataManagers: collection.dataManagerAccountIds.map(displayName),
        viewerIsDataManager: collection.dataManagerAccountIds.includes(viewerAccountId),
      },
    };

    return this.applyScenario(detail);
  }

  updateDatabaseFields(
    viewerAccountId: AccountId,
    databaseId: DatabaseId,
    fields: readonly DatabaseFieldView[],
  ): UpdateDatabaseFieldsResult {
    const database = this.ownedDatabase(viewerAccountId, databaseId);
    if (database === undefined) return this.databasePermissionDenied();

    const normalized = fields.map(normalizeField);
    const errors = validateFields(normalized);
    if (errors.length > 0) {
      return immutableCopy({ status: 'validation-failed', errors, message: '還有欄位需要修正，表單尚未儲存。' });
    }

    const record: StoredDatabaseFields = {
      version: 1,
      savedAt: this.now().toISOString(),
      fields: normalized,
    };
    this.storage.setItem(DATABASE_FIELDS_KEY_PREFIX + database.id, JSON.stringify(record));

    return this.applyScenario(normalized);
  }

  previewDatabaseEntry(
    viewerAccountId: AccountId,
    databaseId: DatabaseId,
    answers: Parameters<DemoRepository['previewDatabaseEntry']>[2],
  ): PreviewDatabaseEntryResult {
    const database = this.ownedDatabase(viewerAccountId, databaseId);
    if (database === undefined) return this.databasePermissionDenied();

    const outcome = evaluateTrial(this.databaseCollection(database.id).fields, answers);
    if ('errors' in outcome) {
      return immutableCopy({
        status: 'validation-failed',
        errors: outcome.errors,
        message: '試填內容還有需要修正的地方。',
      });
    }

    return this.applyScenario({ saved: false as const, entries: outcome.entries });
  }

  getDatabaseTracking(
    viewerAccountId: AccountId,
    databaseId: string,
  ): ReturnType<DemoRepository['getDatabaseTracking']> {
    const database = this.ownedDatabase(viewerAccountId, databaseId);
    if (database === undefined) return this.databasePermissionDenied();
    if (!this.databaseCollection(database.id).dataManagerAccountIds.includes(viewerAccountId)) {
      return this.permissionDenied(
        'database-records',
        '只有指定的資料管理者可以查看收集紀錄。',
      );
    }

    const records = this.consentedRecords(database.id);
    const tracking: DatabaseTrackingView = {
      databaseId: database.id,
      subjects: [...this.seed.trackedSubjects, ...this.chatSubjects()]
        .filter((subject) => subject.databaseId === database.id)
        .map((subject) => {
          const chronological = records.filter((record) => record.subjectId === subject.id);
          return {
            id: subject.id,
            displayName: subject.displayName,
            records: [...chronological].reverse().map(toRecordView),
            comparison: compareRecords(chronological),
          };
        })
        .filter((subject) => subject.records.length > 0),
    };

    return this.applyScenario(tracking);
  }

  listChatThreads(
    viewerAccountId: AccountId,
    assistantId: string,
  ): ReturnType<DemoRepository['listChatThreads']> {
    const assistant = this.usableAssistant(viewerAccountId, assistantId);
    if (assistant === undefined) return this.assistantUsePermissionDenied();

    return this.applyScenario(this.toThreadListView(viewerAccountId, assistant));
  }

  createChatThread(
    viewerAccountId: AccountId,
    assistantId: string,
  ): ReturnType<DemoRepository['createChatThread']> {
    const assistant = this.usableAssistant(viewerAccountId, assistantId);
    if (assistant === undefined) return this.assistantUsePermissionDenied();

    // 不保存對話的助理沒有第二段對話可開，「開新對話」只是把暫時對話清空。
    if (!this.keepsConversations(assistant)) {
      this.ephemeralChats.delete(this.ephemeralKey(viewerAccountId, assistant.id));
      return this.applyScenario(this.toChatView(viewerAccountId, assistant, []));
    }

    const threads = this.storedThreads(viewerAccountId, assistant.id);
    const createdAt = this.now().toISOString();
    const thread: StoredChatThread = {
      id: this.nextThreadId(threads),
      title: CHAT_DEFAULT_THREAD_TITLE,
      titleSource: 'derived',
      createdAt,
      updatedAt: createdAt,
      messages: [],
    };
    this.saveThreads(viewerAccountId, assistant.id, [...threads, thread]);

    return this.applyScenario(
      this.toChatView(viewerAccountId, assistant, [], thread.id, thread.title),
    );
  }

  renameChatThread(
    viewerAccountId: AccountId,
    assistantId: string,
    threadId: string,
    title: string,
  ): RenameChatThreadResult {
    const assistant = this.usableAssistant(viewerAccountId, assistantId);
    if (assistant === undefined) return this.assistantUsePermissionDenied();
    if (!this.keepsConversations(assistant)) return this.chatThreadPermissionDenied();

    const threads = this.storedThreads(viewerAccountId, assistant.id);
    const thread = threads.find((candidate) => candidate.id === threadId);
    if (thread === undefined) return this.chatThreadPermissionDenied();

    const trimmed = title.trim();
    if (trimmed.length === 0) {
      return immutableCopy({ status: 'validation-failed', message: '請輸入對話名稱。' });
    }
    if (trimmed.length > MAX_THREAD_TITLE_LENGTH) {
      return immutableCopy({
        status: 'validation-failed',
        message: `對話名稱請在 ${MAX_THREAD_TITLE_LENGTH} 個字以內。`,
      });
    }

    const renamed: StoredChatThread = { ...thread, title: trimmed, titleSource: 'manual' };
    this.saveThreads(
      viewerAccountId,
      assistant.id,
      threads.map((candidate) => (candidate.id === thread.id ? renamed : candidate)),
    );

    return this.applyScenario(toThreadSummary(renamed));
  }

  deleteChatThread(
    viewerAccountId: AccountId,
    assistantId: string,
    threadId: string,
  ): ReturnType<DemoRepository['deleteChatThread']> {
    const assistant = this.usableAssistant(viewerAccountId, assistantId);
    if (assistant === undefined) return this.assistantUsePermissionDenied();
    if (!this.keepsConversations(assistant)) return this.chatThreadPermissionDenied();

    const threads = this.storedThreads(viewerAccountId, assistant.id);
    if (!threads.some((candidate) => candidate.id === threadId)) {
      return this.chatThreadPermissionDenied();
    }

    this.saveThreads(
      viewerAccountId,
      assistant.id,
      threads.filter((candidate) => candidate.id !== threadId),
    );

    return this.applyScenario(this.toThreadListView(viewerAccountId, assistant));
  }

  getAssistantChat(
    viewerId: ChatViewerId,
    assistantId: string,
    threadId?: string,
  ): ReturnType<DemoRepository['getAssistantChat']> {
    const assistant = this.chatAssistant(viewerId, assistantId);
    if (assistant === undefined) return this.chatAssistantPermissionDenied(viewerId);

    if (!this.keepsConversations(assistant)) {
      if (threadId !== undefined) return this.chatThreadPermissionDenied();
      return this.applyScenario(
        this.toChatView(viewerId, assistant, this.ephemeralMessages(viewerId, assistant.id)),
      );
    }

    const threads = this.storedThreads(viewerId, assistant.id);
    const thread =
      threadId === undefined
        ? [...threads].sort(byRecentActivity)[0]
        : threads.find((candidate) => candidate.id === threadId);
    if (thread === undefined) {
      if (threadId !== undefined) return this.chatThreadPermissionDenied();
      return this.applyScenario(this.toChatView(viewerId, assistant, []));
    }

    return this.applyScenario(
      this.toChatView(viewerId, assistant, thread.messages, thread.id, thread.title),
    );
  }

  sendChatMessage(
    viewerId: ChatViewerId,
    assistantId: string,
    text: string,
    threadId?: string,
  ): SendChatMessageResult {
    const assistant = this.chatAssistant(viewerId, assistantId);
    if (assistant === undefined) return this.chatAssistantPermissionDenied(viewerId);

    const question = text.trim();
    if (question.length === 0) {
      return immutableCopy({ status: 'validation-failed', message: '請先輸入問題。' });
    }
    if (question.length > MAX_QUESTION_LENGTH) {
      return immutableCopy({
        status: 'validation-failed',
        message: `問題請在 ${MAX_QUESTION_LENGTH} 個字以內。`,
      });
    }

    const target = this.resolveChatTarget(viewerId, assistant, threadId);
    if (target === undefined) return this.chatThreadPermissionDenied();

    const messages = this.targetMessages(viewerId, assistant, target);
    const createdAt = this.now().toISOString();
    const next: readonly ChatMessageView[] = [
      ...messages,
      { id: `chat-message-${messages.length + 1}`, author: 'account', text: question, createdAt },
      {
        id: `chat-message-${messages.length + 2}`,
        author: 'assistant',
        reply: this.resolveChatReply(assistant, question),
        createdAt,
      },
    ];

    return this.applyScenario(this.writeChatMessages(viewerId, assistant, target, next));
  }

  reviewChatForm(
    viewerId: ChatViewerId,
    assistantId: string,
    formId: DatabaseId,
    answers: DatabaseTrialAnswers,
  ): ReviewChatFormResult {
    const target = this.chatFormTarget(viewerId, assistantId, formId);
    if (target === undefined) return this.chatAssistantPermissionDenied(viewerId);

    const outcome = evaluateTrial(target.form.fields, answers);
    if ('errors' in outcome) {
      return immutableCopy({
        status: 'validation-failed',
        errors: outcome.errors,
        message: '還有欄位需要修正。',
      });
    }

    return this.applyScenario({ formId: target.form.id, saved: false as const, entries: outcome.entries });
  }

  submitChatForm(
    viewerId: ChatViewerId,
    assistantId: string,
    submission: ChatFormSubmission,
    threadId?: string,
  ): SubmitChatFormResult {
    const target = this.chatFormTarget(viewerId, assistantId, submission.formId);
    if (target === undefined) return this.chatAssistantPermissionDenied(viewerId);

    const { assistant, form } = target;
    const outcome = evaluateTrial(form.fields, submission.answers);
    if ('errors' in outcome) {
      return immutableCopy({
        status: 'validation-failed',
        errors: outcome.errors,
        message: '還有欄位需要修正。',
      });
    }
    if (submission.consent !== true) {
      return immutableCopy({
        status: 'validation-failed',
        errors: [{ fieldId: null, message: '請先勾選同意，才能送出資料。' }],
        message: '尚未同意，資料沒有送出。',
      });
    }

    const chatTarget = this.resolveChatTarget(viewerId, assistant, threadId);
    if (chatTarget === undefined) return this.chatThreadPermissionDenied();

    const recordedAt = this.now().toISOString();
    const existing = this.chatRecords();
    // 未登入訪客的紀錄以訪客 id 當追蹤對象：與任何帳號都不同，也不冒認成帳號。
    const record: DatabaseRecordFixture = {
      id: `record-chat-${existing.length + 1}`,
      databaseId: form.id,
      subjectId: `subject-${viewerId}`,
      recordedAt,
      source: 'assistant-conversation',
      consentStatus: 'consented',
      values: toRecordValues(form.fields, submission.answers),
    };
    this.storage.setItem(CHAT_RECORDS_KEY, JSON.stringify([...existing, record]));

    const messages = this.targetMessages(viewerId, assistant, chatTarget);
    const next: readonly ChatMessageView[] = [
      ...messages,
      {
        id: `chat-message-${messages.length + 1}`,
        author: 'assistant',
        createdAt: recordedAt,
        reply: {
          kind: 'submission-receipt',
          text: `已送出。資料只會交給 ${form.consent.recipient}，你可以隨時申請撤回或刪除。`,
          recipient: form.consent.recipient,
          entries: outcome.entries,
        },
      },
    ];

    return this.applyScenario(
      this.writeChatMessages(viewerId, assistant, chatTarget, next),
    );
  }

  /** 可使用的助理；不存在與無權限都回傳 undefined，呼叫端回覆相同訊息。 */
  private usableAssistant(
    viewerAccountId: AccountId,
    assistantId: string,
  ): AssistantConfigurationView | undefined {
    return this.assistants().find(
      (assistant) =>
        assistant.id === assistantId && this.canUseAssistant(assistant, viewerAccountId),
    );
  }

  private assistantUsePermissionDenied(): PermissionDeniedRepositoryView {
    return this.permissionDenied('assistant-use', '你沒有使用這個助理的權限，或它已不存在。');
  }

  /**
   * 未登入訪客可以開啟的助理：只有**官網嵌入或 LINE 已發布**的才算。
   * 尚未設定、測試中、需要處理與已暫停都當作不存在，所以新建立的助理預設是關著的。
   */
  private anonymouslyOpenAssistant(assistantId: string): AssistantConfigurationView | undefined {
    const assistant = this.assistants().find((candidate) => candidate.id === assistantId);
    if (assistant === undefined) return undefined;

    return isExternallyPublished(
      this.publishingRecord(assistant),
      this.scenario === 'disconnected-channel',
    )
      ? assistant
      : undefined;
  }

  /** 對話的發起者是帳號就看使用權限，是訪客就看助理有沒有對外發布。 */
  private chatAssistant(
    viewerId: ChatViewerId,
    assistantId: string,
  ): AssistantConfigurationView | undefined {
    return isVisitorId(viewerId)
      ? this.anonymouslyOpenAssistant(assistantId)
      : this.usableAssistant(viewerId, assistantId);
  }

  private chatAssistantPermissionDenied(viewerId: ChatViewerId): PermissionDeniedRepositoryView {
    return isVisitorId(viewerId)
      ? this.anonymousUsePermissionDenied()
      : this.assistantUsePermissionDenied();
  }

  /** 「沒有對外發布」與「不存在」回同一則訊息，不含助理名稱，也不揭露是哪一種。 */
  private anonymousUsePermissionDenied(): PermissionDeniedRepositoryView {
    return this.permissionDenied(
      'assistant-use',
      '這個助理沒有對外開放，或連結已失效。請回到原本的網站或 LINE 重新開啟。',
    );
  }

  /** 對話不存在與屬於其他帳號回傳同一則訊息，不洩漏對話標題或是否存在。 */
  private chatThreadPermissionDenied(): PermissionDeniedRepositoryView {
    return this.permissionDenied('chat-thread', '找不到這段對話，或它不屬於你的帳號。');
  }

  private keepsConversations(assistant: AssistantConfigurationView): boolean {
    return assistant.keepOwnConversations !== false;
  }

  private historyMode(assistant: AssistantConfigurationView): ChatHistoryMode {
    return this.keepsConversations(assistant) ? 'saved' : 'not-saved';
  }

  private chatKey(viewerId: ChatViewerId, assistantId: AssistantId): string {
    return `${CHAT_KEY_PREFIX}${viewerId}:${assistantId}`;
  }

  /** 訪客的對話寫進只屬於該分頁的儲存；帳號的對話仍寫進共用的 localStorage。 */
  private chatStorage(viewerId: ChatViewerId): DemoKeyValueStorage {
    return isVisitorId(viewerId) ? this.visitorStorage : this.storage;
  }

  private ephemeralKey(viewerId: ChatViewerId, assistantId: AssistantId): string {
    return `${viewerId}|${assistantId}`;
  }

  private ephemeralMessages(
    viewerId: ChatViewerId,
    assistantId: AssistantId,
  ): readonly ChatMessageView[] {
    return this.ephemeralChats.get(this.ephemeralKey(viewerId, assistantId)) ?? [];
  }

  private storedThreads(
    viewerId: ChatViewerId,
    assistantId: AssistantId,
  ): readonly StoredChatThread[] {
    return normalizeStoredThreads(
      parseJson(this.chatStorage(viewerId).getItem(this.chatKey(viewerId, assistantId))),
    );
  }

  private saveThreads(
    viewerId: ChatViewerId,
    assistantId: AssistantId,
    threads: readonly StoredChatThread[],
  ): void {
    const record: StoredChatRecord = { version: 2, threads };
    this.chatStorage(viewerId).setItem(
      this.chatKey(viewerId, assistantId),
      JSON.stringify(record),
    );
  }

  private nextThreadId(threads: readonly StoredChatThread[]): ChatThreadId {
    const highest = threads.reduce(
      (largest, thread) => Math.max(largest, threadSequence(thread.id)),
      0,
    );
    return `chat-thread-${highest + 1}`;
  }

  /**
   * 決定訊息要寫進哪一段對話。null 代表助理不保存對話、只有一段暫時對話；
   * undefined 代表指定的對話不存在或不屬於這個帳號。
   */
  private resolveChatTarget(
    viewerId: ChatViewerId,
    assistant: AssistantConfigurationView,
    threadId: string | undefined,
  ): StoredChatThread | null | undefined {
    if (!this.keepsConversations(assistant)) {
      return threadId === undefined ? null : undefined;
    }

    const threads = this.storedThreads(viewerId, assistant.id);
    if (threadId !== undefined) {
      return threads.find((candidate) => candidate.id === threadId);
    }

    const latest = [...threads].sort(byRecentActivity)[0];
    if (latest !== undefined) return latest;

    const createdAt = this.now().toISOString();
    return {
      id: this.nextThreadId(threads),
      title: CHAT_DEFAULT_THREAD_TITLE,
      titleSource: 'derived',
      createdAt,
      updatedAt: createdAt,
      messages: [],
    };
  }

  private targetMessages(
    viewerId: ChatViewerId,
    assistant: AssistantConfigurationView,
    target: StoredChatThread | null,
  ): readonly ChatMessageView[] {
    return target === null ? this.ephemeralMessages(viewerId, assistant.id) : target.messages;
  }

  /** 寫入訊息並回傳整段對話；不保存對話的助理只留在記憶體，不碰 storage。 */
  private writeChatMessages(
    viewerId: ChatViewerId,
    assistant: AssistantConfigurationView,
    target: StoredChatThread | null,
    messages: readonly ChatMessageView[],
  ): AssistantChatView {
    if (target === null) {
      this.ephemeralChats.set(this.ephemeralKey(viewerId, assistant.id), messages);
      return this.toChatView(viewerId, assistant, messages);
    }

    const updated: StoredChatThread = {
      ...target,
      title: target.titleSource === 'manual' ? target.title : deriveThreadTitle(messages),
      updatedAt: this.now().toISOString(),
      messages,
    };
    const threads = this.storedThreads(viewerId, assistant.id).filter(
      (candidate) => candidate.id !== updated.id,
    );
    this.saveThreads(viewerId, assistant.id, [...threads, updated]);

    return this.toChatView(viewerId, assistant, messages, updated.id, updated.title);
  }

  /**
   * 匿名統計只計算有訊息的對話段數，不讀取任何對話文字。
   * 未登入訪客的對話只存在他自己的分頁，擁有者的瀏覽器讀不到，所以不會被計入。
   */
  private countChatConversations(assistantId: AssistantId): number {
    return this.seed.accounts.reduce(
      (total, account) =>
        total +
        this.storedThreads(account.id, assistantId).filter(
          (thread) => thread.messages.length > 0,
        ).length,
      0,
    );
  }

  private chatRecords(): readonly DatabaseRecordFixture[] {
    const stored = parseJson(this.storage.getItem(CHAT_RECORDS_KEY));
    return Array.isArray(stored) ? stored.filter(isStoredChatDatabaseRecord) : [];
  }

  /** 由對話提交的紀錄，以提交帳號作為追蹤對象。 */
  private chatSubjects(): readonly TrackedSubjectFixture[] {
    const seen = new Map<string, TrackedSubjectFixture>();
    this.chatRecords().forEach((record) => {
      const key = `${record.databaseId}|${record.subjectId}`;
      if (seen.has(key)) return;
      const account = this.seed.accounts.find((candidate) => `subject-${candidate.id}` === record.subjectId);
      seen.set(key, {
        id: record.subjectId as TrackedSubjectId,
        databaseId: record.databaseId,
        displayName: account?.displayName ?? anonymousSubjectName(record.subjectId),
      });
    });
    return [...seen.values()];
  }

  private toThreadListView(
    viewerAccountId: AccountId,
    assistant: AssistantConfigurationView,
  ): ChatThreadListView {
    const historyMode = this.historyMode(assistant);
    return {
      assistantId: assistant.id,
      assistantName: assistant.name,
      historyMode,
      threads:
        historyMode === 'saved'
          ? [...this.storedThreads(viewerAccountId, assistant.id)]
              .sort(byRecentActivity)
              .map(toThreadSummary)
          : [],
      historyNotice:
        historyMode === 'saved' ? CHAT_HISTORY_SAVED_NOTICE : CHAT_HISTORY_OFF_NOTICE,
    };
  }

  private toChatView(
    viewerId: ChatViewerId,
    assistant: AssistantConfigurationView,
    messages: readonly ChatMessageView[],
    threadId: ChatThreadId | null = null,
    title: string = CHAT_DEFAULT_THREAD_TITLE,
  ): AssistantChatView {
    const profile = this.seed.chatProfiles[assistant.id] ?? DEFAULT_CHAT_PROFILE;
    return {
      assistantId: assistant.id,
      assistantName: assistant.name,
      purpose: assistant.purpose,
      threadId,
      title,
      historyMode: this.historyMode(assistant),
      welcome: profile.welcome,
      privacyNotice: isVisitorId(viewerId) ? CHAT_VISITOR_PRIVACY_NOTICE : CHAT_PRIVACY_NOTICE,
      suggestedPrompts: this.seed.chatResponses
        .filter((fixture) => this.fixtureReply(assistant, fixture) !== null)
        .map((fixture) => ({ id: fixture.id, text: fixture.prompt })),
      messages,
    };
  }

  /** 依關鍵字對應預先準備的回覆；對應不到或來源未連接時回覆查無資料與下一步。 */
  private resolveChatReply(assistant: AssistantConfigurationView, question: string): ChatReplyView {
    const normalized = question.replace(/\s+/g, '');
    for (const fixture of this.seed.chatResponses) {
      const matches = fixture.matchers.some((group) => group.every((keyword) => normalized.includes(keyword)));
      if (!matches) continue;
      const reply = this.fixtureReply(assistant, fixture);
      if (reply !== null) return reply;
    }

    const formAvailable = this.seed.chatResponses.some(
      (fixture) => fixture.answer.kind === 'form-request' && this.fixtureReply(assistant, fixture) !== null,
    );
    return {
      kind: 'no-result',
      text: CHAT_NO_RESULT_TEXT,
      nextSteps: [
        '換個說法再問一次，或點選建議問題。',
        ...(formAvailable ? ['需要專人協助時，輸入「回報訂單問題」留下資料，客服會回覆你。'] : []),
        '急件請直接聯絡門市客服（週一至週五 09:00–18:00）。',
      ],
    };
  }

  private fixtureReply(
    assistant: AssistantConfigurationView,
    fixture: ChatResponseFixture,
  ): ChatReplyView | null {
    const answer = fixture.answer;
    switch (answer.kind) {
      case 'company-data': {
        const citations = answer.citations
          .filter((citation) => assistant.knowledgeBaseIds.includes(citation.knowledgeBaseId))
          .map((citation, index) => ({
            id: `citation-${fixture.id}-${index + 1}` as const,
            knowledgeBaseName:
              this.seed.knowledgeBases.find((knowledgeBase) => knowledgeBase.id === citation.knowledgeBaseId)
                ?.name ?? '已連接的知識庫',
            documentName: citation.documentName,
            excerpt: citation.excerpt,
            updatedLabel: citation.updatedLabel,
          }));
        return citations.length > 0 ? { kind: 'company-data', text: answer.text, citations } : null;
      }
      case 'general-knowledge': {
        return this.allowsGeneralKnowledge(assistant)
          ? { kind: 'general-knowledge', text: answer.text, notice: CHAT_GENERAL_KNOWLEDGE_NOTICE }
          : null;
      }
      case 'form-request': {
        const form = this.chatForm(assistant, answer.databaseId);
        return form === null ? null : { kind: 'form-request', text: answer.text, form };
      }
    }
  }

  /** 助理有連接該資料庫時才提供表單；接收者與可查看者皆取自資料庫設定。 */
  private chatForm(assistant: AssistantConfigurationView, databaseId: DatabaseId): ChatFormView | null {
    if (!assistant.databaseIds.includes(databaseId)) return null;
    const database = this.databases().find((candidate) => candidate.id === databaseId);
    if (database === undefined) return null;

    const collection = this.databaseCollection(database.id);
    const nameOf = (id: AccountId) =>
      this.seed.accounts.find((account) => account.id === id)?.displayName ?? '已停用的帳號';

    return {
      id: database.id,
      title: database.name,
      fields: collection.fields,
      consent: {
        recipient: `${nameOf(database.ownerAccountId)}（${database.name}）`,
        purpose: collection.purpose,
        viewers: collection.dataManagerAccountIds.map(nameOf),
        sensitiveNotice: CHAT_SENSITIVE_NOTICE,
        withdrawalNotice: CHAT_WITHDRAWAL_NOTICE,
      },
    };
  }

  private chatFormTarget(
    viewerId: ChatViewerId,
    assistantId: string,
    formId: DatabaseId,
  ): ChatFormTarget | undefined {
    const assistant = this.chatAssistant(viewerId, assistantId);
    if (assistant === undefined) return undefined;
    const offered = this.seed.chatResponses.some(
      (fixture) => fixture.answer.kind === 'form-request' && fixture.answer.databaseId === formId,
    );
    const form = offered ? this.chatForm(assistant, formId) : null;
    return form === null ? undefined : { assistant, form };
  }

  private databases(): readonly DatabaseView[] {
    return [...this.seed.databases, ...this.createdDatabases().map((entry) => entry.view)];
  }

  private createdDatabases(): readonly StoredCreatedDatabase[] {
    const stored = parseJson(this.storage.getItem(CREATED_DATABASES_KEY));
    return Array.isArray(stored) ? stored.filter(isStoredCreatedDatabase) : [];
  }

  private nextCreatedDatabaseId(): CreatedDatabaseId {
    const existing = new Set<string>(this.createdDatabases().map((entry) => entry.view.id));
    let sequence = this.now().getTime();
    while (existing.has(`database-created-${sequence}`)) sequence += 1;

    return `database-created-${sequence}`;
  }

  private ownedDatabase(viewerAccountId: AccountId, databaseId: string): DatabaseView | undefined {
    return this.databases().find(
      (database) => database.id === databaseId && database.ownerAccountId === viewerAccountId,
    );
  }

  /** 收集設定；使用者儲存過的欄位會覆蓋模板或 fixture 的欄位。 */
  private databaseCollection(databaseId: DatabaseId): DatabaseCollectionFixture {
    const base =
      (this.seed.databaseCollections as Partial<Record<DatabaseId, DatabaseCollectionFixture>>)[databaseId] ??
      this.createdDatabases().find((entry) => entry.view.id === databaseId)?.collection ?? {
        purpose: '',
        templateName: '空白模板',
        dataManagerAccountIds: [],
        fields: [],
      };
    const stored = this.storedDatabaseFields(databaseId);
    return stored === null ? base : { ...base, fields: stored.fields };
  }

  private storedDatabaseFields(databaseId: DatabaseId): StoredDatabaseFields | null {
    const stored = parseJson(this.storage.getItem(DATABASE_FIELDS_KEY_PREFIX + databaseId));
    return isStoredDatabaseFields(stored) ? stored : null;
  }

  /** 只取使用者明確同意提交的紀錄，依時間先後排列。 */
  private consentedRecords(databaseId: DatabaseId): readonly DatabaseRecordFixture[] {
    return [...this.seed.databaseRecords, ...this.chatRecords()]
      .filter((record) => record.databaseId === databaseId && record.consentStatus === 'consented')
      .slice()
      .sort((a, b) => a.recordedAt.localeCompare(b.recordedAt));
  }

  private toDatabaseSummary(database: DatabaseView, viewerAccountId: AccountId): DatabaseSummaryView {
    const collection = this.databaseCollection(database.id);
    const isDataManager = collection.dataManagerAccountIds.includes(viewerAccountId);
    const records = this.consentedRecords(database.id);
    const savedAt = this.storedDatabaseFields(database.id)?.savedAt ?? '';

    return {
      id: database.id,
      name: database.name,
      purpose: collection.purpose,
      templateName: collection.templateName,
      fieldCount: collection.fields.length,
      recordCount: isDataManager ? records.length : null,
      subjectCount: isDataManager ? new Set(records.map((record) => record.subjectId)).size : null,
      connectedAssistantNames: this.assistants()
        .filter(
          (assistant) =>
            assistant.ownerAccountId === viewerAccountId &&
            assistant.databaseIds.includes(database.id),
        )
        .map((assistant) => assistant.name),
      updatedAt: savedAt > database.lastSyncedAt ? savedAt : database.lastSyncedAt,
    };
  }

  private canManageDataSources(viewerAccountId: AccountId): boolean {
    return this.seed.accounts.some(
      (account) =>
        account.id === viewerAccountId &&
        account.permissions.includes('manage-data-sources'),
    );
  }

  private createDatabasePermissionDenied(): PermissionDeniedRepositoryView {
    return this.permissionDenied('database', '只有可管理資料來源的帳號可以建立資料庫。');
  }

  /** 不存在與無權限回傳相同結果，避免透過差異推測資源是否存在。 */
  private databasePermissionDenied(): PermissionDeniedRepositoryView {
    return this.permissionDenied('database', '你沒有這個資料庫的存取權限，或它已不存在。');
  }

  private ownedKnowledgeBase(
    viewerAccountId: AccountId,
    knowledgeBaseId: string,
  ): KnowledgeBaseView | undefined {
    return this.seed.knowledgeBases.find(
      (knowledgeBase) =>
        knowledgeBase.id === knowledgeBaseId &&
        knowledgeBase.ownerAccountId === viewerAccountId,
    );
  }

  private knowledgeRecord(knowledgeBaseId: KnowledgeBaseId): StoredKnowledgeRecord {
    const stored = parseJson(this.storage.getItem(KNOWLEDGE_KEY_PREFIX + knowledgeBaseId));
    if (isStoredKnowledgeRecord(stored)) return stored;

    return {
      version: 1,
      documents: this.seed.knowledgeDocuments[knowledgeBaseId],
      sharing: this.seed.knowledgeSharing[knowledgeBaseId],
    };
  }

  private saveKnowledgeRecord(
    knowledgeBaseId: KnowledgeBaseId,
    record: StoredKnowledgeRecord,
  ): void {
    this.storage.setItem(KNOWLEDGE_KEY_PREFIX + knowledgeBaseId, JSON.stringify(record));
  }

  private updateKnowledgeDocument(
    viewerAccountId: AccountId,
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
    change: (document: KnowledgeDocumentView) => KnowledgeDocumentView,
  ): RepositoryView<KnowledgeDocumentView> {
    const knowledgeBase = this.ownedKnowledgeBase(viewerAccountId, knowledgeBaseId);
    if (knowledgeBase === undefined) return this.knowledgePermissionDenied();

    const record = this.knowledgeRecord(knowledgeBase.id);
    const current = record.documents.find((document) => document.id === documentId);
    if (current === undefined) return this.knowledgePermissionDenied();

    const changed = change(current);
    const updated =
      changed === current ? current : { ...changed, updatedAt: this.now().toISOString() };
    if (updated !== current) {
      this.saveKnowledgeRecord(knowledgeBase.id, {
        ...record,
        documents: record.documents.map((document) =>
          document.id === documentId ? updated : document,
        ),
      });
    }

    return this.applyScenario(updated);
  }

  private toKnowledgeSummary(
    knowledgeBase: KnowledgeBaseView,
    viewerAccountId: AccountId,
  ): KnowledgeBaseSummaryView {
    const { documents, sharing } = this.knowledgeRecord(knowledgeBase.id);
    const updatedAt = documents.reduce(
      (latest, document) => (document.updatedAt > latest ? document.updatedAt : latest),
      knowledgeBase.lastSyncedAt,
    );

    return {
      id: knowledgeBase.id,
      name: knowledgeBase.name,
      purpose: knowledgeBase.purpose,
      documentCount: documents.filter((document) => document.kind === 'document').length,
      faqCount: documents.filter((document) => document.kind === 'faq').length,
      statusCounts: countStatuses(documents),
      sharingScope: sharing.scope,
      connectedAssistantNames: this.assistants()
        .filter(
          (assistant) =>
            assistant.ownerAccountId === viewerAccountId &&
            assistant.knowledgeBaseIds.includes(knowledgeBase.id),
        )
        .map((assistant) => assistant.name),
      updatedAt,
    };
  }

  /** 不存在與無權限回傳相同結果，避免透過差異推測資源是否存在。 */
  private knowledgePermissionDenied(): PermissionDeniedRepositoryView {
    return this.permissionDenied(
      'knowledge-base',
      '你沒有這個知識庫的存取權限，或它已不存在。',
    );
  }

  /** 只有可管理助理的擁有者能編輯；不存在與無權限都回傳 undefined。 */
  private settingsTarget(
    viewerAccountId: AccountId,
    assistantId: string,
  ): AssistantConfigurationView | undefined {
    return this.canManageAssistants(viewerAccountId)
      ? this.ownedAssistant(viewerAccountId, assistantId)
      : undefined;
  }

  /** 不存在與無權限回傳相同結果，也不含助理名稱。 */
  private assistantSettingsPermissionDenied(): PermissionDeniedRepositoryView {
    return this.permissionDenied(
      'assistant-configuration',
      '你沒有這個助理的設定權限，或它已不存在。',
    );
  }

  private storedAssistantSettings(assistantId: AssistantId): StoredAssistantSettings | null {
    return normalizeStoredAssistantSettings(
      parseJson(this.storage.getItem(ASSISTANT_SETTINGS_KEY_PREFIX + assistantId)),
    );
  }

  /** 還沒編輯過時的預設規則：語氣與對話行為都對齊助理目前的實際表現。 */
  private defaultRules(assistant: AssistantConfigurationView): AssistantAnswerRules {
    const profile = this.seed.chatProfiles[assistant.id] ?? DEFAULT_CHAT_PROFILE;
    return {
      ...createEmptyAssistantDraft().rules,
      knowledgeScope: profile.allowGeneralKnowledge
        ? 'allow-general-knowledge'
        : 'company-data-only',
      keepOwnConversations: assistant.keepOwnConversations !== false,
    };
  }

  private assistantSettings(
    assistant: AssistantConfigurationView,
  ): AssistantSettingsView {
    const stored = this.storedAssistantSettings(assistant.id);
    const empty = createEmptyAssistantDraft();

    return {
      configuration: assistant,
      sources: [
        ...assistant.knowledgeBaseIds.map((id): AssistantSourceReference => ({
          id,
          type: 'knowledge-base',
        })),
        ...assistant.databaseIds.map((id): AssistantSourceReference => ({
          id,
          type: 'database',
        })),
      ],
      tone: stored?.tone ?? empty.tone,
      roleInstructions: stored?.roleInstructions ?? '',
      rules: stored?.rules ?? this.defaultRules(assistant),
      savedAt: stored?.savedAt ?? null,
    };
  }

  /** 驗證不通過時完全不寫入：已上線的助理不會因為一次輸入就少掉必要設定。 */
  private commitAssistantSettings(
    next: AssistantSettingsView,
  ): UpdateAssistantSettingsResult {
    const errors = validateAssistantSettings(next);
    if (errors.length > 0) {
      return immutableCopy({
        status: 'validation-failed',
        errors,
        message: errors[0].message,
      });
    }

    this.saveAssistantSettings(next);
    const saved = this.assistants().find(
      (assistant) => assistant.id === next.configuration.id,
    );

    return this.applyScenario(
      saved === undefined ? next : this.assistantSettings(saved),
    );
  }

  private saveAssistantSettings(next: AssistantSettingsView): void {
    const record: StoredAssistantSettings = {
      version: 1,
      savedAt: this.now().toISOString(),
      name: next.configuration.name,
      purpose: next.configuration.purpose,
      audience: next.configuration.audience,
      knowledgeBaseIds: next.sources.flatMap((source) =>
        source.type === 'knowledge-base' ? [source.id] : [],
      ),
      databaseIds: next.sources.flatMap((source) =>
        source.type === 'database' ? [source.id] : [],
      ),
      tone: next.tone,
      roleInstructions: next.roleInstructions,
      rules: next.rules,
    };

    this.storage.setItem(
      ASSISTANT_SETTINGS_KEY_PREFIX + next.configuration.id,
      JSON.stringify(record),
    );
  }

  /** 把建立後編輯過的設定疊回助理上，讓清單、對話與發布都看到同一份內容。 */
  private withSavedSettings(
    assistant: AssistantConfigurationView,
  ): AssistantConfigurationView {
    const stored = this.storedAssistantSettings(assistant.id);
    if (stored === null) return assistant;

    return {
      ...assistant,
      name: stored.name,
      purpose: stored.purpose,
      audience: stored.audience,
      knowledgeBaseIds: stored.knowledgeBaseIds,
      databaseIds: stored.databaseIds,
      keepOwnConversations: stored.rules.keepOwnConversations,
    };
  }

  /** 設定過「只依據我的資料回答」時以設定為準，否則沿用 fixture 的助理個性。 */
  private allowsGeneralKnowledge(assistant: AssistantConfigurationView): boolean {
    const stored = this.storedAssistantSettings(assistant.id);
    return stored === null
      ? (this.seed.chatProfiles[assistant.id] ?? DEFAULT_CHAT_PROFILE).allowGeneralKnowledge
      : stored.rules.knowledgeScope === 'allow-general-knowledge';
  }

  private assistants(): readonly AssistantConfigurationView[] {
    return [...this.seed.assistants, ...this.createdAssistants()].map((assistant) =>
      this.withSavedSettings(assistant),
    );
  }

  private createdAssistants(): readonly AssistantConfigurationView[] {
    const stored = parseJson(this.storage.getItem(CREATED_ASSISTANTS_KEY));
    if (!Array.isArray(stored)) return [];

    return stored
      .filter(
        (entry): entry is AssistantConfigurationView =>
          isRecord(entry) &&
          typeof entry['id'] === 'string' &&
          entry['id'].startsWith('assistant-created-') &&
          typeof entry['ownerAccountId'] === 'string' &&
          Array.isArray(entry['knowledgeBaseIds']) &&
          Array.isArray(entry['databaseIds']) &&
          Array.isArray(entry['sharedWithAccountIds']),
      )
      // 這個欄位是後來才加的：舊資料沒有時視為「保存對話」。
      .map((entry) => ({
        ...entry,
        keepOwnConversations: entry.keepOwnConversations !== false,
      }));
  }

  private nextCreatedAssistantId(): CreatedAssistantId {
    const existing = new Set<string>(
      this.createdAssistants().map((assistant) => assistant.id),
    );
    let sequence = this.now().getTime();
    while (existing.has(`assistant-created-${sequence}`)) sequence += 1;

    return `assistant-created-${sequence}`;
  }

  private connectableSources(
    viewerAccountId: AccountId,
  ): readonly ConnectableSourceView[] {
    if (!this.canManageAssistants(viewerAccountId)) return [];

    const knowledgeBases = this.seed.knowledgeBases
      .filter((knowledgeBase) => knowledgeBase.ownerAccountId === viewerAccountId)
      .map((knowledgeBase): ConnectableSourceView => {
        const summary = this.toKnowledgeSummary(knowledgeBase, viewerAccountId);
        return {
          id: knowledgeBase.id,
          type: 'knowledge-base',
          name: knowledgeBase.name,
          summary: `${summary.documentCount} 份文件、${summary.faqCount} 則 FAQ`,
          permission: 'owner',
          status: connectableKnowledgeStatus(summary.statusCounts),
          updatedAt: knowledgeBase.lastSyncedAt,
        };
      });
    const databases = this.databases()
      .filter((database) => database.ownerAccountId === viewerAccountId)
      .map(
        (database): ConnectableSourceView => ({
          id: database.id,
          type: 'database',
          name: database.name,
          summary: `${database.tableCount} 個資料表`,
          permission: 'read-only',
          status: DATABASE_STATUS[database.status],
          updatedAt: database.lastSyncedAt,
        }),
      );

    return [...knowledgeBases, ...databases];
  }

  private canManageAssistants(viewerAccountId: AccountId): boolean {
    return this.seed.accounts.some(
      (account) =>
        account.id === viewerAccountId &&
        account.permissions.includes('manage-assistants'),
    );
  }

  private draftPermissionDenied(): PermissionDeniedRepositoryView {
    return this.permissionDenied(
      'assistant-draft',
      '只有可管理助理的帳號可以建立助理。',
    );
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
        assistant.audience !== 'account-members')
    );
  }

  /** 只有擁有者可設定發布；不存在與無權限都回傳 undefined，呼叫端回覆相同訊息。 */
  private ownedAssistant(
    viewerAccountId: AccountId,
    assistantId: string,
  ): AssistantConfigurationView | undefined {
    return this.assistants().find(
      (assistant) => assistant.id === assistantId && assistant.ownerAccountId === viewerAccountId,
    );
  }

  private publishingPermissionDenied(): PermissionDeniedRepositoryView {
    return this.permissionDenied('publishing', '你沒有這個助理的發布設定權限，或它已不存在。');
  }

  private channelOverview(viewerAccountId: AccountId): readonly AssistantChannelsView[] {
    return this.assistants()
      .filter((assistant) => assistant.ownerAccountId === viewerAccountId)
      .map((assistant) => {
        const view = this.toPublishingView(assistant);
        return {
          assistantId: assistant.id,
          assistantName: assistant.name,
          channels: PUBLISHING_CHANNEL_TYPES.map((type) => view[type].channel),
        };
      });
  }

  private publishingRecord(assistant: AssistantConfigurationView): PublishingRecord {
    const stored = parseJson(this.storage.getItem(PUBLISHING_KEY_PREFIX + assistant.id));
    if (isPublishingRecord(stored)) return stored;

    return (
      this.seed.publishingRecords[assistant.id] ??
      defaultPublishingRecord(assistant, this.now().toISOString())
    );
  }

  private savePublishingRecord(assistantId: AssistantId, record: PublishingRecord): void {
    this.storage.setItem(PUBLISHING_KEY_PREFIX + assistantId, JSON.stringify(record));
  }

  private toPublishingView(assistant: AssistantConfigurationView): AssistantPublishingView {
    return toAssistantPublishingView(
      assistant,
      this.publishingRecord(assistant),
      this.seed.accounts,
      this.scenario === 'disconnected-channel',
    );
  }
}
