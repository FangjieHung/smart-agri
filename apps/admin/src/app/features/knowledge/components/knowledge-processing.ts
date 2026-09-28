import type { KnowledgeDocumentStatusCounts } from '../../../core/domain/knowledge-base.model';

export type ProcessingTone = 'ready' | 'processing' | 'attention' | 'empty';

export interface ProcessingSummary {
  readonly tone: ProcessingTone;
  readonly symbol: string;
  readonly label: string;
}

/**
 * 以文字與符號彙整知識庫的處理狀態；需要處理的項目優先提示。
 *
 * `contentCount`（文件數 + FAQ 數）是 0 時顯示「尚無內容」（issue #115），不歸類成
 * `ready`（全部可使用）：0 份文件、0 則 FAQ 時原本會落到「沒有需要處理、也沒有處理中」
 * 的分支而顯示「全部可使用」，容易讓人誤以為已經有可用內容。
 */
export function summarizeProcessing(
  counts: KnowledgeDocumentStatusCounts,
  contentCount: number,
): ProcessingSummary {
  const attention = counts['partially-readable'] + counts.failed;
  const pending = counts.queued + counts.processing;
  if (attention > 0) {
    return { tone: 'attention', symbol: '!', label: `${attention} 項需要處理` };
  }
  if (pending > 0) {
    return { tone: 'processing', symbol: '◐', label: `${pending} 項處理中` };
  }
  if (contentCount === 0) {
    return { tone: 'empty', symbol: '–', label: '尚無內容' };
  }
  return { tone: 'ready', symbol: '✓', label: '全部可使用' };
}
