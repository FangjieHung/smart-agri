import type {
  KnowledgeDocumentStatus,
  KnowledgeItemKind,
  KnowledgeSharingScope,
} from '../../../core/domain/knowledge-base.model';

export interface KnowledgeStatusLabel {
  readonly label: string;
  /** 與文字並列的符號，讓狀態不只依賴顏色辨識。 */
  readonly symbol: string;
}

export const DOCUMENT_STATUS_LABELS: Readonly<Record<KnowledgeDocumentStatus, KnowledgeStatusLabel>> = {
  queued: { label: '等待處理', symbol: '○' },
  processing: { label: '處理中', symbol: '◐' },
  ready: { label: '可使用', symbol: '✓' },
  'partially-readable': { label: '部分內容無法讀取', symbol: '!' },
  failed: { label: '處理失敗', symbol: '✕' },
};

export const ITEM_KIND_LABELS: Readonly<Record<KnowledgeItemKind, string>> = {
  document: '文件',
  faq: 'FAQ',
};

export const SHARING_SCOPE_LABELS: Readonly<Record<KnowledgeSharingScope, string>> = {
  private: '只有我',
  'specific-accounts': '指定帳號／團隊',
  public: '公開分享',
};
