import {
  HttpErrorResponse,
  HttpEventType,
  type HttpClient,
  type HttpProgressEvent,
  type HttpResponse,
} from '@angular/common/http';
import { catchError, filter, forkJoin, map, of, switchMap, throwError, type Observable } from 'rxjs';
import type { components } from '../api/api-schema';
import type { AccountId, AccountPermission, AccountRole } from '../domain/account.model';
import type { AssistantTestCaseView, AssistantTestRunView, AssistantTestRunDetailView, AssistantTestCaseInput, AssistantTestCasePatch, AssistantTestCaseImportEntry, AssistantTestCaseExportEntry } from '../domain/assistant-acceptance.model';
import type {
  AssistantConfigurationView,
  AssistantId,
  AssistantPermission,
  AssistantSourceReference,
  AssistantSummaryView,
} from '../domain/assistant.model';
import {
  ASSISTANT_DRAFT_FIELD_STEPS,
  createEmptyAssistantDraft,
  type AssistantDraft,
  type AssistantDraftField,
  type AssistantDraftFieldError,
  type ConnectableSourceView,
  type NamedAssistantDraftView,
  type TrialAnswerCitationView,
  type TrialAnswerPassageView,
  type TrialAnswerRequest,
  type TrialAnswerResultView,
  type TrialAnswerView,
} from '../domain/assistant-draft.model';
import type {
  AssistantSettingsField,
  AssistantSettingsFieldError,
  AssistantSettingsPatch,
  AssistantSettingsView,
} from '../domain/assistant-settings.model';
import {
  EXTERNAL_PUBLISHING_NOT_AVAILABLE_MESSAGE,
  PUBLISHING_CHANNEL_NAMES,
  type AssistantChannelsView,
  type AssistantPublishingView,
  type PlatformSharingView,
  type PublishingChannelStatus,
  type PublishingChannelType,
  type PublishingChannelView,
  type UnavailablePublishingChannelView,
} from '../domain/publishing.model';
import {
  ACCOUNT_PERMISSIONS,
  ACCOUNT_ROLE_DESCRIPTIONS,
  ACCOUNT_ROLE_LABELS,
  normalizeMemberPermissions,
  type TeamMemberView,
  type TeamView,
} from '../domain/team.model';
import {
  isKnowledgeRetrievalUnavailableReason,
  isKnowledgeUploadRejectionReason,
  type CreateKnowledgeBaseInput,
  type KnowledgeAccountRefView,
  type KnowledgeActivityAction,
  type KnowledgeActivityView,
  type KnowledgeBaseDetailView,
  type KnowledgeBaseSummaryView,
  type KnowledgeChunkView,
  type KnowledgeDocumentDetailView,
  type KnowledgeDocumentView,
  type KnowledgeExtractedUnitView,
  type KnowledgeRetrievalPassageView,
  type KnowledgeRetrievalPreviewView,
  type KnowledgeRetrievalUnavailableReason,
  type KnowledgeSharingView,
  type KnowledgeUploadRejectionReason,
  type KnowledgeVersionPreviewView,
  type KnowledgeVersionView,
} from '../domain/knowledge-base.model';
import type { AssistantAnalyticsSummaryView, OperationsSummaryView } from '../domain/operations.model';
import type {
  CreateDatabaseInput,
  DatabaseAccessView,
  DatabaseDetailView,
  DatabaseFieldId,
  DatabaseFieldError,
  DatabaseFieldView,
  DatabaseId,
  DatabaseSubmissionFormView,
  DatabaseSubmissionInput,
  DatabaseSubmissionReceiptView,
  DatabaseSummaryView,
  DatabaseTemplateView,
  DatabaseRecordView,
  DatabaseTrackingView,
  DatabaseTrialAnswers,
  DatabaseUpcomingFeature,
  OwnDatabaseSubmissionView,
  TrackedSubjectView,
  WithdrawnRecordView,
} from '../domain/database.model';
import type { ChatViewerId } from '../domain/account.model';
import type {
  AssistantChatView,
  ChatFormSubmission,
  ChatFormView,
  ChatMessageView,
  ChatReplyView,
  ChatThreadListView,
  ChatThreadSummaryView,
  RecentConversationView,
  SubmissionWithdrawalView,
} from '../domain/conversation.model';
import {
  isRepositoryPermissionDeniedReason,
  type ApproveKnowledgeVersionsResult,
  type CreateAssistantResult,
  type CreateDatabaseResult,
  type DatabaseFieldsValidationFailedView,
  type DatabaseSubmissionConflictView,
  type PreviewDatabaseEntryResult,
  type ReviewDatabaseSubmissionResult,
  type SubmitDatabaseEntryResult,
  type UpdateDatabaseFieldsResult,
  type CreateDatabaseValidationFailedView,
  type CreateKnowledgeBaseResult,
  type UpdateDatabaseAccessResult,
  type CreateMemberInput,
  type CreateMemberResult,
  type DeleteAssistantResult,
  type DeleteKnowledgeResult,
  type DisableKnowledgeDocumentResult,
  type EnableKnowledgeDocumentResult,
  type KnowledgeRetrievalUnavailableView,
  type KnowledgeUploadRejectedView,
  type KnowledgeValidationFailedView,
  type PermissionDeniedRepositoryView,
  type PreviewKnowledgeRetrievalResult,
  type PreviewTrialAnswerResult,
  type RenameChatThreadResult,
  type ReviewChatFormResult,
  type SubmitChatFormResult,
  type WithdrawChatSubmissionResult,
  type RepositoryPermissionDeniedReason,
  type RepositoryView,
  type RetryKnowledgeDocumentResult,
  type UpdateKnowledgeChunkExclusionResult,
  type SaveAssistantDraftResult,
  type UpdateAssistantSettingsResult,
  type UpdatePlatformSharingResult,
  type UpdateKnowledgeSharingResult,
  type UpdateMemberPermissionsResult,
  type UploadKnowledgeDocumentEvent,
} from './demo-repository';
import type { DemoSeed } from './demo-seed';
import { CHAT_WITHDRAWAL_NOTICE, CHAT_WITHDRAWN_NOTICE } from './demo-seed-chat';
import { createMemoryStorage } from './memory-storage';
import {
  DRAFT_REVISION_CONFLICT_MESSAGE,
  KNOWLEDGE_PERMISSION_DENIED_MESSAGE,
  MockDemoRepository,
  normalizeDraftPayload,
  TEAM_PERMISSION_DENIED_MESSAGE,
  type AccountPermissionOverrides,
  type MockDemoRepositoryOptions,
} from './mock-demo-repository';
import { createScopedStorage, type StorageIdentity } from './scoped-storage';

type TeamResponse = components['schemas']['TeamResponse'];
type ApiTeamMember = components['schemas']['TeamMemberResponse'];
type UpdateMemberPermissionsRequest = components['schemas']['UpdateMemberPermissionsRequest'];
type CreateMemberRequest = components['schemas']['CreateMemberRequest'];
type CreateMemberResponse = components['schemas']['CreateMemberResponse'];
type ApiKnowledgeBaseSummary = components['schemas']['KnowledgeBaseSummaryView'];
type ApiKnowledgeBaseDetail = components['schemas']['KnowledgeBaseDetailView'];
type ApiKnowledgeDocument = components['schemas']['KnowledgeDocumentView'];
type ApiKnowledgeSharing = components['schemas']['KnowledgeSharingView'];
type CreateKnowledgeBaseRequest = components['schemas']['CreateKnowledgeBaseRequest'];
type UpdateKnowledgeSharingRequest = components['schemas']['UpdateKnowledgeSharingRequest'];
type ApiKnowledgeDocumentDetail = components['schemas']['KnowledgeDocumentDetailView'];
type ApiKnowledgeVersion = components['schemas']['KnowledgeVersionView'];
type ApiKnowledgeActivity = components['schemas']['KnowledgeActivityView'];
type ApiKnowledgeAccount = components['schemas']['KnowledgeAccountView'];
type ApiKnowledgeVersionPreview = components['schemas']['KnowledgeVersionPreviewView'];
type ApiKnowledgeExtractedUnit = components['schemas']['KnowledgeExtractedUnitView'];
type ApiKnowledgeChunk = components['schemas']['KnowledgeChunkView'];
type ApproveKnowledgeVersionsRequest = components['schemas']['ApproveKnowledgeVersionsRequest'];
type DisableKnowledgeDocumentRequest = components['schemas']['DisableKnowledgeDocumentRequest'];
type UpdateKnowledgeChunkExclusionRequest = components['schemas']['UpdateKnowledgeChunkExclusionRequest'];
type PreviewKnowledgeRetrievalRequest = components['schemas']['PreviewKnowledgeRetrievalRequest'];
type ApiKnowledgeRetrievalPreview = components['schemas']['KnowledgeRetrievalPreviewView'];
type ApiKnowledgeRetrievalPassage = components['schemas']['KnowledgeRetrievalPassageView'];
type ApiChatThreadListView = components['schemas']['ChatThreadListView'];
type ApiChatThreadSummaryView = components['schemas']['ChatThreadSummaryView'];
type ApiAssistantChatView = components['schemas']['AssistantChatView'];
type ApiChatMessageView = components['schemas']['ChatMessageView'];
type ApiChatReplyView = components['schemas']['ChatReplyView'];
type ApiRecentConversationView = components['schemas']['RecentConversationView'];
type RenameChatThreadRequest = components['schemas']['RenameChatThreadRequest'];
type ApiAssistantConfiguration = components['schemas']['AssistantConfigurationView'];
type ApiAssistantSummary = components['schemas']['AssistantSummaryView'];
type ApiAssistantSettings = components['schemas']['AssistantSettingsView'];
type UpdateAssistantSettingsRequest = components['schemas']['UpdateAssistantSettingsRequest'];
type ApiAssistantDraft = components['schemas']['AssistantDraftView'];
type CreateAssistantDraftRequest = components['schemas']['CreateAssistantDraftRequest'];
type SaveAssistantDraftRequest = components['schemas']['SaveAssistantDraftRequest'];
type CreateAssistantFromDraftRequest = components['schemas']['CreateAssistantFromDraftRequest'];
type ApiTrialAnswerRequest = components['schemas']['TrialAnswerRequest'];
type ApiTrialAnswerResponse = components['schemas']['TrialAnswerResponse'];
type ApiTrialAnswerReply = components['schemas']['TrialAnswerReplyView'];
type ApiTrialAnswerCitation = components['schemas']['TrialAnswerCitationView'];
type ApiTrialAnswerPassage = components['schemas']['TrialAnswerPassageView'];
type ApiConnectableSource = components['schemas']['ConnectableSourceView'];
type ApiAssistantPublishing = components['schemas']['AssistantPublishingView'];
type ApiPlatformSharing = components['schemas']['PlatformSharingView'];
type ApiPublishingChannel = components['schemas']['PublishingChannelView'];
type UpdatePlatformSharingRequest = components['schemas']['UpdatePlatformSharingRequest'];
type SetPlatformPausedRequest = components['schemas']['SetPlatformPausedRequest'];
type ApiAssistantAnalytics = components['schemas']['AssistantAnalyticsView'];
type ApiOperationsSummary = components['schemas']['OperationsSummaryView'];
type ApiDatabaseTemplate = components['schemas']['DatabaseTemplateView'];
type ApiDatabaseSummary = components['schemas']['DatabaseSummaryView'];
type ApiDatabaseDetail = components['schemas']['DatabaseDetailView'];
type ApiDatabaseField = components['schemas']['DatabaseFieldView'];
type ApiDatabaseAccess = components['schemas']['DatabaseAccessView'];
type UpdateDatabaseAccessRequest = components['schemas']['UpdateDatabaseAccessRequest'];
type CreateDatabaseRequest = components['schemas']['CreateDatabaseRequest'];
type ApiDatabaseForm = components['schemas']['DatabaseFormView'];
type ApiDatabaseFieldDraft = components['schemas']['DatabaseFieldDraft'];
type ApiDatabaseTrialPreview = components['schemas']['DatabaseTrialPreviewView'];
type SaveDatabaseFormRequest = components['schemas']['SaveDatabaseFormRequest'];
type PreviewDatabaseEntryRequest = components['schemas']['PreviewDatabaseEntryRequest'];
type ApiDatabaseSubmissionForm = components['schemas']['DatabaseSubmissionFormView'];
type ApiDatabaseSubmissionReceipt = components['schemas']['DatabaseSubmissionReceiptView'];
type ReviewDatabaseSubmissionRequest = components['schemas']['ReviewDatabaseSubmissionRequest'];
type SubmitDatabaseEntryRequest = components['schemas']['SubmitDatabaseEntryRequest'];
type ApiChatFormRequest = components['schemas']['ChatFormRequestView'];
type ApiChatFormSubmission = components['schemas']['ChatFormSubmissionView'];
type ReviewChatFormRequest = components['schemas']['ReviewChatFormRequest'];
type SubmitChatFormRequest = components['schemas']['SubmitChatFormRequest'];
type ApiDatabaseOwnSubmissionList = components['schemas']['DatabaseOwnSubmissionListView'];
type ApiDatabaseTracking = components['schemas']['DatabaseTrackingView'];
type ApiDatabaseTrackedSubject = components['schemas']['DatabaseTrackedSubjectView'];

export const API_TEAM_PATH = '/api/v1/team';

export const API_CREATE_MEMBER_PATH = `${API_TEAM_PATH}/members`;

export function apiMemberPermissionsPath(memberId: string): string {
  return `${API_TEAM_PATH}/members/${encodeURIComponent(memberId)}/permissions`;
}

export const API_KNOWLEDGE_BASES_PATH = '/api/v1/knowledge-bases';

export function apiKnowledgeBasePath(knowledgeBaseId: string): string {
  return `${API_KNOWLEDGE_BASES_PATH}/${encodeURIComponent(knowledgeBaseId)}`;
}

export function apiKnowledgeSharingPath(knowledgeBaseId: string): string {
  return `${apiKnowledgeBasePath(knowledgeBaseId)}/sharing`;
}

export function apiKnowledgeDocumentPath(knowledgeBaseId: string, documentId: string): string {
  return `${apiKnowledgeBasePath(knowledgeBaseId)}/documents/${encodeURIComponent(documentId)}`;
}

export function apiKnowledgeDocumentsPath(knowledgeBaseId: string): string {
  return `${apiKnowledgeBasePath(knowledgeBaseId)}/documents`;
}

export function apiKnowledgeDocumentVersionsPath(knowledgeBaseId: string, documentId: string): string {
  return `${apiKnowledgeDocumentPath(knowledgeBaseId, documentId)}/versions`;
}

export function apiKnowledgeRetryPath(knowledgeBaseId: string, documentId: string, versionId: string): string {
  return `${apiKnowledgeDocumentPath(knowledgeBaseId, documentId)}/versions/${encodeURIComponent(versionId)}/retry`;
}

/** 文件詳情：與 `apiKnowledgeDocumentPath` 相同的網址，動詞是 `GET`（issue #47）。 */
export function apiKnowledgeDocumentDetailPath(knowledgeBaseId: string, documentId: string): string {
  return apiKnowledgeDocumentPath(knowledgeBaseId, documentId);
}

export function apiKnowledgeVersionPreviewPath(knowledgeBaseId: string, documentId: string, versionId: string): string {
  return `${apiKnowledgeDocumentPath(knowledgeBaseId, documentId)}/versions/${encodeURIComponent(versionId)}/preview`;
}

export function apiKnowledgeChunkExclusionPath(
  knowledgeBaseId: string,
  documentId: string,
  versionId: string,
  chunkId: string,
): string {
  return `${apiKnowledgeDocumentPath(knowledgeBaseId, documentId)}/versions/${encodeURIComponent(versionId)}/chunks/${encodeURIComponent(chunkId)}/exclusion`;
}

export function apiKnowledgeApprovePath(knowledgeBaseId: string): string {
  return `${apiKnowledgeBasePath(knowledgeBaseId)}/versions/approve`;
}

export function apiKnowledgeDisablePath(knowledgeBaseId: string, documentId: string): string {
  return `${apiKnowledgeDocumentPath(knowledgeBaseId, documentId)}/disable`;
}

export function apiKnowledgeEnablePath(knowledgeBaseId: string, documentId: string): string {
  return `${apiKnowledgeDocumentPath(knowledgeBaseId, documentId)}/enable`;
}

/** 檢索試查（issue #48，M2 Slice 14）。 */
export function apiKnowledgeRetrievalPreviewPath(knowledgeBaseId: string): string {
  return `${apiKnowledgeBasePath(knowledgeBaseId)}/retrieval-preview`;
}

export function apiAssistantChatConversationsPath(assistantId: string): string {
  return `/api/v1/assistants/${encodeURIComponent(assistantId)}/chat/conversations`;
}

