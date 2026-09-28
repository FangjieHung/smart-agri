import type { KnowledgeDocumentStatusCounts } from '../../../core/domain/knowledge-base.model';
import { summarizeProcessing } from './knowledge-processing';

function counts(overrides: Partial<KnowledgeDocumentStatusCounts> = {}): KnowledgeDocumentStatusCounts {
  return {
    queued: 0,
    processing: 0,
    ready: 0,
    'partially-readable': 0,
    failed: 0,
    ...overrides,
  };
}

describe('summarizeProcessing (issue #115)', () => {
  it('shows 尚無內容 for a knowledge base with no documents or FAQs', () => {
    expect(summarizeProcessing(counts(), 0)).toEqual({ tone: 'empty', symbol: '–', label: '尚無內容' });
  });

  it('shows 全部可使用 once there is content and nothing needs attention', () => {
    expect(summarizeProcessing(counts({ ready: 3 }), 3)).toEqual({
      tone: 'ready',
      symbol: '✓',
      label: '全部可使用',
    });
  });

  it('still reports 全部可使用 for a knowledge base with only FAQs (no documents)', () => {
    // 0 份文件、2 則 FAQ：statusCounts 全是 0（FAQ 不算在文件狀態統計裡），但內容不是空的。
    expect(summarizeProcessing(counts(), 2)).toEqual({ tone: 'ready', symbol: '✓', label: '全部可使用' });
  });

  it('prioritizes attention over the empty/ready distinction', () => {
    expect(summarizeProcessing(counts({ failed: 1 }), 1)).toEqual({
      tone: 'attention',
      symbol: '!',
      label: '1 項需要處理',
    });
  });

  it('prioritizes pending processing over the empty/ready distinction', () => {
    expect(summarizeProcessing(counts({ queued: 2 }), 2)).toEqual({
      tone: 'processing',
      symbol: '◐',
      label: '2 項處理中',
    });
  });
});
