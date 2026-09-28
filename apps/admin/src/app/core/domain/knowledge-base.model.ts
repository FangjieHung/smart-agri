import type { AccountId } from './account.model';
import type { AssistantId, AssistantStatus } from './assistant.model';

/**
 * mock 是 `knowledge-product-guide` 這類固定 id（建立的是 `knowledge-created-<序號>`），
 * API 模式是後端的 GUID；兩者都只是不透明字串，畫面不得依格式判斷。
 */
export type KnowledgeBaseId = string;

export interface KnowledgeBaseView {
  readonly id: KnowledgeBaseId;
  readonly ownerAccountId: AccountId;
  readonly name: string;
  readonly purpose: string;
  readonly lastSyncedAt: string;
}

/** 單一文件或 FAQ 的處理狀態（設計文件第 7 節的五種狀態）。 */
export type KnowledgeDocumentStatus =
  | 'queued'
  | 'processing'
  | 'ready'
  | 'partially-readable'
  | 'failed';

export type KnowledgeItemKind = 'document' | 'faq';

/** 與 `KnowledgeBaseId` 相同：mock 的固定 id 或 API 的 GUID，一律當成不透明字串。 */
export type KnowledgeDocumentId = string;

export interface KnowledgeDocumentView {
  readonly id: KnowledgeDocumentId;
  readonly kind: KnowledgeItemKind;
  readonly name: string;
  readonly status: KnowledgeDocumentStatus;
  /** 部分內容無法讀取或處理失敗時的原因；其他狀態為 null。 */
  readonly issue: string | null;
  readonly updatedAt: string;
  /**
   * 最新版本的 id。重新處理是針對「版本」（`POST .../versions/{versionId}/retry`），
   * 所以畫面要把它帶回 `retryKnowledgeDocument`；mock 沒有版本，固定是 `<文件 id>:v1`。
   */
  readonly latestVersionId: string;
  /**
   * 以下欄位是 M2 Slice 13（issue #47）新增的版本確認狀態；為 `undefined` 時畫面視為
   * 「這份文件還沒有版本歷程的資料」（尚未載入或來源是還沒補上這批欄位的舊 fixture），
   * 一律用 `effectiveKnowledgeDocumentState` 這類 helper 讀取，不要直接比對 `undefined`。
   */
  readonly latestVersionNumber?: number;
  readonly latestVersionState?: KnowledgeVersionState;
  /** 目前生效中的版本號；沒有任何版本生效（例如剛上傳、尚未確認）時為 `null`。 */
  readonly effectiveVersionNumber?: number | null;
  /** 是否已被緊急停用；停用中即使有生效版本也不會被引用。 */
  readonly disabled?: boolean;
  /** 這份文件目前是否真的會被助理引用（`effectiveVersionNumber !== null && !disabled`）。 */
  readonly inEffect?: boolean;
}

/** 版本的確認狀態（後端 `KnowledgeVersionState`）：待確認／排程生效／生效中／已封存。 */
export type KnowledgeVersionState = 'pending-review' | 'scheduled' | 'effective' | 'archived';

export const KNOWLEDGE_VERSION_STATE_LABELS: Readonly<Record<KnowledgeVersionState, string>> = {
  'pending-review': '待確認',
  scheduled: '排程生效',
  effective: '已生效',
  archived: '已封存',
};

/** 畫面顯示「可使用」時，一律要同時顯示是否已生效，避免被誤讀成助理已經在用它。 */
export function knowledgeDocumentInEffect(document: KnowledgeDocumentView): boolean {
  return document.inEffect ?? (isUsableKnowledgeDocument(document.status) && document.disabled !== true);
}

export interface KnowledgeAccountRefView {
  readonly id: string;
  readonly displayName: string;
}

export interface KnowledgeVersionView {
  readonly id: string;
  readonly documentId: KnowledgeDocumentId;
  readonly versionNumber: number;
  readonly fileName: string;
  readonly contentType: string;
  readonly sizeBytes: number;
  readonly status: KnowledgeDocumentStatus;
  readonly issue: string | null;
  readonly state: KnowledgeVersionState;
  readonly effectiveFrom: string | null;
  readonly uploadedBy: KnowledgeAccountRefView;
  readonly uploadedAt: string;
  readonly approvedBy: KnowledgeAccountRefView | null;
  readonly approvedAt: string | null;
  readonly updatedAt: string;
}

export const KNOWLEDGE_ACTIVITY_ACTIONS = [
  'document-uploaded',
  'version-uploaded',
  'version-retried',
  'version-approved',
  'chunk-excluded',
  'chunk-included',
  'document-disabled',
  'document-enabled',
  'document-deleted',
] as const;