export function apiAssistantChatConversationPath(assistantId: string, threadId: string): string {
  return `${apiAssistantChatConversationsPath(assistantId)}/${encodeURIComponent(threadId)}`;
}

export function apiAssistantChatPath(assistantId: string, threadId?: string): string {
  const base = `/api/v1/assistants/${encodeURIComponent(assistantId)}/chat`;
  return threadId === undefined ? base : `${base}?conversation=${encodeURIComponent(threadId)}`;
}

export const API_RECENT_CONVERSATIONS_PATH = '/api/v1/chat/recent-conversations';

export const API_ASSISTANTS_PATH = '/api/v1/assistants';

export const API_USABLE_ASSISTANTS_PATH = `${API_ASSISTANTS_PATH}?usable=true`;

export function apiAssistantPath(assistantId: string): string {
  return `${API_ASSISTANTS_PATH}/${encodeURIComponent(assistantId)}`;
}

export const API_OPERATIONS_SUMMARY_PATH = '/api/v1/operations/summary';

export function apiAssistantAnalyticsPath(assistantId: string): string {
  return `${apiAssistantPath(assistantId)}/analytics`;
}

export function apiAssistantTestCasesPath(assistantId: string): string { return `${apiAssistantPath(assistantId)}/test-cases`; }
export function apiAssistantTestCasePath(assistantId: string, caseId: string): string { return `${apiAssistantTestCasesPath(assistantId)}/${encodeURIComponent(caseId)}`; }
export function apiAssistantTestRunsPath(assistantId: string): string { return `${apiAssistantPath(assistantId)}/test-runs`; }
export function apiAssistantTestRunPath(assistantId: string, runId: string): string { return `${apiAssistantTestRunsPath(assistantId)}/${encodeURIComponent(runId)}`; }

export function apiAssistantSettingsPath(assistantId: string): string {
  return `${apiAssistantPath(assistantId)}/settings`;
}

export function apiAssistantKnowledgeSourcePath(assistantId: string, knowledgeBaseId: string): string {
  return `${apiAssistantPath(assistantId)}/sources/knowledge-base/${encodeURIComponent(knowledgeBaseId)}`;
}

export function apiAssistantDatabaseSourcePath(assistantId: string, databaseId: string): string {
  return `${apiAssistantPath(assistantId)}/sources/database/${encodeURIComponent(databaseId)}`;
}

/** 對話中的表單（issue #148）：`.../review` 與 `.../submissions`。 */
export function apiAssistantChatFormPath(assistantId: string, databaseId: string): string {
  return `${apiAssistantPath(assistantId)}/chat/forms/${encodeURIComponent(databaseId)}`;
}

export function apiAssistantPublishingPath(assistantId: string): string {
  return `${apiAssistantPath(assistantId)}/publishing`;
}

export function apiAssistantPlatformSharingPath(assistantId: string): string {
  return `${apiAssistantPublishingPath(assistantId)}/platform`;
}

export function apiAssistantPlatformPausedPath(assistantId: string): string {
  return `${apiAssistantPlatformSharingPath(assistantId)}/paused`;
}

export const API_ASSISTANT_DRAFTS_PATH = '/api/v1/assistant-drafts';

export function apiAssistantDraftPath(draftId: string): string {
  return `${API_ASSISTANT_DRAFTS_PATH}/${encodeURIComponent(draftId)}`;
}

export function apiTrialAnswersPath(draftId: string): string {
  return `${apiAssistantDraftPath(draftId)}/trial-answers`;
}

export const API_CONNECTABLE_SOURCES_PATH = '/api/v1/connectable-sources';

/** 數據庫（issue #142）：模板、清單、建立與詳情。 */
export const API_DATABASE_TEMPLATES_PATH = '/api/v1/database-templates';

export const API_DATABASES_PATH = '/api/v1/databases';

export function apiDatabasePath(databaseId: string): string {
  return `${API_DATABASES_PATH}/${encodeURIComponent(databaseId)}`;
}

/** 同意提交（issue #145）：填寫者的回執；`GET` 本身是自己的提交清單（issue #146）。 */
export const API_SUBMISSIONS_PATH = '/api/v1/submissions';

/** 撤回自己的一筆提交（issue #146）：`POST`，沒有本文。 */
export function apiSubmissionWithdrawalPath(submissionId: string): string {
  return `${API_SUBMISSIONS_PATH}/${encodeURIComponent(submissionId)}/withdrawal`;
}

/** 資料管理者的收集紀錄時間軸（issue #146）。 */
export function apiDatabaseTrackingPath(databaseId: string): string {
  return `${apiDatabasePath(databaseId)}/tracking`;
}

/** API 模式還沒有的趨勢比較（#147）在時間軸裡的佔位：不是「紀錄不足」的判斷，畫面也不顯示它。 */
const API_TRENDS_PENDING_MESSAGE = '趨勢比較將於後續版本開放。';

/** 指定資料管理者（issue #144）：`PUT`，請求是指定後的完整清單。 */
export function apiDatabaseAccessPath(databaseId: string): string {
  return `${apiDatabasePath(databaseId)}/access`;
}

/**
 * API 模式還沒有的資料庫功能（#143–#148 逐張開放後從這裡移除）。詳情頁依此顯示「將於後續版本開放」。
 *
 * `records` 已於 #146 移除：收集紀錄頁籤的時間軸與撤回軌跡走 `GET /api/v1/databases/{id}/tracking`。
 * 剩下的 `trends`（趨勢比較與定期回報摘要）要由伺服器計算，是 #147；在那之前趨勢頁籤顯示「將於後續
 * 版本開放」，不在前端用顯示文字自己算差異。
 *
 * `assistant-connections` 已移除（#148）：詳情的「已連接助理」來自 API。
 */
export const API_UPCOMING_DATABASE_FEATURES: readonly DatabaseUpcomingFeature[] = [
  'trends',
];

/** 草稿 `payload` 的形狀版本（jsonb，形狀由前端決定）；形狀有不相容的變更時才遞增。 */
export const ASSISTANT_DRAFT_SCHEMA_VERSION = 1;

/** 與後端 `ForbiddenReason.AssistantForm` 相同（issue #148）。 */
const ASSISTANT_FORM_DENIED = {
  reason: 'assistant-form',
  message: '這份表單目前無法使用：助理已不再連接這個資料庫，或你沒有填寫它的權限。',
} as const;

/** 設定新密碼前 API 回的 403 訊息；與 API 的 `ForbiddenReason.PasswordChangeRequired` 相同。 */
const PASSWORD_CHANGE_REQUIRED_MESSAGE = '請先設定新密碼，才能使用其他功能。';

/** OpenAPI 沒有描述錯誤本文（`content?: never`），依後端 `ApiErrors` 的形狀在這裡定義。 */
interface ForbiddenBody {
  readonly reason?: unknown;
  readonly message?: unknown;
}

interface ValidationFailedBody {
  readonly message?: unknown;
  readonly errors?: Readonly<Record<string, unknown>>;
}

/**
 * 某個功能區的 403 預設值：body 的 `reason` 不在前端的 union 裡（例如後端保底的
 * `forbidden`）時用這一組，訊息則優先用 body 的 `message`。
 */
interface PermissionDeniedFallback {
  readonly reason: RepositoryPermissionDeniedReason;
  readonly message: string;
}

const TEAM_DENIED: PermissionDeniedFallback = { reason: 'team', message: TEAM_PERMISSION_DENIED_MESSAGE };
const KNOWLEDGE_DENIED: PermissionDeniedFallback = {
  reason: 'knowledge-base',
  message: KNOWLEDGE_PERMISSION_DENIED_MESSAGE,
};

/** 與後端 `DatabaseEndpoints.FormChangedMessage`、mock 的訊息逐字相同（後端的訊息優先）。 */
const DATABASE_FORM_CHANGED_MESSAGE = '這份表單已被更新過，請重新載入後再修改。你這次的修改尚未儲存。';

/** 與後端 `ForbiddenReason.Database`／`DatabaseCreate`、mock 的訊息逐字相同。 */
const DATABASE_DENIED: PermissionDeniedFallback = {
  reason: 'database',
  message: '你沒有這個資料庫的存取權限，或它已不存在。',
};
/** 與後端 `ForbiddenReason.AuthorizedForm`／`SubmissionReceipt`、mock 的訊息逐字相同。 */
const AUTHORIZED_FORM_DENIED: PermissionDeniedFallback = {
  reason: 'authorized-form',
  message: '你沒有填寫這份表單的權限，或它已不存在。',
};
const SUBMISSION_RECEIPT_DENIED: PermissionDeniedFallback = {
  reason: 'authorized-form',
  message: '找不到這張回執，或你沒有查看它的權限。',
};
/** 與後端 `ForbiddenReason.SubmissionWithdrawal`、mock 的訊息逐字相同。 */
const SUBMISSION_WITHDRAWAL_DENIED: PermissionDeniedFallback = {
  reason: 'submission-withdrawal',
  message: '找不到這筆紀錄，或你沒有撤回它的權限。',
};
const DATABASE_CREATE_DENIED: PermissionDeniedFallback = {
  reason: 'database',
  message: '只有可管理資料來源的帳號可以建立資料庫。',
};

const ASSISTANT_CONFIGURATION_DENIED: PermissionDeniedFallback = {
  reason: 'assistant-configuration',
  message: '你沒有這個助理的設定權限，或它已不存在。',
};
const DRAFT_DENIED: PermissionDeniedFallback = {
  reason: 'assistant-draft',
  message: '只有可管理助理的帳號可以建立助理。',
};
const PUBLISHING_DENIED: PermissionDeniedFallback = {
  reason: 'publishing',
  message: '你沒有這個助理的發布設定權限，或它已不存在。',
};
const ASSISTANT_USE_DENIED: PermissionDeniedFallback = {
  reason: 'assistant-use',
  message: '你沒有使用這個助理的權限，或它已不存在。',
};

/** 對話端點的 403 後端一定會帶 `assistant-use` 或 `chat-thread`；這是保底用的預設值。 */
const CHAT_DENIED: PermissionDeniedFallback = ASSISTANT_USE_DENIED;

/**
 * `/me` 中 mock 需要的部分；API 模式由 `HttpSessionBackend.restore()` 提供。
 * `accountId` 是後端的真實帳號 GUID，用來判斷團隊列表裡「哪一列是我自己」；
 * `demoAccountId` 只給仍在 mock 的功能區（`viewerOverride`）沿用現有的權限判斷。
 */
export interface ApiViewerPermissions {
  readonly accountId: string;
  readonly demoAccountId: AccountId;
  readonly permissions: readonly AccountPermission[];
  /**
   * `/me` 的真實組織 GUID，與 `accountId` 一起為 API 模式的 mock storage 鍵值加前綴
   * （見 `scoped-storage.ts`）。選填是為了不強迫既有只關心權限的測試假身分也要補上；
   * 正式的 `HttpSessionBackend.restore()` 一定會填（`ApiIdentity` 的同名欄位）。
   */
  readonly organizationId?: string;
}

export interface HybridDemoRepositoryDeps {
  readonly http: HttpClient;
  /** 最近一次 `/me`（登入與每次啟動時更新）；沒有登入時為 null。 */
  readonly viewerPermissions: () => ApiViewerPermissions | null;
}

/** mock 呈現團隊的順序（seed 的順序就是角色順序）；API 依 id 排序，不能直接沿用。 */
const ROLE_ORDER = Object.keys(ACCOUNT_ROLE_LABELS) as AccountRole[];

/**
 * 仍在 mock 的功能區要用的權限（`MockDemoRepositoryOptions.accountsSource`）。
 *
 * 只覆寫**目前登入者自己**的權限（來自 `/me`，鍵是同角色的 Demo 身分 id）。團隊 API
 * 回傳的其他成員不再經由角色換算成 Demo 身分——同組織有兩位同角色成員時，這個對應
 * 本來就無法一一還原，正是本票要修的問題（issue #36：改權限改到別人）。沒有被覆寫
 * 的 Demo 帳號沿用 seed 的權限；mock 的檢查幾乎都只看目前身分，所以影響限於
 * 「別人的權限」出現在畫面上的地方（例如資料管理者候選人的說明）。
 */
function viewerOverride(
  viewer: ApiViewerPermissions | null,
  fromTeam: OwnPermissionsFromTeam,
): AccountPermissionOverrides {
  if (viewer === null) return {};
  // 團隊 API 比啟動時的 `/me` 新：登入者在團隊頁改了自己的權限時，mock 區域立即生效。
  // 以真實帳號 GUID 比對，同一分頁換帳號登入後不會套用到別人身上。
  const permissions =
    fromTeam.accountId === viewer.accountId && fromTeam.permissions !== null
      ? fromTeam.permissions
      : viewer.permissions;
  return { [viewer.demoAccountId]: permissions };
}

/** 最近一次團隊回應中「登入者自己」那一列的權限。 */
interface OwnPermissionsFromTeam {
  accountId: string | null;
  permissions: readonly AccountPermission[] | null;
}

/**
 * API 模式的 repository：已接上 API 的方法走 HTTP，其餘方法沿用 `MockDemoRepository`
 * （以繼承取得，所以 mock 的所有方法不用逐一轉接）。只由 `provideApiMode()` 建立，
 * mock 建置不含這個檔案。
 *
 * 目前接上 API 的：`getTeam`、`updateMemberPermissions`（M1；M2 起改用真實帳號 GUID
 * 當列表的 key、編輯目標與更新目標，不再換回同角色的 Demo 身分——見 issue #36）；
 * 知識庫的清單、詳情、建立、刪除、重新處理與分享（M2 Slice 11，issue #45）；對話串的讀取
 * 與管理（M3 Slice 9，issue #79）；助理的清單、設定、來源連接、刪除、精靈草稿、由草稿建立、
 * 可連接來源、平台內分享與暫停（M3 Slice 11，issue #81）。M2 期間存在瀏覽器裡的 mock 助理
 * 與草稿不會遷移到 API（M3 計畫第 7 節已決定 4）。
 *
 * 每個方法的形狀都一樣（後續功能區照做）：`http.<verb>` → `map` 成前端 view →
 * `catchError` 把可恢復的狀態碼（403／404／409／422）轉成結果，其餘錯誤原樣拋出，
 * 由畫面顯示「目前無法載入」。
 */
export class HybridDemoRepository extends MockDemoRepository {
  private readonly http: HttpClient;
  private readonly viewerPermissions: () => ApiViewerPermissions | null;
  private readonly ownFromTeam: OwnPermissionsFromTeam;

  constructor(seed: DemoSeed, options: MockDemoRepositoryOptions, deps: HybridDemoRepositoryDeps) {
    const ownFromTeam: OwnPermissionsFromTeam = { accountId: null, permissions: null };
    // 以真實組織／帳號 GUID 為 mock storage 的鍵值加前綴：兩個組織、或同組織中同角色的
    // 兩個帳號，共用同一瀏覽器時不會讀到彼此的草稿、對話等模擬資料（見 scoped-storage.ts）。
    // `deps.viewerPermissions` 在每次呼叫時才讀取，所以即使 repository 在登入前就已由
    // DI 建立，storage 也一定看到「目前」的身分，不是建構當下（那時通常還沒登入）。
    const storage = createScopedStorage(
      options.storage ?? createMemoryStorage(),
      (): StorageIdentity | null => {
        const viewer = deps.viewerPermissions();
        return viewer?.organizationId !== undefined
          ? { organizationId: viewer.organizationId, accountId: viewer.accountId }
          : null;
      },
    );
    super(seed, {
      ...options,
      storage,
      accountsSource: () => viewerOverride(deps.viewerPermissions(), ownFromTeam),
    });
    this.http = deps.http;
    this.viewerPermissions = deps.viewerPermissions;
    this.ownFromTeam = ownFromTeam;
  }

  override getTeam(): Observable<RepositoryView<TeamView>> {
    return this.http.get<TeamResponse>(API_TEAM_PATH).pipe(
      map((response) => this.teamLoaded(response)),
      catchError((error: unknown) => this.permissionDeniedOrThrow(error)),
    );
  }

  /** `memberAccountId` 是團隊列表給的真實帳號 GUID；直接 PUT 到該 id，不再需要先解析。 */
  override updateMemberPermissions(
    memberAccountId: AccountId,
    permissions: readonly AccountPermission[],
  ): Observable<UpdateMemberPermissionsResult> {
    const body: UpdateMemberPermissionsRequest = { permissions: [...permissions] };
    return this.http.put<TeamResponse>(apiMemberPermissionsPath(memberAccountId), body).pipe(
      map((response): UpdateMemberPermissionsResult => this.teamLoaded(response)),
      catchError((error: unknown) =>
        isHttpError(error, 422) ? of(validationFailed(error)) : this.permissionDeniedOrThrow(error),
      ),
    );
  }

