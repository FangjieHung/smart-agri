import type { AccountId } from '../domain/account.model';
import {
  ASSISTANT_WIZARD_STEPS,
  createEmptyAssistantDraft,
  validateAssistantDraft,
  type AssistantDraft,
  type ConnectableSourceStatus,
  type ConnectableSourceView,
  type SavedAssistantDraftView,
  type TrialAnswerRequest,
  type TrialAnswerView,
} from '../domain/assistant-draft.model';
import type {
  AssistantConfigurationView,
  CreatedAssistantId,
  AssistantId,
  AssistantSourceReference,
  AssistantSummaryView,
} from '../domain/assistant.model';
import type {
  AuthorizedFormInput,
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
  DatabaseView,
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
import type { PublishingChannelView } from '../domain/publishing.model';
import {
  compareRecords,
  evaluateTrial,
  normalizeField,
  toRecordView,
  validateFields,
} from './database-tracking';
import { DEMO_SEED, type DemoSeed } from './demo-seed';
import type { DatabaseCollectionFixture, DatabaseRecordFixture } from './demo-seed-databases';
import { createMemoryStorage } from './memory-storage';
import type {
  CreateAssistantResult,
  CreateDatabaseResult,
  DemoKeyValueStorage,
  DemoRepository,
  DemoScenario,
  PermissionDeniedRepositoryView,
  RepositoryPermissionDeniedReason,
  RepositoryView,
  PreviewDatabaseEntryResult,
  UpdateDatabaseFieldsResult,
  UpdateKnowledgeSharingResult,
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
  private readonly now: () => Date;

  constructor(
    private readonly seed: DemoSeed = DEMO_SEED,
    options: MockDemoRepositoryOptions = {},
  ) {
    this.storage = options.storage ?? createMemoryStorage();
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
    };

    this.storage.setItem(
      CREATED_ASSISTANTS_KEY,
      JSON.stringify([...this.createdAssistants(), configuration]),
    );
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
      subjects: this.seed.trackedSubjects
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
    return this.seed.databaseRecords
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

  private assistants(): readonly AssistantConfigurationView[] {
    return [...this.seed.assistants, ...this.createdAssistants()];
  }

  private createdAssistants(): readonly AssistantConfigurationView[] {
    const stored = parseJson(this.storage.getItem(CREATED_ASSISTANTS_KEY));
    if (!Array.isArray(stored)) return [];

    return stored.filter(
      (entry): entry is AssistantConfigurationView =>
        isRecord(entry) &&
        typeof entry['id'] === 'string' &&
        entry['id'].startsWith('assistant-created-') &&
        typeof entry['ownerAccountId'] === 'string' &&
        Array.isArray(entry['knowledgeBaseIds']) &&
        Array.isArray(entry['databaseIds']) &&
        Array.isArray(entry['sharedWithAccountIds']),
    );
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
