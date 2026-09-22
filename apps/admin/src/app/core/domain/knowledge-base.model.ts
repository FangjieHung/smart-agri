import type { AccountId } from './account.model';
import type { AssistantId, AssistantStatus } from './assistant.model';

export type KnowledgeBaseId =
  | 'knowledge-product-guide'
  | 'knowledge-refund-policy'
  | 'knowledge-shipping-faq'
  | 'knowledge-staff-notes';

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

/** Demo 加入的文件 id 格式固定為 `document-demo-<序號>`。 */
export type KnowledgeDocumentId = `document-${string}`;

export interface KnowledgeDocumentView {
  readonly id: KnowledgeDocumentId;
  readonly kind: KnowledgeItemKind;
  readonly name: string;
  readonly status: KnowledgeDocumentStatus;
  /** 部分內容無法讀取或處理失敗時的原因；其他狀態為 null。 */
  readonly issue: string | null;
  readonly updatedAt: string;
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
}

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