  /**
   * 新增組織成員（issue #52，M2 Slice 18）：後端已經套用 `manage-assistants` 與同組織登入
   * 名稱唯一的規則，這裡只轉譯結果——`422` 轉成 validation-failed，`403` 沿用團隊的
   * permission-denied。一次性密碼原樣轉交給畫面，這個方法本身不記錄、不快取它。
   */
  override createMember(input: CreateMemberInput): Observable<CreateMemberResult> {
    const body: CreateMemberRequest = {
      loginName: input.loginName,
      displayName: input.displayName,
      role: input.role,
      permissions: [...input.permissions],
    };
    return this.http.post<CreateMemberResponse>(API_CREATE_MEMBER_PATH, body).pipe(
      map((response): CreateMemberResult => ({
        status: 'ready',
        data: {
          member: toTeamMemberView(response.member, this.viewerPermissions()?.accountId ?? null),
          oneTimePassword: response.oneTimePassword,
        },
      })),
      catchError((error: unknown) =>
        isHttpError(error, 422) ? of(createMemberValidationFailed(error)) : this.permissionDeniedOrThrow(error),
      ),
    );
  }

  private teamLoaded(response: TeamResponse): RepositoryView<TeamView> {
    return {
      status: 'ready',
      data: toTeamView(response, this.rememberOwnPermissions(response)),
    };
  }

  /** 記下團隊回應中登入者自己的權限（見 `viewerOverride`），回傳登入者的帳號 GUID。 */
  private rememberOwnPermissions(response: TeamResponse): string | null {
    const viewerAccountId = this.viewerPermissions()?.accountId ?? null;
    const own = response.members.find((member) => member.id === viewerAccountId);
    if (own !== undefined) {
      this.ownFromTeam.accountId = own.id;
      this.ownFromTeam.permissions = normalizeMemberPermissions(own.permissions);
    }
    return viewerAccountId;
  }

  // ---------- 助理（M3 Slice 11，issue #81） ----------

  /**
   * 管理清單：只列自己擁有的助理。沒有 `manage-assistants`（或尚未登入）時直接回傳空清單，
   * 不打 API——後端這時回 `403`，但對畫面而言就是「還沒有建立助理」，與 mock 相同。
   */
  override listAssistantConfigurations(): Observable<RepositoryView<readonly AssistantConfigurationView[]>> {
    const viewer = this.viewer();
    if (viewer === null || !this.canManageAssistants(viewer)) return of({ status: 'ready', data: [] });

    return this.http.get<ApiAssistantConfiguration[]>(API_ASSISTANTS_PATH).pipe(
      map((response): RepositoryView<readonly AssistantConfigurationView[]> => ({
        status: 'ready',
        data: response.map((assistant) => toAssistantConfiguration(assistant)),
      })),
      catchError((error: unknown) => this.permissionDeniedOrThrow(error, ASSISTANT_CONFIGURATION_DENIED)),
    );
  }

  /**
   * 可以開啟對話的助理：自己的，加上分享給自己的（`?usable=true`）。後端只給 `viewerIsOwner`，
   * 這裡換成前端的 `permission`：擁有者依帳號權限是 `configure`（可管理助理）或 `publish`
   * （只能設定發布），分享對象一律是 `use`。
   */
  override listUsableAssistants(): Observable<RepositoryView<readonly AssistantSummaryView[]>> {
    return this.http.get<ApiAssistantSummary[]>(API_USABLE_ASSISTANTS_PATH).pipe(
      map((response): RepositoryView<readonly AssistantSummaryView[]> => ({
        status: 'ready',
        data: response.map((assistant) => ({
          id: assistant.id as AssistantId,
          name: assistant.name,
          purpose: assistant.purpose,
          status: assistant.status,
          audience: 'account-members',
          permission: assistant.viewerIsOwner ? this.ownerPermission() : 'use',
        })),
      })),
      catchError((error: unknown) => this.permissionDeniedOrThrow(error, ASSISTANT_USE_DENIED)),
    );
  }

  override listAssistantTestCases(assistantId: string): Observable<RepositoryView<readonly AssistantTestCaseView[]>> {
    return this.http.get<AssistantTestCaseView[]>(apiAssistantTestCasesPath(assistantId)).pipe(map(data => ({ status: 'ready' as const, data })), catchError(error => this.permissionDeniedOrThrow(error, ASSISTANT_CONFIGURATION_DENIED)));
  }
  override createAssistantTestCase(assistantId: string, input: AssistantTestCaseInput): Observable<RepositoryView<AssistantTestCaseView>> {
    return this.http.post<AssistantTestCaseView>(apiAssistantTestCasesPath(assistantId), input).pipe(map(data => ({ status: 'ready' as const, data })), catchError(error => this.permissionDeniedOrThrow(error, ASSISTANT_CONFIGURATION_DENIED)));
  }
  override updateAssistantTestCase(assistantId: string, caseId: string, patch: AssistantTestCasePatch): Observable<RepositoryView<AssistantTestCaseView>> {
    return this.http.patch<AssistantTestCaseView>(apiAssistantTestCasePath(assistantId, caseId), patch).pipe(map(data => ({ status: 'ready' as const, data })), catchError(error => this.permissionDeniedOrThrow(error, ASSISTANT_CONFIGURATION_DENIED)));
  }
  override deleteAssistantTestCase(assistantId: string, caseId: string): Observable<RepositoryView<null>> {
    return this.http.delete<void>(apiAssistantTestCasePath(assistantId, caseId)).pipe(map(() => ({ status: 'ready' as const, data: null })), catchError(error => this.permissionDeniedOrThrow(error, ASSISTANT_CONFIGURATION_DENIED)));
  }
  override importAssistantTestCases(assistantId: string, questions: readonly AssistantTestCaseImportEntry[]): Observable<RepositoryView<readonly AssistantTestCaseView[]>> {
    return this.http.post<AssistantTestCaseView[]>(`${apiAssistantTestCasesPath(assistantId)}/import`, { questions }).pipe(map(data => ({ status: 'ready' as const, data })), catchError(error => this.permissionDeniedOrThrow(error, ASSISTANT_CONFIGURATION_DENIED)));
  }
  override exportAssistantTestCases(assistantId: string): Observable<RepositoryView<readonly AssistantTestCaseExportEntry[]>> {
    return this.http.get<{ questions: AssistantTestCaseExportEntry[] }>(`${apiAssistantTestCasesPath(assistantId)}/export`).pipe(map(({ questions }) => ({ status: 'ready' as const, data: questions })), catchError(error => this.permissionDeniedOrThrow(error, ASSISTANT_CONFIGURATION_DENIED)));
  }
  override listAssistantTestRuns(assistantId: string): Observable<RepositoryView<readonly AssistantTestRunView[]>> {
    return this.http.get<AssistantTestRunView[]>(apiAssistantTestRunsPath(assistantId)).pipe(map(data => ({ status: 'ready' as const, data })), catchError(error => this.permissionDeniedOrThrow(error, ASSISTANT_CONFIGURATION_DENIED)));
  }
  override createAssistantTestRun(assistantId: string): Observable<RepositoryView<AssistantTestRunView>> {
    return this.http.post<AssistantTestRunView>(apiAssistantTestRunsPath(assistantId), {}).pipe(map(data => ({ status: 'ready' as const, data })), catchError(error => this.permissionDeniedOrThrow(error, ASSISTANT_CONFIGURATION_DENIED)));
  }
  override getAssistantTestRun(assistantId: string, runId: string): Observable<RepositoryView<AssistantTestRunDetailView>> {
    return this.http.get<AssistantTestRunDetailView>(apiAssistantTestRunPath(assistantId, runId)).pipe(map(data => ({ status: 'ready' as const, data })), catchError(error => this.permissionDeniedOrThrow(error, ASSISTANT_CONFIGURATION_DENIED)));
  }

  override getAssistantSettings(assistantId: string): Observable<RepositoryView<AssistantSettingsView>> {
    return this.http.get<ApiAssistantSettings>(apiAssistantSettingsPath(assistantId)).pipe(
      map((response): RepositoryView<AssistantSettingsView> => ({ status: 'ready', data: toAssistantSettings(response) })),
      catchError((error: unknown) => this.assistantConfigurationDeniedOrThrow(error)),
    );
  }


  override getAssistantAnalyticsSummary(assistantId: string): Observable<RepositoryView<AssistantAnalyticsSummaryView>> {
    return this.http.get<ApiAssistantAnalytics>(apiAssistantAnalyticsPath(assistantId)).pipe(
      map((response): RepositoryView<AssistantAnalyticsSummaryView> => ({ status: 'ready', data: response })),
      catchError((error: unknown) => this.assistantConfigurationDeniedOrThrow(error)),
    );
  }

  override getOperationsSummary(): Observable<RepositoryView<OperationsSummaryView>> {
    return this.http.get<ApiOperationsSummary>(API_OPERATIONS_SUMMARY_PATH).pipe(
      map((response): RepositoryView<OperationsSummaryView> => ({ status: 'ready', data: response })),
      catchError((error: unknown) => this.permissionDeniedOrThrow(error, ASSISTANT_CONFIGURATION_DENIED)),
    );
  }

  /**
   * `PATCH` 只送後端認得的欄位；使用對象（M3 只有組織內）與定期回報（#150）畫面在 API 模式不提供，
   * 即使帶進來也不送出。資料庫寫入（`dataWriteDatabaseId`／`dataWritePurpose`，#148）照送。
   * 後端全有或全無：`422` 時完全沒有寫入。
   */
  override updateAssistantSettings(
    assistantId: string,
    patch: AssistantSettingsPatch,
  ): Observable<UpdateAssistantSettingsResult> {
    const rules = patch.rules;
    const body: UpdateAssistantSettingsRequest = {
      ...(patch.name !== undefined ? { name: patch.name } : {}),
      ...(patch.purpose !== undefined ? { purpose: patch.purpose } : {}),
      ...(patch.tone !== undefined ? { tone: patch.tone } : {}),
      ...(patch.roleInstructions !== undefined ? { roleInstructions: patch.roleInstructions } : {}),
      ...(rules !== undefined
        ? {
            rules: {
              ...(rules.knowledgeScope !== undefined ? { knowledgeScope: rules.knowledgeScope } : {}),
              ...(rules.refusalMessage !== undefined ? { refusalMessage: rules.refusalMessage } : {}),
              ...(rules.showCitations !== undefined ? { showCitations: rules.showCitations } : {}),
              ...(rules.keepOwnConversations !== undefined ? { keepConversations: rules.keepOwnConversations } : {}),
              // 寫入的資料庫（issue #148）：null 送空字串代表「不寫入」；沒帶就不變。
              ...(rules.dataWriteDatabaseId !== undefined ? { dataWriteDatabaseId: rules.dataWriteDatabaseId ?? '' } : {}),
              ...(rules.dataWritePurpose !== undefined ? { dataWritePurpose: rules.dataWritePurpose } : {}),
            },
          }
        : {}),
    };
    return this.http.patch<ApiAssistantSettings>(apiAssistantSettingsPath(assistantId), body).pipe(
      map((response): UpdateAssistantSettingsResult => ({ status: 'ready', data: toAssistantSettings(response) })),
      catchError((error: unknown) =>
        isHttpError(error, 422) ? of(settingsValidationFailed(error)) : this.assistantConfigurationDeniedOrThrow(error),
      ),
    );
  }

  /**
   * 知識庫走 `PUT`／`DELETE .../sources/knowledge-base/{id}`，資料庫（#148）走
   * `.../sources/database/{id}`：讀寫兩端都是 API，伺服器判斷可不可以連接（不經 mock 的種子清單）。
   * `422`（不可連接、最後一個來源）轉成 `sources` 欄位的錯誤。
   */
  override setAssistantSourceConnection(
    assistantId: string,
    source: AssistantSourceReference,
    connected: boolean,
  ): Observable<UpdateAssistantSettingsResult> {
    const path = source.type === 'database'
      ? apiAssistantDatabaseSourcePath(assistantId, source.id)
      : apiAssistantKnowledgeSourcePath(assistantId, source.id);
    const request = connected
      ? this.http.put<ApiAssistantSettings>(path, null)
      : this.http.delete<ApiAssistantSettings>(path);
    return request.pipe(
      map((response): UpdateAssistantSettingsResult => ({ status: 'ready', data: toAssistantSettings(response) })),
      catchError((error: unknown) =>
        isHttpError(error, 422) ? of(settingsValidationFailed(error)) : this.assistantConfigurationDeniedOrThrow(error),
      ),
    );
  }

  /** 後端在同一個交易內連帶刪除所有成員的對話串（M3 計畫決定 G）。 */
  override deleteAssistant(assistantId: string): Observable<DeleteAssistantResult> {
    return this.http.delete<void>(apiAssistantPath(assistantId)).pipe(
      map((): DeleteAssistantResult => ({ status: 'ready', data: null })),
      catchError((error: unknown) => this.assistantConfigurationDeniedOrThrow(error)),
    );
  }

  // ---------- 精靈草稿 ----------

  /** 沒有 `manage-assistants` 時不打 API，回傳與後端相同意義的 `assistant-draft`。 */
  override listNamedAssistantDrafts(): Observable<RepositoryView<readonly NamedAssistantDraftView[]>> {
    const viewer = this.viewer();
    if (viewer === null || !this.canManageAssistants(viewer)) return of(permissionDenied(DRAFT_DENIED));

    return this.http.get<ApiAssistantDraft[]>(API_ASSISTANT_DRAFTS_PATH).pipe(
      map((response): RepositoryView<readonly NamedAssistantDraftView[]> => ({
        status: 'ready',
        data: response.map(toNamedDraft),
      })),
      catchError((error: unknown) => this.permissionDeniedOrThrow(error, DRAFT_DENIED)),
    );
  }

  override createNamedAssistantDraft(): Observable<RepositoryView<NamedAssistantDraftView>> {
    const body: CreateAssistantDraftRequest = {
      payload: createEmptyAssistantDraft() as unknown as CreateAssistantDraftRequest['payload'],
      schemaVersion: ASSISTANT_DRAFT_SCHEMA_VERSION,
    };
    return this.http.post<ApiAssistantDraft>(API_ASSISTANT_DRAFTS_PATH, body).pipe(
      map((response): RepositoryView<NamedAssistantDraftView> => ({ status: 'ready', data: toNamedDraft(response) })),
      catchError((error: unknown) => this.permissionDeniedOrThrow(error, DRAFT_DENIED)),
    );
  }

  override getNamedAssistantDraft(draftId: string): Observable<RepositoryView<NamedAssistantDraftView>> {
    return this.http.get<ApiAssistantDraft>(apiAssistantDraftPath(draftId)).pipe(
      map((response): RepositoryView<NamedAssistantDraftView> => ({ status: 'ready', data: toNamedDraft(response) })),
      catchError((error: unknown) => this.draftDeniedOrThrow(error)),
    );
  }

  /**
   * `PUT` 帶上次讀到的 `revision`；`409 draft-revision-conflict`（另一個分頁先存過）轉成
   * `conflict`，訊息用後端的。`422`（內容不是物件或超過 64 KB）與其他錯誤一樣以 error 傳出，
   * 畫面顯示「自動儲存失敗」。
   */
  override saveNamedAssistantDraft(
    draftId: string,
    draft: AssistantDraft,
    revision: number,
  ): Observable<SaveAssistantDraftResult> {
    const body: SaveAssistantDraftRequest = {
      payload: draft as unknown as SaveAssistantDraftRequest['payload'],
      revision,
      schemaVersion: ASSISTANT_DRAFT_SCHEMA_VERSION,
    };
    return this.http.put<ApiAssistantDraft>(apiAssistantDraftPath(draftId), body).pipe(
      map((response): SaveAssistantDraftResult => ({ status: 'ready', data: toNamedDraft(response) })),
      catchError((error: unknown) =>
        isHttpError(error, 409)
          ? of<SaveAssistantDraftResult>({
              status: 'conflict',
              message: bodyMessage(error) ?? DRAFT_REVISION_CONFLICT_MESSAGE,
            })
          : this.draftDeniedOrThrow(error),
      ),
    );
  }

  override discardNamedAssistantDraft(draftId: string): Observable<RepositoryView<null>> {
    return this.http.delete<void>(apiAssistantDraftPath(draftId)).pipe(
      map((): RepositoryView<null> => ({ status: 'ready', data: null })),
      catchError((error: unknown) => this.draftDeniedOrThrow(error)),
    );
  }

