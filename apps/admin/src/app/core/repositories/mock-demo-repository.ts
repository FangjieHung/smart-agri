import { defer, of, type Observable } from 'rxjs';
import {
  isVisitorId,
  type AccountId,
  type AccountPermission,
  type AccountRole,
  type AccountView,
  type ChatViewerId,
} from '../domain/account.model';
import {
  ASSISTANT_WIZARD_STEPS,
  createEmptyAssistantDraft,
  validateAssistantDraft,
  type AssistantAnswerRules,
  type AssistantDraft,
  type AssistantTone,
  type ConnectableSourceStatus,
  type ConnectableSourceView,
  type NamedAssistantDraftView,
  type SavedAssistantDraftView,
  type TrialAnswerRequest,
  type TrialAnswerView,
  type TrialAnswerResultView,
} from '../domain/assistant-draft.model';
import type { AssistantAcceptanceStatus, AssistantTestCaseView, AssistantTestRunView, AssistantTestRunDetailView, AssistantTestResultView, AssistantTestCaseInput, AssistantTestCasePatch, AssistantTestCaseImportEntry, AssistantTestCaseExportEntry } from '../domain/assistant-acceptance.model';
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
  RecentConversationView,
  StructuredSubmissionView,
  SubmissionWithdrawalView,
} from '../domain/conversation.model';
import type { AssistantAnalyticsSummaryView, OperationsSummaryView } from '../domain/operations.model';
import type {
  CreatedDatabaseId,
  DatabaseAccessView,
  DatabaseDetailView,
  DatabaseFieldView,
  DatabaseId,
  DatabaseSummaryView,
  DatabaseTrackingView,
  DatabaseTrialAnswers,
  DatabaseView,
  PeriodicReportView,
  TrackedSubjectId,
  TrackedSubjectView,
} from '../domain/database.model';
import {
  isRetryableKnowledgeDocument,
  isUsableKnowledgeDocument,
  precheckKnowledgeUpload,
  KNOWLEDGE_BASE_NAME_MAX_LENGTH,
  KNOWLEDGE_BASE_PURPOSE_MAX_LENGTH,
  KNOWLEDGE_DISABLE_REASON_MAX_LENGTH,
  KNOWLEDGE_DISABLE_REASON_REQUIRED_MESSAGE,
  KNOWLEDGE_DOCUMENT_STATUSES,
  KNOWLEDGE_RETRIEVAL_DEFAULT_THRESHOLD,
  KNOWLEDGE_RETRIEVAL_DEFAULT_TOP,
  KNOWLEDGE_RETRIEVAL_QUESTION_MAX_LENGTH,
  KNOWLEDGE_RETRIEVAL_QUESTION_REQUIRED_MESSAGE,
  KNOWLEDGE_RETRIEVAL_QUESTION_TOO_LONG_MESSAGE,
  type CreateKnowledgeBaseInput,
  type KnowledgeAccountRefView,
  type KnowledgeActivityAction,
  type KnowledgeActivityView,
  type KnowledgeBaseDetailView,
  type KnowledgeBaseId,
  type KnowledgeBaseSummaryView,
  type KnowledgeBaseView,
  type KnowledgeChunkView,
  type KnowledgeConnectedAssistantView,
  type KnowledgeDocumentDetailView,
  type KnowledgeDocumentId,
  type KnowledgeDocumentStatus,
  type KnowledgeDocumentStatusCounts,
  type KnowledgeDocumentView,
  type KnowledgeExtractedUnitView,
  type KnowledgeRetrievalPassageView,
  type KnowledgeRetrievalPreviewView,
  type KnowledgeSharingScope,
  type KnowledgeSharingView,
  type KnowledgeUnitIssue,
  type KnowledgeUnitLocationKind,
  type KnowledgeVersionPreviewView,
  type KnowledgeVersionState,
  type KnowledgeVersionView,
} from '../domain/knowledge-base.model';
import {
  PUBLISHING_CHANNEL_TYPES,
  type AssistantChannelsView,
  type AssistantPublishingView,
  type ConfigurableAssistantPublishingView,
  type LineSettingsInput,
  type PublishingChannelView,
  type PublishingChannelType,
  type WebsiteEmbedSettings,
} from '../domain/publishing.model';
import {
  ACCOUNT_PERMISSIONS,
  ACCOUNT_ROLE_DESCRIPTIONS,
  ACCOUNT_ROLE_LABELS,
  isAccountPermission,
  lockedPermissionsFor,
  normalizeMemberPermissions,
  validateMemberPermissions,
  type TeamMemberView,
  type TeamView,
} from '../domain/team.model';
import {
  canReadConsentedRecords,
  databaseAccessCandidates,
  normalizeDataManagers,
  DATABASE_RECORDS_DENIED_MESSAGE,
} from './database-access';
import {
  buildPeriodicReport,
  compareRecords,
  evaluateTrial,
  normalizeField,
  toRecordValues,
  toRecordView,
  toWithdrawnRecordView,
  validateFields,
} from './database-tracking';
import {
  ANONYMOUS_VISITOR_SUBJECT_NAME,
  CHAT_CITATIONS_OFF_NOTICE,
  CHAT_DEFAULT_THREAD_TITLE,
  CHAT_GENERAL_KNOWLEDGE_NOTICE,
  CHAT_HISTORY_OFF_NOTICE,
  CHAT_HISTORY_SAVED_NOTICE,
  CHAT_PRIVACY_NOTICE,
  CHAT_SENSITIVE_NOTICE,
  CHAT_THREAD_TITLE_MAX_LENGTH,
  CHAT_VISITOR_PRIVACY_NOTICE,
  CHAT_VISITOR_WITHDRAWAL_NOTICE,
  CHAT_WITHDRAWAL_NOTICE,
  CHAT_WITHDRAWAL_UNAVAILABLE_NOTICE,
  CHAT_WITHDRAWN_NOTICE,
  DEFAULT_CHAT_PROFILE,
  type ChatResponseFixture,
} from './demo-seed-chat';
import { DEMO_SEED, type DemoSeed, type KnowledgeDocumentFixture } from './demo-seed';
import type {
  DatabaseCollectionFixture,
  DatabaseRecordFixture,
  TrackedSubjectFixture,
} from './demo-seed-databases';
import { createMemoryStorage } from './memory-storage';
import type { PublishingRecord } from './demo-seed-publishing';
import {
  canActivateLine,
  canManagePublishing,
  canOpenInPlatform,
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
  ApproveKnowledgeVersionsResult,
  CreateAssistantResult,
  CreateDatabaseResult,
  DeleteAssistantResult,
  CreateKnowledgeBaseResult,
  CreateMemberInput,
  CreateMemberResult,
  DeleteKnowledgeResult,
  DemoKeyValueStorage,
  DemoRepository,
  DemoScenario,
  DisableKnowledgeDocumentResult,
  EnableKnowledgeDocumentResult,
  PermissionDeniedRepositoryView,
  PreviewKnowledgeRetrievalResult,
  PreviewTrialAnswerResult,
  RepositoryPermissionDeniedReason,
  RepositoryView,
  PreviewDatabaseEntryResult,
  RenameChatThreadResult,
  SaveAssistantDraftResult,
  RetryKnowledgeDocumentResult,
  ReviewChatFormResult,
  SendChatMessageResult,
  SubmitChatFormResult,
  UpdateDatabaseFieldsResult,
  UpdateAssistantSettingsResult,
  UpdateDatabaseAccessResult,
  UpdateKnowledgeChunkExclusionResult,
  UpdateKnowledgeSharingResult,
  UpdateMemberPermissionsResult,
  UpdatePlatformSharingResult,
  UpdateWebsiteEmbedResult,
  UploadKnowledgeDocumentEvent,
  UploadKnowledgeDocumentResult,
  WithdrawChatSubmissionResult,
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
  /**
   * 非同步契約的方法（目前是團隊的兩個）不再接收 viewer，改從這裡讀目前的 Demo 身分；
   * 正式注入時接到 `DemoSessionService.activeAccountId`。未提供時視為沒有登入。
   */
  readonly viewer?: () => AccountId | null;
  /**
   * 對話的非同步契約（`getAssistantChat`，issue #79）不再接收 viewer，改從這裡讀目前的
   * 發起者：已選擇的 Demo 身分優先，其次是這個瀏覽器分頁的匿名訪客。未提供時退回
   * `viewer`（只支援 Demo 身分，不支援訪客）。
   */
  readonly chatViewer?: () => ChatViewerId | null;
  /**
   * API 模式：以 API 取得的權限取代 seed 與本機的團隊設定，讓仍在 mock 的功能區
   * （發布、收集紀錄等）套用真實權限。回傳值以 Demo 身分 id 為鍵，只覆寫**目前登入者
   * 自己**這一筆（`hybrid-demo-repository.ts` 的 `viewerOverride`）；其他組織成員不再
   * 經由角色換算成 Demo id，因為同角色的多人無法一一對應，沒有列出的帳號沿用 seed 的權限。
   * 未提供時（mock 模式）行為與過去相同：seed 疊上 `sme-demo:team-permissions`。
   */
  readonly accountsSource?: () => AccountPermissionOverrides;
}

/** 團隊的 403 訊息；API 的 `ForbiddenReason.Team` 使用同一句話。 */
export const TEAM_PERMISSION_DENIED_MESSAGE =
  '只有可管理助理與團隊的帳號可以查看或變更團隊成員權限。';

/** 以 Demo 身分 id 為鍵的權限覆寫。 */
export type AccountPermissionOverrides = Readonly<
  Partial<Record<AccountId, readonly AccountPermission[]>>
>;

const DRAFT_KEY_PREFIX = 'sme-demo:assistant-draft:';
const NAMED_DRAFTS_KEY_PREFIX = 'sme-demo:assistant-drafts:';
const CREATED_ASSISTANTS_KEY = 'sme-demo:created-assistants';
const ASSISTANT_ACCEPTANCE_KEY = 'sme-demo:assistant-acceptance';
/** 被刪除的種子助理（種子是唯讀 fixture，只能記下「已刪除」）。 */
const DELETED_ASSISTANTS_KEY = 'sme-demo:deleted-assistants';

/** 與 API 的 `409 draft-revision-conflict` 訊息逐字相同。 */
export const DRAFT_REVISION_CONFLICT_MESSAGE = '這份草稿已在其他分頁被更新過，請重新載入後再修改。';

