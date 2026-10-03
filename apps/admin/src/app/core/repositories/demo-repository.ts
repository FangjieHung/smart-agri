import type {
  AccountId,
  AccountPermission,
  AccountRole,
  AccountView,
  ChatViewerId,
} from '../domain/account.model';
import type {
  AssistantTestCaseView, AssistantTestRunView, AssistantTestRunDetailView, AssistantTestCaseInput, AssistantTestCasePatch, AssistantTestCaseImportEntry, AssistantTestCaseExportEntry,
} from '../domain/assistant-acceptance.model';
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
  NamedAssistantDraftView,
  TrialAnswerRequest,
  TrialAnswerResultView,
  TrialAnswerUnavailableView,
  TrialAnswerValidationFailedView,
  TrialQuestionView,
} from '../domain/assistant-draft.model';
import type {
  AssistantSettingsFieldError,
  AssistantSettingsPatch,
  AssistantSettingsView,
} from '../domain/assistant-settings.model';
import type {
  CreateDatabaseInput,
  DatabaseAccessView,
  DatabaseDetailView,
  DatabaseFieldError,
  DatabaseFieldView,
  DatabaseFormSavedView,
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
  ChatThreadListView,
  ChatThreadSummaryView,
  ConversationId,
  PrivateConversationView,
  RecentConversationView,
  StructuredSubmissionView,
} from '../domain/conversation.model';
import type {
  CreateKnowledgeBaseInput,
  KnowledgeBaseDetailView,
  KnowledgeBaseId,
  KnowledgeBaseSummaryView,
  KnowledgeBaseView,
  KnowledgeChunkView,
  KnowledgeDocumentDetailView,
  KnowledgeDocumentId,
  KnowledgeDocumentView,
  KnowledgeRetrievalPreviewView,
  KnowledgeRetrievalUnavailableReason,
  KnowledgeSharingView,
  KnowledgeUploadRejectionReason,
  KnowledgeVersionPreviewView,
  KnowledgeVersionView,
} from '../domain/knowledge-base.model';
import type { Observable } from 'rxjs';
import type { AssistantAnalyticsSummaryView, OperationsSummaryView } from '../domain/operations.model';
import type { TeamMemberView, TeamView } from '../domain/team.model';
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

/** Demo 可切換的畫面情境；`ready` 以外都用於預覽錯誤與等待狀態。 */
export const DEMO_SCENARIOS = [
  'ready',
  'loading',
  'partial-failure',
  'permission-denied',
  'disconnected-channel',
  // 對話（issue #80）：串流完的回答沒有通過引用驗證，整則換成「查無資料」。
  'answer-rejected',
] as const;

export type DemoScenario = (typeof DEMO_SCENARIOS)[number];

export type RepositoryUnavailableResource = 'knowledge-sync';

export const REPOSITORY_PERMISSION_DENIED_REASONS = [
  'scenario',
  'private-conversation',
  'assistant-configuration',
  'authorized-form',
  'assistant-draft',
  'knowledge-base',
  'database',
  'database-records',
  'assistant-use',
  'chat-thread',
  'submission-withdrawal',
  'publishing',
  'team',
  'assistant-issue',
  /** API 模式：帳號仍是 `setup` 的一次性密碼，設定新密碼前其他端點一律拒絕。 */
  'password-change-required',
] as const;

export type RepositoryPermissionDeniedReason = (typeof REPOSITORY_PERMISSION_DENIED_REASONS)[number];

export function isRepositoryPermissionDeniedReason(
  value: unknown,
): value is RepositoryPermissionDeniedReason {
  return (REPOSITORY_PERMISSION_DENIED_REASONS as readonly unknown[]).includes(value);
}

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

/**
 * 精靈草稿保存時的 `revision` 已經不是最新（另一個分頁先存過）：API 的
 * `409 draft-revision-conflict`。這次保存完全沒有寫入，畫面要請使用者重新載入。
 */
export interface DraftConflictView {
  readonly status: 'conflict';
  readonly message: string;
}

export type SaveAssistantDraftResult = RepositoryView<NamedAssistantDraftView> | DraftConflictView;

/**
 * 試問結果（issue #82）：422（問題空白或超過長度限制）與 503（嵌入或對話模型未設定、
 * 呼叫失敗）各自獨立於 `RepositoryView`（403／loading／partial-failure）之外，
 * 因為兩者都不是「讀不到」，而是這次呼叫本身被拒絕或暫時無法完成。
 */
export type PreviewTrialAnswerResult =
  | RepositoryView<TrialAnswerResultView>
  | TrialAnswerValidationFailedView
  | TrialAnswerUnavailableView;

/** 刪除成功時 `data` 是 null（API 回 `204`）。 */
export type DeleteAssistantResult = RepositoryView<null>;

export interface AssistantSettingsValidationFailedView {
  readonly status: 'validation-failed';
  readonly errors: readonly AssistantSettingsFieldError[];
  readonly message: string;
}