  /**
   * `POST /api/v1/assistants { draftId }`：後端以伺服器上保存的草稿為準（呼叫端要先保存），
   * 所以 `draft` 在這裡不送出。`422` 的 `errors` 是「欄位 → 訊息陣列」物件，轉成前端的
   * 逐欄錯誤清單；`403` 的 reason 照 body（草稿不存在是 `assistant-draft`）。
   */
  override createAssistantFromDraft(draftId: string): Observable<CreateAssistantResult> {
    const body: CreateAssistantFromDraftRequest = { draftId };
    return this.http.post<ApiAssistantConfiguration>(API_ASSISTANTS_PATH, body).pipe(
      map((response): CreateAssistantResult => ({ status: 'ready', data: toAssistantConfiguration(response) })),
      catchError((error: unknown) =>
        isHttpError(error, 422) ? of(draftValidationFailed(error)) : this.draftDeniedOrThrow(error),
      ),
    );
  }

  /**
   * `POST /api/v1/assistant-drafts/{id}/trial-answers`（issue #82，M3 計畫 Slice 8、12）：
   * 依伺服器上保存的草稿呼叫真實回答流程，`request.sources`／`request.rules` 只有 Mock
   * 用得到，這裡不送出。`422`（問題空白或超過 2,000 字）轉成 `validation-failed`；
   * `503`（嵌入或對話模型未設定、呼叫失敗）轉成 `unavailable`；別人的草稿與不存在的草稿
   * 回同一個 `403 assistant-draft`。
   */
  override previewTrialAnswer(
    draftId: string,
    request: TrialAnswerRequest,
  ): Observable<PreviewTrialAnswerResult> {
    const body: ApiTrialAnswerRequest = { question: request.question };
    return this.http.post<ApiTrialAnswerResponse>(apiTrialAnswersPath(draftId), body).pipe(
      map((response): PreviewTrialAnswerResult => ({
        status: 'ready',
        data: toTrialAnswerResult(request.question, response),
      })),
      catchError((error: unknown) => {
        if (isHttpError(error, 422)) {
          return of<PreviewTrialAnswerResult>({
            status: 'validation-failed',
            message: bodyMessage(error) ?? '請確認輸入的問題。',
          });
        }
        if (isHttpError(error, 503)) {
          return of<PreviewTrialAnswerResult>({
            status: 'unavailable',
            message: bodyMessage(error) ?? '目前無法產生回答，請稍後再試。',
          });
        }
        return this.draftDeniedOrThrow(error);
      }),
    );
  }

  /**
   * 可連接來源（`GET /api/v1/connectable-sources`）：自己的、公開的，以及分享給自己的知識庫
   * （M3 計畫 Slice 2）。資料庫屬於 M4，API 模式不列出。沒有 `manage-assistants` 時
   * （或尚未登入）直接回傳空清單，不必打 API，與 mock 相同。
   */
  override listConnectableSources(): Observable<RepositoryView<readonly ConnectableSourceView[]>> {
    const viewer = this.viewer();
    if (viewer === null || !this.canManageAssistants(viewer)) return of({ status: 'ready', data: [] });

    return this.http.get<ApiConnectableSource[]>(API_CONNECTABLE_SOURCES_PATH).pipe(
      map((response): RepositoryView<readonly ConnectableSourceView[]> => ({
        status: 'ready',
        data: response.map(toConnectableSource),
      })),
      catchError((error: unknown) => this.permissionDeniedOrThrow(error, DRAFT_DENIED)),
    );
  }

  // ---------- 發布（只有組織內部分享；官網與 LINE 在後續版本開放） ----------

  /**
   * 發布管道總覽：後端沒有總覽端點，所以讀自己擁有的助理，再逐一讀它的發布設定。
   * 某一個助理讀取被拒（例如沒有 `manage-publishing`）時略過它，與 mock 只列可設定者相同。
   */
  override listChannelOverview(): Observable<RepositoryView<readonly AssistantChannelsView[]>> {
    const viewer = this.viewer();
    if (viewer === null || !this.canManageAssistants(viewer)) return of({ status: 'ready', data: [] });

    return this.http.get<ApiAssistantConfiguration[]>(API_ASSISTANTS_PATH).pipe(
      switchMap((assistants) =>
        assistants.length === 0
          ? of([])
          : forkJoin(
              assistants.map((assistant) =>
                this.http.get<ApiAssistantPublishing>(apiAssistantPublishingPath(assistant.id)).pipe(
                  map((response): AssistantChannelsView | null => {
                    const view = toAssistantPublishing(response);
                    return {
                      assistantId: view.assistantId,
                      assistantName: view.assistantName,
                      channels: [view.platform.channel, view.website.channel, view.line.channel],
                    };
                  }),
                  catchError((error: unknown) => (isHttpError(error, 403) ? of(null) : throwError(() => error))),
                ),
              ),
            ),
      ),
      map((entries): RepositoryView<readonly AssistantChannelsView[]> => ({
        status: 'ready',
        data: entries.filter((entry): entry is AssistantChannelsView => entry !== null),
      })),
      catchError((error: unknown) => this.permissionDeniedOrThrow(error, PUBLISHING_DENIED)),
    );
  }

  override listPublishingChannels(): Observable<RepositoryView<readonly PublishingChannelView[]>> {
    return this.listChannelOverview().pipe(
      map((result): RepositoryView<readonly PublishingChannelView[]> =>
        result.status === 'ready' || result.status === 'partial-failure'
          ? { status: 'ready', data: result.data.flatMap((entry) => entry.channels) }
          : result,
      ),
    );
  }

  override getAssistantPublishing(assistantId: string): Observable<RepositoryView<AssistantPublishingView>> {
    return this.http.get<ApiAssistantPublishing>(apiAssistantPublishingPath(assistantId)).pipe(
      map((response): RepositoryView<AssistantPublishingView> => ({ status: 'ready', data: toAssistantPublishing(response) })),
      catchError((error: unknown) => this.publishingDeniedOrThrow(error)),
    );
  }

  /**
   * 整份取代分享對象。後端會默默濾掉自己、未知與其他組織的帳號（與知識庫分享相同），
   * 所以沒有 validation-failed；回傳的 `allowedAccountIds` 才是實際生效的清單。
   */
  override updatePlatformSharing(
    assistantId: string,
    accountIds: readonly AccountId[],
  ): Observable<UpdatePlatformSharingResult> {
    const body: UpdatePlatformSharingRequest = { accountIds: [...accountIds] };
    return this.http.put<ApiPlatformSharing>(apiAssistantPlatformSharingPath(assistantId), body).pipe(
      map((response): UpdatePlatformSharingResult => ({ status: 'ready', data: toPlatformSharing(response) })),
      catchError((error: unknown) => this.publishingDeniedOrThrow(error)),
    );
  }

  /** 只有平台內管道可以暫停（暫停的就是助理本身）；官網與 LINE 尚未開放，視同沒有權限。 */
  override setPublishingChannelPaused(
    assistantId: string,
    channelType: PublishingChannelType,
    paused: boolean,
  ): Observable<RepositoryView<PublishingChannelView>> {
    if (channelType !== 'platform') return of(permissionDenied(PUBLISHING_DENIED));

    const body: SetPlatformPausedRequest = { paused };
    return this.http.put<ApiPublishingChannel>(apiAssistantPlatformPausedPath(assistantId), body).pipe(
      map((response): RepositoryView<PublishingChannelView> => ({ status: 'ready', data: toPublishingChannel(response) })),
      catchError((error: unknown) => this.publishingDeniedOrThrow(error)),
    );
  }

  /** 擁有者在前端的 `permission`：可管理助理 → `configure`，只能設定發布 → `publish`。 */
  private ownerPermission(): AssistantPermission {
    const viewer = this.viewer();
    if (viewer !== null && this.canManageAssistants(viewer)) return 'configure';
    const permissions = this.viewerPermissions()?.permissions ?? [];
    return permissions.includes('manage-publishing') ? 'publish' : 'use';
  }

  /** `403`（不存在、別人的、別的組織的都一樣）與 `404`（id 不是 GUID，例如 mock 的助理 id）。 */
  private assistantConfigurationDeniedOrThrow(error: unknown): Observable<PermissionDeniedRepositoryView> {
    if (isHttpError(error, 404)) return of(permissionDenied(ASSISTANT_CONFIGURATION_DENIED));
    return this.permissionDeniedOrThrow(error, ASSISTANT_CONFIGURATION_DENIED);
  }

  private draftDeniedOrThrow(error: unknown): Observable<PermissionDeniedRepositoryView> {
    if (isHttpError(error, 404)) return of(permissionDenied(DRAFT_DENIED));
    return this.permissionDeniedOrThrow(error, DRAFT_DENIED);
  }

  private publishingDeniedOrThrow(error: unknown): Observable<PermissionDeniedRepositoryView> {
    if (isHttpError(error, 404)) return of(permissionDenied(PUBLISHING_DENIED));
    return this.permissionDeniedOrThrow(error, PUBLISHING_DENIED);
  }

  // ---------- 數據庫（issue #142）----------

  override listDatabaseTemplates(): Observable<RepositoryView<readonly DatabaseTemplateView[]>> {
    return this.http.get<ApiDatabaseTemplate[]>(API_DATABASE_TEMPLATES_PATH).pipe(
      map((response): RepositoryView<readonly DatabaseTemplateView[]> => ({
        status: 'ready',
        data: response.map((template) => ({
          id: template.id,
          name: template.name,
          description: template.description,
          fields: template.fields.map(toDatabaseField),
        })),
      })),
      catchError((error: unknown) => this.permissionDeniedOrThrow(error, DATABASE_CREATE_DENIED)),
    );
  }

  override listDatabaseSummaries(): Observable<RepositoryView<readonly DatabaseSummaryView[]>> {
    return this.http.get<ApiDatabaseSummary[]>(API_DATABASES_PATH).pipe(
      map((response): RepositoryView<readonly DatabaseSummaryView[]> => ({
        status: 'ready',
        data: response.map(toDatabaseSummary),
      })),
      catchError((error: unknown) => this.permissionDeniedOrThrow(error, DATABASE_DENIED)),
    );
  }

  /**
   * 寫入也走 API（不是只換讀取清單）：模板 id 與名稱由伺服器驗證，`422` 轉成
   * `validation-failed`、`403` 轉成 `database`；其他錯誤原樣拋出，由畫面保留輸入讓使用者重試。
   */
  override createDatabaseFromTemplate(input: CreateDatabaseInput): Observable<CreateDatabaseResult> {
    const body: CreateDatabaseRequest = { templateId: input.templateId, name: input.name };
    return this.http.post<ApiDatabaseSummary>(API_DATABASES_PATH, body).pipe(
      map((response): CreateDatabaseResult => ({ status: 'ready', data: toDatabaseSummary(response) })),
      catchError((error: unknown) =>
        isHttpError(error, 422)
          ? of(databaseValidationFailed(error))
          : this.permissionDeniedOrThrow(error, DATABASE_CREATE_DENIED),
      ),
    );
  }

  /** `403 database`（不存在、別人的、別的組織的都一樣）；網址 id 不是 GUID 時路由不符是 `404`，對畫面一樣。 */
  override getDatabaseDetail(databaseId: string): Observable<RepositoryView<DatabaseDetailView>> {
    return this.http.get<ApiDatabaseDetail>(apiDatabasePath(databaseId)).pipe(
      map((response): RepositoryView<DatabaseDetailView> => ({ status: 'ready', data: toDatabaseDetail(response) })),
      catchError((error: unknown) =>
        isHttpError(error, 404) ? of(permissionDenied(DATABASE_DENIED)) : this.permissionDeniedOrThrow(error, DATABASE_DENIED),
      ),
    );
  }

  /**
   * 指定資料管理者（issue #144）。讀取（詳情的 `access`）與寫入都走 API：清單裡的帳號必須是同
   * 組織帳號，否則 `422` 轉成 `validation-failed`、什麼都不寫入；`403`（不存在、別人的、別的組織的
   * 都一樣，非擁有者也一樣）轉成 `database` permission-denied；`409`（同時有人也在改）轉成
   * `validation-failed` 並顯示伺服器的訊息。成功回傳更新後的完整權限畫面。
   */
  override updateDatabaseAccess(
    databaseId: DatabaseId,
    dataManagerAccountIds: readonly AccountId[],
  ): Observable<UpdateDatabaseAccessResult> {
    const body: UpdateDatabaseAccessRequest = { dataManagerAccountIds: [...dataManagerAccountIds] };
    return this.http.put<ApiDatabaseAccess>(apiDatabaseAccessPath(databaseId), body).pipe(
      map((response): UpdateDatabaseAccessResult => ({ status: 'ready', data: toDatabaseAccess(response) })),
      catchError((error: unknown) =>
        isHttpError(error, 422) || isHttpError(error, 409)
          ? of<UpdateDatabaseAccessResult>({
              status: 'validation-failed',
              message: bodyMessage(error) ?? '這次指定沒有儲存，請再試一次。',
            })
          : isHttpError(error, 404)
            ? of(permissionDenied(DATABASE_DENIED))
            : this.permissionDeniedOrThrow(error, DATABASE_DENIED),
      ),
    );
  }

  /**
   * `PUT /api/v1/databases/{id}/form`（issue #143）：把欄位存成下一個版本，帶編輯時讀到的版本號。
   * `409`（有人先存過）轉成 `conflict`、`422` 轉成逐欄錯誤（`fields[i]…` 的 i 對回 `fields[i].id`）、
   * `403`／`404` 轉成 `database`；其他錯誤（5xx、連線中斷）原樣拋出，由畫面保留草稿讓使用者重試。
   */
  override updateDatabaseFields(
    databaseId: string,
    fields: readonly DatabaseFieldView[],
    baseFormVersion: number,
  ): Observable<UpdateDatabaseFieldsResult> {
    const body: SaveDatabaseFormRequest = {
      baseVersionNumber: baseFormVersion,
      fields: fields.map(toDatabaseFieldDraft),
    };
    return this.http.put<ApiDatabaseForm>(`${apiDatabasePath(databaseId)}/form`, body).pipe(
      map((response): UpdateDatabaseFieldsResult => ({
        status: 'ready',
        data: { formVersion: response.versionNumber, fields: response.fields.map(toDatabaseField) },
      })),
      catchError((error: unknown): Observable<UpdateDatabaseFieldsResult> => {
        if (isHttpError(error, 409)) {
          return of({ status: 'conflict', message: bodyMessage(error) ?? DATABASE_FORM_CHANGED_MESSAGE });
        }
        if (isHttpError(error, 422)) return of(databaseFieldsValidationFailed(error, 'fields', fields));
        return isHttpError(error, 404)
          ? of(permissionDenied(DATABASE_DENIED))
          : this.permissionDeniedOrThrow(error, DATABASE_DENIED);
      }),
    );
  }

  /**
   * `POST /api/v1/databases/{id}/form/preview`：伺服器用正式提交同一套規則驗證答案，不寫入任何資料。
   * `422` 的鍵是 `answers.<欄位 id>`。
   */
  override previewDatabaseEntry(
    databaseId: string,
    answers: DatabaseTrialAnswers,
  ): Observable<PreviewDatabaseEntryResult> {
    // OpenAPI 把「字串或字串陣列」的自由形狀寫成 Record<string, never>；實際上就是答案本身。
    const body: PreviewDatabaseEntryRequest = { answers: answers as unknown as Record<string, never> };
    return this.http.post<ApiDatabaseTrialPreview>(`${apiDatabasePath(databaseId)}/form/preview`, body).pipe(
      map((response): PreviewDatabaseEntryResult => ({
        status: 'ready',
        data: {
          saved: false,
          formVersion: response.formVersion,
          entries: response.entries.map((entry) => ({
            fieldId: toDatabaseFieldId(entry.fieldId),
            label: entry.label,
            display: entry.display,
          })),
        },
      })),
      catchError((error: unknown): Observable<PreviewDatabaseEntryResult> => {
        if (isHttpError(error, 422)) return of(databaseFieldsValidationFailed(error, 'answers', []));
        return isHttpError(error, 404)
          ? of(permissionDenied(DATABASE_DENIED))
          : this.permissionDeniedOrThrow(error, DATABASE_DENIED);
      }),
    );
  }

  /**
   * 表單連結的填寫頁（issue #145）：`GET /api/v1/databases/{id}/submission-form`。`403`（不存在、
   * 別的組織、沒有 `submit-authorized-forms` 都一樣）與 id 不是 GUID 的 `404` 都轉成 `authorized-form`。
   */
  override getDatabaseSubmissionForm(databaseId: string): Observable<RepositoryView<DatabaseSubmissionFormView>> {
    return this.http.get<ApiDatabaseSubmissionForm>(`${apiDatabasePath(databaseId)}/submission-form`).pipe(
      map((response): RepositoryView<DatabaseSubmissionFormView> => ({
        status: 'ready',
        data: {
          databaseId: response.databaseId,
          databaseName: response.databaseName,
          purpose: response.purpose,
          recipient: response.recipient,
          viewers: response.viewers,
          sensitiveNotice: response.sensitiveNotice,
          withdrawalNotice: response.withdrawalNotice,
          formVersion: response.form.versionNumber,
          fields: response.form.fields.map(toDatabaseField),
        },
      })),
      catchError((error: unknown) =>
        isHttpError(error, 404)
          ? of(permissionDenied(AUTHORIZED_FORM_DENIED))
          : this.permissionDeniedOrThrow(error, AUTHORIZED_FORM_DENIED),
      ),
    );
  }

