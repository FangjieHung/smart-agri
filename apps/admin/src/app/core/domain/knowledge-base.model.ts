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
}

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