export type UpdateAssistantSettingsResult =
  | RepositoryView<AssistantSettingsView>
  | AssistantSettingsValidationFailedView;

/**
 * 知識庫寫入被拒絕但可以修正後再試：API 的 `422`（名稱、分享對象）與 `409`（版本還在處理、
 * 不是失敗的版本不能重試）。只有一句給人看的訊息，畫面直接顯示，不必分辨是哪一個欄位。
 */
export interface KnowledgeValidationFailedView {
  readonly status: 'validation-failed';
  readonly message: string;
}

/** 舊名稱，與 `KnowledgeValidationFailedView` 相同。 */
export type SharingValidationFailedView = KnowledgeValidationFailedView;

export type UpdateKnowledgeSharingResult =
  | RepositoryView<KnowledgeSharingView>
  | KnowledgeValidationFailedView;

export type CreateKnowledgeBaseResult =
  | RepositoryView<KnowledgeBaseSummaryView>
  | KnowledgeValidationFailedView;

export type RetryKnowledgeDocumentResult =
  | RepositoryView<KnowledgeDocumentView>
  | KnowledgeValidationFailedView;

/** 刪除成功時 `data` 是 null（API 回 `204`，沒有內容可以回傳）。 */
export type DeleteKnowledgeResult = RepositoryView<null>;

/**
 * 版本確認相關（M2 Slice 13，issue #47）：排除段落、批次確認生效、緊急停用／恢復皆共用
 * `KnowledgeValidationFailedView`——422（例如批次確認時有版本不可確認、停用未填原因）與
 * 409（例如同時有其他人變更、文件已停用／已啟用）都只有一句給人看的訊息，畫面直接顯示。
 */
export type UpdateKnowledgeChunkExclusionResult =
  | RepositoryView<KnowledgeChunkView>
  | KnowledgeValidationFailedView;

/**
 * 批次確認生效：回傳的清單與 `versionIds` 一一對應（每個 id 各自現在的狀態）。
 * 只要其中一個版本不可確認（不屬於這個知識庫、還在處理中、已經確認過），整批都不會寫入，
 * 回傳 `validation-failed`，訊息會點名是哪些版本。
 */
export type ApproveKnowledgeVersionsResult =
  | RepositoryView<readonly KnowledgeVersionView[]>
  | KnowledgeValidationFailedView;

export type DisableKnowledgeDocumentResult =
  | RepositoryView<KnowledgeDocumentView>
  | KnowledgeValidationFailedView;

export type EnableKnowledgeDocumentResult =
  | RepositoryView<KnowledgeDocumentView>
  | KnowledgeValidationFailedView;

/**
 * 檢索試查（issue #48，M2 Slice 14）：嵌入模型暫時無法使用，或部署尚未設定嵌入模型
 * （API 的 `503`）。`reason` 是機器可讀的原因，`message` 是可直接顯示的訊息。
 */
export interface KnowledgeRetrievalUnavailableView {
  readonly status: 'unavailable';
  readonly reason: KnowledgeRetrievalUnavailableReason;
  readonly message: string;
}

/**
 * 問題空白或超過 500 字回傳 validation-failed；嵌入模型的問題回傳 `unavailable`；
 * 其餘讀取以外的錯誤（5xx、連線中斷）以 Observable 的 error 傳出。
 */
export type PreviewKnowledgeRetrievalResult =
  | RepositoryView<KnowledgeRetrievalPreviewView>
  | KnowledgeValidationFailedView
  | KnowledgeRetrievalUnavailableView;

/**
 * 一個檔案被拒絕上傳（issue #46，M2 Slice 12）：`reason` 是機器可讀的原因（與後端
 * `KnowledgeUploadRejectionReason` 逐字相同），`message` 是給人看的訊息。
 * `existingDocumentName` 只在 `duplicate-content` 時有值：內容相同的既有文件名稱。
 */
export interface KnowledgeUploadRejectedView {
  readonly status: 'rejected';
  readonly reason: KnowledgeUploadRejectionReason;
  readonly message: string;
  readonly existingDocumentName?: string;
}

/**
 * 上傳中的進度（0–100）。`uploadKnowledgeDocument` 的 Observable 在完成前可以送出
 * 0 到多次進度事件，最後一定以 `UploadKnowledgeDocumentResult` 其中一種結束並 complete；
 * mock 不會送出進度事件（沒有真的網路傳輸可以量）。
 */
export interface KnowledgeUploadProgressView {
  readonly status: 'progress';
  readonly percent: number;
}

/**
 * 單一檔案上傳的最終結果：成功、被拒絕，或沒有權限（知識庫不存在、不是自己的）；
 * `loading`／`partial-failure` 只在 Demo 情境切換器選了對應情境時出現。
 * 讀取以外的錯誤（5xx、連線中斷）以 Observable 的 error 傳出，由畫面顯示「無法上傳」。
 */
