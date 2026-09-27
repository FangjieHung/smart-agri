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