export type KnowledgeActivityAction = (typeof KNOWLEDGE_ACTIVITY_ACTIONS)[number];

export interface KnowledgeActivityView {
  readonly id: string;
  readonly action: KnowledgeActivityAction;
  readonly actor: KnowledgeAccountRefView | null;
  readonly at: string;
  readonly versionId: string | null;
  readonly versionNumber: number | null;
  /** 只有 `document-disabled` 會帶原因；活動紀錄保留停用原因（M2 計畫第 4 節）。 */
  readonly reason: string | null;
}

export interface KnowledgeDocumentDetailView {
  readonly document: KnowledgeDocumentView;
  readonly createdAt: string;
  readonly disabledAt: string | null;
  readonly disabledBy: KnowledgeAccountRefView | null;
  readonly disabledReason: string | null;
  /** 由新到舊。 */
  readonly versions: readonly KnowledgeVersionView[];
  /** 由新到舊。 */
  readonly activities: readonly KnowledgeActivityView[];
}

export type KnowledgeUnitLocationKind = 'page' | 'section' | 'sheet' | 'faq';

export type KnowledgeUnitIssue = 'too-little-text' | 'garbled-text' | 'rows-truncated' | null;

export interface KnowledgeChunkView {
  readonly id: string;
  readonly locationLabel: string;
  readonly text: string;
  /** 排除後不納入檢索，但仍會顯示在預覽中。 */
  readonly excluded: boolean;
}

export interface KnowledgeExtractedUnitView {
  readonly ordinal: number;
  readonly locationKind: KnowledgeUnitLocationKind;
  readonly locationLabel: string;
  readonly readable: boolean;
  readonly issueCode: KnowledgeUnitIssue;
  readonly text: string;
  readonly chunks: readonly KnowledgeChunkView[];
}

export interface KnowledgeVersionPreviewView {
  readonly documentId: KnowledgeDocumentId;
  readonly versionId: string;
  readonly versionNumber: number;
  readonly fileName: string;
  readonly status: KnowledgeDocumentStatus;
  readonly issue: string | null;
  readonly units: readonly KnowledgeExtractedUnitView[];
}

/** 待確認或排程生效：詳情頁「只看待確認」篩選、批次確認生效的候選判斷都用這個。 */
export function isAwaitingApprovalKnowledgeDocument(document: KnowledgeDocumentView): boolean {
  const state = document.latestVersionState;
  return state === 'pending-review' || state === 'scheduled';
}

/** 與後端 `KnowledgeReviewRules.MaxDisableReasonLength` 相同。 */
export const KNOWLEDGE_DISABLE_REASON_MAX_LENGTH = 500;

export const KNOWLEDGE_DISABLE_REASON_REQUIRED_MESSAGE = '請說明緊急停用的原因。';

export type KnowledgeSharingScope = 'private' | 'specific-accounts' | 'public';

export interface KnowledgeSharingView {
  readonly scope: KnowledgeSharingScope;
  /** 只有 scope 為 specific-accounts 時有意義。 */
  readonly sharedWithAccountIds: readonly AccountId[];
  /** 公開分享時，原始文件能否下載由擁有者另行設定。 */
  readonly allowOriginalDownload: boolean;
}

export interface KnowledgeConnectedAssistantView {
  readonly id: AssistantId;
  readonly name: string;
  readonly status: AssistantStatus;
}

export interface KnowledgeShareTargetView {
  readonly id: AccountId;
  readonly displayName: string;
}

export type KnowledgeDocumentStatusCounts = Readonly<
  Record<KnowledgeDocumentStatus, number>
>;

export interface KnowledgeBaseSummaryView {
  readonly id: KnowledgeBaseId;
  readonly name: string;
  readonly purpose: string;
  readonly documentCount: number;
  readonly faqCount: number;
  readonly statusCounts: KnowledgeDocumentStatusCounts;
  readonly sharingScope: KnowledgeSharingScope;
  readonly connectedAssistantNames: readonly string[];
  readonly updatedAt: string;
  /**
   * 目前帳號能否開啟、變更、分享與刪除（API 的同名欄位）。畫面只看這個旗標，
   * 不自己拿擁有者 id 與目前帳號比對——API 模式的帳號是 GUID，與 Demo 身分對不上。
   */
  readonly viewerCanManage: boolean;
  /** M2 Slice 13：依生效狀態統計的文件數（`undefined` 時畫面以 0 顯示）。 */
  readonly inEffectCount?: number;
  readonly awaitingApprovalCount?: number;
  readonly disabledCount?: number;
}