  /** 確認同意前的預覽：`POST .../submission-form/review`，不寫入。`409` 是表單已改版。 */
  override reviewDatabaseSubmission(
    databaseId: string,
    formVersion: number,
    answers: DatabaseTrialAnswers,
  ): Observable<ReviewDatabaseSubmissionResult> {
    const body: ReviewDatabaseSubmissionRequest = {
      formVersionNumber: formVersion,
      answers: answers as unknown as Record<string, never>,
    };
    return this.http.post<ApiDatabaseTrialPreview>(`${apiDatabasePath(databaseId)}/submission-form/review`, body).pipe(
      map((response): ReviewDatabaseSubmissionResult => ({
        status: 'ready',
        data: {
          saved: false,
          formVersion: response.formVersion,
          entries: response.entries.map((entry) => ({
            fieldId: toDatabaseFieldId(entry.fieldId),
            label: entry.label,
            display: entry.display,
          })),
        },
      })),
      catchError((error: unknown) => this.submissionRefusedOrThrow(error)),
    );
  }

  /**
   * `POST /api/v1/databases/{id}/submissions`：讀寫兩端都走 API（mock 的收集紀錄不參與）。`201`
   * 新回執、`200` 同一個提交編號重送得到的同一張回執，畫面不必分辨；`422`（欄位錯誤、未同意）、
   * `409`（表單已改版、編號已用在別的內容）、`403` 轉成對應結果；5xx 與連線中斷原樣拋出，
   * 畫面保留答案並用同一個 `submissionId` 重試。
   */
  override submitDatabaseEntry(databaseId: string, input: DatabaseSubmissionInput): Observable<SubmitDatabaseEntryResult> {
    const body: SubmitDatabaseEntryRequest = {
      submissionId: input.submissionId,
      formVersionNumber: input.formVersion,
      consent: input.consent,
      answers: input.answers as unknown as Record<string, never>,
    };
    return this.http.post<ApiDatabaseSubmissionReceipt>(`${apiDatabasePath(databaseId)}/submissions`, body).pipe(
      map((response): SubmitDatabaseEntryResult => ({ status: 'ready', data: toSubmissionReceipt(response) })),
      catchError((error: unknown) => this.submissionRefusedOrThrow(error)),
    );
  }

  /** `GET /api/v1/submissions/{id}`：只有提交者本人拿得到。 */
  override getDatabaseSubmissionReceipt(
    submissionId: string,
  ): Observable<RepositoryView<DatabaseSubmissionReceiptView>> {
    return this.http.get<ApiDatabaseSubmissionReceipt>(`${API_SUBMISSIONS_PATH}/${encodeURIComponent(submissionId)}`).pipe(
      map((response): RepositoryView<DatabaseSubmissionReceiptView> => ({
        status: 'ready',
        data: toSubmissionReceipt(response),
      })),
      catchError((error: unknown) =>
        isHttpError(error, 404)
          ? of(permissionDenied(SUBMISSION_RECEIPT_DENIED))
          : this.permissionDeniedOrThrow(error, SUBMISSION_RECEIPT_DENIED),
      ),
    );
  }

  private submissionRefusedOrThrow(
    error: unknown,
  ): Observable<DatabaseFieldsValidationFailedView | DatabaseSubmissionConflictView | PermissionDeniedRepositoryView> {
    if (isHttpError(error, 409)) {
      const reason = (error.error as ForbiddenBody | null)?.reason;
      return of({
        status: 'conflict',
        reason: reason === 'submission-key-reused' ? 'submission-key-reused' : 'form-version-changed',
        message: bodyMessage(error) ?? '這份表單已更新，請重新載入後再填寫。',
      });
    }
    if (isHttpError(error, 422)) return of(databaseFieldsValidationFailed(error, 'answers', []));
    return isHttpError(error, 404)
      ? of(permissionDenied(AUTHORIZED_FORM_DENIED))
      : this.permissionDeniedOrThrow(error, AUTHORIZED_FORM_DENIED);
  }

  /**
   * `GET /api/v1/submissions`（issue #146）：自己的提交，新到舊，含已撤回的軌跡。mock 的回執不參與。
   */
  override listOwnDatabaseSubmissions(): Observable<RepositoryView<readonly OwnDatabaseSubmissionView[]>> {
    return this.http.get<ApiDatabaseOwnSubmissionList>(API_SUBMISSIONS_PATH).pipe(
      map((response): RepositoryView<readonly OwnDatabaseSubmissionView[]> => ({
        status: 'ready',
        data: response.submissions.map((submission) => ({
          id: submission.id,
          receiptNumber: submission.receiptNumber,
          submittedAt: submission.submittedAt,
          databaseId: submission.databaseId,
          databaseName: submission.databaseName,
          formVersion: submission.formVersionNumber,
          source: submission.source,
          // 後端一律送出（有效時為 null）；仍接受省略。
          withdrawnAt: submission.withdrawnAt ?? null,
        })),
      })),
      catchError((error: unknown) => this.permissionDeniedOrThrow(error, SUBMISSION_RECEIPT_DENIED)),
    );
  }

  /**
   * `POST /api/v1/submissions/{id}/withdrawal`（issue #146）：`200` 撤回後的回執（已撤回過也是同一張）；
   * `403`（不存在、別人的、別組織的同一則）與 id 不是 GUID 的 `404` 轉成 `submission-withdrawal`。
   * 5xx 與連線中斷原樣拋出：伺服器在同一個交易裡撤回，失敗就什麼都沒變，畫面可以再按一次。
   */
  override withdrawDatabaseSubmission(submissionId: string): Observable<RepositoryView<DatabaseSubmissionReceiptView>> {
    return this.http.post<ApiDatabaseSubmissionReceipt>(apiSubmissionWithdrawalPath(submissionId), {}).pipe(
      map((response): RepositoryView<DatabaseSubmissionReceiptView> => ({
        status: 'ready',
        data: toSubmissionReceipt(response),
      })),
      catchError((error: unknown) =>
        isHttpError(error, 404)
          ? of(permissionDenied(SUBMISSION_WITHDRAWAL_DENIED))
          : this.permissionDeniedOrThrow(error, SUBMISSION_WITHDRAWAL_DENIED),
      ),
    );
  }

  /**
   * `GET /api/v1/databases/{id}/tracking`（issue #146）：時間軸依追蹤對象（＝提交帳號）分組，有效紀錄與
   * 撤回軌跡分開。`403 database-records`（看得到資料庫但不能讀）照 body 的 reason；`403 database` 與
   * id 不是 GUID 的 `404` 是 `database`。趨勢比較（`comparison`）與定期回報是 #147：這裡填「尚未提供」
   * 的佔位與空陣列，畫面以 `upcomingFeatures` 的 `trends` 不顯示它們。
   */
  override getDatabaseTracking(databaseId: string): Observable<RepositoryView<DatabaseTrackingView>> {
    return this.http.get<ApiDatabaseTracking>(apiDatabaseTrackingPath(databaseId)).pipe(
      map((response): RepositoryView<DatabaseTrackingView> => ({
        status: 'ready',
        data: {
          databaseId: response.databaseId,
          subjects: response.subjects.map(toTrackedSubject),
          periodicReports: [],
        },
      })),
      catchError((error: unknown) =>
        isHttpError(error, 404)
          ? of(permissionDenied(DATABASE_DENIED))
          : this.permissionDeniedOrThrow(error, DATABASE_DENIED),
      ),
    );
  }

  // ---------- 知識庫 ----------

  override listKnowledgeBaseSummaries(): Observable<RepositoryView<readonly KnowledgeBaseSummaryView[]>> {
    return this.http.get<ApiKnowledgeBaseSummary[]>(API_KNOWLEDGE_BASES_PATH).pipe(
      map((response): RepositoryView<readonly KnowledgeBaseSummaryView[]> => ({
        status: 'ready',
        data: response.map((summary) => this.fromApiSummary(summary)),
      })),
      catchError((error: unknown) => this.permissionDeniedOrThrow(error, KNOWLEDGE_DENIED)),
    );
  }

  override getKnowledgeBaseDetail(knowledgeBaseId: string): Observable<RepositoryView<KnowledgeBaseDetailView>> {
    return this.http.get<ApiKnowledgeBaseDetail>(apiKnowledgeBasePath(knowledgeBaseId)).pipe(
      map((response): RepositoryView<KnowledgeBaseDetailView> => ({
        status: 'ready',
        data: this.fromApiDetail(response),
      })),
      catchError((error: unknown) => this.knowledgeDeniedOrThrow(error)),
    );
  }

  override createKnowledgeBase(input: CreateKnowledgeBaseInput): Observable<CreateKnowledgeBaseResult> {
    const body: CreateKnowledgeBaseRequest = { name: input.name, purpose: input.purpose };
    return this.http.post<ApiKnowledgeBaseSummary>(API_KNOWLEDGE_BASES_PATH, body).pipe(
      map((response): CreateKnowledgeBaseResult => ({ status: 'ready', data: this.fromApiSummary(response) })),
      catchError((error: unknown) =>
        isHttpError(error, 422)
          ? of(knowledgeValidationFailed(error))
          : this.permissionDeniedOrThrow(error, KNOWLEDGE_DENIED),
      ),
    );
  }

  override deleteKnowledgeBase(knowledgeBaseId: string): Observable<DeleteKnowledgeResult> {
    return this.http.delete<void>(apiKnowledgeBasePath(knowledgeBaseId)).pipe(
      map((): DeleteKnowledgeResult => ({ status: 'ready', data: null })),
      catchError((error: unknown) => this.knowledgeDeniedOrThrow(error)),
    );
  }

  override deleteKnowledgeDocument(knowledgeBaseId: string, documentId: string): Observable<DeleteKnowledgeResult> {
    return this.http.delete<void>(apiKnowledgeDocumentPath(knowledgeBaseId, documentId)).pipe(
      map((): DeleteKnowledgeResult => ({ status: 'ready', data: null })),
      catchError((error: unknown) => this.knowledgeDeniedOrThrow(error)),
    );
  }

  override retryKnowledgeDocument(
    knowledgeBaseId: string,
    documentId: string,
    versionId: string,
  ): Observable<RetryKnowledgeDocumentResult> {
    return this.http
      .post<ApiKnowledgeDocument>(apiKnowledgeRetryPath(knowledgeBaseId, documentId, versionId), null)
      .pipe(
        map((response): RetryKnowledgeDocumentResult => ({ status: 'ready', data: toKnowledgeDocument(response) })),
        catchError((error: unknown) =>
          isHttpError(error, 409)
            ? of(knowledgeValidationFailed(error))
            : this.knowledgeDeniedOrThrow(error),
        ),
      );
  }

  override updateKnowledgeSharing(
    knowledgeBaseId: string,
    sharing: KnowledgeSharingView,
  ): Observable<UpdateKnowledgeSharingResult> {
    const body: UpdateKnowledgeSharingRequest = {
      scope: sharing.scope,
      sharedWithAccountIds: [...sharing.sharedWithAccountIds],
      allowOriginalDownload: sharing.allowOriginalDownload,
    };
    return this.http.put<ApiKnowledgeSharing>(apiKnowledgeSharingPath(knowledgeBaseId), body).pipe(
      map((response): UpdateKnowledgeSharingResult => ({ status: 'ready', data: toKnowledgeSharing(response) })),
      catchError((error: unknown) =>
        isHttpError(error, 422)
          ? of(knowledgeValidationFailed(error))
          : this.knowledgeDeniedOrThrow(error),
      ),
    );
  }

  // ---------- 版本確認、抽取預覽與緊急停用（issue #47，M2 Slice 13） ----------

  override getKnowledgeDocumentDetail(
    knowledgeBaseId: string,
    documentId: string,
  ): Observable<RepositoryView<KnowledgeDocumentDetailView>> {
    return this.http.get<ApiKnowledgeDocumentDetail>(apiKnowledgeDocumentDetailPath(knowledgeBaseId, documentId)).pipe(
      map((response): RepositoryView<KnowledgeDocumentDetailView> => ({
        status: 'ready',
        data: toKnowledgeDocumentDetail(response),
      })),
      catchError((error: unknown) => this.knowledgeDeniedOrThrow(error)),
    );
  }

  override previewKnowledgeVersion(
    knowledgeBaseId: string,
    documentId: string,
    versionId: string,
  ): Observable<RepositoryView<KnowledgeVersionPreviewView>> {
    return this.http
      .get<ApiKnowledgeVersionPreview>(apiKnowledgeVersionPreviewPath(knowledgeBaseId, documentId, versionId))
      .pipe(
        map((response): RepositoryView<KnowledgeVersionPreviewView> => ({
          status: 'ready',
          data: toKnowledgeVersionPreview(response),
        })),
        catchError((error: unknown) => this.knowledgeDeniedOrThrow(error)),
      );
  }

  override updateKnowledgeChunkExclusion(
    knowledgeBaseId: string,
    documentId: string,
    versionId: string,
    chunkId: string,
    excluded: boolean,
  ): Observable<UpdateKnowledgeChunkExclusionResult> {
    const body: UpdateKnowledgeChunkExclusionRequest = { excluded };
    return this.http
      .put<ApiKnowledgeChunk>(apiKnowledgeChunkExclusionPath(knowledgeBaseId, documentId, versionId, chunkId), body)
      .pipe(
        map((response): UpdateKnowledgeChunkExclusionResult => ({
          status: 'ready',
          data: toKnowledgeChunk(response),
        })),
        catchError((error: unknown) =>
          isHttpError(error, 422) ? of(knowledgeValidationFailed(error)) : this.knowledgeDeniedOrThrow(error),
        ),
      );
  }

  override approveKnowledgeVersions(
    knowledgeBaseId: string,
    versionIds: readonly string[],
    effectiveFrom?: string,
  ): Observable<ApproveKnowledgeVersionsResult> {
    const body: ApproveKnowledgeVersionsRequest = {
      versionIds: [...versionIds],
      ...(effectiveFrom !== undefined ? { effectiveFrom } : {}),
    };
    return this.http.post<ApiKnowledgeVersion[]>(apiKnowledgeApprovePath(knowledgeBaseId), body).pipe(
      map((response): ApproveKnowledgeVersionsResult => ({
        status: 'ready',
        data: response.map(toKnowledgeVersion),
      })),
      catchError((error: unknown) =>
        isHttpError(error, 422) || isHttpError(error, 409)
          ? of(knowledgeValidationFailed(error))
          : this.knowledgeDeniedOrThrow(error),
      ),
    );
  }

  override disableKnowledgeDocument(
    knowledgeBaseId: string,
    documentId: string,
    reason: string,
  ): Observable<DisableKnowledgeDocumentResult> {
    const body: DisableKnowledgeDocumentRequest = { reason };
    return this.http.post<ApiKnowledgeDocument>(apiKnowledgeDisablePath(knowledgeBaseId, documentId), body).pipe(
      map((response): DisableKnowledgeDocumentResult => ({ status: 'ready', data: toKnowledgeDocument(response) })),
      catchError((error: unknown) =>
        isHttpError(error, 422) || isHttpError(error, 409)
          ? of(knowledgeValidationFailed(error))
          : this.knowledgeDeniedOrThrow(error),
      ),
    );
  }

  override enableKnowledgeDocument(
    knowledgeBaseId: string,
    documentId: string,
  ): Observable<EnableKnowledgeDocumentResult> {
    return this.http.post<ApiKnowledgeDocument>(apiKnowledgeEnablePath(knowledgeBaseId, documentId), null).pipe(
      map((response): EnableKnowledgeDocumentResult => ({ status: 'ready', data: toKnowledgeDocument(response) })),
      catchError((error: unknown) =>
        isHttpError(error, 409) ? of(knowledgeValidationFailed(error)) : this.knowledgeDeniedOrThrow(error),
      ),
    );
  }

