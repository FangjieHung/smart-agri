import type { KnowledgeDocumentStatusCounts } from '../../../core/domain/knowledge-base.model';

export type ProcessingTone = 'ready' | 'processing' | 'attention';

export interface ProcessingSummary {
  readonly tone: ProcessingTone;
  readonly symbol: string;
  readonly label: string;
}

/** 以文字與符號彙整知識庫的處理狀態；需要處理的項目優先提示。 */
export function summarizeProcessing(counts: KnowledgeDocumentStatusCounts): ProcessingSummary {
  const attention = counts['partially-readable'] + counts.failed;
  const pending = counts.queued + counts.processing;
  if (attention > 0) {
    return { tone: 'attention', symbol: '!', label: `${attention} 項需要處理` };
  }
  if (pending > 0) {
    return { tone: 'processing', symbol: '◐', label: `${pending} 項處理中` };
  }
  return { tone: 'ready', symbol: '✓', label: '全部可使用' };
}