/** 建立知識庫的輸入；名稱必填（去頭尾空白後 1–100 字），用途可空白（最多 500 字）。 */
export interface CreateKnowledgeBaseInput {
  readonly name: string;
  readonly purpose: string;
}

export const KNOWLEDGE_BASE_NAME_MAX_LENGTH = 100;
export const KNOWLEDGE_BASE_PURPOSE_MAX_LENGTH = 500;

export interface KnowledgeBaseDetailView {
  readonly summary: KnowledgeBaseSummaryView;
  readonly documents: readonly KnowledgeDocumentView[];
  readonly connectedAssistants: readonly KnowledgeConnectedAssistantView[];
  readonly sharing: KnowledgeSharingView;
  /** 指定帳號分享時可選擇的對象，不含擁有者本人。 */
  readonly shareTargets: readonly KnowledgeShareTargetView[];
}

export const KNOWLEDGE_DOCUMENT_STATUSES: readonly KnowledgeDocumentStatus[] = [
  'queued',
  'processing',
  'ready',
  'partially-readable',
  'failed',
];

/** 部分內容無法讀取的文件仍可被引用其餘可讀內容。 */
export function isUsableKnowledgeDocument(status: KnowledgeDocumentStatus): boolean {
  return status === 'ready' || status === 'partially-readable';
}

export function needsKnowledgeAttention(status: KnowledgeDocumentStatus): boolean {
  return status === 'partially-readable' || status === 'failed';
}

/**
 * 只有處理失敗的版本可以重新處理（後端 `KnowledgeVersionRules.RetryRefusal`）：
 * 部分內容無法讀取的文件再處理一次，讀到的還是同樣的頁面。
 */
export function isRetryableKnowledgeDocument(status: KnowledgeDocumentStatus): boolean {
  return status === 'failed';
}

/** 等待中或處理中：詳情頁有這種項目時才需要輪詢。 */
export function isPendingKnowledgeDocument(status: KnowledgeDocumentStatus): boolean {
  return status === 'queued' || status === 'processing';
}

/**
 * 上傳文件（issue #46，M2 Slice 12）：後端與前端預檢共用同一套副檔名與大小上限
 * （`KnowledgeUploadRules`、`KnowledgeOptions.DefaultMaxFileBytes`）。前端的預檢只是先擋掉
 * 明顯不會過的檔案，省一次來回；實際結果一律以後端回應為準。
 */
export const KNOWLEDGE_UPLOAD_ACCEPTED_EXTENSIONS = ['.pdf', '.docx', '.xlsx', '.txt', '.md'] as const;

export const KNOWLEDGE_UPLOAD_ACCEPT = KNOWLEDGE_UPLOAD_ACCEPTED_EXTENSIONS.join(',');

/** 與後端 `KnowledgeOptions.DefaultMaxFileBytes` 相同（20 MB）。 */
export const KNOWLEDGE_UPLOAD_MAX_FILE_BYTES = 20 * 1024 * 1024;

export const KNOWLEDGE_UPLOAD_UNSUPPORTED_TYPE_MESSAGE =
  '只支援 PDF、Word（.docx）、Excel（.xlsx）、純文字（.txt）與 Markdown（.md）檔案。';

/** 每個檔案被拒絕上傳的原因；wire name 與後端 `KnowledgeUploadRejectionReason` 逐字相同。 */
export const KNOWLEDGE_UPLOAD_REJECTION_REASONS = [
  'file-missing',
  'too-many-files',
  'file-unreadable',
  'invalid-file-name',
  'invalid-batch-id',
  'file-too-large',
  'unsupported-file-type',
  'file-content-mismatch',
  'duplicate-content',
  'duplicate-name',
] as const;

export type KnowledgeUploadRejectionReason = (typeof KNOWLEDGE_UPLOAD_REJECTION_REASONS)[number];

export function isKnowledgeUploadRejectionReason(value: unknown): value is KnowledgeUploadRejectionReason {
  return (KNOWLEDGE_UPLOAD_REJECTION_REASONS as readonly unknown[]).includes(value);
}

/** 檔名重複時可以改成「上傳新版本」；只有這個原因會提供這個選項。 */
export function offersUploadAsNewVersion(reason: KnowledgeUploadRejectionReason): boolean {
  return reason === 'duplicate-name';
}

/**
 * 檢索試查（issue #48，M2 Slice 14）：與正式對話同一套檢索邏輯，只回傳命中的段落，
 * 不生成任何回答（後端 `KnowledgeRetrievalPassageView`）。
 */