export type UploadKnowledgeDocumentResult =
  | RepositoryView<KnowledgeDocumentView>
  | KnowledgeUploadRejectedView;

export type UploadKnowledgeDocumentEvent = KnowledgeUploadProgressView | UploadKnowledgeDocumentResult;

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

/**
 * 儲存表單時 `baseFormVersion` 已經不是最新（另一個人或另一個分頁先存過）：API 的
 * `409 form-version-changed`。這次完全沒有寫入，畫面要請使用者重新載入再改。
 */
export interface DatabaseFieldsConflictView {
  readonly status: 'conflict';
  readonly message: string;
}

export type UpdateDatabaseFieldsResult =
  | RepositoryView<DatabaseFormSavedView>
  | DatabaseFieldsValidationFailedView
  | DatabaseFieldsConflictView;

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

export type WithdrawChatSubmissionResult =
  | RepositoryView<AssistantChatView>
  | ChatValidationFailedView;

export type RenameChatThreadResult =
  | RepositoryView<ChatThreadSummaryView>
  | ChatValidationFailedView;

export interface PublishingValidationFailedView {
  readonly status: 'validation-failed';
  readonly errors: readonly PublishingFieldError[];
  readonly message: string;
}

export type UpdatePlatformSharingResult =
  | RepositoryView<PlatformSharingView>
  | PublishingValidationFailedView;

export interface TeamValidationFailedView {
  readonly status: 'validation-failed';
  readonly message: string;
}

export type UpdateMemberPermissionsResult =
  | RepositoryView<TeamView>
  | TeamValidationFailedView;

/** 新增成員的輸入；`role` 與 `permissions` 由團隊設定的表單提供。 */
export interface CreateMemberInput {
  readonly loginName: string;
  readonly displayName: string;
  readonly role: AccountRole;
  readonly permissions: readonly AccountPermission[];
}

/**
 * 新增成功的結果：新成員（可以直接併入團隊清單，或等 `getTeam()` 重新整批讀取）與
 * 一次性密碼。`oneTimePassword` 只在這次回應中出現一次——重新整理頁面或重新讀取
 * 團隊清單都拿不回來，畫面必須當場顯示完就不再保留。
 */
export interface CreatedMemberView {
  readonly member: TeamMemberView;
  readonly oneTimePassword: string;
}

export type CreateMemberResult =
  | RepositoryView<CreatedMemberView>
  | TeamValidationFailedView;

export interface DatabaseAccessValidationFailedView {
  readonly status: 'validation-failed';
  readonly message: string;
}