/** 舊資料沒有 `revision`：視為第一版。 */
function storedRevision(item: Record<string, unknown>): number {
  const revision = item['revision'];
  return typeof revision === 'number' && Number.isInteger(revision) && revision > 0 ? revision : 1;
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

/**
 * 只接受結構正確的草稿內容；欄位缺漏時以預設值補齊，毀損時回傳 null。API 模式的
 * 草稿 `payload`（jsonb，形狀完全由前端決定）也用這個函式還原。
 */
export function normalizeDraftPayload(stored: unknown): AssistantDraft | null {
  if (!isRecord(stored)) return null;

  const empty = createEmptyAssistantDraft();
  const step = ASSISTANT_WIZARD_STEPS.find((candidate) => candidate === stored['currentStep']);
  /**
   * 舊版本（issue #82 之前）以 `testedQuestionIds: TrialQuestionId[]` 記錄「已試問」；
   * 沒有 `hasTrialAnswer` 時從舊欄位推導，讓既有的本機草稿不會因為改版而整份被丟棄。
   */
  const hasTrialAnswer =
    typeof stored['hasTrialAnswer'] === 'boolean'
      ? stored['hasTrialAnswer']
      : Array.isArray(stored['testedQuestionIds'])
        ? stored['testedQuestionIds'].length > 0
        : null;

  if (
    typeof stored['name'] !== 'string' ||
    typeof stored['purpose'] !== 'string' ||
    !Array.isArray(stored['sources']) ||
    hasTrialAnswer === null ||
    !isRecord(stored['rules']) ||
    step === undefined
  ) {
    return null;
  }

  return {
    ...empty,
    ...stored,
    hasTrialAnswer,
    rules: { ...empty.rules, ...stored['rules'] },
    currentStep: step,
  } as AssistantDraft;
}

/** 只接受結構正確的草稿；欄位缺漏時以預設值補齊，毀損時視為沒有草稿。 */
function normalizeStoredDraft(value: unknown): SavedAssistantDraftView | null {
  if (
    !isRecord(value) ||
    value['version'] !== 1 ||
    typeof value['savedAt'] !== 'string'
  ) {
    return null;
  }

  const draft = normalizeDraftPayload(value['draft']);
  return draft === null ? null : { draft, savedAt: value['savedAt'] };
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

/** 團隊權限：整個 Demo 只有一份，key 不分帳號。 */
const TEAM_PERMISSIONS_KEY = 'sme-demo:team-permissions';

interface StoredTeamPermissions {
  readonly version: 1;
  readonly savedAt: string;
  readonly members: Readonly<Partial<Record<AccountId, readonly AccountPermission[]>>>;
}

function normalizeStoredTeamPermissions(value: unknown): StoredTeamPermissions | null {
  if (!isRecord(value) || value['version'] !== 1) return null;
  if (typeof value['savedAt'] !== 'string' || !isRecord(value['members'])) return null;

  const members: Partial<Record<AccountId, readonly AccountPermission[]>> = {};
  for (const [accountId, permissions] of Object.entries(value['members'])) {
    if (!Array.isArray(permissions)) continue;
    // 不認得的權限值直接丟掉，而不是整份作廢：舊資料仍然開得起來。
    members[accountId as AccountId] = normalizeMemberPermissions(
      permissions.filter(isAccountPermission),
    );
  }

  return { version: 1, savedAt: value['savedAt'], members };
}

/**
 * 在這台瀏覽器建立的成員（issue #52，M2 Slice 18）：Demo 的三個身分是 seed 固定的，
 * 新增的成員另外存一份、附加到 `accounts()` 的結果後面，不會覆蓋 seed。
 */
const CREATED_MEMBERS_KEY = 'sme-demo:created-members';

const ACCOUNT_ROLES = Object.keys(ACCOUNT_ROLE_LABELS) as readonly AccountRole[];

function isAccountRole(value: unknown): value is AccountRole {
  return (ACCOUNT_ROLES as readonly unknown[]).includes(value);
}

interface StoredCreatedMember {
  readonly id: AccountId;
  readonly loginName: string;
  readonly displayName: string;
  readonly role: AccountRole;
  readonly permissions: readonly AccountPermission[];
}

function normalizeStoredCreatedMembers(value: unknown): readonly StoredCreatedMember[] {
  if (!Array.isArray(value)) return [];
  const members: StoredCreatedMember[] = [];
  for (const entry of value) {
    if (
      !isRecord(entry) ||
      typeof entry['id'] !== 'string' ||
      typeof entry['loginName'] !== 'string' ||
      typeof entry['displayName'] !== 'string' ||
      !isAccountRole(entry['role']) ||
      !Array.isArray(entry['permissions'])
    ) {
      continue;
    }

    members.push({
      id: entry['id'] as AccountId,
      loginName: entry['loginName'],
      displayName: entry['displayName'],
      role: entry['role'],
      permissions: normalizeMemberPermissions(entry['permissions'].filter(isAccountPermission)),
    });
  }

  return members;
}

/** 新增成員的帳號 id 尾碼；瀏覽器內建的 `crypto.randomUUID()` 已足夠不重複。 */
function cryptoRandomId(): string {
  return crypto.randomUUID();
}

/**
 * Mock 模式的一次性密碼：字首清楚標示這只是示範用，不是真的可登入的密碼
 * （mock 的三個 Demo 身分本來就沒有密碼）。
 */
function mockOneTimePassword(): string {
  return `Demo-${crypto.randomUUID().replace(/-/g, '').slice(0, 12)}`;
}

/** 單一資料庫的資料管理者指定；與表單欄位分開存，兩者互不影響。 */
const DATABASE_ACCESS_KEY_PREFIX = 'sme-demo:database-access:';

interface StoredDatabaseAccess {
  readonly version: 1;
  readonly savedAt: string;
  readonly dataManagerAccountIds: readonly AccountId[];
}

function isStoredDatabaseAccess(value: unknown): value is StoredDatabaseAccess {
  return (
    isRecord(value) &&
    value['version'] === 1 &&
    typeof value['savedAt'] === 'string' &&
    Array.isArray(value['dataManagerAccountIds']) &&
    value['dataManagerAccountIds'].every((id) => typeof id === 'string')
  );
}

const KNOWLEDGE_KEY_PREFIX = 'sme-demo:knowledge:';
/** 在這台瀏覽器建立的知識庫（`KnowledgeBaseView[]`）。 */
const CREATED_KNOWLEDGE_BASES_KEY = 'sme-demo:created-knowledge-bases';
/** 已刪除的知識庫 id；seed 的知識庫無法從 seed 移除，所以另外記下來。 */
const DELETED_KNOWLEDGE_BASES_KEY = 'sme-demo:deleted-knowledge-bases';

/**
 * 保存的文件：畫面的欄位，加上 mock 才有的 `processingStartedAt`（重新處理的時間）。
 * 有這個欄位的文件，狀態依經過時間計算（`mockKnowledgeProcessingState`），不寫回 storage；
 * seed 本來就是等待中或處理中的文件沒有這個欄位，維持示範用的固定狀態。
 */
interface StoredKnowledgeDocument extends KnowledgeDocumentFixture {
  readonly latestVersionId?: string;
  readonly processingStartedAt?: string;
}

interface StoredKnowledgeRecord {
  readonly version: 1;
  readonly documents: readonly StoredKnowledgeDocument[];
  readonly sharing: KnowledgeSharingView;
  /**
   * 上傳時記錄的檔案大小，以文件 id 為鍵（issue #46）：mock 依規則「不讀取檔案內容」，
   * 用大小模擬「內容重複」（`duplicate-content`），只涵蓋這台瀏覽器上傳過的文件；
   * seed 內建的文件沒有記錄大小，不會被拿來比對。
   */
  readonly uploadedSizes?: Readonly<Record<string, number>>;
  /**
   * 版本確認狀態（issue #47，M2 Slice 13），以文件 id 為鍵；沒有紀錄的文件視為
   * `defaultKnowledgeReview()`（seed 文件與尚未被這個功能碰過的文件都是這樣，
   * 維持「已經生效」的示範資料相容行為）。
   */
  readonly reviews?: Readonly<Record<string, StoredKnowledgeReview>>;
}

/** 已封存的版本：`versions`／版本歷程只用來顯示，不影響任何判斷。 */
interface StoredKnowledgePriorVersion {
  readonly versionNumber: number;
  readonly fileName: string;
  readonly uploadedByAccountId?: string;
  readonly uploadedAt: string;
  readonly approvedByAccountId?: string;
  readonly approvedAt?: string;
  readonly effectiveFrom?: string;
}

interface StoredKnowledgeActivity {
  readonly id: string;
  readonly action: KnowledgeActivityAction;
  readonly actorAccountId?: string;
  readonly at: string;
  readonly versionId?: string;
  readonly versionNumber?: number;
  readonly reason?: string;
}

/**
 * 一份文件的版本確認狀態：目前版本（待確認或已確認生效）、封存的歷史版本、
 * 停用狀態與活動紀錄、段落排除設定。與 `StoredKnowledgeDocument` 分開存放，
 * 讓既有的處理狀態時間模型（`processingStartedAt`）維持不變，兩者互不影響。
 */
interface StoredKnowledgeReview {
  readonly versionNumber: number;
  readonly fileName: string;
  readonly reviewState: 'pending-review' | 'approved';
  readonly uploadedByAccountId?: string;
  readonly uploadedAt: string;
  readonly effectiveFrom?: string;
  readonly approvedByAccountId?: string;
  readonly approvedAt?: string;
  readonly priorVersions: readonly StoredKnowledgePriorVersion[];
  readonly disabledAt?: string;
  readonly disabledByAccountId?: string;
  readonly disabledReason?: string;
  /** chunk id（含版本前綴，不會跨版本碰撞）→ 是否排除。 */
  readonly chunkExclusions: Readonly<Record<string, boolean>>;
  /** 由舊到新；讀取時反轉成新到舊。 */
  readonly activities: readonly StoredKnowledgeActivity[];
  /**
   * 最近一次「已生效」的版本（M2 計畫：上傳新版本不會立刻取代目前生效的版本）。
   * 上傳新版本時原封不動帶過來，只有這個版本自己被確認生效時才更新——這樣
   * `versionNumber`／`reviewState` 已經是最新上傳的版本，但畫面上「是否生效」看的是
   * 這兩個欄位，兩者不會互相干擾。
   */
  readonly lastEffectiveVersionNumber?: number;
  readonly lastEffectiveFrom?: string;
}

/** 沒有被這個功能碰過的文件（seed 或舊資料）：視為第 1 版、已在建立當下確認生效。 */
function defaultKnowledgeReview(document: { readonly name: string; readonly updatedAt: string }): StoredKnowledgeReview {
  return {
    versionNumber: 1,
    fileName: document.name,
    reviewState: 'approved',
    uploadedAt: document.updatedAt,
    effectiveFrom: document.updatedAt,
    priorVersions: [],
    chunkExclusions: {},
    activities: [],
    lastEffectiveVersionNumber: 1,
    lastEffectiveFrom: document.updatedAt,
  };
}

interface KnowledgeReviewDerivedFields {
  readonly latestVersionNumber: number;
  readonly latestVersionState: KnowledgeVersionState;
  readonly effectiveVersionNumber: number | null;
  readonly disabled: boolean;
  readonly inEffect: boolean;
}

/**
 * 把版本確認狀態換算成畫面要顯示的欄位。`latestVersionState` 只描述「最新上傳的那個版本」
 * 自己的確認狀態；`effectiveVersionNumber`／`inEffect` 看的是「最近一次已經生效、且生效
 * 日期已到」的版本——上傳新版本、甚至還在待確認，都不影響原本已生效的版本繼續被引用
 * （M2 計畫第 3 節「每個版本都要人工確認生效」）。mock 沒有替每個版本各自保存處理狀態，
 * 所以這裡不看文件目前的處理狀態（那是「最新版本」的狀態，不是「生效版本」的狀態）。
 */
function deriveKnowledgeReviewFields(review: StoredKnowledgeReview, now: Date): KnowledgeReviewDerivedFields {
  const disabled = review.disabledAt !== undefined;
  const approvedAndDue =
    review.reviewState === 'approved' &&
    review.effectiveFrom !== undefined &&
    Date.parse(review.effectiveFrom) <= now.getTime();
  const scheduled =
    review.reviewState === 'approved' &&
    review.effectiveFrom !== undefined &&
    Date.parse(review.effectiveFrom) > now.getTime();
  const latestVersionState: KnowledgeVersionState = scheduled
    ? 'scheduled'
    : approvedAndDue
      ? 'effective'
      : 'pending-review';
  const lastEffectiveDue =
    review.lastEffectiveVersionNumber !== undefined &&
    review.lastEffectiveFrom !== undefined &&
    Date.parse(review.lastEffectiveFrom) <= now.getTime();
  const effectiveVersionNumber = lastEffectiveDue ? (review.lastEffectiveVersionNumber as number) : null;
  return {
    latestVersionNumber: review.versionNumber,
    latestVersionState,
    effectiveVersionNumber,
    disabled,
    inEffect: effectiveVersionNumber !== null && !disabled,
  };
}

/** mock：開始處理後，前 2 秒是「等待處理」。 */
export const MOCK_KNOWLEDGE_QUEUED_MS = 2000;
/** mock：接著 3 秒是「處理中」，之後就是「可使用」（開始後第 5 秒）。 */
export const MOCK_KNOWLEDGE_PROCESSING_MS = 3000;

/**
 * mock 的處理進度：只看「開始處理後經過多久」，不需要任何計時器或推進按鈕，所以詳情頁
 * 重新讀取（輪詢）就能看到進度，與 API 模式的行為相同。
 */
export function mockKnowledgeProcessingState(
  processingStartedAt: string,
  now: Date,
): { readonly status: KnowledgeDocumentStatus; readonly updatedAt: string } {
  const started = Date.parse(processingStartedAt);
  const elapsed = now.getTime() - started;
  if (elapsed < MOCK_KNOWLEDGE_QUEUED_MS) return { status: 'queued', updatedAt: processingStartedAt };
  if (elapsed < MOCK_KNOWLEDGE_QUEUED_MS + MOCK_KNOWLEDGE_PROCESSING_MS) {
    return { status: 'processing', updatedAt: new Date(started + MOCK_KNOWLEDGE_QUEUED_MS).toISOString() };
  }
  return {
    status: 'ready',
    updatedAt: new Date(started + MOCK_KNOWLEDGE_QUEUED_MS + MOCK_KNOWLEDGE_PROCESSING_MS).toISOString(),
  };
}

/** 與 API 的訊息相同（`KnowledgeVersionRules`、`KnowledgeBaseDetailsRules`、`ForbiddenReason`）。 */
const KNOWLEDGE_STILL_PROCESSING_MESSAGE = '這個版本還在等待或處理中，處理完成後才能重試。';
const KNOWLEDGE_NOT_FAILED_MESSAGE = '只有處理失敗的版本可以重試。';
const KNOWLEDGE_NAME_REQUIRED_MESSAGE = '請輸入知識庫名稱。';
const KNOWLEDGE_NAME_TOO_LONG_MESSAGE = `知識庫名稱最多 ${KNOWLEDGE_BASE_NAME_MAX_LENGTH} 個字。`;
const KNOWLEDGE_PURPOSE_TOO_LONG_MESSAGE = `用途說明最多 ${KNOWLEDGE_BASE_PURPOSE_MAX_LENGTH} 個字。`;
export const KNOWLEDGE_CREATE_PERMISSION_DENIED_MESSAGE = '只有可管理資料來源的帳號可以建立知識庫。';
export const KNOWLEDGE_PERMISSION_DENIED_MESSAGE = '你沒有這個知識庫的存取權限，或它已不存在。';

/** 與後端 `KnowledgeReviewRules`（issue #47，M2 Slice 13）相同的訊息。 */
export const KNOWLEDGE_VERSION_NOT_APPROVABLE_MESSAGE =
  '這些版本剛剛有其他變更，或不是待確認的版本，請重新整理後再試一次。';
export const KNOWLEDGE_DOCUMENT_ALREADY_DISABLED_MESSAGE = '這份文件已經停用了。';
export const KNOWLEDGE_DOCUMENT_NOT_DISABLED_MESSAGE = '這份文件目前沒有停用，不需要恢復。';
const KNOWLEDGE_DISABLE_REASON_TOO_LONG_MESSAGE = `停用原因最多 ${KNOWLEDGE_DISABLE_REASON_MAX_LENGTH} 個字。`;
const KNOWLEDGE_EFFECTIVE_DATE_INVALID_MESSAGE = '生效日期格式不正確。';

/** 與後端 `KnowledgeUploadRules.CheckDuplicates`／`CheckNewVersionDuplicate` 訊息格式相同。 */
function duplicateContentMessage(existingDocumentName: string): string {
  return `這份檔案的內容與「${existingDocumentName}」完全相同，不需要重複上傳。`;
}

function duplicateNameMessage(fileName: string): string {
  return `這個知識庫已經有名為「${fileName}」的文件。要更新它的內容，請改用「上傳新版本」。`;
}

function isKnowledgeBaseView(value: unknown): value is KnowledgeBaseView {
  return (
    isRecord(value) &&
    typeof value['id'] === 'string' &&
    typeof value['ownerAccountId'] === 'string' &&
    typeof value['name'] === 'string' &&
    typeof value['purpose'] === 'string' &&
    typeof value['lastSyncedAt'] === 'string'
  );
}

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

/**
 * 匯出給 `HybridDemoRepository`：API 模式的知識庫可連接狀態要與 mock 用同一套規則換算。
 * `contentCount`（文件數 + FAQ 數）是 0 時顯示「尚無內容」（issue #115），不歸類成
 * `ready`（全部可使用），避免讓人誤以為已經有可用內容。
 */
export function connectableKnowledgeStatus(
  counts: KnowledgeDocumentStatusCounts,
  contentCount: number,
): ConnectableSourceStatus {
  if (counts.queued + counts.processing > 0) return 'processing';
  if (counts['partially-readable'] + counts.failed > 0) return 'needs-attention';
  if (contentCount === 0) return 'empty';
  return 'ready';
}

/**
 * mock 檢索試查的分數（issue #48）：問題與段落文字的字元二連字（bigram）重疊比例，
 * 0–1。不是真的語意相似度，只是讓「問題明顯對應到段落」與「完全不相關」分得開，
 * 足以示範「有結果」與「低於門檻」兩種畫面。
 */
function mockKeywordScore(question: string, text: string): number {
  const questionGrams = mockCharBigrams(question);
  const textGrams = mockCharBigrams(text);
  if (questionGrams.size === 0 || textGrams.size === 0) return 0;
  let hits = 0;
  for (const gram of questionGrams) {
    if (textGrams.has(gram)) hits += 1;
  }
  return Math.min(1, hits / questionGrams.size);
}

function mockCharBigrams(value: string): ReadonlySet<string> {
  const normalized = value.replace(/\s+/gu, '');
  const grams = new Set<string>();
  for (let index = 0; index < normalized.length - 1; index += 1) {
    grams.add(normalized.slice(index, index + 2));
  }
  if (grams.size === 0 && normalized.length > 0) grams.add(normalized);
  return grams;
}

/** 與後端 `DatabaseEndpoints.FormChangedMessage` 逐字相同。 */
const DATABASE_FORM_CHANGED_MESSAGE = '這份表單已被更新過，請重新載入後再修改。你這次的修改尚未儲存。';

const CREATED_DATABASES_KEY = 'sme-demo:created-databases';
const DATABASE_FIELDS_KEY_PREFIX = 'sme-demo:database-fields:';

interface StoredCreatedDatabase {
  readonly view: DatabaseView;
  readonly collection: DatabaseCollectionFixture;
}

interface StoredDatabaseFields {
  /** 儲存格式的版本，不是表單版本。 */
  readonly version: 1;
  readonly savedAt: string;
  /** 表單版本：沒存過（或舊格式沒有這個欄位）是 1，每次儲存加 1，與後端的 `formVersion` 同義。 */
  readonly formVersion?: number;
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

/** 與後端 `POST .../trial-answers` 的 422 限制相同（M3 計畫 Slice 8，PR #92）。 */
const MAX_TRIAL_QUESTION_LENGTH = 2000;
/** 示範用的門檻與命中分數；只用來展示 UI，不代表真實的相似度計算。 */
const TRIAL_ANSWER_THRESHOLD = 0.3;
const TRIAL_ANSWER_MATCH_SCORE = 0.82;

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
  protected readonly viewer: () => AccountId | null;
  protected readonly chatViewer: () => ChatViewerId | null;
  private readonly accountsSource: (() => AccountPermissionOverrides) | null;
  /**
   * 助理關閉「保存自己的對話」時，對話只留在這個 repository 實例的記憶體裡：
   * 不寫入 storage、不列在對話紀錄中，重新整理（重新建立實例）就消失。
   */
  private readonly ephemeralChats = new Map<string, readonly ChatMessageView[]>();
  private readonly acceptanceCases = new Map<string, AssistantTestCaseView[]>();
  private readonly acceptanceRuns = new Map<string, AssistantTestRunView[]>();
  private readonly acceptanceResults = new Map<string, AssistantTestResultView[]>();
  private readonly acceptanceStatuses = new Map<string, AssistantAcceptanceStatus>();
  private readonly acceptancePolls = new Map<string, number>();
  private acceptanceOrdinal = 0;

  constructor(
    private readonly seed: DemoSeed = DEMO_SEED,
    options: MockDemoRepositoryOptions = {},
  ) {
    this.storage = options.storage ?? createMemoryStorage();
    this.visitorStorage = options.visitorStorage ?? createMemoryStorage();
    this.now = options.now ?? (() => new Date());
    this.viewer = options.viewer ?? (() => null);
    this.chatViewer = options.chatViewer ?? (() => this.viewer());
    this.accountsSource = options.accountsSource ?? null;
    this.restoreAcceptance();
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
    return this.applyScenario(this.accounts());
  }

  /** 以 `defer` 包住：與 HTTP 一樣是 cold Observable，訂閱時才讀取（也才寫入）。 */
  getTeam(): Observable<RepositoryView<TeamView>> {
    return defer(() => of(this.readTeam(this.viewer())));
  }

  updateMemberPermissions(
    memberAccountId: AccountId,
    permissions: readonly AccountPermission[],
  ): Observable<UpdateMemberPermissionsResult> {
    return defer(() => of(this.writeMemberPermissions(this.viewer(), memberAccountId, permissions)));
  }

  createMember(input: CreateMemberInput): Observable<CreateMemberResult> {
    return defer(() => of(this.writeCreateMember(this.viewer(), input)));
  }

  private readTeam(viewerAccountId: AccountId | null): RepositoryView<TeamView> {
    if (viewerAccountId === null || !this.canManageAssistants(viewerAccountId)) {
      return this.teamPermissionDenied();
    }
    return this.applyScenario(this.teamView(viewerAccountId));
  }

  private writeMemberPermissions(
    viewerAccountId: AccountId | null,
    memberAccountId: AccountId,
    permissions: readonly AccountPermission[],
  ): UpdateMemberPermissionsResult {
    if (viewerAccountId === null || !this.canManageAssistants(viewerAccountId)) {
      return this.teamPermissionDenied();
    }

    const member = this.accounts().find((account) => account.id === memberAccountId);
    // 不存在的成員與沒有權限共用同一句話，避免從差異推測有哪些帳號。
    if (member === undefined) return this.teamPermissionDenied();

    const problem = validateMemberPermissions(member, viewerAccountId, permissions);
    if (problem !== null) {
      return immutableCopy({ status: 'validation-failed', message: problem });
    }

    const stored = this.storedTeamPermissions();
    const record: StoredTeamPermissions = {
      version: 1,
      savedAt: this.now().toISOString(),
      members: {
        ...(stored?.members ?? {}),
        [memberAccountId]: normalizeMemberPermissions(permissions),
      },
    };
    this.storage.setItem(TEAM_PERMISSIONS_KEY, JSON.stringify(record));

    return this.applyScenario(this.teamView(viewerAccountId));
  }

  /**
   * Mock 版本的 issue #52：登入名稱只跟這台瀏覽器已建立的成員比對（seed 的三個 Demo
   * 身分沒有登入名稱可比），一次性密碼是明顯標示為示範用的字串，不是真的密碼。
   */
  private writeCreateMember(viewerAccountId: AccountId | null, input: CreateMemberInput): CreateMemberResult {
    if (viewerAccountId === null || !this.canManageAssistants(viewerAccountId)) {
      return this.teamPermissionDenied();
    }

    const loginName = input.loginName.trim();
    const displayName = input.displayName.trim();
    if (loginName.length === 0 || loginName.length > 64) {
      return { status: 'validation-failed', message: '登入名稱必須是 1 到 64 個字元。' };
    }
    if (displayName.length === 0 || displayName.length > 200) {
      return { status: 'validation-failed', message: '顯示名稱必須是 1 到 200 個字元。' };
    }
    if (!isAccountRole(input.role)) {
      return { status: 'validation-failed', message: '有不認得的角色值，這次沒有新增成員。' };
    }
    if (input.permissions.some((permission) => !isAccountPermission(permission))) {
      return { status: 'validation-failed', message: '有不認得的權限值，這次沒有新增成員。' };
    }

    const normalizedLogin = loginName.toLowerCase();
    const created = this.createdMembers();
    if (created.some((member) => member.loginName.toLowerCase() === normalizedLogin)) {
      return { status: 'validation-failed', message: '這個登入名稱在目前組織已經有人使用，請改用其他名稱。' };
    }

    const member: StoredCreatedMember = {
      id: `account-${cryptoRandomId()}` as AccountId,
      loginName,
      displayName,
      role: input.role,
      permissions: normalizeMemberPermissions(input.permissions),
    };
    this.storage.setItem(CREATED_MEMBERS_KEY, JSON.stringify([...created, member]));

    const view = this.teamView(viewerAccountId);
    const newMember = view.members.find((candidate) => candidate.id === member.id);
    if (newMember === undefined) {
      // 不會發生：member 剛寫入 storage，teamView() 一定看得到它。留著只為了型別安全。
      return this.teamPermissionDenied();
    }

    return this.applyScenario({ member: newMember, oneTimePassword: mockOneTimePassword() });
  }

  private createdMembers(): readonly StoredCreatedMember[] {
    return normalizeStoredCreatedMembers(parseJson(this.storage.getItem(CREATED_MEMBERS_KEY)));
  }

  listAssistantConfigurations(): Observable<RepositoryView<readonly AssistantConfigurationView[]>> {
    return this.signedIn(
      (viewer) => this.listAssistantConfigurationsSync(viewer),
      () => this.applyScenario([]),
    );
  }

  listUsableAssistants(): Observable<RepositoryView<readonly AssistantSummaryView[]>> {
    return this.signedIn(
      (viewer) => this.listUsableAssistantsSync(viewer),
      () => this.applyScenario([]),
    );
  }

  listAssistantTestCases(assistantId: string): Observable<RepositoryView<readonly AssistantTestCaseView[]>> {
    return this.signedIn(viewer => this.isAssistantOwner(viewer, assistantId) ? this.applyScenario(this.acceptanceCases.get(assistantId) ?? []) : this.assistantSettingsPermissionDenied(), () => this.assistantSettingsPermissionDenied());
  }
  createAssistantTestCase(assistantId: string, input: AssistantTestCaseInput): Observable<RepositoryView<AssistantTestCaseView>> {
    return this.signedIn(viewer => {
      if (!this.isAssistantOwner(viewer, assistantId)) return this.assistantSettingsPermissionDenied();
      const now = new Date().toISOString();
      const item: AssistantTestCaseView = { id: `mock-case-${++this.acceptanceOrdinal}`, assistantId, question: input.question ?? '', category: (input.category ?? 'common') as AssistantTestCaseView['category'], expectedKind: (input.expectedKind ?? 'company-data') as AssistantTestCaseView['expectedKind'], expectedDocumentIds: input.expectedDocumentIds ?? [], followUpOfId: input.followUpOfId ?? null, ordinal: this.acceptanceOrdinal, createdAt: now, updatedAt: now };
      this.acceptanceCases.set(assistantId, [...(this.acceptanceCases.get(assistantId) ?? []), item]);
      this.persistAcceptance();
      return this.applyScenario(item);
    }, () => this.assistantSettingsPermissionDenied());
  }
  updateAssistantTestCase(assistantId: string, caseId: string, patch: AssistantTestCasePatch): Observable<RepositoryView<AssistantTestCaseView>> {
    return this.signedIn(viewer => {
      if (!this.isAssistantOwner(viewer, assistantId)) return this.assistantSettingsPermissionDenied();
      const items = this.acceptanceCases.get(assistantId) ?? []; const old = items.find(item => item.id === caseId); if (!old) return this.assistantSettingsPermissionDenied();
      const item = { ...old, ...(patch.question !== undefined ? { question: patch.question ?? '' } : {}), ...(patch.category !== undefined ? { category: (patch.category ?? 'common') as AssistantTestCaseView['category'] } : {}), ...(patch.expectedKind !== undefined ? { expectedKind: (patch.expectedKind ?? 'company-data') as AssistantTestCaseView['expectedKind'] } : {}), ...(patch.expectedDocumentIds !== undefined ? { expectedDocumentIds: patch.expectedDocumentIds ?? [] } : {}), ...(patch.followUpOfId !== undefined ? { followUpOfId: patch.followUpOfId ?? null } : {}), updatedAt: new Date().toISOString() };
      this.acceptanceCases.set(assistantId, items.map(candidate => candidate.id === caseId ? item : candidate));
      this.persistAcceptance();
      return this.applyScenario(item);
    }, () => this.assistantSettingsPermissionDenied());
  }
  deleteAssistantTestCase(assistantId: string, caseId: string): Observable<RepositoryView<null>> {
    return this.signedIn(viewer => { if (!this.isAssistantOwner(viewer, assistantId)) return this.assistantSettingsPermissionDenied(); this.acceptanceCases.set(assistantId, (this.acceptanceCases.get(assistantId) ?? []).filter(item => item.id !== caseId)); this.persistAcceptance(); return this.applyScenario(null); }, () => this.assistantSettingsPermissionDenied());
  }
  importAssistantTestCases(assistantId: string, questions: readonly AssistantTestCaseImportEntry[]): Observable<RepositoryView<readonly AssistantTestCaseView[]>> {
    return this.signedIn(viewer => {
      if (!this.isAssistantOwner(viewer, assistantId)) return this.assistantSettingsPermissionDenied();
      for (const question of questions) this.createMockCase(assistantId, question);
      this.persistAcceptance();
      return this.applyScenario(this.acceptanceCases.get(assistantId) ?? []);
    }, () => this.assistantSettingsPermissionDenied());
  }
  exportAssistantTestCases(assistantId: string): Observable<RepositoryView<readonly AssistantTestCaseExportEntry[]>> {
    return this.signedIn(viewer => { if (!this.isAssistantOwner(viewer, assistantId)) return this.assistantSettingsPermissionDenied(); return this.applyScenario((this.acceptanceCases.get(assistantId) ?? []).map(item => ({ id: item.id, question: item.question, category: item.category, expectedKind: item.expectedKind, expectedCitedDocuments: [...item.expectedDocumentIds], followUpOf: item.followUpOfId }))); }, () => this.assistantSettingsPermissionDenied());
  }
  listAssistantTestRuns(assistantId: string): Observable<RepositoryView<readonly AssistantTestRunView[]>> {
    return this.signedIn(viewer => {
      if (!this.isAssistantOwner(viewer, assistantId)) return this.assistantSettingsPermissionDenied();
      const runs = this.acceptanceRuns.get(assistantId) ?? [];
      const active = runs.find(run => run.status === 'queued' || run.status === 'running');
      if (active) {
        const polls = (this.acceptancePolls.get(active.id) ?? 0) + 1;
        this.acceptancePolls.set(active.id, polls);
        const status = polls === 1 ? 'running' : 'completed';
        const results = this.acceptanceResults.get(active.id) ?? [];
        const updated: AssistantTestRunView = {
          ...active,
          status,
          startedAt: active.startedAt ?? this.now().toISOString(),
          completedAt: status === 'completed' ? this.now().toISOString() : null,
          passedCount: status === 'completed' ? results.filter(result => result.passed).length : 0,
          failedCount: status === 'completed' ? results.filter(result => !result.passed).length : 0,
        };
        this.acceptanceRuns.set(assistantId, runs.map(run => run.id === active.id ? updated : run));
        if (status === 'completed') {
          this.acceptanceStatuses.set(assistantId, updated.failedCount > 0 ? 'failed' : 'passed');
        }
        this.persistAcceptance();
      }
      return this.applyScenario(this.acceptanceRuns.get(assistantId) ?? []);
    }, () => this.assistantSettingsPermissionDenied());
  }
  createAssistantTestRun(assistantId: string): Observable<RepositoryView<AssistantTestRunView>> {
    return this.signedIn(viewer => {
      if (!this.isAssistantOwner(viewer, assistantId)) return this.assistantSettingsPermissionDenied();
      const cases = this.acceptanceCases.get(assistantId) ?? [];
      const now = this.now().toISOString();
      const run: AssistantTestRunView = {
        id: `mock-run-${++this.acceptanceOrdinal}`, assistantId, trigger: 'manual', status: 'queued',
        rerunRequested: false, queuedAt: now, startedAt: null, completedAt: null,
        passedCount: 0, failedCount: 0, promptVersion: 'demo', model: 'mock', minScore: 0.3,
      };
      const results: AssistantTestResultView[] = cases.map((item, index) => {
        const failed = item.category === 'exception';
        return {
          id: `${run.id}-result-${index}`, testCaseId: item.id, ordinal: item.ordinal,
          question: item.question, expectedKind: item.expectedKind,
          expectedDocumentIds: [...item.expectedDocumentIds],
          actualKind: failed ? 'no-result' : item.expectedKind,
          answerText: failed ? '示範回答未符合預期。' : item.expectedKind === 'no-result' ? '找不到足夠資料，無法回答。' : '這是示範重跑結果。',
          citedDocumentIds: failed ? [] : [...item.expectedDocumentIds],
          rejectionReason: null, topScore: failed ? null : 0.78,
          passed: !failed, failureReason: failed ? 'kind-mismatch' : null,
        };
      });
      this.acceptanceResults.set(run.id, results);
      this.acceptanceRuns.set(assistantId, [run, ...(this.acceptanceRuns.get(assistantId) ?? [])]);
      this.persistAcceptance();
      return this.applyScenario(run);
    }, () => this.assistantSettingsPermissionDenied());
  }
  getAssistantTestRun(assistantId: string, runId: string): Observable<RepositoryView<AssistantTestRunDetailView>> {
    return this.signedIn(viewer => {
      if (!this.isAssistantOwner(viewer, assistantId)) return this.assistantSettingsPermissionDenied();
      const run = (this.acceptanceRuns.get(assistantId) ?? []).find(item => item.id === runId);
      if (!run) return this.assistantSettingsPermissionDenied();
      const results = run.status === 'completed' ? this.acceptanceResults.get(run.id) ?? [] : [];
      return this.applyScenario({ run, results });
    }, () => this.assistantSettingsPermissionDenied());
  }
  private isAssistantOwner(viewer: AccountId, assistantId: string): boolean { return this.assistants().some(item => item.id === assistantId && item.ownerAccountId === viewer); }
  private createMockCase(assistantId: string, input: AssistantTestCaseImportEntry): void {
    const now = new Date().toISOString(); const ordinal = ++this.acceptanceOrdinal;
    const item: AssistantTestCaseView = { id: input.id ?? `mock-case-${ordinal}`, assistantId, question: input.question ?? '', category: (input.category ?? 'common') as AssistantTestCaseView['category'], expectedKind: (input.expectedKind ?? 'company-data') as AssistantTestCaseView['expectedKind'], expectedDocumentIds: input.expectedCitedDocuments ?? [], followUpOfId: input.followUpOf ?? null, ordinal, createdAt: now, updatedAt: now };
    this.acceptanceCases.set(assistantId, [...(this.acceptanceCases.get(assistantId) ?? []), item]);
  }

  private restoreAcceptance(): void {
    const state = parseJson(this.storage.getItem(ASSISTANT_ACCEPTANCE_KEY));
    if (!isRecord(state)) return;
    if (typeof state['ordinal'] === 'number' && Number.isSafeInteger(state['ordinal'])) {
      this.acceptanceOrdinal = state['ordinal'];
    }
    const restoreArrayMap = <T>(source: unknown, target: Map<string, T[]>): void => {
      if (!isRecord(source)) return;
      for (const [id, values] of Object.entries(source)) {
        if (Array.isArray(values)) target.set(id, values as T[]);
      }
    };
    restoreArrayMap(state['cases'], this.acceptanceCases);
    restoreArrayMap(state['runs'], this.acceptanceRuns);
    restoreArrayMap(state['results'], this.acceptanceResults);
    if (isRecord(state['statuses'])) {
      for (const [id, status] of Object.entries(state['statuses'])) {
        if (status === 'not-accepted' || status === 'passed' || status === 'failed' || status === 'outdated') {
          this.acceptanceStatuses.set(id, status);
        }
      }
    }
  }

  private persistAcceptance(): void {
    this.storage.setItem(ASSISTANT_ACCEPTANCE_KEY, JSON.stringify({
      ordinal: this.acceptanceOrdinal,
      cases: Object.fromEntries(this.acceptanceCases),
      runs: Object.fromEntries(this.acceptanceRuns),
      results: Object.fromEntries(this.acceptanceResults),
      statuses: Object.fromEntries(this.acceptanceStatuses),
    }));
  }

  getAssistantSettings(assistantId: string): Observable<RepositoryView<AssistantSettingsView>> {
    return this.signedIn(
      (viewer) => this.getAssistantSettingsSync(viewer, assistantId),
      () => this.assistantSettingsPermissionDenied(),
    );
  }

  updateAssistantSettings(
    assistantId: string,
    patch: AssistantSettingsPatch,
  ): Observable<UpdateAssistantSettingsResult> {
    return this.signedIn(
      (viewer) => this.updateAssistantSettingsSync(viewer, assistantId, patch),
      () => this.assistantSettingsPermissionDenied(),
    );
  }

  setAssistantSourceConnection(
    assistantId: string,
    source: AssistantSourceReference,
    connected: boolean,
  ): Observable<UpdateAssistantSettingsResult> {
    return this.signedIn(
      (viewer) => this.setAssistantSourceConnectionSync(viewer, assistantId, source, connected),
      () => this.assistantSettingsPermissionDenied(),
    );
  }

  /**
   * 刪除自己的助理：建立精靈建立的從清單移除，種子助理記在 `deleted-assistants`（種子是
   * 唯讀 fixture）；一併清掉它的設定與發布紀錄。對話紀錄以帳號為鍵分散在 storage 中，
   * 助理不存在後就再也開不到（`assistant-use`），效果與 API 連帶刪除相同。
   */
  deleteAssistant(assistantId: string): Observable<DeleteAssistantResult> {
    return this.signedIn(
      (viewer): DeleteAssistantResult => {
        const assistant = this.settingsTarget(viewer, assistantId);
        if (assistant === undefined) return this.assistantSettingsPermissionDenied();

        const created = this.createdAssistants();
        if (created.some((candidate) => candidate.id === assistant.id)) {
          this.storage.setItem(
            CREATED_ASSISTANTS_KEY,
            JSON.stringify(created.filter((candidate) => candidate.id !== assistant.id)),
          );
        } else {
          this.storage.setItem(
            DELETED_ASSISTANTS_KEY,
            JSON.stringify([...this.deletedAssistantIds(), assistant.id]),
          );
        }
        this.storage.removeItem(ASSISTANT_SETTINGS_KEY_PREFIX + assistant.id);
        this.storage.removeItem(PUBLISHING_KEY_PREFIX + assistant.id);
        this.acceptanceCases.delete(assistant.id);
        for (const run of this.acceptanceRuns.get(assistant.id) ?? []) {
          this.acceptanceResults.delete(run.id);
          this.acceptancePolls.delete(run.id);
        }
        this.acceptanceRuns.delete(assistant.id);
        this.acceptanceStatuses.delete(assistant.id);
        this.persistAcceptance();
        return this.applyScenario(null);
      },
      () => this.assistantSettingsPermissionDenied(),
    );
  }

  /**
   * 以目前 Demo 身分執行一次讀寫，包成 cold Observable（訂閱時才執行）；尚未選擇身分時
   * 回傳 `signedOut()` 的結果。
   */
  private signedIn<T>(run: (viewer: AccountId) => T, signedOut: () => T): Observable<T> {
    return defer(() => {
      const viewer = this.viewer();
      return of(viewer === null ? signedOut() : run(viewer));
    });
  }

  private listAssistantConfigurationsSync(
    viewerAccountId: AccountId,
  ): RepositoryView<readonly AssistantConfigurationView[]> {
    const configurations = this.assistants().filter(
      (assistant) => assistant.ownerAccountId === viewerAccountId,
    );

    return this.applyScenario(configurations);
  }

  private listUsableAssistantsSync(
    viewerAccountId: AccountId,
  ): RepositoryView<readonly AssistantSummaryView[]> {
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

  private getAssistantSettingsSync(
    viewerAccountId: AccountId,
    assistantId: string,
  ): RepositoryView<AssistantSettingsView> {
    const assistant = this.settingsTarget(viewerAccountId, assistantId);
    if (assistant === undefined) return this.assistantSettingsPermissionDenied();

    return this.applyScenario(this.assistantSettings(assistant));
  }

  private updateAssistantSettingsSync(
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

  private setAssistantSourceConnectionSync(
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
    const knowledgeBases = this.knowledgeBases().filter(
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
    const viewer = this.accounts().find((account) => account.id === viewerAccountId);
    const submissions = this.seed.structuredSubmissions.filter(
      (submission) =>
        submission.consentStatus === 'consented' &&
        // 與收集紀錄同一個判斷點：帳號層級權限＋被指定為這筆資料的管理者。
        canReadConsentedRecords(viewer, [submission.dataManagerAccountId]),
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
      !this.hasPermission(viewerAccountId, 'submit-authorized-forms') ||
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


  getAssistantAnalyticsSummary(assistantId: string): Observable<RepositoryView<AssistantAnalyticsSummaryView>> {
    return defer(() => {
      const viewer = this.viewer();
      const assistant = this.assistants().find((candidate) => candidate.id === assistantId);
      if (viewer === null || assistant?.ownerAccountId !== viewer) {
        return of(this.permissionDenied('assistant-configuration', '只有助理擁有者可查看使用統計。'));
      }
      const seeded = this.seed.analytics.find((candidate) => candidate.assistantId === assistantId);
      const totalReplies = seeded?.conversationCount ?? 0;
      const result: AssistantAnalyticsSummaryView = {
        from: '2026-09-17', to: '2026-09-23', totalReplies,
        replyKinds: [{ kind: 'company-data', count: Math.max(totalReplies - 4, 0) }, { kind: 'general-knowledge', count: 2 }, { kind: 'no-result', count: 2 }],
        rejectionReasons: [{ reason: 'below-threshold', count: 2 }, { reason: 'citation-out-of-range', count: 0 }, { reason: 'no-citation', count: 0 }, { reason: 'cannot-answer', count: 0 }, { reason: 'empty-answer', count: 0 }],
        mostCitedDocuments: [{ documentId: 'document-product-guide', documentName: '商品使用指南', count: Math.max(totalReplies - 3, 0) }],
      };
      return of(this.applyScenario(result));
    });
  }

  getOperationsSummary(): Observable<RepositoryView<OperationsSummaryView>> {
    return defer(() => {
      const viewer = this.viewer();
      if (viewer === null || !this.canManageAssistants(viewer)) {
        return of(this.permissionDenied('assistant-configuration', '你沒有查看營運追蹤的權限。'));
      }
      const assistants = this.seed.analytics.flatMap((analytics) => {
        const assistant = this.assistants().find((candidate) => candidate.id === analytics.assistantId);
        return assistant === undefined ? [] : [{ assistantId: assistant.id, assistantName: assistant.name, totalReplies: analytics.conversationCount, noResultRate: analytics.conversationCount === 0 ? 0 : 2 / analytics.conversationCount, rejectedCitationRate: 0 }];
      });
      const result: OperationsSummaryView = {
        from: '2026-09-17', to: '2026-09-23', assistants,
        mostCitedDocuments: [{ documentId: 'document-product-guide', documentName: '商品使用指南', count: 15 }],
        knowledge: { processingFailedCount: 0, overduePendingReviewCount: 0 },
        issues: { openCount: 2, averageResolutionHours: 18 },
      };
      return of(this.applyScenario(result));
    });
  }

  listPublishingChannels(): Observable<RepositoryView<readonly PublishingChannelView[]>> {
    return this.signedIn(
      (viewer) => this.listPublishingChannelsSync(viewer),
      () => this.applyScenario([]),
    );
  }

  listChannelOverview(): Observable<RepositoryView<readonly AssistantChannelsView[]>> {
    return this.signedIn(
      (viewer) => this.listChannelOverviewSync(viewer),
      () => this.applyScenario([]),
    );
  }

  getAssistantPublishing(assistantId: string): Observable<RepositoryView<AssistantPublishingView>> {
    return this.signedIn(
      (viewer) => this.getAssistantPublishingSync(viewer, assistantId),
      () => this.publishingPermissionDenied(),
    );
  }

  updatePlatformSharing(
    assistantId: string,
    accountIds: readonly AccountId[],
  ): Observable<UpdatePlatformSharingResult> {
    return this.signedIn(
      (viewer) => this.updatePlatformSharingSync(viewer, assistantId, accountIds),
      () => this.publishingPermissionDenied(),
    );
  }

  setPublishingChannelPaused(
    assistantId: string,
    channelType: PublishingChannelType,
    paused: boolean,
  ): Observable<RepositoryView<PublishingChannelView>> {
    return this.signedIn(
      (viewer) => this.setPublishingChannelPausedSync(viewer, assistantId, channelType, paused),
      () => this.publishingPermissionDenied(),
    );
  }

  private listPublishingChannelsSync(
    viewerAccountId: AccountId,
  ): RepositoryView<readonly PublishingChannelView[]> {
    return this.applyScenario(
      this.channelOverview(viewerAccountId).flatMap((entry) => entry.channels),
    );
  }

  private listChannelOverviewSync(
    viewerAccountId: AccountId,
  ): RepositoryView<readonly AssistantChannelsView[]> {
    return this.applyScenario(this.channelOverview(viewerAccountId));
  }

  private getAssistantPublishingSync(
    viewerAccountId: AccountId,
    assistantId: string,
  ): RepositoryView<AssistantPublishingView> {
    const assistant = this.publishingTarget(viewerAccountId, assistantId);
    if (assistant === undefined) return this.publishingPermissionDenied();

    return this.applyScenario(this.toPublishingView(assistant));
  }

  private updatePlatformSharingSync(
    viewerAccountId: AccountId,
    assistantId: string,
    accountIds: readonly AccountId[],
  ): UpdatePlatformSharingResult {
    const assistant = this.publishingTarget(viewerAccountId, assistantId);
    if (assistant === undefined) return this.publishingPermissionDenied();

    const allowed = normalizePlatformAccounts(assistant, this.accounts(), accountIds);
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
    const assistant = this.publishingTarget(viewerAccountId, assistantId);
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
    const assistant = this.publishingTarget(viewerAccountId, assistantId);
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
    const assistant = this.publishingTarget(viewerAccountId, assistantId);
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
    const assistant = this.publishingTarget(viewerAccountId, assistantId);
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
    const assistant = this.publishingTarget(viewerAccountId, assistantId);
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

  private setPublishingChannelPausedSync(
    viewerAccountId: AccountId,
    assistantId: string,
    channelType: PublishingChannelType,
    paused: boolean,
  ): RepositoryView<PublishingChannelView> {
    const assistant = this.publishingTarget(viewerAccountId, assistantId);
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

  listConnectableSources(): ReturnType<DemoRepository['listConnectableSources']> {
    return defer(() => {
      const viewer = this.viewer();
      return of(this.applyScenario(viewer === null ? [] : this.connectableSources(viewer)));
    });
  }

  listTrialQuestions(): ReturnType<DemoRepository['listTrialQuestions']> {
    return this.applyScenario(
      this.seed.trialQuestions.map(({ id, text }) => ({ id, text })),
    );
  }

  /**
   * 試問（issue #82）：以自由輸入的問題與固定題組的關鍵字比對，模擬真實回答流程；不接受
   * `draftId`（Mock 不需要，草稿內容由呼叫端的 `request.sources`／`request.rules` 提供）。
   */
  previewTrialAnswer(
    _draftId: string,
    request: TrialAnswerRequest,
  ): Observable<PreviewTrialAnswerResult> {
    return this.signedIn(
      (viewer): PreviewTrialAnswerResult => this.previewTrialAnswerSync(viewer, request),
      () => this.draftPermissionDenied(),
    );
  }

  private previewTrialAnswerSync(
    viewerAccountId: AccountId,
    request: TrialAnswerRequest,
  ): PreviewTrialAnswerResult {
    if (!this.canManageAssistants(viewerAccountId)) {
      return this.draftPermissionDenied();
    }

    const question = request.question.trim();
    if (question.length === 0) {
      return immutableCopy({ status: 'validation-failed', message: '請先輸入問題。' });
    }
    if (question.length > MAX_TRIAL_QUESTION_LENGTH) {
      return immutableCopy({
        status: 'validation-failed',
        message: `問題請在 ${MAX_TRIAL_QUESTION_LENGTH} 個字以內。`,
      });
    }

    const normalized = question.replace(/\s+/g, '');
    const fixture = this.seed.trialQuestions.find(
      (candidate) => candidate.keyword !== null && normalized.includes(candidate.keyword),
    );
    const companyAnswer = fixture?.companyAnswer ?? null;
    const knowledgeBase =
      companyAnswer === null
        ? undefined
        : this.knowledgeBases().find(
            (candidate) =>
              candidate.id === companyAnswer.sourceId &&
              candidate.ownerAccountId === viewerAccountId &&
              request.sources.some(
                (source) =>
                  source.type === 'knowledge-base' && source.id === candidate.id,
              ),
          );

    const threshold = TRIAL_ANSWER_THRESHOLD;
    let reply: TrialAnswerView;
    let passages: TrialAnswerResultView['passages'] = [];
    if (companyAnswer !== null && knowledgeBase !== undefined) {
      const passage = {
        knowledgeBaseName: knowledgeBase.name,
        documentName: companyAnswer.documentName,
        locationLabel: companyAnswer.locationLabel,
        excerpt: companyAnswer.excerpt,
        score: TRIAL_ANSWER_MATCH_SCORE,
      };
      passages = [passage];
      reply = {
        kind: 'company-data',
        text: companyAnswer.text,
        citations: request.rules.showCitations ? [{ ...passage }] : [],
        citationNotice: request.rules.showCitations
          ? null
          : '助理的規則關閉了「顯示引用出處」，回答仍然標示成組織資料。',
      };
    } else if (
      fixture?.generalAnswer != null &&
      request.rules.knowledgeScope === 'allow-general-knowledge'
    ) {
      reply = {
        kind: 'general-knowledge',
        text: fixture.generalAnswer,
        notice: '這是一般知識補充，不是組織資料。',
      };
    } else {
      reply = {
        kind: 'no-result',
        text: request.rules.refusalMessage,
        nextSteps: ['換個說法再問一次，或點選建議問題。'],
      };
    }

    return this.applyScenario<TrialAnswerResultView>({ question, reply, passages, threshold });
  }

  private namedDrafts(viewerAccountId: AccountId): NamedAssistantDraftView[] {
    const raw = parseJson(this.storage.getItem(NAMED_DRAFTS_KEY_PREFIX + viewerAccountId));
    const records: NamedAssistantDraftView[] = Array.isArray(raw)
      ? raw.flatMap((item) => {
          if (!isRecord(item) || typeof item['id'] !== 'string') return [];
          const saved = normalizeStoredDraft(item);
          return saved === null ? [] : [{ ...saved, id: item['id'], revision: storedRevision(item) }];
        })
      : [];
    const legacy = normalizeStoredDraft(parseJson(this.storage.getItem(DRAFT_KEY_PREFIX + viewerAccountId)));
    if (legacy !== null) {
      const migrated = [{ id: 'draft-legacy', ...legacy, revision: 1 }, ...records];
      this.writeNamedDrafts(viewerAccountId, migrated);
      this.storage.removeItem(DRAFT_KEY_PREFIX + viewerAccountId);
      return migrated;
    }
    return records;
  }

  private writeNamedDrafts(viewerAccountId: AccountId, drafts: readonly NamedAssistantDraftView[]): void {
    this.storage.setItem(NAMED_DRAFTS_KEY_PREFIX + viewerAccountId, JSON.stringify(drafts.map((item) => ({ ...item, version: 1 }))));
  }

  /** 不存在、別人的草稿與沒有 `manage-assistants` 都回傳同一個結果（API 的 `403 assistant-draft`）。 */
  private ownedNamedDraft(viewerAccountId: AccountId, draftId: string): NamedAssistantDraftView | undefined {
    if (!this.canManageAssistants(viewerAccountId)) return undefined;
    return this.namedDrafts(viewerAccountId).find((item) => item.id === draftId);
  }

  listNamedAssistantDrafts(): Observable<RepositoryView<readonly NamedAssistantDraftView[]>> {
    return this.signedIn(
      (viewer): RepositoryView<readonly NamedAssistantDraftView[]> => {
        if (!this.canManageAssistants(viewer)) return this.draftPermissionDenied();
        const drafts = [...this.namedDrafts(viewer)].sort((a, b) => b.savedAt.localeCompare(a.savedAt));
        return this.applyScenario(drafts);
      },
      () => this.draftPermissionDenied(),
    );
  }

  createNamedAssistantDraft(): Observable<RepositoryView<NamedAssistantDraftView>> {
    return this.signedIn(
      (viewer): RepositoryView<NamedAssistantDraftView> => {
        if (!this.canManageAssistants(viewer)) return this.draftPermissionDenied();
        const drafts = this.namedDrafts(viewer);
        const used = new Set(drafts.map((item) => item.id));
        let number = 1;
        while (used.has(`draft-${number}`)) number++;
        const created: NamedAssistantDraftView = {
          id: `draft-${number}`,
          draft: createEmptyAssistantDraft(),
          savedAt: this.now().toISOString(),
          revision: 1,
        };
        this.writeNamedDrafts(viewer, [...drafts, created]);
        return this.applyScenario(created);
      },
      () => this.draftPermissionDenied(),
    );
  }

  getNamedAssistantDraft(draftId: string): Observable<RepositoryView<NamedAssistantDraftView>> {
    return this.signedIn(
      (viewer): RepositoryView<NamedAssistantDraftView> => {
        const draft = this.ownedNamedDraft(viewer, draftId);
        return draft === undefined ? this.draftPermissionDenied() : this.applyScenario(draft);
      },
      () => this.draftPermissionDenied(),
    );
  }

  saveNamedAssistantDraft(
    draftId: string,
    draft: AssistantDraft,
    revision: number,
  ): Observable<SaveAssistantDraftResult> {
    return this.signedIn(
      (viewer): SaveAssistantDraftResult => {
        const current = this.ownedNamedDraft(viewer, draftId);
        if (current === undefined) return this.draftPermissionDenied();
        if (current.revision !== revision) {
          return immutableCopy({ status: 'conflict', message: DRAFT_REVISION_CONFLICT_MESSAGE });
        }
        const saved: NamedAssistantDraftView = {
          id: draftId,
          draft,
          savedAt: this.now().toISOString(),
          revision: current.revision + 1,
        };
        this.writeNamedDrafts(viewer, this.namedDrafts(viewer).map((item) => (item.id === draftId ? saved : item)));
        return this.applyScenario(saved);
      },
      () => this.draftPermissionDenied(),
    );
  }

  discardNamedAssistantDraft(draftId: string): Observable<RepositoryView<null>> {
    return this.signedIn(
      (viewer): RepositoryView<null> => {
        if (this.ownedNamedDraft(viewer, draftId) === undefined) return this.draftPermissionDenied();
        this.removeNamedDraft(viewer, draftId);
        return this.applyScenario(null);
      },
      () => this.draftPermissionDenied(),
    );
  }

  private removeNamedDraft(viewerAccountId: AccountId, draftId: string): void {
    this.writeNamedDrafts(viewerAccountId, this.namedDrafts(viewerAccountId).filter((item) => item.id !== draftId));
  }

  createAssistantFromDraft(draftId: string, draft: AssistantDraft): Observable<CreateAssistantResult> {
    return this.signedIn(
      (viewer) => this.createAssistantFromDraftSync(viewer, draftId, draft),
      () => this.draftPermissionDenied(),
    );
  }

  private createAssistantFromDraftSync(
    viewerAccountId: AccountId,
    draftId: string,
    draft: AssistantDraft,
  ): CreateAssistantResult {
    if (this.ownedNamedDraft(viewerAccountId, draftId) === undefined) {
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
    this.removeNamedDraft(viewerAccountId, draftId);

    return this.applyScenario(configuration);
  }

  listKnowledgeBaseSummaries(): ReturnType<DemoRepository['listKnowledgeBaseSummaries']> {
    return defer(() => of(this.readKnowledgeSummaries(this.viewer())));
  }

  getKnowledgeBaseDetail(knowledgeBaseId: string): ReturnType<DemoRepository['getKnowledgeBaseDetail']> {
    return defer(() => of(this.readKnowledgeDetail(this.viewer(), knowledgeBaseId)));
  }

  createKnowledgeBase(input: CreateKnowledgeBaseInput): Observable<CreateKnowledgeBaseResult> {
    return defer(() => of(this.writeNewKnowledgeBase(this.viewer(), input)));
  }

  deleteKnowledgeBase(knowledgeBaseId: KnowledgeBaseId): Observable<DeleteKnowledgeResult> {
    return defer(() => of(this.removeKnowledgeBase(this.viewer(), knowledgeBaseId)));
  }

  deleteKnowledgeDocument(
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
  ): Observable<DeleteKnowledgeResult> {
    return defer(() => of(this.removeKnowledgeDocument(this.viewer(), knowledgeBaseId, documentId)));
  }

  /** mock 沒有版本，`versionId` 只用來和 API 對齊簽章；判斷以文件本身為準。 */
  retryKnowledgeDocument(
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
    // 簽章與契約（及 API 模式的覆寫）一致；mock 只有一個版本，用不到。
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    _versionId: string,
  ): Observable<RetryKnowledgeDocumentResult> {
    return defer(() => of(this.requeueKnowledgeDocument(this.viewer(), knowledgeBaseId, documentId)));
  }

  updateKnowledgeSharing(
    knowledgeBaseId: KnowledgeBaseId,
    sharing: KnowledgeSharingView,
  ): Observable<UpdateKnowledgeSharingResult> {
    return defer(() => of(this.writeKnowledgeSharing(this.viewer(), knowledgeBaseId, sharing)));
  }

  /** mock 不會送出進度事件：沒有真的網路傳輸可以量，直接以結果 complete。 */
  uploadKnowledgeDocument(
    knowledgeBaseId: KnowledgeBaseId,
    file: File,
  ): Observable<UploadKnowledgeDocumentEvent> {
    return defer(() => of(this.writeUploadedKnowledgeDocument(this.viewer(), knowledgeBaseId, file)));
  }

  uploadKnowledgeDocumentVersion(
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
    file: File,
  ): Observable<UploadKnowledgeDocumentEvent> {
    return defer(() => of(this.writeUploadedKnowledgeDocumentVersion(this.viewer(), knowledgeBaseId, documentId, file)));
  }

  getKnowledgeDocumentDetail(
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
  ): Observable<RepositoryView<KnowledgeDocumentDetailView>> {
    return defer(() => of(this.readKnowledgeDocumentDetail(this.viewer(), knowledgeBaseId, documentId)));
  }

  previewKnowledgeVersion(
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
    versionId: string,
  ): Observable<RepositoryView<KnowledgeVersionPreviewView>> {
    return defer(() => of(this.readKnowledgeVersionPreview(this.viewer(), knowledgeBaseId, documentId, versionId)));
  }

  updateKnowledgeChunkExclusion(
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
    versionId: string,
    chunkId: string,
    excluded: boolean,
  ): Observable<UpdateKnowledgeChunkExclusionResult> {
    return defer(() =>
      of(this.writeKnowledgeChunkExclusion(this.viewer(), knowledgeBaseId, documentId, versionId, chunkId, excluded)),
    );
  }

  approveKnowledgeVersions(
    knowledgeBaseId: KnowledgeBaseId,
    versionIds: readonly string[],
    effectiveFrom?: string,
  ): Observable<ApproveKnowledgeVersionsResult> {
    return defer(() => of(this.writeApprovedKnowledgeVersions(this.viewer(), knowledgeBaseId, versionIds, effectiveFrom)));
  }

  disableKnowledgeDocument(
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
    reason: string,
  ): Observable<DisableKnowledgeDocumentResult> {
    return defer(() => of(this.writeDisabledKnowledgeDocument(this.viewer(), knowledgeBaseId, documentId, reason)));
  }

  enableKnowledgeDocument(
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
  ): Observable<EnableKnowledgeDocumentResult> {
    return defer(() => of(this.writeEnabledKnowledgeDocument(this.viewer(), knowledgeBaseId, documentId)));
  }

  previewKnowledgeRetrieval(
    knowledgeBaseId: KnowledgeBaseId,
    question: string,
    includePending: boolean,
  ): Observable<PreviewKnowledgeRetrievalResult> {
    return defer(() =>
      of(this.readKnowledgeRetrievalPreview(this.viewer(), knowledgeBaseId, question, includePending)),
    );
  }

  private readKnowledgeSummaries(
    viewerAccountId: AccountId | null,
  ): RepositoryView<readonly KnowledgeBaseSummaryView[]> {
    if (viewerAccountId === null) return this.knowledgePermissionDenied();
    return this.applyScenario(
      this.knowledgeBases()
        .filter((knowledgeBase) => knowledgeBase.ownerAccountId === viewerAccountId)
        .map((knowledgeBase) => this.toKnowledgeSummary(knowledgeBase, viewerAccountId)),
    );
  }

  private readKnowledgeDetail(
    viewerAccountId: AccountId | null,
    knowledgeBaseId: string,
  ): RepositoryView<KnowledgeBaseDetailView> {
    const knowledgeBase = this.ownedKnowledgeBase(viewerAccountId, knowledgeBaseId);
    if (viewerAccountId === null || knowledgeBase === undefined) return this.knowledgePermissionDenied();

    const detail: KnowledgeBaseDetailView = {
      summary: this.toKnowledgeSummary(knowledgeBase, viewerAccountId),
      documents: this.knowledgeDocuments(knowledgeBase.id),
      connectedAssistants: this.connectedKnowledgeAssistants(viewerAccountId, knowledgeBase.id),
      sharing: this.knowledgeRecord(knowledgeBase.id).sharing,
      shareTargets: this.accounts()
        .filter((account) => account.id !== viewerAccountId)
        .map(({ id, displayName }) => ({ id, displayName })),
    };

    return this.applyScenario(detail);
  }

  private writeNewKnowledgeBase(
    viewerAccountId: AccountId | null,
    input: CreateKnowledgeBaseInput,
  ): CreateKnowledgeBaseResult {
    if (viewerAccountId === null || !this.canManageDataSources(viewerAccountId)) {
      return this.permissionDenied('knowledge-base', KNOWLEDGE_CREATE_PERMISSION_DENIED_MESSAGE);
    }

    const name = input.name.trim();
    const purpose = input.purpose.trim();
    // 與 API 相同：第一個錯誤當訊息（名稱優先）。
    const problem =
      name.length === 0
        ? KNOWLEDGE_NAME_REQUIRED_MESSAGE
        : name.length > KNOWLEDGE_BASE_NAME_MAX_LENGTH
          ? KNOWLEDGE_NAME_TOO_LONG_MESSAGE
          : purpose.length > KNOWLEDGE_BASE_PURPOSE_MAX_LENGTH
            ? KNOWLEDGE_PURPOSE_TOO_LONG_MESSAGE
            : null;
    if (problem !== null) return immutableCopy({ status: 'validation-failed', message: problem });

    const existing = new Set<string>(this.allKnowledgeBases().map((knowledgeBase) => knowledgeBase.id));
    let sequence = this.now().getTime();
    while (existing.has(`knowledge-created-${sequence}`)) sequence += 1;
    const knowledgeBase: KnowledgeBaseView = {
      id: `knowledge-created-${sequence}`,
      ownerAccountId: viewerAccountId,
      name,
      purpose,
      lastSyncedAt: this.now().toISOString(),
    };
    this.storage.setItem(
      CREATED_KNOWLEDGE_BASES_KEY,
      JSON.stringify([...this.createdKnowledgeBases(), knowledgeBase]),
    );

    return this.applyScenario(this.toKnowledgeSummary(knowledgeBase, viewerAccountId));
  }

  private removeKnowledgeBase(
    viewerAccountId: AccountId | null,
    knowledgeBaseId: string,
  ): DeleteKnowledgeResult {
    const knowledgeBase = this.ownedKnowledgeBase(viewerAccountId, knowledgeBaseId);
    if (knowledgeBase === undefined) return this.knowledgePermissionDenied();

    this.storage.setItem(
      DELETED_KNOWLEDGE_BASES_KEY,
      JSON.stringify([...this.deletedKnowledgeBaseIds(), knowledgeBase.id]),
    );
    this.storage.removeItem(KNOWLEDGE_KEY_PREFIX + knowledgeBase.id);

    return this.applyScenario(null);
  }

  private removeKnowledgeDocument(
    viewerAccountId: AccountId | null,
    knowledgeBaseId: string,
    documentId: KnowledgeDocumentId,
  ): DeleteKnowledgeResult {
    const knowledgeBase = this.ownedKnowledgeBase(viewerAccountId, knowledgeBaseId);
    if (knowledgeBase === undefined) return this.knowledgePermissionDenied();

    const record = this.knowledgeRecord(knowledgeBase.id);
    if (!record.documents.some((document) => document.id === documentId)) {
      return this.knowledgePermissionDenied();
    }
    this.saveKnowledgeRecord(knowledgeBase.id, {
      ...record,
      documents: record.documents.filter((document) => document.id !== documentId),
    });

    return this.applyScenario(null);
  }

  /** 與 API 相同：只有處理失敗的文件可以重試，其餘回傳 validation-failed（API 的 `409`）。 */
  private requeueKnowledgeDocument(
    viewerAccountId: AccountId | null,
    knowledgeBaseId: string,
    documentId: KnowledgeDocumentId,
  ): RetryKnowledgeDocumentResult {
    const knowledgeBase = this.ownedKnowledgeBase(viewerAccountId, knowledgeBaseId);
    if (knowledgeBase === undefined) return this.knowledgePermissionDenied();

    const record = this.knowledgeRecord(knowledgeBase.id);
    const current = this.knowledgeDocuments(knowledgeBase.id).find((document) => document.id === documentId);
    if (current === undefined) return this.knowledgePermissionDenied();
    if (!isRetryableKnowledgeDocument(current.status)) {
      return immutableCopy({
        status: 'validation-failed',
        message:
          current.status === 'queued' || current.status === 'processing'
            ? KNOWLEDGE_STILL_PROCESSING_MESSAGE
            : KNOWLEDGE_NOT_FAILED_MESSAGE,
      });
    }

    const startedAt = this.now().toISOString();
    this.saveKnowledgeRecord(knowledgeBase.id, {
      ...record,
      documents: record.documents.map((document): StoredKnowledgeDocument =>
        document.id === documentId
          ? { ...document, status: 'queued', issue: null, updatedAt: startedAt, processingStartedAt: startedAt }
          : document,
      ),
    });

    const retried = this.knowledgeDocuments(knowledgeBase.id).find((document) => document.id === documentId);
    return retried === undefined ? this.knowledgePermissionDenied() : this.applyScenario(retried);
  }

  private writeKnowledgeSharing(
    viewerAccountId: AccountId | null,
    knowledgeBaseId: string,
    sharing: KnowledgeSharingView,
  ): UpdateKnowledgeSharingResult {
    const knowledgeBase = this.ownedKnowledgeBase(viewerAccountId, knowledgeBaseId);
    if (knowledgeBase === undefined) return this.knowledgePermissionDenied();

    const validTargets = new Set<string>(
      this.accounts()
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

  /**
   * 上傳一個檔案成為新文件（issue #46）：與 API 相同的檢查順序——擁有者、大小與副檔名
   * （`precheckKnowledgeUpload`）、內容重複（依大小模擬，只比對這台瀏覽器上傳過的文件）、
   * 名稱重複。全部通過才寫入一份新文件，狀態依經過時間計算（`processingStartedAt`）。
   */
  private writeUploadedKnowledgeDocument(
    viewerAccountId: AccountId | null,
    knowledgeBaseId: string,
    file: File,
  ): UploadKnowledgeDocumentResult {
    const knowledgeBase = this.ownedKnowledgeBase(viewerAccountId, knowledgeBaseId);
    if (knowledgeBase === undefined) return this.knowledgePermissionDenied();

    const precheck = precheckKnowledgeUpload(file);
    if (precheck !== null) {
      return immutableCopy({ status: 'rejected', reason: precheck.reason, message: precheck.message });
    }

    const record = this.knowledgeRecord(knowledgeBase.id);
    const sizes = record.uploadedSizes ?? {};
    const documents = this.knowledgeDocuments(knowledgeBase.id);
    const duplicateContentId = Object.keys(sizes).find((id) => sizes[id] === file.size);
    const duplicateContent =
      duplicateContentId === undefined
        ? undefined
        : documents.find((document) => document.id === duplicateContentId);
    if (duplicateContent !== undefined) {
      return immutableCopy({
        status: 'rejected',
        reason: 'duplicate-content',
        message: duplicateContentMessage(duplicateContent.name),
        existingDocumentName: duplicateContent.name,
      });
    }

    if (documents.some((document) => document.name === file.name)) {
      return immutableCopy({
        status: 'rejected',
        reason: 'duplicate-name',
        message: duplicateNameMessage(file.name),
      });
    }

    const existingIds = new Set(documents.map((document) => document.id));
    let sequence = this.now().getTime();
    while (existingIds.has(`${knowledgeBase.id}-doc-${sequence}`)) sequence += 1;
    const id = `${knowledgeBase.id}-doc-${sequence}`;
    const startedAt = this.now().toISOString();
    const created: StoredKnowledgeDocument = {
      id,
      kind: 'document',
      name: file.name,
      status: 'queued',
      issue: null,
      updatedAt: startedAt,
      processingStartedAt: startedAt,
    };
    const review: StoredKnowledgeReview = {
      versionNumber: 1,
      fileName: file.name,
      reviewState: 'pending-review',
      uploadedByAccountId: viewerAccountId ?? undefined,
      uploadedAt: startedAt,
      priorVersions: [],
      chunkExclusions: {},
      activities: [this.knowledgeActivity('version-uploaded', viewerAccountId, startedAt, { versionNumber: 1 })],
    };
    this.saveKnowledgeRecord(knowledgeBase.id, {
      ...record,
      documents: [...record.documents, created],
      uploadedSizes: { ...sizes, [id]: file.size },
      reviews: { ...record.reviews, [id]: review },
    });

    const uploaded = this.knowledgeDocuments(knowledgeBase.id).find((document) => document.id === id);
    return uploaded === undefined ? this.knowledgePermissionDenied() : this.applyScenario(uploaded);
  }

  /**
   * 改把這個檔案當成既有文件的新版本上傳：與 API 一樣沒有名稱規則（文件保留自己的
   * 名稱），但內容仍不能與知識庫中任何版本相同。mock 沒有真的多版本模型，這裡只更新
   * 既有文件的處理狀態（與重新處理相同的時間模型），文件本身的名稱不變。
   */
  private writeUploadedKnowledgeDocumentVersion(
    viewerAccountId: AccountId | null,
    knowledgeBaseId: string,
    documentId: KnowledgeDocumentId,
    file: File,
  ): UploadKnowledgeDocumentResult {
    const knowledgeBase = this.ownedKnowledgeBase(viewerAccountId, knowledgeBaseId);
    if (knowledgeBase === undefined) return this.knowledgePermissionDenied();

    const record = this.knowledgeRecord(knowledgeBase.id);
    const documents = this.knowledgeDocuments(knowledgeBase.id);
    const target = documents.find((document) => document.id === documentId);
    if (target === undefined) return this.knowledgePermissionDenied();

    const precheck = precheckKnowledgeUpload(file);
    if (precheck !== null) {
      return immutableCopy({ status: 'rejected', reason: precheck.reason, message: precheck.message });
    }

    const sizes = record.uploadedSizes ?? {};
    const duplicateContentId = Object.keys(sizes).find((id) => sizes[id] === file.size);
    const duplicateContent =
      duplicateContentId === undefined
        ? undefined
        : documents.find((document) => document.id === duplicateContentId);
    if (duplicateContent !== undefined) {
      return immutableCopy({
        status: 'rejected',
        reason: 'duplicate-content',
        message: duplicateContentMessage(duplicateContent.name),
        existingDocumentName: duplicateContent.name,
      });
    }

    const startedAt = this.now().toISOString();
    const previousReview = record.reviews?.[documentId] ?? defaultKnowledgeReview(target);
    const nextVersionNumber = previousReview.versionNumber + 1;
    const archivedPrior: StoredKnowledgePriorVersion = {
      versionNumber: previousReview.versionNumber,
      fileName: previousReview.fileName,
      uploadedByAccountId: previousReview.uploadedByAccountId,
      uploadedAt: previousReview.uploadedAt,
      approvedByAccountId: previousReview.approvedByAccountId,
      approvedAt: previousReview.approvedAt,
      effectiveFrom: previousReview.effectiveFrom,
    };
    const review: StoredKnowledgeReview = {
      versionNumber: nextVersionNumber,
      fileName: file.name,
      reviewState: 'pending-review',
      uploadedByAccountId: viewerAccountId ?? undefined,
      uploadedAt: startedAt,
      priorVersions: [...previousReview.priorVersions, archivedPrior],
      chunkExclusions: {},
      activities: [
        ...previousReview.activities,
        this.knowledgeActivity('version-uploaded', viewerAccountId, startedAt, { versionNumber: nextVersionNumber }),
      ],
      // 目前生效中的版本（如果有）不因為上傳新版本而改變，直到新版本自己被確認生效為止。
      lastEffectiveVersionNumber: previousReview.lastEffectiveVersionNumber,
      lastEffectiveFrom: previousReview.lastEffectiveFrom,
    };
    this.saveKnowledgeRecord(knowledgeBase.id, {
      ...record,
      documents: record.documents.map((document): StoredKnowledgeDocument =>
        document.id === documentId
          ? { ...document, status: 'queued', issue: null, updatedAt: startedAt, processingStartedAt: startedAt }
          : document,
      ),
      uploadedSizes: { ...sizes, [documentId]: file.size },
      reviews: { ...record.reviews, [documentId]: review },
    });

    const updated = this.knowledgeDocuments(knowledgeBase.id).find((document) => document.id === documentId);
    return updated === undefined ? this.knowledgePermissionDenied() : this.applyScenario(updated);
  }

  // ---------- 版本確認、抽取預覽與緊急停用（issue #47，M2 Slice 13） ----------

  private knowledgeActivity(
    action: KnowledgeActivityAction,
    actorAccountId: string | null,
    at: string,
    extra: { readonly versionId?: string; readonly versionNumber?: number; readonly reason?: string } = {},
  ): StoredKnowledgeActivity {
    return {
      id: `activity-${at}-${Math.random().toString(36).slice(2, 8)}`,
      action,
      actorAccountId: actorAccountId ?? undefined,
      at,
      ...extra,
    };
  }

  private knowledgeAccountRef(accountId: string | undefined): KnowledgeAccountRefView | null {
    if (accountId === undefined) return null;
    const account = this.accounts().find((candidate) => candidate.id === accountId);
    return { id: accountId, displayName: account?.displayName ?? accountId };
  }

  private knowledgeReview(knowledgeBaseId: KnowledgeBaseId, documentId: KnowledgeDocumentId): StoredKnowledgeReview {
    const record = this.knowledgeRecord(knowledgeBaseId);
    const stored = record.reviews?.[documentId];
    if (stored !== undefined) return stored;
    const document = record.documents.find((candidate) => candidate.id === documentId);
    return defaultKnowledgeReview(document ?? { name: documentId, updatedAt: this.now().toISOString() });
  }

  private readKnowledgeDocumentDetail(
    viewerAccountId: AccountId | null,
    knowledgeBaseId: string,
    documentId: KnowledgeDocumentId,
  ): RepositoryView<KnowledgeDocumentDetailView> {
    const knowledgeBase = this.ownedKnowledgeBase(viewerAccountId, knowledgeBaseId);
    if (knowledgeBase === undefined) return this.knowledgePermissionDenied();
    const document = this.knowledgeDocuments(knowledgeBase.id).find((candidate) => candidate.id === documentId);
    if (document === undefined) return this.knowledgePermissionDenied();

    const review = this.knowledgeReview(knowledgeBase.id, documentId);
    const now = this.now();
    const versions: KnowledgeVersionView[] = [
      {
        id: `${documentId}:v${review.versionNumber}`,
        documentId,
        versionNumber: review.versionNumber,
        fileName: review.fileName,
        contentType: 'application/octet-stream',
        sizeBytes: this.knowledgeRecord(knowledgeBase.id).uploadedSizes?.[documentId] ?? 0,
        status: document.status,
        issue: document.issue,
        state: deriveKnowledgeReviewFields(review, now).latestVersionState,
        effectiveFrom: review.effectiveFrom ?? null,
        uploadedBy: this.knowledgeAccountRef(review.uploadedByAccountId) ?? { id: documentId, displayName: '不明帳號' },
        uploadedAt: review.uploadedAt,
        approvedBy: this.knowledgeAccountRef(review.approvedByAccountId),
        approvedAt: review.approvedAt ?? null,
        updatedAt: document.updatedAt,
      },
      ...review.priorVersions
        .slice()
        .reverse()
        .map(
          (prior): KnowledgeVersionView => ({
            id: `${documentId}:v${prior.versionNumber}`,
            documentId,
            versionNumber: prior.versionNumber,
            fileName: prior.fileName,
            contentType: 'application/octet-stream',
            sizeBytes: 0,
            status: 'ready',
            issue: null,
            state: 'archived',
            effectiveFrom: prior.effectiveFrom ?? null,
            uploadedBy: this.knowledgeAccountRef(prior.uploadedByAccountId) ?? { id: documentId, displayName: '不明帳號' },
            uploadedAt: prior.uploadedAt,
            approvedBy: this.knowledgeAccountRef(prior.approvedByAccountId),
            approvedAt: prior.approvedAt ?? null,
            updatedAt: prior.approvedAt ?? prior.uploadedAt,
          }),
        ),
    ];

    const activities: KnowledgeActivityView[] = review.activities
      .slice()
      .reverse()
      .map((activity) => ({
        id: activity.id,
        action: activity.action,
        actor: this.knowledgeAccountRef(activity.actorAccountId),
        at: activity.at,
        versionId: activity.versionId ?? null,
        versionNumber: activity.versionNumber ?? null,
        reason: activity.reason ?? null,
      }));

    const detail: KnowledgeDocumentDetailView = {
      document,
      createdAt: review.priorVersions[0]?.uploadedAt ?? review.uploadedAt,
      disabledAt: review.disabledAt ?? null,
      disabledBy: this.knowledgeAccountRef(review.disabledByAccountId),
      disabledReason: review.disabledReason ?? null,
      versions,
      activities,
    };
    return this.applyScenario(detail);
  }

  private readKnowledgeVersionPreview(
    viewerAccountId: AccountId | null,
    knowledgeBaseId: string,
    documentId: KnowledgeDocumentId,
    versionId: string,
  ): RepositoryView<KnowledgeVersionPreviewView> {
    const knowledgeBase = this.ownedKnowledgeBase(viewerAccountId, knowledgeBaseId);
    if (knowledgeBase === undefined) return this.knowledgePermissionDenied();
    const document = this.knowledgeDocuments(knowledgeBase.id).find((candidate) => candidate.id === documentId);
    if (document === undefined) return this.knowledgePermissionDenied();

    const review = this.knowledgeReview(knowledgeBase.id, documentId);
    const versionNumber =
      versionId === `${documentId}:v${review.versionNumber}`
        ? review.versionNumber
        : (review.priorVersions.find((prior) => `${documentId}:v${prior.versionNumber}` === versionId)
            ?.versionNumber ?? review.versionNumber);
    const fileName =
      versionNumber === review.versionNumber
        ? review.fileName
        : (review.priorVersions.find((prior) => prior.versionNumber === versionNumber)?.fileName ?? review.fileName);

    const preview: KnowledgeVersionPreviewView = {
      documentId,
      versionId,
      versionNumber,
      fileName,
      status: document.status,
      issue: document.issue,
      units: this.knowledgeExtractedUnits(document, versionId, review),
    };
    return this.applyScenario(preview);
  }

  /**
   * mock 沒有真的抽取內容：依文件名稱與版本產生固定的示範單位與段落，讓預覽畫面
   * 有東西可以顯示與切換排除；`issue` 存在時最後一個單位標記為不可讀，示範「部分內容
   * 無法讀取」的畫面（與文件本身的 `partially-readable`/`failed` 呼應）。
   */
  private knowledgeExtractedUnits(
    document: KnowledgeDocumentView,
    versionId: string,
    review: StoredKnowledgeReview,
  ): readonly KnowledgeExtractedUnitView[] {
    const locationKind: KnowledgeUnitLocationKind = document.kind === 'faq' ? 'faq' : 'page';
    const unitCount = document.kind === 'faq' ? 1 : 2;
    return Array.from({ length: unitCount }, (_, index) => {
      const ordinal = index + 1;
      const isLastUnreadable = document.issue !== null && ordinal === unitCount;
      const locationLabel =
        locationKind === 'faq' ? 'FAQ 內容' : locationKind === 'page' ? `第 ${ordinal} 頁` : `第 ${ordinal} 節`;
      const chunkId = `${versionId}:u${ordinal}:c1`;
      const issueCode: KnowledgeUnitIssue = isLastUnreadable ? 'too-little-text' : null;
      const chunk: KnowledgeChunkView = {
        id: chunkId,
        locationLabel,
        text: isLastUnreadable
          ? '（這個單位找不到足夠的可讀文字）'
          : `這是「${document.name}」第 ${ordinal} 個抽取單位的示範內容，用於預覽與排除段落的展示。`,
        excluded: review.chunkExclusions[chunkId] ?? false,
      };
      const unit: KnowledgeExtractedUnitView = {
        ordinal,
        locationKind,
        locationLabel,
        readable: !isLastUnreadable,
        issueCode,
        text: chunk.text,
        chunks: isLastUnreadable ? [] : [chunk],
      };
      return unit;
    });
  }

  /**
   * 檢索試查（issue #48，M2 Slice 14）：mock 沒有真的嵌入模型，改以「問題與段落文字的
   * 字元二連字重疊比例」模擬分數——document 命名與示範段落都嵌著文件名稱，FAQ 的名稱
   * 本身就是問句，用同樣或相近的問題試查即可示範「有結果」；問一個完全不相關的問題
   * 分數會接近 0，示範「低於門檻」。只搜尋可用（`ready`／`partially-readable`）的文件。
   */
  private readKnowledgeRetrievalPreview(
    viewerAccountId: AccountId | null,
    knowledgeBaseId: string,
    question: string,
    includePending: boolean,
  ): PreviewKnowledgeRetrievalResult {
    const knowledgeBase = this.ownedKnowledgeBase(viewerAccountId, knowledgeBaseId);
    if (knowledgeBase === undefined) return this.knowledgePermissionDenied();

    const trimmed = question.trim();
    if (trimmed.length === 0) {
      return { status: 'validation-failed', message: KNOWLEDGE_RETRIEVAL_QUESTION_REQUIRED_MESSAGE };
    }
    if (trimmed.length > KNOWLEDGE_RETRIEVAL_QUESTION_MAX_LENGTH) {
      return { status: 'validation-failed', message: KNOWLEDGE_RETRIEVAL_QUESTION_TOO_LONG_MESSAGE };
    }

    const passages: KnowledgeRetrievalPassageView[] = [];
    for (const document of this.knowledgeDocuments(knowledgeBase.id)) {
      if (!isUsableKnowledgeDocument(document.status)) continue;
      const review = this.knowledgeReview(knowledgeBase.id, document.id);
      const effectiveVersionNumber = document.effectiveVersionNumber ?? null;
      const latestVersionState = document.latestVersionState ?? 'effective';

      if (effectiveVersionNumber !== null) {
        passages.push(
          ...this.mockRetrievalPassages(document, review, effectiveVersionNumber, 'effective', trimmed),
        );
      }
      if (
        includePending &&
        (latestVersionState === 'pending-review' || latestVersionState === 'scheduled') &&
        review.versionNumber !== effectiveVersionNumber
      ) {
        passages.push(
          ...this.mockRetrievalPassages(document, review, review.versionNumber, latestVersionState, trimmed),
        );
      }
    }

    const top = passages.sort((left, right) => right.score - left.score).slice(0, KNOWLEDGE_RETRIEVAL_DEFAULT_TOP);
    const threshold = KNOWLEDGE_RETRIEVAL_DEFAULT_THRESHOLD;
    const preview: KnowledgeRetrievalPreviewView = {
      passages: top,
      threshold,
      belowThreshold: !top.some((passage) => passage.score >= threshold),
    };
    return this.applyScenario(preview);
  }

  /** 一份文件、一個版本命中的段落：略過已排除的段落與不可讀的抽取單位。 */
  private mockRetrievalPassages(
    document: KnowledgeDocumentView,
    review: StoredKnowledgeReview,
    versionNumber: number,
    versionState: KnowledgeVersionState,
    question: string,
  ): readonly KnowledgeRetrievalPassageView[] {
    const versionId = `${document.id}:v${versionNumber}`;
    const passages: KnowledgeRetrievalPassageView[] = [];
    for (const unit of this.knowledgeExtractedUnits(document, versionId, review)) {
      if (!unit.readable) continue;
      for (const chunk of unit.chunks) {
        if (chunk.excluded) continue;
        const score = mockKeywordScore(question, `${document.name} ${chunk.locationLabel} ${chunk.text}`);
        passages.push({
          documentId: document.id,
          documentName: document.name,
          versionNumber,
          versionState,
          locationLabel: chunk.locationLabel,
          excerpt: chunk.text,
          score,
          versionId,
          chunkId: chunk.id,
        });
      }
    }
    return passages;
  }

  private writeKnowledgeChunkExclusion(
    viewerAccountId: AccountId | null,
    knowledgeBaseId: string,
    documentId: KnowledgeDocumentId,
    versionId: string,
    chunkId: string,
    excluded: boolean,
  ): UpdateKnowledgeChunkExclusionResult {
    const knowledgeBase = this.ownedKnowledgeBase(viewerAccountId, knowledgeBaseId);
    if (knowledgeBase === undefined) return this.knowledgePermissionDenied();
    const document = this.knowledgeDocuments(knowledgeBase.id).find((candidate) => candidate.id === documentId);
    if (document === undefined) return this.knowledgePermissionDenied();

    const record = this.knowledgeRecord(knowledgeBase.id);
    const review = this.knowledgeReview(knowledgeBase.id, documentId);
    const wasExcluded = review.chunkExclusions[chunkId] ?? false;
    const now = this.now().toISOString();
    const changed = wasExcluded !== excluded;
    const updatedReview: StoredKnowledgeReview = {
      ...review,
      chunkExclusions: { ...review.chunkExclusions, [chunkId]: excluded },
      activities: changed
        ? [
            ...review.activities,
            this.knowledgeActivity(excluded ? 'chunk-excluded' : 'chunk-included', viewerAccountId, now, {
              versionId,
            }),
          ]
        : review.activities,
    };
    this.saveKnowledgeRecord(knowledgeBase.id, { ...record, reviews: { ...record.reviews, [documentId]: updatedReview } });

    const [unit] = this.knowledgeExtractedUnits(document, versionId, updatedReview).filter((candidate) =>
      candidate.chunks.some((chunk) => chunk.id === chunkId),
    );
    const chunk = unit?.chunks.find((candidate) => candidate.id === chunkId);
    return chunk === undefined ? this.knowledgePermissionDenied() : this.applyScenario(chunk);
  }

  private writeApprovedKnowledgeVersions(
    viewerAccountId: AccountId | null,
    knowledgeBaseId: string,
    versionIds: readonly string[],
    effectiveFrom: string | undefined,
  ): ApproveKnowledgeVersionsResult {
    const knowledgeBase = this.ownedKnowledgeBase(viewerAccountId, knowledgeBaseId);
    if (knowledgeBase === undefined) return this.knowledgePermissionDenied();

    let effectiveFromIso = this.now().toISOString();
    if (effectiveFrom !== undefined) {
      const parsed = Date.parse(effectiveFrom);
      if (Number.isNaN(parsed)) {
        return immutableCopy({ status: 'validation-failed', message: KNOWLEDGE_EFFECTIVE_DATE_INVALID_MESSAGE });
      }
      effectiveFromIso = new Date(parsed).toISOString();
    }

    const record = this.knowledgeRecord(knowledgeBase.id);
    const documents = this.knowledgeDocuments(knowledgeBase.id);
    // 每個 versionId 都必須指向這個知識庫裡「目前待確認」的版本；任何一個不符合就整批不寫入。
    const targets = versionIds.map((versionId) => {
      const documentId = versionId.includes(':v') ? versionId.slice(0, versionId.lastIndexOf(':v')) : versionId;
      const document = documents.find((candidate) => candidate.id === documentId);
      const review = document === undefined ? undefined : this.knowledgeReview(knowledgeBase.id, documentId);
      const approvable =
        document !== undefined &&
        review !== undefined &&
        review.reviewState === 'pending-review' &&
        `${documentId}:v${review.versionNumber}` === versionId;
      return { versionId, documentId, review, approvable };
    });
    if (targets.some((target) => !target.approvable)) {
      return immutableCopy({ status: 'validation-failed', message: KNOWLEDGE_VERSION_NOT_APPROVABLE_MESSAGE });
    }

    const approvedAt = this.now().toISOString();
    let updatedReviews = { ...record.reviews };
    for (const target of targets) {
      const review = target.review as StoredKnowledgeReview;
      updatedReviews = {
        ...updatedReviews,
        [target.documentId]: {
          ...review,
          reviewState: 'approved',
          effectiveFrom: effectiveFromIso,
          approvedByAccountId: viewerAccountId ?? undefined,
          approvedAt,
          lastEffectiveVersionNumber: review.versionNumber,
          lastEffectiveFrom: effectiveFromIso,
          activities: [
            ...review.activities,
            this.knowledgeActivity('version-approved', viewerAccountId, approvedAt, {
              versionId: target.versionId,
              versionNumber: review.versionNumber,
            }),
          ],
        },
      };
    }
    this.saveKnowledgeRecord(knowledgeBase.id, { ...record, reviews: updatedReviews });

    const refreshedDocuments = this.knowledgeDocuments(knowledgeBase.id);
    const results: KnowledgeVersionView[] = targets.map((target) => {
      const review = updatedReviews[target.documentId] as StoredKnowledgeReview;
      const document = refreshedDocuments.find((candidate) => candidate.id === target.documentId);
      const status = document?.status ?? 'ready';
      return {
        id: target.versionId,
        documentId: target.documentId,
        versionNumber: review.versionNumber,
        fileName: review.fileName,
        contentType: 'application/octet-stream',
        sizeBytes: record.uploadedSizes?.[target.documentId] ?? 0,
        status,
        issue: document?.issue ?? null,
        state: deriveKnowledgeReviewFields(review, this.now()).latestVersionState,
        effectiveFrom: review.effectiveFrom ?? null,
        uploadedBy: this.knowledgeAccountRef(review.uploadedByAccountId) ?? { id: target.documentId, displayName: '不明帳號' },
        uploadedAt: review.uploadedAt,
        approvedBy: this.knowledgeAccountRef(review.approvedByAccountId),
        approvedAt: review.approvedAt ?? null,
        updatedAt: approvedAt,
      };
    });
    return this.applyScenario(results);
  }

  private writeDisabledKnowledgeDocument(
    viewerAccountId: AccountId | null,
    knowledgeBaseId: string,
    documentId: KnowledgeDocumentId,
    reason: string,
  ): DisableKnowledgeDocumentResult {
    const knowledgeBase = this.ownedKnowledgeBase(viewerAccountId, knowledgeBaseId);
    if (knowledgeBase === undefined) return this.knowledgePermissionDenied();
    const document = this.knowledgeDocuments(knowledgeBase.id).find((candidate) => candidate.id === documentId);
    if (document === undefined) return this.knowledgePermissionDenied();

    const trimmedReason = reason.trim();
    if (trimmedReason.length === 0) {
      return immutableCopy({ status: 'validation-failed', message: KNOWLEDGE_DISABLE_REASON_REQUIRED_MESSAGE });
    }
    if (trimmedReason.length > KNOWLEDGE_DISABLE_REASON_MAX_LENGTH) {
      return immutableCopy({ status: 'validation-failed', message: KNOWLEDGE_DISABLE_REASON_TOO_LONG_MESSAGE });
    }

    const record = this.knowledgeRecord(knowledgeBase.id);
    const review = this.knowledgeReview(knowledgeBase.id, documentId);
    if (review.disabledAt !== undefined) {
      return immutableCopy({ status: 'validation-failed', message: KNOWLEDGE_DOCUMENT_ALREADY_DISABLED_MESSAGE });
    }

    const now = this.now().toISOString();
    const updatedReview: StoredKnowledgeReview = {
      ...review,
      disabledAt: now,
      disabledByAccountId: viewerAccountId ?? undefined,
      disabledReason: trimmedReason,
      activities: [
        ...review.activities,
        this.knowledgeActivity('document-disabled', viewerAccountId, now, { reason: trimmedReason }),
      ],
    };
    this.saveKnowledgeRecord(knowledgeBase.id, { ...record, reviews: { ...record.reviews, [documentId]: updatedReview } });

    const updated = this.knowledgeDocuments(knowledgeBase.id).find((candidate) => candidate.id === documentId);
    return updated === undefined ? this.knowledgePermissionDenied() : this.applyScenario(updated);
  }

  private writeEnabledKnowledgeDocument(
    viewerAccountId: AccountId | null,
    knowledgeBaseId: string,
    documentId: KnowledgeDocumentId,
  ): EnableKnowledgeDocumentResult {
    const knowledgeBase = this.ownedKnowledgeBase(viewerAccountId, knowledgeBaseId);
    if (knowledgeBase === undefined) return this.knowledgePermissionDenied();
    const document = this.knowledgeDocuments(knowledgeBase.id).find((candidate) => candidate.id === documentId);
    if (document === undefined) return this.knowledgePermissionDenied();

    const record = this.knowledgeRecord(knowledgeBase.id);
    const review = this.knowledgeReview(knowledgeBase.id, documentId);
    if (review.disabledAt === undefined) {
      return immutableCopy({ status: 'validation-failed', message: KNOWLEDGE_DOCUMENT_NOT_DISABLED_MESSAGE });
    }

    const now = this.now().toISOString();
    const updatedReview: StoredKnowledgeReview = {
      ...review,
      disabledAt: undefined,
      disabledByAccountId: undefined,
      disabledReason: undefined,
      activities: [...review.activities, this.knowledgeActivity('document-enabled', viewerAccountId, now)],
    };
    this.saveKnowledgeRecord(knowledgeBase.id, { ...record, reviews: { ...record.reviews, [documentId]: updatedReview } });

    const updated = this.knowledgeDocuments(knowledgeBase.id).find((candidate) => candidate.id === documentId);
    return updated === undefined ? this.knowledgePermissionDenied() : this.applyScenario(updated);
  }

  listDatabaseTemplates(): ReturnType<DemoRepository['listDatabaseTemplates']> {
    return defer(() => {
      const viewerAccountId = this.viewer();
      if (viewerAccountId === null || !this.canManageDataSources(viewerAccountId)) {
        return of(this.createDatabasePermissionDenied());
      }
      return of(this.applyScenario(this.seed.databaseTemplates));
    });
  }

  listDatabaseSummaries(): ReturnType<DemoRepository['listDatabaseSummaries']> {
    return defer(() => {
      const viewerAccountId = this.viewer();
      if (viewerAccountId === null) return of(this.databasePermissionDenied());
      return of(
        this.applyScenario(
          this.databases()
            .filter((database) => database.ownerAccountId === viewerAccountId)
            .map((database) => this.toDatabaseSummary(database, viewerAccountId)),
        ),
      );
    });
  }

  createDatabaseFromTemplate(
    input: Parameters<DemoRepository['createDatabaseFromTemplate']>[0],
  ): Observable<CreateDatabaseResult> {
    return defer(() => of(this.writeNewDatabase(this.viewer(), input)));
  }

  getDatabaseDetail(databaseId: string): ReturnType<DemoRepository['getDatabaseDetail']> {
    return defer(() => of(this.readDatabaseDetail(this.viewer(), databaseId)));
  }

  /** 與 API 相同的檢查順序：權限、模板、名稱；第一個錯誤當訊息（`DatabaseCreationRules`）。 */
  private writeNewDatabase(
    viewerAccountId: AccountId | null,
    input: Parameters<DemoRepository['createDatabaseFromTemplate']>[0],
  ): CreateDatabaseResult {
    if (viewerAccountId === null || !this.canManageDataSources(viewerAccountId)) {
      return this.createDatabasePermissionDenied();
    }

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

  private readDatabaseDetail(viewerAccountId: AccountId | null, databaseId: string): RepositoryView<DatabaseDetailView> {
    if (viewerAccountId === null) return this.databasePermissionDenied();
    const database = this.ownedDatabase(viewerAccountId, databaseId);
    if (database === undefined) return this.databasePermissionDenied();

    const collection = this.databaseCollection(database.id);
    const detail: DatabaseDetailView = {
      summary: this.toDatabaseSummary(database, viewerAccountId),
      formVersion: this.databaseFormVersion(database.id),
      fields: collection.fields,
      connectedAssistants: this.assistants()
        .filter(
          (assistant) =>
            assistant.ownerAccountId === viewerAccountId &&
            assistant.databaseIds.includes(database.id),
        )
        .map(({ id, name, status }) => ({ id, name, status })),
      access: this.databaseAccessView(database, viewerAccountId),
      upcomingFeatures: [],
    };

    return this.applyScenario(detail);
  }

  updateDatabaseFields(
    databaseId: DatabaseId,
    fields: readonly DatabaseFieldView[],
    baseFormVersion: number,
  ): Observable<UpdateDatabaseFieldsResult> {
    return defer(() => of(this.writeDatabaseFields(this.viewer(), databaseId, fields, baseFormVersion)));
  }

  /** 與 API 相同的檢查順序：擁有者、版本是否過期、欄位驗證；任何一項失敗都不寫入。 */
  private writeDatabaseFields(
    viewerAccountId: AccountId | null,
    databaseId: DatabaseId,
    fields: readonly DatabaseFieldView[],
    baseFormVersion: number,
  ): UpdateDatabaseFieldsResult {
    if (viewerAccountId === null) return this.databasePermissionDenied();
    const database = this.ownedDatabase(viewerAccountId, databaseId);
    if (database === undefined) return this.databasePermissionDenied();

    const currentVersion = this.databaseFormVersion(database.id);
    if (baseFormVersion !== currentVersion) {
      return immutableCopy({ status: 'conflict', message: DATABASE_FORM_CHANGED_MESSAGE });
    }

    const normalized = fields.map(normalizeField);
    const errors = validateFields(normalized);
    if (errors.length > 0) {
      return immutableCopy({ status: 'validation-failed', errors, message: errors[0].message });
    }

    // 沒有任何變動就不新增版本（與 API 相同）。
    const current = this.databaseCollection(database.id).fields;
    if (JSON.stringify(current) === JSON.stringify(normalized)) {
      return this.applyScenario({ formVersion: currentVersion, fields: normalized });
    }

    const record: StoredDatabaseFields = {
      version: 1,
      savedAt: this.now().toISOString(),
      formVersion: currentVersion + 1,
      fields: normalized,
    };
    this.storage.setItem(DATABASE_FIELDS_KEY_PREFIX + database.id, JSON.stringify(record));

    return this.applyScenario({ formVersion: currentVersion + 1, fields: normalized });
  }

  updateDatabaseAccess(
    viewerAccountId: AccountId,
    databaseId: DatabaseId,
    dataManagerAccountIds: readonly AccountId[],
  ): UpdateDatabaseAccessResult {
    // 只有擁有者可以指定；不存在與無權限回傳同一句話。
    const database = this.ownedDatabase(viewerAccountId, databaseId);
    if (database === undefined) return this.databasePermissionDenied();

    const normalized = normalizeDataManagers(this.accounts(), dataManagerAccountIds);
    if (normalized === null) {
      return immutableCopy({
        status: 'validation-failed',
        message: '有不認得的帳號，這次指定沒有儲存。',
      });
    }

    const record: StoredDatabaseAccess = {
      version: 1,
      savedAt: this.now().toISOString(),
      dataManagerAccountIds: normalized,
    };
    this.storage.setItem(DATABASE_ACCESS_KEY_PREFIX + database.id, JSON.stringify(record));

    return this.applyScenario(this.databaseAccessView(database, viewerAccountId));
  }

  previewDatabaseEntry(
    databaseId: DatabaseId,
    answers: DatabaseTrialAnswers,
  ): Observable<PreviewDatabaseEntryResult> {
    return defer(() => of(this.runDatabaseTrial(this.viewer(), databaseId, answers)));
  }

  private runDatabaseTrial(
    viewerAccountId: AccountId | null,
    databaseId: DatabaseId,
    answers: DatabaseTrialAnswers,
  ): PreviewDatabaseEntryResult {
    if (viewerAccountId === null) return this.databasePermissionDenied();
    const database = this.ownedDatabase(viewerAccountId, databaseId);
    if (database === undefined) return this.databasePermissionDenied();

    const outcome = evaluateTrial(this.databaseCollection(database.id).fields, answers);
    if ('errors' in outcome) {
      return immutableCopy({
        status: 'validation-failed',
        errors: outcome.errors,
        message: outcome.errors[0].message,
      });
    }

    return this.applyScenario({
      saved: false as const,
      formVersion: this.databaseFormVersion(database.id),
      entries: outcome.entries,
    });
  }

  getDatabaseTracking(
    viewerAccountId: AccountId,
    databaseId: string,
  ): ReturnType<DemoRepository['getDatabaseTracking']> {
    const database = this.ownedDatabase(viewerAccountId, databaseId);
    if (database === undefined) return this.databasePermissionDenied();
    if (!this.canReadRecords(viewerAccountId, database.id)) {
      return this.permissionDenied('database-records', DATABASE_RECORDS_DENIED_MESSAGE);
    }

    const records = this.consentedRecords(database.id);
    const withdrawn = this.withdrawnRecords(database.id);
    const subjects = [...this.seed.trackedSubjects, ...this.chatSubjects()]
      .filter((subject) => subject.databaseId === database.id)
      .map((subject) => {
        const chronological = records.filter((record) => record.subjectId === subject.id);
        return {
          id: subject.id,
          displayName: subject.displayName,
          records: [...chronological].reverse().map(toRecordView),
          withdrawals: withdrawn
            .filter((record) => record.subjectId === subject.id)
            .map(toWithdrawnRecordView),
          comparison: compareRecords(chronological),
        };
      })
      // 全部撤回的追蹤對象仍然留著：只剩軌跡，但不能無聲消失。
      .filter((subject) => subject.records.length > 0 || subject.withdrawals.length > 0);
    const tracking: DatabaseTrackingView = {
      databaseId: database.id,
      subjects,
      periodicReports: this.periodicReports(database.id, records, subjects),
    };

    return this.applyScenario(tracking);
  }

  /**
   * 非同步契約（issue #79）：目前帳號由 `this.viewer()` 推導，不再由呼叫端傳入。
   * 沒有登入時視為沒有使用權限，回傳與「助理不存在」相同的 `assistant-use`。
   */
  listChatThreads(assistantId: string): Observable<RepositoryView<ChatThreadListView>> {
    return defer(() => {
      const viewerAccountId = this.viewer();
      if (viewerAccountId === null) return of(this.assistantUsePermissionDenied());
      return of(this.listChatThreadsSync(viewerAccountId, assistantId));
    });
  }

  private listChatThreadsSync(
    viewerAccountId: AccountId,
    assistantId: string,
  ): RepositoryView<ChatThreadListView> {
    const assistant = this.usableAssistant(viewerAccountId, assistantId);
    if (assistant === undefined) return this.assistantUsePermissionDenied();

    return this.applyScenario(this.toThreadListView(viewerAccountId, assistant));
  }

  createChatThread(assistantId: string): Observable<RepositoryView<AssistantChatView>> {
    return defer(() => {
      const viewerAccountId = this.viewer();
      if (viewerAccountId === null) return of(this.assistantUsePermissionDenied());
      return of(this.createChatThreadSync(viewerAccountId, assistantId));
    });
  }

  private createChatThreadSync(
    viewerAccountId: AccountId,
    assistantId: string,
  ): RepositoryView<AssistantChatView> {
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
    assistantId: string,
    threadId: string,
    title: string,
  ): Observable<RenameChatThreadResult> {
    return defer(() => {
      const viewerAccountId = this.viewer();
      if (viewerAccountId === null) return of(this.assistantUsePermissionDenied());
      return of(this.renameChatThreadSync(viewerAccountId, assistantId, threadId, title));
    });
  }

  private renameChatThreadSync(
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
    assistantId: string,
    threadId: string,
  ): Observable<RepositoryView<ChatThreadListView>> {
    return defer(() => {
      const viewerAccountId = this.viewer();
      if (viewerAccountId === null) return of(this.assistantUsePermissionDenied());
      return of(this.deleteChatThreadSync(viewerAccountId, assistantId, threadId));
    });
  }

  private deleteChatThreadSync(
    viewerAccountId: AccountId,
    assistantId: string,
    threadId: string,
  ): RepositoryView<ChatThreadListView> {
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

  /**
   * 跨助理最近 10 個對話串（issue #79）：只列出目前帳號可使用、且保存對話的助理，
   * 依最後活動時間由新到舊。沒有登入時回傳空清單，讓側欄靜靜不顯示，不當成錯誤。
   */
  listRecentChatThreads(): Observable<RepositoryView<readonly RecentConversationView[]>> {
    return defer(() => {
      const viewerAccountId = this.viewer();
      if (viewerAccountId === null) return of(this.applyScenario([]));

      const usable = this.listUsableAssistantsSync(viewerAccountId);
      const assistants =
        usable.status === 'ready' || usable.status === 'partial-failure' ? usable.data : [];
      const recent = assistants
        .flatMap((assistant) => {
          const result = this.listChatThreadsSync(viewerAccountId, assistant.id);
          if (
            (result.status !== 'ready' && result.status !== 'partial-failure') ||
            result.data.historyMode !== 'saved'
          ) {
            return [];
          }
          return result.data.threads.map(
            (thread): RecentConversationView => ({
              assistantId: assistant.id,
              assistantName: assistant.name,
              threadId: thread.id,
              title: thread.title,
              messageCount: thread.messageCount,
              updatedAt: thread.updatedAt,
            }),
          );
        })
        .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))
        .slice(0, 10);

      return of(this.applyScenario(recent));
    });
  }

  /**
   * 非同步契約（issue #79）：目前發起者由 `this.chatViewer()` 推導（Demo 帳號優先，
   * 其次是這個分頁的匿名訪客），不再由呼叫端傳入。
   */
  getAssistantChat(
    assistantId: string,
    threadId?: string,
  ): Observable<RepositoryView<AssistantChatView>> {
    return defer(() => {
      const viewerId = this.chatViewer();
      if (viewerId === null) return of(this.assistantUsePermissionDenied());
      return of(this.getAssistantChatSync(viewerId, assistantId, threadId));
    });
  }

  private getAssistantChatSync(
    viewerId: ChatViewerId,
    assistantId: string,
    threadId?: string,
  ): RepositoryView<AssistantChatView> {
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
        reply: this.scenarioChatReply(viewerId, assistant, question),
        createdAt,
      },
    ];

    return this.applyScenario(this.writeChatMessages(viewerId, assistant, target, next));
  }

  discardChatReply(viewerId: ChatViewerId, assistantId: string, messageId: string, threadId?: string): void {
    const assistant = this.chatAssistant(viewerId, assistantId);
    if (assistant === undefined) return;
    const target = this.resolveChatTarget(viewerId, assistant, threadId);
    if (target === undefined) return;

    const messages = this.targetMessages(viewerId, assistant, target);
    if (!messages.some((message) => message.id === messageId && message.author === 'assistant')) return;
    this.writeChatMessages(
      viewerId,
      assistant,
      target,
      messages.filter((message) => message.id !== messageId),
    );
  }

  /**
   * `?demoScenario=answer-rejected`（issue #80）：模擬串流完的回答沒有通過引用驗證，
   * 文字問題一律改回「查無資料」；表單流程照常，才不會卡住其他 Demo。
   */
  private scenarioChatReply(
    viewerId: ChatViewerId,
    assistant: AssistantConfigurationView,
    question: string,
  ): ChatReplyView {
    const reply = this.resolveChatReply(viewerId, assistant, question);
    if (this.scenario !== 'answer-rejected' || reply.kind === 'form-request') return reply;
    return this.noResultReply(viewerId, assistant);
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
          text: `已送出。資料只會交給 ${form.consent.recipient}，你可以在這張收據上撤回。`,
          recipient: form.consent.recipient,
          recordId: record.id,
          entries: outcome.entries,
          // 讀取時一律以 `chatRecords()` 重新判定，這裡只是寫入當下的狀態。
          withdrawal: this.withdrawalView(viewerId, record),
        },
      },
    ];

    return this.applyScenario(
      this.writeChatMessages(viewerId, assistant, chatTarget, next),
    );
  }

  withdrawChatSubmission(
    viewerId: ChatViewerId,
    assistantId: string,
    recordId: string,
    threadId?: string,
  ): WithdrawChatSubmissionResult {
    const assistant = this.chatAssistant(viewerId, assistantId);
    if (assistant === undefined) return this.chatAssistantPermissionDenied(viewerId);

    const target = this.resolveChatTarget(viewerId, assistant, threadId);
    if (target === undefined) return this.chatThreadPermissionDenied();

    const records = this.chatRecords();
    const record = records.find((candidate) => candidate.id === recordId);
    // 只有提交者本人能撤回：資料管理者與其他提交者都會得到同一則訊息。
    if (record === undefined || record.subjectId !== this.subjectIdOf(viewerId)) {
      return this.permissionDenied(
        'submission-withdrawal',
        '找不到這筆紀錄，或你沒有撤回它的權限。',
      );
    }
    if (record.consentStatus === 'withdrawn') {
      return immutableCopy({ status: 'validation-failed', message: '這筆資料已經撤回過了。' });
    }

    // 撤回＝內容真的從收集紀錄移除，只留下不含內容的軌跡。
    const withdrawn: DatabaseRecordFixture = {
      id: record.id,
      databaseId: record.databaseId,
      subjectId: record.subjectId,
      recordedAt: record.recordedAt,
      source: record.source,
      consentStatus: 'withdrawn',
      withdrawnAt: this.now().toISOString(),
      values: [],
    };
    this.storage.setItem(
      CHAT_RECORDS_KEY,
      JSON.stringify(records.map((candidate) => (candidate.id === record.id ? withdrawn : candidate))),
    );

    return this.applyScenario(
      this.toChatView(
        viewerId,
        assistant,
        this.targetMessages(viewerId, assistant, target),
        target?.id ?? null,
        target?.title ?? CHAT_DEFAULT_THREAD_TITLE,
      ),
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
    return this.accounts().reduce(
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
      const account = this.accounts().find((candidate) => `subject-${candidate.id}` === record.subjectId);
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
        .filter((fixture) => this.fixtureReply(viewerId, assistant, fixture) !== null)
        .map((fixture) => ({ id: fixture.id, text: fixture.prompt })),
      messages: this.resolveReceipts(viewerId, messages),
    };
  }

  /**
   * 收據上的撤回狀態不保存在訊息裡，每次讀取都以目前的收集紀錄重新判定，
   * 這樣在別處撤回後，這段對話的收據也會立刻反映。
   */
  private resolveReceipts(
    viewerId: ChatViewerId,
    messages: readonly ChatMessageView[],
  ): readonly ChatMessageView[] {
    const hasReceipt = messages.some(
      (message) => message.author === 'assistant' && message.reply.kind === 'submission-receipt',
    );
    if (!hasReceipt) return messages;

    const records = this.chatRecords();
    return messages.map((message): ChatMessageView => {
      if (message.author !== 'assistant' || message.reply.kind !== 'submission-receipt') return message;
      const reply = message.reply;
      // 舊版收據沒有 recordId；別人的紀錄也一律當作指認不到，不洩漏它存在。
      const recordId = reply.recordId ?? null;
      const record =
        recordId === null
          ? undefined
          : records.find(
              (candidate) =>
                candidate.id === recordId && candidate.subjectId === this.subjectIdOf(viewerId),
            );
      return {
        ...message,
        reply: { ...reply, recordId, withdrawal: this.withdrawalView(viewerId, record) },
      };
    });
  }

  /** 提交者在收集紀錄中的追蹤對象 id；未登入訪客用分頁內的訪客 id，不冒認任何帳號。 */
  private subjectIdOf(viewerId: ChatViewerId): TrackedSubjectId {
    return `subject-${viewerId}`;
  }

  /** 同意畫面與收據共用的撤回說明；未登入訪客的版本會多說分頁結束後就指認不到。 */
  private withdrawalNotice(viewerId: ChatViewerId): string {
    return isVisitorId(viewerId) ? CHAT_VISITOR_WITHDRAWAL_NOTICE : CHAT_WITHDRAWAL_NOTICE;
  }

  private withdrawalView(
    viewerId: ChatViewerId,
    record: DatabaseRecordFixture | undefined,
  ): SubmissionWithdrawalView {
    if (record === undefined) {
      return {
        status: 'unavailable',
        withdrawnDateLabel: '',
        notice: CHAT_WITHDRAWAL_UNAVAILABLE_NOTICE,
      };
    }
    if (record.consentStatus === 'withdrawn') {
      return {
        status: 'withdrawn',
        withdrawnDateLabel: (record.withdrawnAt ?? '').slice(0, 10),
        notice: CHAT_WITHDRAWN_NOTICE,
      };
    }

    return { status: 'available', withdrawnDateLabel: '', notice: this.withdrawalNotice(viewerId) };
  }

  /** 依關鍵字對應預先準備的回覆；對應不到或來源未連接時回覆查無資料與下一步。 */
  private resolveChatReply(
    viewerId: ChatViewerId,
    assistant: AssistantConfigurationView,
    question: string,
  ): ChatReplyView {
    const normalized = question.replace(/\s+/g, '');
    for (const fixture of this.seed.chatResponses) {
      const matches = fixture.matchers.some((group) => group.every((keyword) => normalized.includes(keyword)));
      if (!matches) continue;
      const reply = this.fixtureReply(viewerId, assistant, fixture);
      if (reply !== null) return reply;
    }

    return this.noResultReply(viewerId, assistant);
  }

  private noResultReply(viewerId: ChatViewerId, assistant: AssistantConfigurationView): ChatReplyView {
    const formAvailable = this.seed.chatResponses.some(
      (fixture) =>
        fixture.answer.kind === 'form-request' &&
        this.fixtureReply(viewerId, assistant, fixture) !== null,
    );
    return {
      kind: 'no-result',
      // 規則裡的「找不到資料時怎麼回覆」就是這一句；種子助理的預設值即 CHAT_NO_RESULT_TEXT。
      text: this.assistantRules(assistant).refusalMessage,
      nextSteps: [
        '換個說法再問一次，或點選建議問題。',
        ...(formAvailable ? ['需要專人協助時，輸入「回報訂單問題」留下資料，客服會回覆你。'] : []),
        '急件請直接聯絡門市客服（週一至週五 09:00–18:00）。',
      ],
    };
  }

  private fixtureReply(
    viewerId: ChatViewerId,
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
        // 先用引用來源判斷助理有沒有這份組織資料，再決定要不要把出處顯示出來：
        // 規則關掉的是「出處」，不是「這題有沒有答案」。
        if (citations.length === 0) return null;
        return this.showsCitations(assistant)
          ? { kind: 'company-data', text: answer.text, citations, citationNotice: null }
          : {
              kind: 'company-data',
              text: answer.text,
              citations: [],
              citationNotice: CHAT_CITATIONS_OFF_NOTICE,
            };
      }
      case 'general-knowledge': {
        return this.allowsGeneralKnowledge(assistant)
          ? { kind: 'general-knowledge', text: answer.text, notice: CHAT_GENERAL_KNOWLEDGE_NOTICE }
          : null;
      }
      case 'form-request': {
        const form = this.chatForm(viewerId, assistant, answer.databaseId);
        return form === null ? null : { kind: 'form-request', text: answer.text, form };
      }
    }
  }

  /** 助理有連接該資料庫時才提供表單；接收者與可查看者皆取自資料庫設定。 */
  private chatForm(
    viewerId: ChatViewerId,
    assistant: AssistantConfigurationView,
    databaseId: DatabaseId,
  ): ChatFormView | null {
    if (!assistant.databaseIds.includes(databaseId)) return null;
    const database = this.databases().find((candidate) => candidate.id === databaseId);
    if (database === undefined) return null;

    const collection = this.databaseCollection(database.id);
    const nameOf = (id: AccountId) =>
      this.accounts().find((account) => account.id === id)?.displayName ?? '已停用的帳號';

    return {
      id: database.id,
      title: database.name,
      fields: collection.fields,
      consent: {
        recipient: `${nameOf(database.ownerAccountId)}（${database.name}）`,
        purpose: collection.purpose,
        viewers: collection.dataManagerAccountIds.map(nameOf),
        sensitiveNotice: CHAT_SENSITIVE_NOTICE,
        withdrawalNotice: this.withdrawalNotice(viewerId),
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
    const form = offered ? this.chatForm(viewerId, assistant, formId) : null;
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
    const fields = this.storedDatabaseFields(databaseId)?.fields ?? base.fields;
    const access = this.storedDatabaseAccess(databaseId);
    return {
      ...base,
      fields,
      dataManagerAccountIds: access?.dataManagerAccountIds ?? base.dataManagerAccountIds,
    };
  }

  private storedDatabaseAccess(databaseId: DatabaseId): StoredDatabaseAccess | null {
    const stored = parseJson(this.storage.getItem(DATABASE_ACCESS_KEY_PREFIX + databaseId));
    return isStoredDatabaseAccess(stored) ? stored : null;
  }

  /** 收集紀錄的唯一判斷點：帳號層級權限 ＋ 這個資料庫的資料管理者指定。 */
  private canReadRecords(viewerAccountId: AccountId, databaseId: DatabaseId): boolean {
    return canReadConsentedRecords(
      this.accounts().find((account) => account.id === viewerAccountId),
      this.databaseCollection(databaseId).dataManagerAccountIds,
    );
  }

  private databaseAccountRef(id: AccountId): { readonly id: AccountId; readonly displayName: string } {
    return {
      id,
      displayName: this.accounts().find((account) => account.id === id)?.displayName ?? '已停用的帳號',
    };
  }

  private databaseAccessView(
    database: DatabaseView,
    viewerAccountId: AccountId,
  ): DatabaseAccessView {
    const accounts = this.accounts();
    const displayName = (id: AccountId) => this.databaseAccountRef(id);
    const managers = this.databaseCollection(database.id).dataManagerAccountIds;

    return {
      owner: displayName(database.ownerAccountId),
      dataManagers: managers.map(displayName),
      viewerIsDataManager: managers.includes(viewerAccountId),
      viewerCanReadRecords: this.canReadRecords(viewerAccountId, database.id),
      viewerCanManageAccess: database.ownerAccountId === viewerAccountId,
      candidates: databaseAccessCandidates(accounts),
      savedAt: this.storedDatabaseAccess(database.id)?.savedAt ?? null,
    };
  }

  private databaseFormVersion(databaseId: DatabaseId): number {
    return this.storedDatabaseFields(databaseId)?.formVersion ?? 1;
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

  /** 已撤回同意的紀錄，由新到舊；內容已移除，只用來顯示軌跡。 */
  private withdrawnRecords(databaseId: DatabaseId): readonly DatabaseRecordFixture[] {
    return [...this.seed.databaseRecords, ...this.chatRecords()]
      .filter((record) => record.databaseId === databaseId && record.consentStatus === 'withdrawn')
      .slice()
      .sort((a, b) => b.recordedAt.localeCompare(a.recordedAt));
  }

  private toDatabaseSummary(database: DatabaseView, viewerAccountId: AccountId): DatabaseSummaryView {
    const collection = this.databaseCollection(database.id);
    // 看不看得到數量，跟看不看得到紀錄用同一個判斷點。
    const isDataManager = this.canReadRecords(viewerAccountId, database.id);
    const records = this.consentedRecords(database.id);
    const savedAt = this.storedDatabaseFields(database.id)?.savedAt ?? '';

    return {
      id: database.id,
      name: database.name,
      purpose: collection.purpose,
      owner: this.databaseAccountRef(database.ownerAccountId),
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
    return this.hasPermission(viewerAccountId, 'manage-data-sources');
  }

  private createDatabasePermissionDenied(): PermissionDeniedRepositoryView {
    return this.permissionDenied('database', '只有可管理資料來源的帳號可以建立資料庫。');
  }

  /** 不存在與無權限回傳相同結果，避免透過差異推測資源是否存在。 */
  private databasePermissionDenied(): PermissionDeniedRepositoryView {
    return this.permissionDenied('database', '你沒有這個資料庫的存取權限，或它已不存在。');
  }

  private ownedKnowledgeBase(
    viewerAccountId: AccountId | null,
    knowledgeBaseId: string,
  ): KnowledgeBaseView | undefined {
    if (viewerAccountId === null) return undefined;
    return this.knowledgeBases().find(
      (knowledgeBase) =>
        knowledgeBase.id === knowledgeBaseId &&
        knowledgeBase.ownerAccountId === viewerAccountId,
    );
  }

  /** seed 加上這台瀏覽器建立的知識庫，扣掉已刪除的。 */
  private knowledgeBases(): readonly KnowledgeBaseView[] {
    const deleted = new Set(this.deletedKnowledgeBaseIds());
    return this.allKnowledgeBases().filter((knowledgeBase) => !deleted.has(knowledgeBase.id));
  }

  /** 含已刪除的（產生新 id 時避免重複使用）。 */
  private allKnowledgeBases(): readonly KnowledgeBaseView[] {
    return [...this.seed.knowledgeBases, ...this.createdKnowledgeBases()];
  }

  private createdKnowledgeBases(): readonly KnowledgeBaseView[] {
    const stored = parseJson(this.storage.getItem(CREATED_KNOWLEDGE_BASES_KEY));
    return Array.isArray(stored) ? stored.filter(isKnowledgeBaseView) : [];
  }

  private deletedKnowledgeBaseIds(): readonly string[] {
    const stored = parseJson(this.storage.getItem(DELETED_KNOWLEDGE_BASES_KEY));
    return Array.isArray(stored) ? stored.filter((id): id is string => typeof id === 'string') : [];
  }

  private knowledgeRecord(knowledgeBaseId: KnowledgeBaseId): StoredKnowledgeRecord {
    const stored = parseJson(this.storage.getItem(KNOWLEDGE_KEY_PREFIX + knowledgeBaseId));
    if (isStoredKnowledgeRecord(stored)) return stored;

    return {
      version: 1,
      documents: this.seed.knowledgeDocuments[knowledgeBaseId] ?? [],
      sharing: this.seed.knowledgeSharing[knowledgeBaseId] ?? {
        scope: 'private',
        sharedWithAccountIds: [],
        allowOriginalDownload: false,
      },
    };
  }

  /**
   * 畫面看到的文件：補上版本 id，依經過時間算出重新處理中文件的狀態，並疊上版本確認狀態
   * （issue #47，M2 Slice 13：`latestVersionNumber`／`latestVersionState`／
   * `effectiveVersionNumber`／`disabled`／`inEffect`）。
   */
  private knowledgeDocuments(knowledgeBaseId: KnowledgeBaseId): readonly KnowledgeDocumentView[] {
    const now = this.now();
    const record = this.knowledgeRecord(knowledgeBaseId);
    return record.documents.map(
      ({ processingStartedAt, latestVersionId, ...document }): KnowledgeDocumentView => {
        const withStatus = {
          ...document,
          ...(processingStartedAt === undefined
            ? {}
            : mockKnowledgeProcessingState(processingStartedAt, now)),
        };
        const review = record.reviews?.[document.id] ?? defaultKnowledgeReview(document);
        const reviewFields = deriveKnowledgeReviewFields(review, now);
        return {
          ...withStatus,
          latestVersionId: latestVersionId ?? `${document.id}:v${review.versionNumber}`,
          ...reviewFields,
        };
      },
    );
  }

  private saveKnowledgeRecord(
    knowledgeBaseId: KnowledgeBaseId,
    record: StoredKnowledgeRecord,
  ): void {
    this.storage.setItem(KNOWLEDGE_KEY_PREFIX + knowledgeBaseId, JSON.stringify(record));
  }

  /**
   * 目前帳號自己的助理中，連接了這個知識庫的那些。助理在 M3 之前仍是 mock 資料，
   * 所以 API 模式（`HybridDemoRepository`）也用這裡補上後端沒有的「已連接助理」。
   */
  protected connectedKnowledgeAssistants(
    viewerAccountId: AccountId,
    knowledgeBaseId: string,
  ): readonly KnowledgeConnectedAssistantView[] {
    return this.assistants()
      .filter(
        (assistant) =>
          assistant.ownerAccountId === viewerAccountId &&
          assistant.knowledgeBaseIds.includes(knowledgeBaseId),
      )
      .map(({ id, name, status }) => ({ id, name, status }));
  }

  private toKnowledgeSummary(
    knowledgeBase: KnowledgeBaseView,
    viewerAccountId: AccountId,
  ): KnowledgeBaseSummaryView {
    const documents = this.knowledgeDocuments(knowledgeBase.id);
    const { sharing } = this.knowledgeRecord(knowledgeBase.id);
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
      connectedAssistantNames: this.connectedKnowledgeAssistants(viewerAccountId, knowledgeBase.id).map(
        (assistant) => assistant.name,
      ),
      updatedAt,
      viewerCanManage: knowledgeBase.ownerAccountId === viewerAccountId,
      inEffectCount: documents.filter((document) => document.inEffect === true).length,
      awaitingApprovalCount: documents.filter(
        (document) => document.latestVersionState === 'pending-review' || document.latestVersionState === 'scheduled',
      ).length,
      disabledCount: documents.filter((document) => document.disabled === true).length,
    };
  }

  /** 不存在與無權限回傳相同結果，避免透過差異推測資源是否存在。 */
  private knowledgePermissionDenied(): PermissionDeniedRepositoryView {
    return this.permissionDenied('knowledge-base', KNOWLEDGE_PERMISSION_DENIED_MESSAGE);
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
      // 種子助理沒有存過設定，預設值來自 seed，讓未編輯過的助理也看得出差異。
      ...(this.seed.assistantRuleDefaults[assistant.id] ?? {}),
    };
  }

  /** 目前生效的回答規則：存過設定就以設定為準，否則用助理的預設規則。 */
  private assistantRules(assistant: AssistantConfigurationView): AssistantAnswerRules {
    return this.storedAssistantSettings(assistant.id)?.rules ?? this.defaultRules(assistant);
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
    return this.assistantRules(assistant).knowledgeScope === 'allow-general-knowledge';
  }

  /** 「顯示引用出處」關掉時，組織資料的回答仍然標示成組織資料，只是不附原文片段。 */
  private showsCitations(assistant: AssistantConfigurationView): boolean {
    return this.assistantRules(assistant).showCitations;
  }

  /**
   * 對這個資料庫開啟「定期回報」的助理。排程由最近一次已同意的紀錄推算，
   * 摘要沿用 `compareRecords` 算好的字串——這裡不重新計算任何數字。
   */
  private periodicReports(
    databaseId: DatabaseId,
    chronological: readonly DatabaseRecordFixture[],
    subjects: readonly TrackedSubjectView[],
  ): readonly PeriodicReportView[] {
    const latest = chronological.at(-1);
    const anchorLabel = (latest?.recordedAt ?? this.now().toISOString()).slice(0, 10);

    return this.assistants().flatMap((assistant) => {
      const rules = this.assistantRules(assistant);
      if (rules.periodicReport === 'off' || rules.dataWriteDatabaseId !== databaseId) return [];
      return [
        buildPeriodicReport({
          assistantName: assistant.name,
          schedule: rules.periodicReport,
          purpose: rules.dataWritePurpose,
          anchorLabel,
          subjects,
        }),
      ];
    });
  }

  private assistants(): readonly AssistantConfigurationView[] {
    const deleted = new Set<string>(this.deletedAssistantIds());
    return [...this.seed.assistants, ...this.createdAssistants()]
      .filter((assistant) => !deleted.has(assistant.id))
      .map((assistant) => ({
        ...this.withSavedSettings(assistant),
        acceptanceStatus: this.acceptanceStatuses.get(assistant.id) ?? assistant.acceptanceStatus ?? 'not-accepted',
      }));
  }

  private deletedAssistantIds(): readonly string[] {
    const stored = parseJson(this.storage.getItem(DELETED_ASSISTANTS_KEY));
    return Array.isArray(stored) ? stored.filter((id): id is string => typeof id === 'string') : [];
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

  /**
   * 精靈建立助理與設定頁連接來源時，用來驗證「這個來源是否可連接」的清單。
   * `HybridDemoRepository` 會覆寫：API 模式的知識庫是真實 GUID，不在 mock 的種子資料裡。
   */
  protected connectableSources(
    viewerAccountId: AccountId,
  ): readonly ConnectableSourceView[] {
    if (!this.canManageAssistants(viewerAccountId)) return [];

    const knowledgeBases = this.knowledgeBases()
      .filter((knowledgeBase) => knowledgeBase.ownerAccountId === viewerAccountId)
      .map((knowledgeBase): ConnectableSourceView => {
        const summary = this.toKnowledgeSummary(knowledgeBase, viewerAccountId);
        return {
          id: knowledgeBase.id,
          type: 'knowledge-base',
          name: knowledgeBase.name,
          summary: `${summary.documentCount} 份文件、${summary.faqCount} 則 FAQ`,
          permission: 'owner',
          status: connectableKnowledgeStatus(summary.statusCounts, summary.documentCount + summary.faqCount),
          updatedAt: knowledgeBase.lastSyncedAt,
        };
      });

    return [...knowledgeBases, ...this.connectableDatabaseSources(viewerAccountId)];
  }

  /**
   * 資料庫可連接清單；沒有 `manage-assistants` 權限時回傳空陣列。獨立成 protected 方法，
   * 讓 `HybridDemoRepository` 在知識庫已改真實資料後，仍能補上仍是 mock 的資料庫（M2 範圍外）。
   */
  protected connectableDatabaseSources(
    viewerAccountId: AccountId,
  ): readonly ConnectableSourceView[] {
    if (!this.canManageAssistants(viewerAccountId)) return [];

    return this.databases()
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
  }

  protected canManageAssistants(viewerAccountId: AccountId): boolean {
    return this.hasPermission(viewerAccountId, 'manage-assistants');
  }

  private hasPermission(
    viewerAccountId: AccountId,
    permission: AccountPermission,
  ): boolean {
    return this.accounts().some(
      (account) =>
        account.id === viewerAccountId && account.permissions.includes(permission),
    );
  }

  /**
   * 目前生效的帳號清單：seed 的三個 Demo 身分，加上這台瀏覽器新增的成員
   * （issue #52），疊上團隊設定改過的權限。
   * **所有權限判斷都必須經過這裡**，否則改了團隊設定畫面不會跟著變。
   */
  private accounts(): readonly AccountView[] {
    // API 模式只信任 API 給的權限；這台瀏覽器之前在 mock 模式改過的團隊設定一律不套用。
    const overrides = this.accountsSource?.() ?? this.storedTeamPermissions()?.members;
    const created: readonly AccountView[] = this.createdMembers().map((member) => ({
      id: member.id,
      displayName: member.displayName,
      role: member.role,
      permissions: member.permissions,
    }));

    if (overrides === undefined) return [...this.seed.accounts, ...created];

    const withOverrides = (account: AccountView): AccountView => {
      const permissions = overrides[account.id];
      return permissions === undefined ? account : { ...account, permissions };
    };

    return [...this.seed.accounts.map(withOverrides), ...created.map(withOverrides)];
  }

  private storedTeamPermissions(): StoredTeamPermissions | null {
    return normalizeStoredTeamPermissions(parseJson(this.storage.getItem(TEAM_PERMISSIONS_KEY)));
  }

  private teamView(viewerAccountId: AccountId): TeamView {
    return {
      members: this.accounts().map(
        (account): TeamMemberView => ({
          id: account.id,
          displayName: account.displayName,
          role: account.role,
          roleLabel: ACCOUNT_ROLE_LABELS[account.role],
          roleDescription: ACCOUNT_ROLE_DESCRIPTIONS[account.role],
          permissions: account.permissions,
          isViewer: account.id === viewerAccountId,
          lockedPermissions: lockedPermissionsFor(account, viewerAccountId),
        }),
      ),
      permissions: ACCOUNT_PERMISSIONS,
      savedAt: this.storedTeamPermissions()?.savedAt ?? null,
    };
  }

  /** 不存在的成員與無權限共用同一句話，也不含任何成員名稱。 */
  private teamPermissionDenied(): PermissionDeniedRepositoryView {
    return this.permissionDenied('team', TEAM_PERMISSION_DENIED_MESSAGE);
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

  /**
   * 平台內誰能開啟這個助理：交給 `canOpenInPlatform()` 判斷，
   * 也就是「使用對象決定哪一種人、平台內分享的勾選清單決定哪些帳號、擁有者永遠開得了」。
   * 未登入訪客走的是另一條路（`anonymouslyOpenAssistant()`，只看有沒有對外發布）。
   */
  private canUseAssistant(
    assistant: AssistantConfigurationView,
    viewerAccountId: AccountId,
  ): boolean {
    if (assistant.ownerAccountId === viewerAccountId) return true;

    const viewer = this.accounts().find((account) => account.id === viewerAccountId);
    if (viewer === undefined) return false;

    return canOpenInPlatform(assistant, this.publishingRecord(assistant), viewer);
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

  /**
   * 發布設定的唯一入口：擁有者 ＋ `manage-publishing`（`canManagePublishing()`）。
   * 不存在、不是擁有者、沒有權限三種情況都回傳 undefined，呼叫端回覆同一句話。
   */
  private publishingTarget(
    viewerAccountId: AccountId,
    assistantId: string,
  ): AssistantConfigurationView | undefined {
    const assistant = this.assistants().find((candidate) => candidate.id === assistantId);
    if (assistant === undefined) return undefined;
    return this.canManagePublishingFor(viewerAccountId, assistant) ? assistant : undefined;
  }

  private canManagePublishingFor(
    viewerAccountId: AccountId,
    assistant: AssistantConfigurationView,
  ): boolean {
    const viewer = this.accounts().find((account) => account.id === viewerAccountId);
    return viewer !== undefined && canManagePublishing(assistant, viewer);
  }

  private publishingPermissionDenied(): PermissionDeniedRepositoryView {
    return this.permissionDenied('publishing', '你沒有這個助理的發布設定權限，或它已不存在。');
  }

  private channelOverview(viewerAccountId: AccountId): readonly AssistantChannelsView[] {
    return this.assistants()
      .filter((assistant) => this.canManagePublishingFor(viewerAccountId, assistant))
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

  private toPublishingView(assistant: AssistantConfigurationView): ConfigurableAssistantPublishingView {
    return toAssistantPublishingView(
      assistant,
      this.publishingRecord(assistant),
      this.accounts(),
      this.scenario === 'disconnected-channel',
    );
  }
}