export interface KnowledgeRetrievalPassageView {
  readonly documentId: KnowledgeDocumentId;
  readonly documentName: string;
  readonly versionNumber: number;
  /** `effective`，或含 `includePending` 時該文件最新的待確認／排程生效版本。 */
  readonly versionState: KnowledgeVersionState;
  /** 「第 2 頁」、章節標題路徑，或工作表列數。 */
  readonly locationLabel: string;
  /** 段落內容，超過後端上限會被截斷並以「…」結尾。 */
  readonly excerpt: string;
  /** 與問題的餘弦相似度，最高為 1，分數越高代表越相關。 */
  readonly score: number;
  readonly versionId: string;
  readonly chunkId: string;
}

export interface KnowledgeRetrievalPreviewView {
  /** 依分數由高到低，即使全部低於門檻也會列出，最多 5 筆。 */
  readonly passages: readonly KnowledgeRetrievalPassageView[];
  /** 相關性門檻（後端 `Retrieval:MinScore`，目前預設 0.3）。 */
  readonly threshold: number;
  /** 沒有任何段落達到門檻：只使用組織資料的助理會回答「查無結果」。 */
  readonly belowThreshold: boolean;
}

/** 與後端 `KnowledgeRetrievalRules.QuestionMaxLength` 相同。 */
export const KNOWLEDGE_RETRIEVAL_QUESTION_MAX_LENGTH = 500;

export const KNOWLEDGE_RETRIEVAL_QUESTION_REQUIRED_MESSAGE = '請輸入要試查的問題。';

export const KNOWLEDGE_RETRIEVAL_QUESTION_TOO_LONG_MESSAGE = `問題最多 ${KNOWLEDGE_RETRIEVAL_QUESTION_MAX_LENGTH} 個字。`;

/** 與後端 `KnowledgeRetrievalSettings.DefaultMinScore`／`DefaultTop` 相同（M2 計畫定案）。 */
export const KNOWLEDGE_RETRIEVAL_DEFAULT_THRESHOLD = 0.3;
export const KNOWLEDGE_RETRIEVAL_DEFAULT_TOP = 5;

/** 低於門檻時的提示文字（issue #48）。 */
export const KNOWLEDGE_RETRIEVAL_BELOW_THRESHOLD_MESSAGE =
  '助理設定為只用組織資料時，這題會回答查無結果。';

/** 嵌入模型暫時無法使用，或部署尚未設定嵌入模型（API 的 `503`）。 */
export const KNOWLEDGE_RETRIEVAL_UNAVAILABLE_REASONS = ['embedding-unavailable', 'embedding-not-configured'] as const;

export type KnowledgeRetrievalUnavailableReason = (typeof KNOWLEDGE_RETRIEVAL_UNAVAILABLE_REASONS)[number];

export function isKnowledgeRetrievalUnavailableReason(value: unknown): value is KnowledgeRetrievalUnavailableReason {
  return (KNOWLEDGE_RETRIEVAL_UNAVAILABLE_REASONS as readonly unknown[]).includes(value);
}

function knowledgeUploadExtension(fileName: string): string {
  const dot = fileName.lastIndexOf('.');
  return dot === -1 ? '' : fileName.slice(dot).toLowerCase();
}

/** 前端預檢结果：`null` 代表看起來沒問題（仍要送到後端才算數）。 */
export interface KnowledgeUploadPrecheckFailure {
  readonly reason: KnowledgeUploadRejectionReason;
  readonly message: string;
}

/**
 * 送出前先在前端檢查副檔名與大小，讓使用者不必等一次網路來回就知道明顯會被拒絕的檔案；
 * 不讀取檔案內容，所以無法預先偵測重複或格式偽裝，那些只能由後端回應。
 */
export function precheckKnowledgeUpload(file: File): KnowledgeUploadPrecheckFailure | null {
  if (file.size > KNOWLEDGE_UPLOAD_MAX_FILE_BYTES) {
    return {
      reason: 'file-too-large',
      message: `檔案超過 ${Math.round(KNOWLEDGE_UPLOAD_MAX_FILE_BYTES / (1024 * 1024))} MB 的上限，請分割或壓縮後再上傳。`,
    };
  }
  const extension = knowledgeUploadExtension(file.name);
  if (!(KNOWLEDGE_UPLOAD_ACCEPTED_EXTENSIONS as readonly string[]).includes(extension)) {
    return { reason: 'unsupported-file-type', message: KNOWLEDGE_UPLOAD_UNSUPPORTED_TYPE_MESSAGE };
  }
  return null;
}