  override previewKnowledgeRetrieval(
    knowledgeBaseId: string,
    question: string,
    includePending: boolean,
  ): Observable<PreviewKnowledgeRetrievalResult> {
    const body: PreviewKnowledgeRetrievalRequest = { question, includePending };
    return this.http
      .post<ApiKnowledgeRetrievalPreview>(apiKnowledgeRetrievalPreviewPath(knowledgeBaseId), body)
      .pipe(
        map((response): PreviewKnowledgeRetrievalResult => ({
          status: 'ready',
          data: toKnowledgeRetrievalPreview(response),
        })),
        catchError((error: unknown) => {
          if (isHttpError(error, 422)) return of(knowledgeValidationFailed(error));
          if (isHttpError(error, 503)) return of(retrievalUnavailable(error));
          return this.knowledgeDeniedOrThrow(error);
        }),
      );
  }

  // ---------- 對話（issue #79：讀取與對話串管理走 API；送出訊息走 AG-UI 串流，見 core/chat 與 #80） ----------

  override listChatThreads(assistantId: string): Observable<RepositoryView<ChatThreadListView>> {
    return this.http.get<ApiChatThreadListView>(apiAssistantChatConversationsPath(assistantId)).pipe(
      map((response): RepositoryView<ChatThreadListView> => ({
        status: 'ready',
        data: toChatThreadListView(response),
      })),
      catchError((error: unknown) => this.chatDeniedOrThrow(error)),
    );
  }

  override createChatThread(assistantId: string): Observable<RepositoryView<AssistantChatView>> {
    return this.http.post<ApiAssistantChatView>(apiAssistantChatConversationsPath(assistantId), null).pipe(
      map((response): RepositoryView<AssistantChatView> => ({
        status: 'ready',
        data: toAssistantChatView(response),
      })),
      catchError((error: unknown) => this.chatDeniedOrThrow(error)),
    );
  }

  override renameChatThread(
    assistantId: string,
    threadId: string,
    title: string,
  ): Observable<RenameChatThreadResult> {
    const body: RenameChatThreadRequest = { title };
    return this.http
      .patch<ApiChatThreadSummaryView>(apiAssistantChatConversationPath(assistantId, threadId), body)
      .pipe(
        map((response): RenameChatThreadResult => ({ status: 'ready', data: toChatThreadSummary(response) })),
        catchError((error: unknown) =>
          isHttpError(error, 422) ? of(chatValidationFailed(error)) : this.chatDeniedOrThrow(error),
        ),
      );
  }

  override deleteChatThread(
    assistantId: string,
    threadId: string,
  ): Observable<RepositoryView<ChatThreadListView>> {
    return this.http.delete<ApiChatThreadListView>(apiAssistantChatConversationPath(assistantId, threadId)).pipe(
      map((response): RepositoryView<ChatThreadListView> => ({
        status: 'ready',
        data: toChatThreadListView(response),
      })),
      catchError((error: unknown) => this.chatDeniedOrThrow(error)),
    );
  }

  /** 側欄的最近對話：讀取失敗時當作沒有可顯示的，不讓整個側欄跟著顯示錯誤。 */
  override listRecentChatThreads(): Observable<RepositoryView<readonly RecentConversationView[]>> {
    return this.http.get<ApiRecentConversationView[]>(API_RECENT_CONVERSATIONS_PATH).pipe(
      map((response): RepositoryView<readonly RecentConversationView[]> => ({
        status: 'ready',
        data: response.map(toRecentConversationView),
      })),
      catchError(() => of({ status: 'ready' as const, data: [] })),
    );
  }

  override getAssistantChat(
    assistantId: string,
    threadId?: string,
  ): Observable<RepositoryView<AssistantChatView>> {
    return this.http.get<ApiAssistantChatView>(apiAssistantChatPath(assistantId, threadId)).pipe(
      map((response): RepositoryView<AssistantChatView> => ({
        status: 'ready',
        data: toAssistantChatView(response),
      })),
      catchError((error: unknown) => this.chatDeniedOrThrow(error)),
    );
  }

  /**
   * 對話中表單的確認（issue #148）：`POST .../chat/forms/{databaseId}/review`，不寫入。讀寫兩端都走
   * API——表單 id 是 API 給的資料庫 GUID，不經 mock 的種子清單驗證（#49 的教訓）。
   */
  override reviewChatForm(
    _viewerId: ChatViewerId,
    assistantId: string,
    formId: DatabaseId,
    formVersion: number,
    answers: DatabaseTrialAnswers,
  ): Observable<ReviewChatFormResult> {
    const body: ReviewChatFormRequest = {
      formVersionNumber: formVersion,
      answers: answers as unknown as Record<string, never>,
    };
    return this.http.post<ApiDatabaseTrialPreview>(`${apiAssistantChatFormPath(assistantId, formId)}/review`, body).pipe(
      map((response): ReviewChatFormResult => ({
        status: 'ready',
        data: {
          formId,
          saved: false,
          entries: response.entries.map((entry) => ({
            fieldId: toDatabaseFieldId(entry.fieldId),
            label: entry.label,
            display: entry.display,
          })),
        },
      })),
      catchError((error: unknown) => this.chatFormRefusedOrThrow(error)),
    );
  }

  /**
   * 對話中表單的送出：`POST .../chat/forms/{databaseId}/submissions`。`201`／`200`（同一個
   * `submissionId` 重送）都回同一張收據與收據訊息；`403 assistant-form`（已不再連接、分享或權限
   * 收回）、`409`、`422` 轉成對應結果；5xx 與連線中斷原樣拋出，畫面以同一個編號重試。
   */
  override submitChatForm(
    _viewerId: ChatViewerId,
    assistantId: string,
    submission: ChatFormSubmission,
    threadId?: string,
  ): Observable<SubmitChatFormResult> {
    const body: SubmitChatFormRequest = {
      submissionId: submission.submissionId,
      formVersionNumber: submission.formVersion,
      consent: submission.consent,
      answers: submission.answers as unknown as Record<string, never>,
      threadId: threadId ?? null,
    };
    return this.http
      .post<ApiChatFormSubmission>(`${apiAssistantChatFormPath(assistantId, submission.formId)}/submissions`, body)
      .pipe(
        map((response): SubmitChatFormResult => {
          const message = toChatMessage(response.message);
          return { status: 'ready', data: { message, threadId: threadId ?? null } };
        }),
        catchError((error: unknown) => this.chatFormRefusedOrThrow(error)),
      );
  }

  /**
   * 對話收據的撤回（#146 接上 #148）：收據的 `recordId` 是 `record-<提交 id>`，走與表單連結相同的
   * `POST /api/v1/submissions/{id}/withdrawal`（只有提交者本人；別人的、不存在的一律
   * `submission-withdrawal`）。伺服器的撤回是冪等的，已撤回過也回 `ready`；對話表不需要改，重新讀取時
   * 收據依提交 id 即時讀回執，就會是已撤回、不含內容。
   */
  override withdrawChatSubmission(
    _viewerId: ChatViewerId,
    _assistantId: string,
    recordId: string,
  ): Observable<WithdrawChatSubmissionResult> {
    const submissionId = recordId.startsWith(RECORD_ID_PREFIX) ? recordId.slice(RECORD_ID_PREFIX.length) : recordId;
    return this.withdrawDatabaseSubmission(submissionId).pipe(
      map((result): WithdrawChatSubmissionResult =>
        result.status === 'ready' || result.status === 'partial-failure'
          ? { status: 'ready', data: toChatWithdrawal(result.data) }
          : result,
      ),
    );
  }

  private chatFormRefusedOrThrow(
    error: unknown,
  ): Observable<DatabaseFieldsValidationFailedView | DatabaseSubmissionConflictView | PermissionDeniedRepositoryView> {
    if (isHttpError(error, 409)) {
      const reason = (error.error as ForbiddenBody | null)?.reason;
      return of({
        status: 'conflict',
        reason: reason === 'submission-key-reused' ? 'submission-key-reused' : 'form-version-changed',
        message: bodyMessage(error) ?? '這份表單已更新，請重新載入最新的表單後再填寫。',
      });
    }
    if (isHttpError(error, 422)) return of(databaseFieldsValidationFailed(error, 'answers', []));
    return this.permissionDeniedOrThrow(error, ASSISTANT_FORM_DENIED);
  }

  /** `403` 的 `reason` 一定是 `assistant-use` 或 `chat-thread`；`404` 視同對話不存在。 */
  private chatDeniedOrThrow(error: unknown): Observable<PermissionDeniedRepositoryView> {
    if (isHttpError(error, 404)) return of(permissionDenied({ reason: 'chat-thread', message: '找不到這段對話，或它不屬於你的帳號。' }));
    return this.permissionDeniedOrThrow(error, CHAT_DENIED);
  }

  /**
   * 上傳一個檔案成為新文件（issue #46）：`reportProgress` 讓 `HttpClient` 送出上傳進度事件
   * （`observe: 'events'`），只轉出上傳進度與最終回應，其餘事件類型（`Sent` 等）忽略。
   * `413`／`415`／`422` 轉成逐檔的 `rejected` 結果；其餘沿用知識庫的 403／404 轉換。
   */
  override uploadKnowledgeDocument(
    knowledgeBaseId: string,
    file: File,
  ): Observable<UploadKnowledgeDocumentEvent> {
    return this.postKnowledgeUpload(apiKnowledgeDocumentsPath(knowledgeBaseId), file);
  }

  override uploadKnowledgeDocumentVersion(
    knowledgeBaseId: string,
    documentId: string,
    file: File,
  ): Observable<UploadKnowledgeDocumentEvent> {
    return this.postKnowledgeUpload(apiKnowledgeDocumentVersionsPath(knowledgeBaseId, documentId), file);
  }

  private postKnowledgeUpload(path: string, file: File): Observable<UploadKnowledgeDocumentEvent> {
    const body = new FormData();
    body.append('file', file, file.name);
    return this.http.post<ApiKnowledgeDocument>(path, body, { reportProgress: true, observe: 'events' }).pipe(
      filter(
        (event): event is HttpProgressEvent | HttpResponse<ApiKnowledgeDocument> =>
          event.type === HttpEventType.UploadProgress || event.type === HttpEventType.Response,
      ),
      map((event): UploadKnowledgeDocumentEvent => {
        if (event.type === HttpEventType.Response) {
          return { status: 'ready', data: toKnowledgeDocument(event.body as ApiKnowledgeDocument) };
        }
        // event.type === HttpEventType.UploadProgress（篩選過，只剩這兩種）
        const percent = event.total ? Math.min(99, Math.round((event.loaded / event.total) * 100)) : 0;
        return { status: 'progress', percent };
      }),
      catchError((error: unknown) =>
        isHttpError(error, 413) || isHttpError(error, 415) || isHttpError(error, 422)
          ? of(uploadRejected(error))
          : this.knowledgeDeniedOrThrow(error),
      ),
    );
  }

  private fromApiSummary(summary: ApiKnowledgeBaseSummary): KnowledgeBaseSummaryView {
    return {
      id: summary.id,
      name: summary.name,
      purpose: summary.purpose,
      documentCount: summary.documentCount,
      faqCount: summary.faqCount,
      statusCounts: { ...summary.statusCounts },
      sharingScope: summary.sharingScope,
      // 後端的知識庫回應沒有「已連接助理」，mock 助理在 API 模式也已不存在（issue #81）：
      // 畫面在 API 模式改成提示到助理的「資料來源」頁籤查看，這裡一律是空清單。
      connectedAssistantNames: [],
      updatedAt: summary.updatedAt,
      viewerCanManage: summary.viewerCanManage,
      inEffectCount: summary.inEffectCount,
      awaitingApprovalCount: summary.awaitingApprovalCount,
      disabledCount: summary.disabledCount,
    };
  }

  private fromApiDetail(detail: ApiKnowledgeBaseDetail): KnowledgeBaseDetailView {
    return {
      summary: this.fromApiSummary(detail.summary),
      documents: detail.documents.map(toKnowledgeDocument),
      connectedAssistants: [],
      sharing: toKnowledgeSharing(detail.sharing),
      shareTargets: detail.shareTargets.map(({ id, displayName }) => ({ id, displayName })),
    };
  }

  /**
   * 以 id 指定知識庫的端點：`403 knowledge-base`（不存在、別人的、別的組織的都一樣），
   * 以及 `404`——路由只接受 GUID（`{id:guid}`），網址裡不是 GUID 的 id（例如 mock 的
   * `knowledge-product-guide`）根本不會進到端點。兩者對畫面都是「無法查看這個知識庫」。
   */
  private knowledgeDeniedOrThrow(error: unknown): Observable<PermissionDeniedRepositoryView> {
    if (isHttpError(error, 404)) return of(permissionDenied(KNOWLEDGE_DENIED));
    return this.permissionDeniedOrThrow(error, KNOWLEDGE_DENIED);
  }

  /**
   * 403 轉成 permission-denied；其他錯誤（5xx、連線中斷）交給畫面的錯誤狀態。
   *
   * `reason` 照 body 實際的值對應（例如 `knowledge-base`、`password-change-required`），
   * 不再全部折成同一個；body 的值不在前端 union 裡時（後端保底的 `forbidden`），才用
   * `fallback` 的 reason。訊息優先用 body 的 `message`——後端與 mock 的訊息逐字相同。
   *
   * 團隊端點的 403 只有兩種原因：呼叫者沒有權限，或成員不存在／不屬於這個組織——
   * 後端刻意讓兩者回傳一模一樣的內容（`ApiErrors.NotFound` 就是 `Forbidden`），
   * 知識庫也一樣，所以這裡不必另外分辨「找不到」。
   */
  private permissionDeniedOrThrow(
    error: unknown,
    fallback: PermissionDeniedFallback = TEAM_DENIED,
  ): Observable<PermissionDeniedRepositoryView> {
    if (!isHttpError(error, 403)) return throwError(() => error);
    const body = (error.error ?? {}) as ForbiddenBody;
    const message = typeof body.message === 'string' ? body.message : null;
    if (body.reason === 'password-change-required') {
      return of({
        status: 'permission-denied',
        reason: 'password-change-required',
        message: message ?? PASSWORD_CHANGE_REQUIRED_MESSAGE,
      });
    }
    const reason = isRepositoryPermissionDeniedReason(body.reason) ? body.reason : fallback.reason;
    return of({ status: 'permission-denied', reason, message: message ?? fallback.message });
  }
}

function isHttpError(error: unknown, status: number): error is HttpErrorResponse {
  return error instanceof HttpErrorResponse && error.status === status;
}

function permissionDenied(fallback: PermissionDeniedFallback): PermissionDeniedRepositoryView {
  return { status: 'permission-denied', reason: fallback.reason, message: fallback.message };
}

/** `422`（逐欄錯誤）與 `409`（`reason` + `message`）的共同點：一句給人看的 `message`。 */
/** `422`：伺服器的 `message` 是第一個錯誤（模板優先，再來是名稱）。 */
function databaseValidationFailed(error: HttpErrorResponse): CreateDatabaseValidationFailedView {
  return { status: 'validation-failed', message: bodyMessage(error) ?? '請確認模板與資料庫名稱後再試一次。' };
}

/**
 * 後端 `422` 的 `errors` 是「鍵 → 訊息陣列」。儲存表單的鍵是 `fields`（整份表單）、
 * `fields[2]`、`fields[2].options`…，第 i 個對回送出的 `fields[i].id`；試填的鍵是
 * `answers.<欄位 id>`。不認得的鍵（例如 `baseVersionNumber`）算整份表單層級的錯誤。
 * 每個鍵可能有多則訊息，各自成為一筆。
 */
function databaseFieldsValidationFailed(
  error: HttpErrorResponse,
  scope: 'fields' | 'answers',
  sent: readonly DatabaseFieldView[],
): DatabaseFieldsValidationFailedView {
  const body = (error.error ?? {}) as ValidationFailedBody;
  const errors: DatabaseFieldError[] = Object.entries(body.errors ?? {}).flatMap(([key, value]) => {
    const messages = Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : [];
    const fieldId = errorKeyToFieldId(key, scope, sent);
    return messages.map((message) => ({ fieldId, message }));
  });
  const message = bodyMessage(error) ?? '還有需要修正的地方，這次沒有儲存。';
  return { status: 'validation-failed', errors: errors.length > 0 ? errors : [{ fieldId: null, message }], message };
}

function errorKeyToFieldId(
  key: string,
  scope: 'fields' | 'answers',
  sent: readonly DatabaseFieldView[],
): DatabaseFieldId | null {
  if (scope === 'answers') {
    return key.startsWith('answers.') ? toDatabaseFieldId(key.slice('answers.'.length)) : null;
  }
  const index = /^fields\[(\d+)\]/.exec(key)?.[1];
  return index === undefined ? null : (sent[Number(index)]?.id ?? null);
}