export type UpdateDatabaseAccessResult =
  | RepositoryView<DatabaseAccessView>
  | DatabaseAccessValidationFailedView;

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
  /**
   * 團隊與權限。Demo 的「團隊」就是三個 Demo 身分，不是真實身分系統：沒有邀請、
   * 沒有離職、沒有密碼，只能改「每個成員被允許做什麼」。只有具備 `manage-assistants`
   * 的帳號看得到，其餘一律回傳 `team` 的 permission-denied，訊息不含任何成員名稱。
   *
   * 第一組改成非同步契約的方法（M2 之後各功能區沿用同一個模式）：viewer 由工作階段推導，
   * 不再由呼叫端傳入；回傳 cold Observable，訂閱時才讀取。
   */
  getTeam(): Observable<RepositoryView<TeamView>>;
  /**
   * 變更單一成員的權限並立刻套用到所有檢查點（`listAccounts()` 之後就會回傳新的值）。
   * 只有具備 `manage-assistants` 的帳號可以呼叫；成員不存在與無權限回傳相同結果。
   * 操作者不能移除自己的 `manage-assistants`（移除後就打不開團隊設定），
   * 這種情況與不認得的權限值一樣回傳 validation-failed，完全不寫入。
   */
  updateMemberPermissions(
    memberAccountId: AccountId,
    permissions: readonly AccountPermission[],
  ): Observable<UpdateMemberPermissionsResult>;
  /**
   * 新增這個組織的成員帳號（issue #52，M2 Slice 18）：只有具備 `manage-assistants` 的帳號可以
   * 呼叫；同組織登入名稱重複回傳 validation-failed，不同組織可以重複。回傳的一次性密碼只在
   * 這次結果中出現一次。
   */
  createMember(input: CreateMemberInput): Observable<CreateMemberResult>;
  /*
   * 助理（M3 Slice 11，issue #81）：沿用 `getTeam`／知識庫的非同步契約——viewer 由工作階段
   * 推導，不由呼叫端傳入；回傳 cold Observable，訂閱時才讀取或寫入。API 模式
   * （`HybridDemoRepository`）走 `/api/v1/assistants`，mock 以 `defer(() => of(...))` 實作。
   * 以 id 指定的方法：id 來自網址、未經驗證；不存在或不是自己的一律回傳相同的
   * permission-denied，訊息不含助理名稱。讀取以外的錯誤以 Observable 的 error 傳出。
   */
  /** 目前帳號擁有的助理（管理清單）；沒有 `manage-assistants` 時是空清單。 */
  listAssistantConfigurations(): Observable<RepositoryView<readonly AssistantConfigurationView[]>>;
  /** 目前帳號可以開啟對話的助理：自己的，加上分享給自己的（API：`?usable=true`）。 */
  listUsableAssistants(): Observable<RepositoryView<readonly AssistantSummaryView[]>>;
  getAssistantSources(
    viewerAccountId: AccountId,
    assistantId: AssistantId,
  ): RepositoryView<readonly AssistantSourceReference[]>;
  /**
   * 建立後可編輯的助理設定（概覽／資料來源／回答與記錄三個頁籤共用同一份）。
   * 不存在或非擁有者一律回傳相同的 `assistant-configuration` permission-denied。
   */
  listAssistantTestCases(assistantId: string): Observable<RepositoryView<readonly AssistantTestCaseView[]>>;
  createAssistantTestCase(assistantId: string, input: AssistantTestCaseInput): Observable<RepositoryView<AssistantTestCaseView>>;
  updateAssistantTestCase(assistantId: string, caseId: string, patch: AssistantTestCasePatch): Observable<RepositoryView<AssistantTestCaseView>>;
  deleteAssistantTestCase(assistantId: string, caseId: string): Observable<RepositoryView<null>>;
  importAssistantTestCases(assistantId: string, questions: readonly AssistantTestCaseImportEntry[]): Observable<RepositoryView<readonly AssistantTestCaseView[]>>;
  exportAssistantTestCases(assistantId: string): Observable<RepositoryView<readonly AssistantTestCaseExportEntry[]>>;
  listAssistantTestRuns(assistantId: string): Observable<RepositoryView<readonly AssistantTestRunView[]>>;
  createAssistantTestRun(assistantId: string): Observable<RepositoryView<AssistantTestRunView>>;
  getAssistantTestRun(assistantId: string, runId: string): Observable<RepositoryView<AssistantTestRunDetailView>>;
  getAssistantSettings(assistantId: string): Observable<RepositoryView<AssistantSettingsView>>;
  /**
   * 自動保存單次變更，立即套用到這個已存在的助理上，沒有「儲存」按鈕。
   * 驗證不通過時完全不寫入（全有或全無），並回傳逐欄錯誤。
   */
  updateAssistantSettings(
    assistantId: string,
    patch: AssistantSettingsPatch,
  ): Observable<UpdateAssistantSettingsResult>;
  /**
   * 連接或解除連接單一資料來源；只保存 id 與類型，不複製來源內容。
   * 只接受這個帳號可以連接的來源，也不接受解除最後一個來源（validation-failed）。
   * API 模式的資料庫來源一律回傳 validation-failed（資料庫將於後續版本開放）。
   */
  setAssistantSourceConnection(
    assistantId: string,
    source: AssistantSourceReference,
    connected: boolean,
  ): Observable<UpdateAssistantSettingsResult>;
  /**
   * 刪除助理，連同**所有成員**與它的對話紀錄（M3 計畫決定 G）；無法復原。
   * 不存在或非擁有者回傳 `assistant-configuration` permission-denied。
   */
  deleteAssistant(assistantId: string): Observable<DeleteAssistantResult>;
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
  getAssistantAnalyticsSummary(assistantId: string): Observable<RepositoryView<AssistantAnalyticsSummaryView>>;
  getOperationsSummary(): Observable<RepositoryView<OperationsSummaryView>>;
  /** 目前帳號可以設定發布的助理的所有管道（每個助理固定三個）。 */
  listPublishingChannels(): Observable<RepositoryView<readonly PublishingChannelView[]>>;
  /** 發布管道總覽：依助理分組，每個助理固定平台內、官網與 LINE 三個管道。 */
  listChannelOverview(): Observable<RepositoryView<readonly AssistantChannelsView[]>>;
  /**
   * 單一助理的三個管道設定。id 來自網址、未經驗證；不存在或非擁有者時一律回傳
   * 相同的 `publishing` permission-denied，訊息不包含資源名稱。API 模式的官網與 LINE
   * 是 `UnavailablePublishingChannelView`（對外發布將於後續版本開放）。
   */
  getAssistantPublishing(assistantId: string): Observable<RepositoryView<AssistantPublishingView>>;
  /** 指定可在平台內使用助理的帳號（整份取代）；空清單代表只有擁有者自己可以使用。 */
  updatePlatformSharing(
    assistantId: string,
    accountIds: readonly AccountId[],
  ): Observable<UpdatePlatformSharingResult>;
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
    assistantId: string,
    channelType: PublishingChannelType,
    paused: boolean,
  ): Observable<RepositoryView<PublishingChannelView>>;
  listAssistantTemplates(): RepositoryView<readonly AssistantTemplateView[]>;
  /**
   * 知識庫與資料庫混合的可連接來源清單；沿用 `listKnowledgeBaseSummaries` 的非同步契約
   * ——viewer 由工作階段推導，不由呼叫端傳入，回傳 cold Observable，訂閱時才讀取。
   * API 模式（`HybridDemoRepository`）的知識庫走 HTTP，資料庫仍是 mock（M2 範圍外）。
   */
  listConnectableSources(): Observable<RepositoryView<readonly ConnectableSourceView[]>>;
  listTrialQuestions(): RepositoryView<readonly TrialQuestionView[]>;
  /**
   * 試問與正式對話用同一套回覆類型（issue #82，M3 計畫 Slice 12）：API 模式呼叫真實回答
   * 流程（`POST /api/v1/assistant-drafts/{id}/trial-answers`），Mock 模式以固定 fixture
   * 模擬。API 模式以伺服器上保存的草稿為準，`request.sources`／`request.rules` 只有
   * Mock 用得到。viewer 由工作階段推導，回傳 cold Observable，訂閱時才讀取。
   */
  previewTrialAnswer(
    draftId: string,
    request: TrialAnswerRequest,
  ): Observable<PreviewTrialAnswerResult>;
  /*
   * 精靈草稿（M3 Slice 11）：每個帳號可以有多份具名草稿，只有擁有者看得到。需要
   * `manage-assistants`，否則回傳 `assistant-draft` permission-denied；以 id 指定的方法
   * 遇到不存在或別人的草稿也回傳同一個 permission-denied。
   */
  /** 目前帳號的具名草稿，最近保存的在前。 */
  listNamedAssistantDrafts(): Observable<RepositoryView<readonly NamedAssistantDraftView[]>>;
  /** 新增一份空白草稿。 */
  createNamedAssistantDraft(): Observable<RepositoryView<NamedAssistantDraftView>>;
  getNamedAssistantDraft(draftId: string): Observable<RepositoryView<NamedAssistantDraftView>>;
  /**
   * 保存整份草稿。`revision` 是最近一次讀到或存到的版本；已經不是最新（另一個分頁先存過）
   * 時回傳 `conflict`，完全不寫入。成功時回傳的 `revision` 是下一次保存要帶的值。
   */
  saveNamedAssistantDraft(
    draftId: string,
    draft: AssistantDraft,
    revision: number,
  ): Observable<SaveAssistantDraftResult>;
  discardNamedAssistantDraft(draftId: string): Observable<RepositoryView<null>>;
  /**
   * 由已保存的草稿建立助理（API：`POST /api/v1/assistants { draftId }`），成功後刪除該草稿。
   * 呼叫前要先把 `draft` 保存到 `draftId`——API 以伺服器上的草稿為準，mock 驗證 `draft`。
   * 逐欄驗證失敗回傳 validation-failed，草稿保留、不建立任何助理。
   */
  createAssistantFromDraft(draftId: string, draft: AssistantDraft): Observable<CreateAssistantResult>;
  /*
   * 知識庫（M2 Slice 11）：沿用 `getTeam` 的非同步契約——viewer 由工作階段推導，不由
   * 呼叫端傳入；回傳 cold Observable，訂閱時才讀取或寫入。mock 以 `defer(() => of(...))`
   * 實作，API 模式（`HybridDemoRepository`）走 HTTP，畫面不必知道是哪一種。
   *
   * 所有以 id 指定的方法：id 來自網址或畫面、未經驗證；不存在、屬於別人或屬於其他組織
   * 一律回傳相同的 `knowledge-base` permission-denied，訊息不含資源名稱。讀取以外的錯誤
   * （5xx、連線中斷）以 Observable 的 error 傳出，由畫面顯示「目前無法載入」。
   */
  /** 目前帳號擁有的知識庫摘要：文件／FAQ 數量、狀態統計、分享範圍與已連接助理。 */
  listKnowledgeBaseSummaries(): Observable<RepositoryView<readonly KnowledgeBaseSummaryView[]>>;
  /**
   * 知識庫詳情。處理狀態依「開始處理後經過的時間」計算（mock）或由後端工作更新（API），
   * 所以畫面在有等待中或處理中的項目時重新讀取即可看到進度，不需要另外推進。
   */
  getKnowledgeBaseDetail(knowledgeBaseId: string): Observable<RepositoryView<KnowledgeBaseDetailView>>;
  /**
   * 建立一個只有自己看得到（`private`）的空知識庫。需要 `manage-data-sources`，否則回傳
   * `knowledge-base` permission-denied；名稱空白或過長回傳 validation-failed，完全不寫入。
   */
  createKnowledgeBase(input: CreateKnowledgeBaseInput): Observable<CreateKnowledgeBaseResult>;
  /** 刪除知識庫與其中所有文件、FAQ 與分享設定；無法復原。 */
  deleteKnowledgeBase(knowledgeBaseId: KnowledgeBaseId): Observable<DeleteKnowledgeResult>;
  /** 刪除單一文件或 FAQ（含所有版本）；無法復原。 */
  deleteKnowledgeDocument(
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
  ): Observable<DeleteKnowledgeResult>;
  /**
   * 把處理失敗的版本重新排入處理；等待中、處理中或不是失敗的版本回傳 validation-failed
   * （API 的 `409`）。`versionId` 是清單上的 `latestVersionId`。
   */
  retryKnowledgeDocument(
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
    versionId: string,
  ): Observable<RetryKnowledgeDocumentResult>;
  /** 指定帳號分享時至少要選一個分享對象，否則回傳 validation-failed，完全不寫入。 */
  updateKnowledgeSharing(
    knowledgeBaseId: KnowledgeBaseId,
    sharing: KnowledgeSharingView,
  ): Observable<UpdateKnowledgeSharingResult>;
  /**
   * 上傳一個檔案成為知識庫的新文件（issue #46，M2 Slice 12）：每個檔案各自呼叫一次，
   * 畫面自己決定並行數與逐檔重試，這裡只負責一個檔案。回傳的 Observable 依序送出
   * 0 到多次上傳進度、最後一個結果（成功、被拒絕或沒有權限）後才 complete；訂閱時才真正
   * 送出（cold）。前端已用 `precheckKnowledgeUpload` 檢查過副檔名與大小，但這裡仍會重新
   * 檢查一次——後端規則若改變，畫面不需要跟著改。
   */
  uploadKnowledgeDocument(
    knowledgeBaseId: KnowledgeBaseId,
    file: File,
  ): Observable<UploadKnowledgeDocumentEvent>;
  /**
   * 檔名重複時，改把這個檔案當成既有文件的新版本上傳（`documentId` 來自畫面依檔名找到的
   * 那份文件）。文件保留原本的名稱，版本保留自己的檔名；內容與知識庫中任何版本相同一律
   * 回傳 `duplicate-content`。新版本一律待審核，不會立刻取代目前生效的版本。
   */
  uploadKnowledgeDocumentVersion(
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
    file: File,
  ): Observable<UploadKnowledgeDocumentEvent>;
  /**
   * 文件詳情（issue #47，M2 Slice 13）：版本歷程（新到舊）與活動紀錄（新到舊，含停用原因）。
   * id 來自畫面、未經驗證；不存在或不是自己的知識庫一律回傳相同的 `knowledge-base`
   * permission-denied。
   */
  getKnowledgeDocumentDetail(
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
  ): Observable<RepositoryView<KnowledgeDocumentDetailView>>;
  /**
   * 抽取預覽：依頁／章節／工作表列出抽取單位，標示不可讀的單位與原因，並列出每個單位
   * 切出的段落（含目前是否被排除）。
   */
  previewKnowledgeVersion(
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
    versionId: string,
  ): Observable<RepositoryView<KnowledgeVersionPreviewView>>;
  /** 切換單一段落是否「不納入檢索」；設回原本的值不會多寫一筆活動紀錄。 */
  updateKnowledgeChunkExclusion(
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
    versionId: string,
    chunkId: string,
    excluded: boolean,
  ): Observable<UpdateKnowledgeChunkExclusionResult>;
  /**
   * 批次確認一或多個版本生效；省略 `effectiveFrom` 代表立即生效。任何一個版本不可確認
   * （不屬於這個知識庫、還在等待或處理中、已經確認過）就整批不寫入，回傳
   * validation-failed，點名是哪些版本。
   */
  approveKnowledgeVersions(
    knowledgeBaseId: KnowledgeBaseId,
    versionIds: readonly string[],
    effectiveFrom?: string,
  ): Observable<ApproveKnowledgeVersionsResult>;
  /**
   * 緊急停用：無論任何版本的確認狀態，立刻讓整份文件不再被引用；必須填寫原因，
   * 原因會保留在活動紀錄。已停用的文件回傳 validation-failed（API 的 `409`）。
   */
  disableKnowledgeDocument(
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
    reason: string,
  ): Observable<DisableKnowledgeDocumentResult>;
  /**
   * 恢復：回到停用前的狀態，加上停用期間新確認生效的版本。不是停用中的文件回傳
   * validation-failed（API 的 `409`）。
   */
  enableKnowledgeDocument(
    knowledgeBaseId: KnowledgeBaseId,
    documentId: KnowledgeDocumentId,
  ): Observable<EnableKnowledgeDocumentResult>;
  /**
   * 檢索試查（issue #48，M2 Slice 14）：與正式對話同一套檢索邏輯（後端 `KnowledgeRetriever`）
   * 試問這個知識庫，不生成任何回答——用來確認一份文件會不會被引用、引用到第幾頁。
   * `includePending` 額外納入每份文件最新的待確認／排程生效版本（`versionState` 標成該狀態；
   * 只給試查用，正式對話一律只看生效版本）。任何有權開啟這個知識庫的人都可以試查。
   */
  previewKnowledgeRetrieval(
    knowledgeBaseId: KnowledgeBaseId,
    question: string,
    includePending: boolean,
  ): Observable<PreviewKnowledgeRetrievalResult>;
  /**
   * 資料庫入口先問「你要收集什麼」；只有可管理資料來源的帳號可以取得模板（否則
   * `database` permission-denied）。API 模式：`GET /api/v1/database-templates`（issue #142）。
   */
  listDatabaseTemplates(): Observable<RepositoryView<readonly DatabaseTemplateView[]>>;
  /**
   * 目前帳號擁有的資料庫摘要；非指定資料管理者看不到紀錄數量（API 模式在 #144／#146 之前
   * 一律為 null）。API 模式：`GET /api/v1/databases`。
   */
  listDatabaseSummaries(): Observable<RepositoryView<readonly DatabaseSummaryView[]>>;
  /**
   * 以模板建立資料庫，模板欄位成為初始表單。沒有資料來源管理權限是 `database`
   * permission-denied；名稱或模板無效是 `validation-failed`。讀取以外的失敗（5xx、連線中斷）
   * 以 Observable 的 error 傳出，畫面保留輸入讓使用者重試。API 模式：`POST /api/v1/databases`。
   */
  createDatabaseFromTemplate(input: CreateDatabaseInput): Observable<CreateDatabaseResult>;
  /**
   * 資料庫詳情。id 來自網址、未經驗證；不存在或無權限時一律回傳相同的
   * permission-denied，訊息不包含資源名稱。API 模式：`GET /api/v1/databases/{id}`。
   */
  getDatabaseDetail(databaseId: string): Observable<RepositoryView<DatabaseDetailView>>;
  /**
   * 把欄位存成下一個表單版本（舊版本不變）；只接受六種欄位類型，不支援條件跳題或公式。
   * `baseFormVersion` 是編輯時讀到的 `DatabaseDetailView.formVersion`，不是最新就回 `conflict`、
   * 不寫入。欄位錯誤（422）逐欄回報；欄位與 `fields` 順序一一對應。
   */
  updateDatabaseFields(
    databaseId: DatabaseId,
    fields: readonly DatabaseFieldView[],
    baseFormVersion: number,
  ): Observable<UpdateDatabaseFieldsResult>;
  /**
   * 指定誰是這個資料庫的資料管理者，也就是誰可以查看收集紀錄與趨勢比較。
   * 只有資料庫擁有者可以變更；不存在與無權限回傳相同的 `database` permission-denied。
   *
   * 被指定的帳號還必須有帳號層級的「查看同意提交的紀錄」權限才真的看得到
   * （`canReadConsentedRecords()`）；候選清單會標示誰目前還沒有。
   * **移除只收回查看權限，不刪除任何紀錄**：重新指定就原封不動回來。
   */
  updateDatabaseAccess(
    viewerAccountId: AccountId,
    databaseId: DatabaseId,
    dataManagerAccountIds: readonly AccountId[],
  ): UpdateDatabaseAccessResult;
  /**
   * 試填：以目前已儲存的表單驗證答案並回傳預覽，不會建立紀錄。API 模式由伺服器驗證，用的是
   * 日後正式提交（#145）同一套規則。
   */
  previewDatabaseEntry(
    databaseId: DatabaseId,
    answers: DatabaseTrialAnswers,
  ): Observable<PreviewDatabaseEntryResult>;
  /**
   * 收集紀錄與比較。只有指定資料管理者可查看，且只包含使用者明確同意提交的紀錄；
   * 本次／上次／首次差異與文字摘要皆在此預先算好。
   */
  getDatabaseTracking(
    viewerAccountId: AccountId,
    databaseId: string,
  ): RepositoryView<DatabaseTrackingView>;
  /**
   * 目前帳號與助理的對話清單，依最後活動時間由新到舊。id 來自網址、未經驗證；
   * 不存在或無使用權限時回傳 `assistant-use` 的 permission-denied。
   * 助理關閉「保存自己的對話」時 threads 為空，並以 historyNotice 說明原因。
   *
   * 非同步契約（M3 Slice 9，issue #79）：目前帳號由 repository 內部的工作階段推導，
   * 不再由呼叫端傳入；API 模式走 `GET /api/v1/assistants/{id}/chat/conversations`。
   */
  listChatThreads(assistantId: string): Observable<RepositoryView<ChatThreadListView>>;
  /** 開一段新的空白對話並立刻保存；不保存對話的助理回傳同一段暫時對話。 */
  createChatThread(assistantId: string): Observable<RepositoryView<AssistantChatView>>;
  /** 改名。threadId 來自網址、未經驗證；不存在或屬於其他帳號一律回傳 `chat-thread`。 */
  renameChatThread(
    assistantId: string,
    threadId: string,
    title: string,
  ): Observable<RenameChatThreadResult>;
  /** 刪除一段對話，回傳剩下的清單；不存在或屬於其他帳號一律回傳 `chat-thread`。 */
  deleteChatThread(
    assistantId: string,
    threadId: string,
  ): Observable<RepositoryView<ChatThreadListView>>;
  /**
   * 跨助理最近 10 個對話串，只列出目前仍可使用的助理
   * （API 模式：`GET /api/v1/chat/recent-conversations`）。
   */
  listRecentChatThreads(): Observable<RepositoryView<readonly RecentConversationView[]>>;
  /**
   * 目前發起者與助理的一段私人對話。id 來自網址、未經驗證；助理不存在或無使用權限時
   * 回傳 `assistant-use`，對話不存在或屬於其他發起者時回傳 `chat-thread`，兩者的訊息
   * 都不包含資源名稱。省略 threadId 時開啟最後一次使用的對話，沒有就回傳空白對話。
   * 對話只屬於發起者，助理擁有者也看不到其他人的內容。
   *
   * 目前發起者由 repository 內部的工作階段推導，不再由呼叫端傳入：已選擇的 Demo 帳號
   * 優先，其次是這個瀏覽器分頁的匿名訪客（`VisitorId`，僅 Mock 模式提供，API 模式的
   * 匿名對話留待 M5）。訪客只能開啟**已發布到官網嵌入或 LINE**的助理；其餘一律回傳與
   * 「助理不存在」相同的 `assistant-use`，不洩漏助理名稱或是否存在。訪客的對話與每個
   * 帳號、每位其他訪客都互相隔離，且只存在於該瀏覽器分頁。
   */
  getAssistantChat(assistantId: string, threadId?: string): Observable<RepositoryView<AssistantChatView>>;
  /**
   * 以預先準備的 response map 回覆；對應不到時回覆查無資料與下一步，不模擬 LLM。
   * 省略 threadId 時寫入最後一次使用的對話，沒有就開一段新的。
   */
  sendChatMessage(
    viewerId: ChatViewerId,
    assistantId: string,
    text: string,
    threadId?: string,
  ): SendChatMessageResult;
  /**
   * Mock 串流被使用者停止時（issue #80）：移除 `sendChatMessage` 剛寫入的那一則助理回覆，
   * 只保留使用者訊息，與 API 模式「中斷時只保存問題」的後端行為一致。
   * 找不到這則助理訊息時什麼都不做。
   */
  discardChatReply(viewerId: ChatViewerId, assistantId: string, messageId: string, threadId?: string): void;
  /** 對話中表單的送出前確認：只驗證並整理填寫值，不會建立紀錄。 */
  reviewChatForm(
    viewerId: ChatViewerId,
    assistantId: string,
    formId: DatabaseId,
    answers: DatabaseTrialAnswers,
  ): ReviewChatFormResult;
  /**
   * 使用者明確同意後才會建立結構化紀錄，並寫入該資料庫的收集紀錄；
   * 只有指定資料管理者可在收集紀錄中看到。未同意時回傳 validation-failed。
   */
  submitChatForm(
    viewerId: ChatViewerId,
    assistantId: string,
    submission: ChatFormSubmission,
    threadId?: string,
  ): SubmitChatFormResult;
  /**
   * 撤回同意：**只有提交者本人**可以撤回自己送出的紀錄，資料管理者不能代為撤回或刪除。
   * 撤回後這筆紀錄的內容會從收集紀錄移除，也不再計入趨勢比較，只留下一筆不含內容的
   * 軌跡（提交與撤回時間、來源），讓資料管理者仍查得到「有一筆資料被撤回了」。
   *
   * recordId 來自收據、未經驗證；不存在或不屬於這位發起者一律回傳相同的
   * `submission-withdrawal` permission-denied，訊息不包含任何填寫內容。
   * 已撤回過的紀錄回傳 validation-failed，不會再寫入一次。
   *
   * 發起者可以是 Demo 帳號，也可以是未登入訪客（紀錄記在 `subject-<visitorId>`）。
   * 訪客的 id 只存在該瀏覽器分頁：分頁結束後就再也指認不到自己的紀錄，同意畫面與
   * 收據的說明會直接告訴訪客這件事，而不是假裝之後還撤得回來。
   */
  withdrawChatSubmission(
    viewerId: ChatViewerId,
    assistantId: string,
    recordId: string,
    threadId?: string,
  ): WithdrawChatSubmissionResult;
}

export const DEMO_SECURITY_NOTICE =
  '此 mock 僅用於視覺 Demo，不提供真實驗證與資料安全邊界，不使用真實資料，也不連接真實 AI。';