/** 送給伺服器的欄位：與 `DatabaseFieldView` 同形，量尺與單位照送，伺服器會依類型整理。 */
function toDatabaseFieldDraft(field: DatabaseFieldView): ApiDatabaseFieldDraft {
  return {
    id: field.id,
    label: field.label,
    type: field.type,
    required: field.required,
    options: [...field.options],
    scale: field.scale === null ? null : { ...field.scale },
    unit: field.unit,
  };
}

function toDatabaseFieldId(id: string): DatabaseFieldId {
  // 後端的欄位 id 一律是 `field-…`（`DatabaseFormVersion.EnsureValid`）；這裡只是讓型別對上。
  return (id.startsWith('field-') ? id : `field-${id}`) as DatabaseFieldId;
}

/** 回執：欄位快照照原樣保留；`source` 是後端的 wire name，與前端的 `DatabaseRecordSource` 相同。 */
function toSubmissionReceipt(receipt: ApiDatabaseSubmissionReceipt): DatabaseSubmissionReceiptView {
  return {
    id: receipt.id,
    receiptNumber: receipt.receiptNumber,
    submittedAt: receipt.submittedAt,
    databaseId: receipt.databaseId,
    databaseName: receipt.databaseName,
    purpose: receipt.purpose,
    recipient: receipt.recipient,
    viewers: receipt.viewers,
    formVersion: receipt.formVersionNumber,
    source: receipt.source,
    entries: receipt.entries.map((entry) => ({
      fieldId: toDatabaseFieldId(entry.fieldId),
      label: entry.label,
      display: entry.display,
    })),
    // 後端一律送出（有效時為 null）；仍接受省略，避免把有效回執誤判成已撤回或反之。
    withdrawnAt: receipt.withdrawnAt ?? null,
  };
}

/**
 * 時間軸的一位追蹤對象。id 沿用 mock 的 `subject-<帳號 id>`、紀錄 id 加上 `record-` 前綴，只為了對上
 * 前端的樣板字面型別；日期標籤取 ISO 的日期部分（UTC），與 mock 相同。
 */
function toTrackedSubject(subject: ApiDatabaseTrackedSubject): TrackedSubjectView {
  const records = subject.records.map(
    (record): DatabaseRecordView => ({
      id: `record-${record.id}`,
      recordedAt: record.submittedAt,
      dateLabel: record.submittedAt.slice(0, 10),
      source: record.source,
      entries: record.entries.map((entry) => ({
        fieldId: toDatabaseFieldId(entry.fieldId),
        label: entry.label,
        display: entry.display,
      })),
    }),
  );
  return {
    id: `subject-${subject.subject.id}`,
    displayName: subject.subject.displayName,
    records,
    withdrawals: subject.withdrawals.map(
      (trail): WithdrawnRecordView => ({
        id: `record-${trail.id}`,
        submittedAt: trail.submittedAt,
        submittedDateLabel: trail.submittedAt.slice(0, 10),
        withdrawnDateLabel: trail.withdrawnAt.slice(0, 10),
        source: trail.source,
      }),
    ),
    comparison: { status: 'insufficient-records', recordCount: records.length, message: API_TRENDS_PENDING_MESSAGE },
  };
}

function toDatabaseField(field: ApiDatabaseField): DatabaseFieldView {
  return {
    id: toDatabaseFieldId(field.id),
    label: field.label,
    type: field.type,
    required: field.required,
    options: [...field.options],
    // 後端一律送出 `scale`（不適用時是 null）；仍同時接受省略，避免回應形狀變動時整頁壞掉。
    scale: field.scale == null ? null : { ...field.scale },
    unit: field.unit ?? '',
  };
}

/**
 * 後端摘要沒有紀錄數量與已連接助理：紀錄要等資料管理者與提交（#144–#146），在那之前任何回應都
 * 不透露數量，這裡一律是 null／空陣列（畫面在 API 模式顯示「將於後續版本開放」）。
 */
function toDatabaseSummary(summary: ApiDatabaseSummary): DatabaseSummaryView {
  return {
    id: summary.id,
    name: summary.name,
    purpose: summary.purpose,
    owner: { id: summary.owner.id, displayName: summary.owner.displayName },
    // 後端一律送出 `viewerCanManage`；省略時保守地當成唯讀。
    viewerCanManage: summary.viewerCanManage === true,
    templateName: summary.templateName,
    fieldCount: summary.fieldCount,
    recordCount: null,
    subjectCount: null,
    connectedAssistantNames: [...(summary.connectedAssistantNames ?? [])],
    updatedAt: summary.updatedAt,
  };
}

function toDatabaseDetail(detail: ApiDatabaseDetail): DatabaseDetailView {
  const summary = toDatabaseSummary(detail.summary);
  return {
    summary,
    formVersion: detail.form.versionNumber,
    fields: detail.form.fields.map(toDatabaseField),
    connectedAssistants: (detail.connectedAssistants ?? []).map((assistant) => ({
      id: assistant.id as AssistantId,
      name: assistant.name,
      status: assistant.status,
    })),
    access: toDatabaseAccess(detail.access),
    upcomingFeatures: API_UPCOMING_DATABASE_FEATURES,
  };
}

function toDatabaseAccount(account: { readonly id: string; readonly displayName: string }) {
  return { id: account.id, displayName: account.displayName };
}

/**
 * 後端的權限畫面（issue #144）：`dataManagers` 是「已指定」，`effectiveReaders` 是「目前可讀」
 * （已指定且現在具備帳號層級權限）。`lastChange` 在從未變更時是 `null`；仍同時接受省略，
 * 因為後端的其他欄位曾用 `WhenWritingNull` 省略鍵，不要讓一個缺的鍵弄壞整頁。
 */
function toDatabaseAccess(access: ApiDatabaseAccess): DatabaseAccessView {
  return {
    owner: toDatabaseAccount(access.owner),
    dataManagers: access.dataManagers.map((manager) => toDatabaseAccount(manager.account)),
    effectiveReaders: access.effectiveReaders.map(toDatabaseAccount),
    viewerIsDataManager: access.viewerIsDataManager,
    viewerCanReadRecords: access.viewerCanReadRecords,
    viewerCanManageAccess: access.viewerCanManageAccess,
    candidates: access.candidates.map((candidate) => ({
      id: candidate.id,
      displayName: candidate.displayName,
      roleLabel: ACCOUNT_ROLE_LABELS[candidate.role],
      hasReadPermission: candidate.hasReadPermission,
    })),
    savedAt: access.lastChange?.changedAt ?? null,
    savedBy: access.lastChange == null ? null : toDatabaseAccount(access.lastChange.changedBy),
  };
}

function knowledgeValidationFailed(error: HttpErrorResponse): KnowledgeValidationFailedView {
  return {
    status: 'validation-failed',
    message: bodyMessage(error) ?? '這次變更沒有儲存，請再試一次。',
  };
}

interface ReasonBody {
  readonly reason?: unknown;
  readonly message?: unknown;
}

/**
 * 檢索試查的 `503`（issue #48）：`reason` 是 `embedding-unavailable` 或
 * `embedding-not-configured`，不認得的值保底當成前者（暫時性問題，可重試）。
 */
function retrievalUnavailable(error: HttpErrorResponse): KnowledgeRetrievalUnavailableView {
  const body = (error.error ?? {}) as ReasonBody;
  const reason: KnowledgeRetrievalUnavailableReason = isKnowledgeRetrievalUnavailableReason(body.reason)
    ? body.reason
    : 'embedding-unavailable';
  const message = typeof body.message === 'string' ? body.message : '嵌入模型暫時無法使用，請稍後重試。';
  return { status: 'unavailable', reason, message };
}

/** 413／415 沒有 `reason`（只有 422 的 body 有）時的保底原因。 */
function fallbackUploadReason(status: number): KnowledgeUploadRejectionReason {
  if (status === 413) return 'file-too-large';
  if (status === 415) return 'unsupported-file-type';
  return 'file-unreadable';
}

interface UploadRejectionBody {
  readonly reason?: unknown;
  readonly message?: unknown;
  readonly existingDocumentName?: unknown;
}

/** `413`／`415`／`422`：`reason` 照 body 實際的值對應，訊息與 `existingDocumentName` 原樣轉交。 */
function uploadRejected(error: HttpErrorResponse): KnowledgeUploadRejectedView {
  const body = (error.error ?? {}) as UploadRejectionBody;
  const reason = isKnowledgeUploadRejectionReason(body.reason) ? body.reason : fallbackUploadReason(error.status);
  const message = typeof body.message === 'string' ? body.message : '這個檔案沒有上傳成功，請再試一次。';
  const existingDocumentName = typeof body.existingDocumentName === 'string' ? body.existingDocumentName : undefined;
  return {
    status: 'rejected',
    reason,
    message,
    ...(existingDocumentName !== undefined ? { existingDocumentName } : {}),
  };
}

function bodyMessage(error: HttpErrorResponse): string | null {
  const body = (error.error ?? {}) as ValidationFailedBody;
  if (typeof body.message === 'string') return body.message;
  const firstField = Object.values(body.errors ?? {})[0];
  return Array.isArray(firstField) && typeof firstField[0] === 'string' ? firstField[0] : null;
}

// ---------- 助理的轉換 ----------

/**
 * 後端的助理只有 M3 資料模型的欄位（計畫第 4 節）：沒有使用對象（M3 只開放組織內部）、
 * 分享清單、資料庫與「保存對話」。清單不需要這些欄位，這裡填入 M3 的固定值；
 * 已連接的知識庫與對話保存以設定（`toAssistantSettings`）為準。
 */
function toAssistantConfiguration(
  assistant: ApiAssistantConfiguration,
  knowledgeBaseIds: readonly string[] = [],
  keepOwnConversations = true,
  databaseIds: readonly string[] = [],
): AssistantConfigurationView {
  return {
    id: assistant.id as AssistantId,
    ownerAccountId: assistant.ownerAccountId as AccountId,
    name: assistant.name,
    purpose: assistant.purpose,
    status: assistant.status,
    audience: 'account-members',
    sharedWithAccountIds: [],
    knowledgeBaseIds: [...knowledgeBaseIds],
    databaseIds: [...databaseIds],
    keepOwnConversations,
    acceptanceStatus: assistant.acceptanceStatus,
  };
}

/** `savedAt`：建立後從未修改過（`updatedAt === createdAt`）時是 null，與 mock 的「尚未編輯」相同。 */
function toAssistantSettings(settings: ApiAssistantSettings): AssistantSettingsView {
  const { configuration, rules } = settings;
  return {
    configuration: toAssistantConfiguration(
      configuration, settings.knowledgeBaseIds, rules.keepConversations, settings.databaseIds,
    ),
    sources: [
      ...settings.knowledgeBaseIds.map((id): AssistantSourceReference => ({ id, type: 'knowledge-base' })),
      ...settings.databaseIds.map((id): AssistantSourceReference => ({ id, type: 'database' })),
    ],
    tone: settings.tone,
    roleInstructions: settings.roleInstructions,
    rules: {
      knowledgeScope: rules.knowledgeScope,
      refusalMessage: rules.refusalMessage,
      showCitations: rules.showCitations,
      keepOwnConversations: rules.keepConversations,
      // 資料庫寫入（#148）來自 API；定期回報是 #150，後端還沒有。
      dataWriteDatabaseId: rules.dataWriteDatabaseId ?? null,
      dataWritePurpose: rules.dataWritePurpose,
      periodicReport: 'off',
    },
    savedAt: configuration.updatedAt === configuration.createdAt ? null : configuration.updatedAt,
  };
}

/** 草稿的 `payload` 是前端自己的 `AssistantDraft`；形狀不對（理論上不會）時當成空白草稿。 */
function toNamedDraft(draft: ApiAssistantDraft): NamedAssistantDraftView {
  return {
    id: draft.id,
    draft: normalizeDraftPayload(draft.payload) ?? createEmptyAssistantDraft(),
    savedAt: draft.savedAt,
    revision: draft.revision,
  };
}

function toTrialAnswerCitation(citation: ApiTrialAnswerCitation): TrialAnswerCitationView {
  return {
    knowledgeBaseName: citation.knowledgeBaseName,
    documentName: citation.documentName,
    locationLabel: citation.locationLabel,
    excerpt: citation.excerpt,
    score: citation.score,
  };
}

function toTrialAnswerPassage(passage: ApiTrialAnswerPassage): TrialAnswerPassageView {
  return {
    knowledgeBaseName: passage.knowledgeBaseName,
    documentName: passage.documentName,
    locationLabel: passage.locationLabel,
    excerpt: passage.excerpt,
    score: passage.score,
  };
}

/**
 * 後端用一個形狀（`kind`、`text`、`citations`、`notice`、`nextSteps` 全部都在）表示三種回覆，
 * 不像 `TrialAnswerView` 依 `kind` 分成互斥的欄位；這裡依 `kind` 只取用該種類真正需要的欄位。
 */
function toTrialAnswerReply(reply: ApiTrialAnswerReply): TrialAnswerView {
  switch (reply.kind) {
    case 'company-data':
      return {
        kind: 'company-data',
        text: reply.text,
        citations: reply.citations.map(toTrialAnswerCitation),
        citationNotice: reply.notice,
      };
    case 'general-knowledge':
      return {
        kind: 'general-knowledge',
        text: reply.text,
        notice: reply.notice ?? '',
      };
    case 'no-result':
      return {
        kind: 'no-result',
        text: reply.text,
        nextSteps: reply.nextSteps,
      };
  }
}

function toTrialAnswerResult(question: string, response: ApiTrialAnswerResponse): TrialAnswerResultView {
  return {
    question,
    reply: toTrialAnswerReply(response.reply),
    passages: response.passages.map(toTrialAnswerPassage),
    threshold: response.threshold,
  };
}

// `empty`（issue #115）：後端目前未必已回傳這個值，先收在允許清單裡，等後端補上就能生效；
// 沒收到已知值時仍退回 `ready`（與原本行為一致）。
const CONNECTABLE_STATUSES: readonly ConnectableSourceView['status'][] = [
  'ready',
  'processing',
  'needs-attention',
  'empty',
];

function toConnectableSource(source: ApiConnectableSource): ConnectableSourceView {
  const common = {
    name: source.name,
    summary: source.summary,
    permission: source.permission === 'owner' ? ('owner' as const) : ('read-only' as const),
    status: CONNECTABLE_STATUSES.find((status) => status === source.status) ?? 'ready',
    updatedAt: source.updatedAt,
  };
  if (source.type === 'database') return { ...common, id: source.id, type: 'database' };
  return {
    id: source.id,
    type: 'knowledge-base',
    name: source.name,
    summary: source.summary,
    permission: source.permission === 'owner' ? 'owner' : 'read-only',
    status: CONNECTABLE_STATUSES.find((status) => status === source.status) ?? 'ready',
    updatedAt: source.updatedAt,
  };
}

const CHANNEL_STATUSES: readonly PublishingChannelStatus[] = [
  'not-configured',
  'testing',
  'published',
  'needs-attention',
  'paused',
];

/**
 * 後端的管道 `name` 是助理名稱；前端的卡片與清單用的是管道名稱（「組織內部分享」），
 * 所以名稱依類型換成前端的固定文字。
 */
function toPublishingChannel(channel: ApiPublishingChannel): PublishingChannelView {
  const type: PublishingChannelType = channel.type === 'website' || channel.type === 'line' ? channel.type : 'platform';
  return {
    id: `channel-${type}:${channel.assistantId as AssistantId}`,
    assistantId: channel.assistantId as AssistantId,
    ownerAccountId: channel.ownerAccountId as AccountId,
    name: PUBLISHING_CHANNEL_NAMES[type],
    type,
    status: CHANNEL_STATUSES.find((status) => status === channel.status) ?? 'not-configured',
    statusDetail: channel.statusDetail,
    updatedAt: channel.updatedAt,
  };
}

function toPlatformSharing(platform: ApiPlatformSharing): PlatformSharingView {
  return {
    channel: toPublishingChannel(platform.channel),
    usagePath: platform.usagePath,
    allowedAccountIds: platform.allowedAccountIds.map((id) => id as AccountId),
    candidates: platform.candidates.map((candidate) => ({
      id: candidate.id as AccountId,
      displayName: candidate.displayName,
      // M3 的分享只看帳號，不看角色（計畫第 3 節），所以不顯示使用對象。
      audienceLabel: '組織成員',
    })),
  };
}

/** 官網與 LINE 在 M3 固定是 `not-available`：卡片顯示「尚未設定」加上後端的說明。 */
function unavailableChannel(
  type: Exclude<PublishingChannelType, 'platform'>,
  platform: PublishingChannelView,
  message: string,
): UnavailablePublishingChannelView {
  return {
    availability: 'not-available',
    message,
    channel: {
      id: `channel-${type}:${platform.assistantId}`,
      assistantId: platform.assistantId,
      ownerAccountId: platform.ownerAccountId,
      name: PUBLISHING_CHANNEL_NAMES[type],
      type,
      status: 'not-configured',
      statusDetail: message,
      updatedAt: platform.updatedAt,
    },
  };
}

function toAssistantPublishing(response: ApiAssistantPublishing): AssistantPublishingView {
  const platform = toPlatformSharing(response.platform);
  return {
    assistantId: response.assistantId as AssistantId,
    assistantName: response.assistantName,
    platform,
    website: unavailableChannel('website', platform.channel, response.website.message || EXTERNAL_PUBLISHING_NOT_AVAILABLE_MESSAGE),
    line: unavailableChannel('line', platform.channel, response.line.message || EXTERNAL_PUBLISHING_NOT_AVAILABLE_MESSAGE),
  };
}

/**
 * 後端 `422` 的 `errors` 是「欄位 → 訊息陣列」物件（`ApiErrors.ValidationFailed`），前端是
 * `{ field, message }[]`。認得的欄位照原名，其餘併到 `fallback`，訊息一則都不丟。
 */
function fieldErrorsOf<F extends string>(
  error: HttpErrorResponse,
  known: readonly F[],
  fallback: F,
): { field: F; message: string }[] {
  const body = (error.error ?? {}) as ValidationFailedBody;
  const errors: { field: F; message: string }[] = [];
  for (const [key, messages] of Object.entries(body.errors ?? {})) {
    const field = known.find((candidate) => candidate === key) ?? fallback;
    for (const message of Array.isArray(messages) ? messages : [messages]) {
      if (typeof message === 'string') errors.push({ field, message });
    }
  }
  return errors;
}

const SETTINGS_FIELDS: readonly AssistantSettingsField[] = [
  'name',
  'purpose',
  'audience',
  'tone',
  'roleInstructions',
  'knowledgeScope',
  'refusalMessage',
  'dataWritePurpose',
  'sources',
];

function settingsValidationFailed(error: HttpErrorResponse): UpdateAssistantSettingsResult {
  const errors: AssistantSettingsFieldError[] = fieldErrorsOf(error, SETTINGS_FIELDS, 'name');
  const message = bodyMessage(error) ?? errors[0]?.message ?? '這次變更沒有儲存，請再試一次。';
  return {
    status: 'validation-failed',
    errors: errors.length > 0 ? errors : [{ field: 'sources', message }],
    message,
  };
}

const DRAFT_FIELDS = Object.keys(ASSISTANT_DRAFT_FIELD_STEPS) as AssistantDraftField[];

function draftValidationFailed(error: HttpErrorResponse): CreateAssistantResult {
  const errors: AssistantDraftFieldError[] = fieldErrorsOf(error, DRAFT_FIELDS, 'name');
  return {
    status: 'validation-failed',
    errors,
    message: bodyMessage(error) ?? errors[0]?.message ?? '還有必要設定尚未完成。',
  };
}

function toKnowledgeDocument(document: ApiKnowledgeDocument): KnowledgeDocumentView {
  return {
    id: document.id,
    kind: document.kind,
    name: document.name,
    status: document.status,
    issue: document.issue,
    updatedAt: document.updatedAt,
    latestVersionId: document.latestVersionId,
    latestVersionNumber: document.latestVersionNumber,
    latestVersionState: document.latestVersionState,
    effectiveVersionNumber: document.effectiveVersionNumber,
    disabled: document.disabled,
    inEffect: document.inEffect,
  };
}

function toKnowledgeAccountRef(account: ApiKnowledgeAccount | null): KnowledgeAccountRefView | null {
  return account === null ? null : { id: account.id, displayName: account.displayName };
}

function toKnowledgeVersion(version: ApiKnowledgeVersion): KnowledgeVersionView {
  return {
    id: version.id,
    documentId: version.documentId,
    versionNumber: version.versionNumber,
    fileName: version.fileName,
    contentType: version.contentType,
    sizeBytes: version.sizeBytes,
    status: version.status,
    issue: version.issue,
    state: version.state,
    effectiveFrom: version.effectiveFrom,
    uploadedBy: toKnowledgeAccountRef(version.uploadedBy) ?? { id: version.id, displayName: '不明帳號' },
    uploadedAt: version.uploadedAt,
    approvedBy: toKnowledgeAccountRef(version.approvedBy),
    approvedAt: version.approvedAt,
    updatedAt: version.updatedAt,
  };
}

/**
 * 活動紀錄的 `action` 後端是跨知識庫事件的完整聯集（含分享、知識庫本身的建立／刪除），
 * 但這個端點（文件詳情）只會回傳與這份文件或其版本有關的子集；未知值一律當成
 * `document-uploaded`（畫面只用來挑圖示與文字，不影響任何判斷）。
 */
function toKnowledgeActivityAction(action: ApiKnowledgeActivity['action']): KnowledgeActivityAction {
  const known: readonly string[] = [
    'document-uploaded',
    'version-uploaded',
    'version-retried',
    'version-approved',
    'chunk-excluded',
    'chunk-included',
    'document-disabled',
    'document-enabled',
    'document-deleted',
  ];
  return (known.includes(action) ? action : 'document-uploaded') as KnowledgeActivityAction;
}

function toKnowledgeActivity(activity: ApiKnowledgeActivity): KnowledgeActivityView {
  return {
    id: activity.id,
    action: toKnowledgeActivityAction(activity.action),
    actor: toKnowledgeAccountRef(activity.actor),
    at: activity.at,
    versionId: activity.versionId,
    versionNumber: activity.versionNumber,
    reason: activity.reason,
  };
}

function toKnowledgeDocumentDetail(detail: ApiKnowledgeDocumentDetail): KnowledgeDocumentDetailView {
  return {
    document: toKnowledgeDocument(detail.document),
    createdAt: detail.createdAt,
    disabledAt: detail.disabledAt,
    disabledBy: toKnowledgeAccountRef(detail.disabledBy),
    disabledReason: detail.disabledReason,
    versions: detail.versions.map(toKnowledgeVersion),
    activities: detail.activities.map(toKnowledgeActivity),
  };
}

function toKnowledgeChunk(chunk: ApiKnowledgeChunk): KnowledgeChunkView {
  return { id: chunk.id, locationLabel: chunk.locationLabel, text: chunk.text, excluded: chunk.excluded };
}

function toKnowledgeExtractedUnit(unit: ApiKnowledgeExtractedUnit): KnowledgeExtractedUnitView {
  return {
    ordinal: unit.ordinal,
    locationKind: unit.locationKind,
    locationLabel: unit.locationLabel,
    readable: unit.readable,
    issueCode: unit.issueCode,
    text: unit.text,
    chunks: unit.chunks.map(toKnowledgeChunk),
  };
}

function toKnowledgeRetrievalPassage(passage: ApiKnowledgeRetrievalPassage): KnowledgeRetrievalPassageView {
  return {
    documentId: passage.documentId,
    documentName: passage.documentName,
    versionNumber: passage.versionNumber,
    versionState: passage.versionState,
    locationLabel: passage.locationLabel,
    excerpt: passage.excerpt,
    score: passage.score,
    versionId: passage.versionId,
    chunkId: passage.chunkId,
  };
}

function toKnowledgeRetrievalPreview(preview: ApiKnowledgeRetrievalPreview): KnowledgeRetrievalPreviewView {
  return {
    passages: preview.passages.map(toKnowledgeRetrievalPassage),
    threshold: preview.threshold,
    belowThreshold: preview.belowThreshold,
  };
}

function toKnowledgeVersionPreview(preview: ApiKnowledgeVersionPreview): KnowledgeVersionPreviewView {
  return {
    documentId: preview.documentId,
    versionId: preview.versionId,
    versionNumber: preview.versionNumber,
    fileName: preview.fileName,
    status: preview.status,
    issue: preview.issue,
    units: preview.units.map(toKnowledgeExtractedUnit),
  };
}

function toKnowledgeSharing(sharing: ApiKnowledgeSharing): KnowledgeSharingView {
  return {
    scope: sharing.scope,
    sharedWithAccountIds: [...sharing.sharedWithAccountIds],
    allowOriginalDownload: sharing.allowOriginalDownload,
  };
}

/** `422`（標題空白或過長）只有一句 `message`。 */
function chatValidationFailed(error: HttpErrorResponse): RenameChatThreadResult {
  return {
    status: 'validation-failed',
    message: bodyMessage(error) ?? '這次沒有儲存，請再試一次。',
  };
}

function toChatThreadSummary(summary: ApiChatThreadSummaryView): ChatThreadSummaryView {
  return {
    id: summary.id,
    title: summary.title,
    messageCount: summary.messageCount,
    updatedAt: summary.updatedAt,
  };
}

function toChatThreadListView(response: ApiChatThreadListView): ChatThreadListView {
  return {
    assistantId: response.assistantId as AssistantId,
    assistantName: response.assistantName,
    historyMode: response.historyMode as ChatThreadListView['historyMode'],
    threads: response.threads.map((thread) => ({
      id: thread.id,
      title: thread.title,
      messageCount: thread.messageCount,
      updatedAt: thread.updatedAt,
    })),
    historyNotice: response.historyNotice,
  };
}

/** API 的提交 id 對上前端紀錄 id 樣板型別（`record-${string}`）用的前綴；時間軸與對話收據相同。 */
const RECORD_ID_PREFIX = 'record-';

/**
 * 對話收據上的撤回狀態，依伺服器即時讀到的回執決定（#146）：已撤回顯示撤回日期（ISO 日期部分，UTC），
 * 否則提交者本人可以撤回。API 模式沒有匿名訪客，所以不用訪客版的說明。
 */
function toChatWithdrawal(receipt: DatabaseSubmissionReceiptView): SubmissionWithdrawalView {
  if (receipt.withdrawnAt !== null) {
    return { status: 'withdrawn', withdrawnDateLabel: receipt.withdrawnAt.slice(0, 10), notice: CHAT_WITHDRAWN_NOTICE };
  }
  return { status: 'available', withdrawnDateLabel: '', notice: `回執編號 ${receipt.receiptNumber}。${CHAT_WITHDRAWAL_NOTICE}` };
}

/**
 * 後端的 `ChatReplyView` 是同一個扁平形狀（不適用的欄位為 null），不是前端的
 * discriminated union（`docs/plans/2026-09-27-backend-milestone-3-in-platform-chat.md`
 * 第 3 節「與前端型別的差異」）；這裡依 `kind` 轉回前端的變體。`form-request` 與
 * `submission-receipt` 是 #148：表單來自伺服器（`form` 為 null 代表已無法使用），收據由
 * 伺服器依提交 id 即時讀取，訊息本身不含填寫內容。
 */
function toChatReply(reply: ApiChatReplyView): ChatReplyView {
  if (reply.kind === 'form-request') {
    return { kind: 'form-request', text: reply.text, form: reply.form ? toChatForm(reply.form) : null };
  }
  if (reply.kind === 'submission-receipt') {
    const receipt = reply.receipt ?? null;
    return {
      kind: 'submission-receipt',
      text: reply.text,
      recipient: receipt?.recipient ?? '',
      // 收據以提交 id 指認紀錄（與時間軸相同的 `record-` 前綴）；讀不到回執就不提供撤回。
      recordId: receipt === null ? null : `${RECORD_ID_PREFIX}${receipt.id}`,
      entries: receipt === null ? [] : toSubmissionReceipt(receipt).entries,
      withdrawal: receipt === null
        ? { status: 'unavailable', withdrawnDateLabel: '', notice: '目前無法讀取這張收據。' }
        : toChatWithdrawal(toSubmissionReceipt(receipt)),
    };
  }
  if (reply.kind === 'general-knowledge') {
    return { kind: 'general-knowledge', text: reply.text, notice: reply.notice ?? '' };
  }
  if (reply.kind === 'no-result') {
    return { kind: 'no-result', text: reply.text, nextSteps: [...reply.nextSteps] };
  }
  // 'company-data'，以及任何後端未來新增、前端還不認得的 kind 一律當成組織資料回覆。
  return {
    kind: 'company-data',
    text: reply.text,
    citations: reply.citations.map((citation) => ({
      id: citation.id,
      knowledgeBaseName: citation.knowledgeBaseName,
      documentName: citation.documentName,
      excerpt: citation.excerpt,
      updatedLabel: citation.updatedLabel,
    })),
    citationNotice: reply.notice ?? null,
  };
}

function toChatForm(form: ApiChatFormRequest): ChatFormView {
  return {
    id: form.id,
    title: form.title,
    formVersion: form.formVersion,
    fields: form.fields.map(toDatabaseField),
    consent: {
      recipient: form.consent.recipient,
      purpose: form.consent.purpose,
      viewers: [...form.consent.viewers],
      sensitiveNotice: form.consent.sensitiveNotice,
      withdrawalNotice: form.consent.withdrawalNotice,
    },
  };
}

/** 也給 AG-UI 串流的 `smartagri.reply` 使用（`ag-ui-chat-runner.ts`；值與 `GET chat` 的訊息相同）。 */
export function toChatMessage(message: ApiChatMessageView): ChatMessageView {
  // issue #106：後端現在一律送出 `reply`（有值或明確的 `null`），不再用
  // `JsonIgnore(WhenWritingNull)` 省略——PR #103 修過一次「省略鍵被誤判成 undefined」的 bug，
  // 這裡繼續容忍 `undefined` 只是防禦性寫法，不代表現在真的會發生。
  if (message.reply !== null && message.reply !== undefined) {
    return { id: message.id, author: 'assistant', reply: toChatReply(message.reply), createdAt: message.createdAt };
  }
  return { id: message.id, author: 'account', text: message.text ?? '', createdAt: message.createdAt };
}

function toAssistantChatView(response: ApiAssistantChatView): AssistantChatView {
  return {
    assistantId: response.assistantId as AssistantId,
    assistantName: response.assistantName,
    purpose: response.purpose,
    threadId: response.threadId,
    title: response.title,
    historyMode: response.historyMode as AssistantChatView['historyMode'],
    welcome: response.welcome,
    privacyNotice: response.privacyNotice,
    // 後端目前固定回傳 `[]`（PR #94）；id 一律當成不透明字串轉型，即使日後開放也不必再改。
    suggestedPrompts: response.suggestedPrompts.map((prompt) => ({
      id: prompt.id as AssistantChatView['suggestedPrompts'][number]['id'],
      text: prompt.text,
    })),
    messages: response.messages.map(toChatMessage),
  };
}

function toRecentConversationView(view: ApiRecentConversationView): RecentConversationView {
  return {
    assistantId: view.assistantId as AssistantId,
    assistantName: view.assistantName,
    threadId: view.threadId,
    title: view.title,
    messageCount: view.messageCount,
    updatedAt: view.updatedAt,
  };
}

/** 422：移除自己的 `manage-assistants` 或送出不認得的權限值；後端的訊息與 mock 相同。 */
function validationFailed(error: HttpErrorResponse): UpdateMemberPermissionsResult {
  return {
    status: 'validation-failed',
    message: bodyMessage(error) ?? '這次變更沒有儲存，請再試一次。',
  };
}

/** 422：登入名稱重複、欄位長度不對或不認得的角色／權限值；後端的訊息直接顯示。 */
function createMemberValidationFailed(error: HttpErrorResponse): CreateMemberResult {
  return {
    status: 'validation-failed',
    message: bodyMessage(error) ?? '這次沒有新增成員，請再試一次。',
  };
}

/** `viewerAccountId` 是目前登入者的真實帳號 GUID（來自 `/me`），不是 Demo 身分 id。 */
function toTeamMemberView(member: ApiTeamMember, viewerAccountId: string | null): TeamMemberView {
  return {
    id: member.id,
    displayName: member.displayName,
    role: member.role,
    roleLabel: ACCOUNT_ROLE_LABELS[member.role],
    roleDescription: ACCOUNT_ROLE_DESCRIPTIONS[member.role],
    permissions: normalizeMemberPermissions(member.permissions),
    isViewer: member.id === viewerAccountId,
    lockedPermissions: normalizeMemberPermissions(member.lockedPermissions),
  };
}

/** `viewerAccountId` 是目前登入者的真實帳號 GUID（來自 `/me`），不是 Demo 身分 id。 */
export function toTeamView(response: TeamResponse, viewerAccountId: string | null): TeamView {
  const members = response.members
    .map((member) => toTeamMemberView(member, viewerAccountId))
    // API 已依 id 排序；這裡改用 mock 慣用的角色順序呈現。`sort` 是穩定排序，
    // 同角色的多位成員（例如兩位 smb-internal）維持 API 回傳的順序。
    .sort((a, b) => ROLE_ORDER.indexOf(a.role) - ROLE_ORDER.indexOf(b.role));

  return { members, permissions: ACCOUNT_PERMISSIONS, savedAt: response.savedAt };
}
