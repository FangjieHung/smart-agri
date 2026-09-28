import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';
import type {
  KnowledgeRetrievalPreviewView,
  KnowledgeRetrievalUnavailableReason,
} from '../../../../core/domain/knowledge-base.model';
import type {
  KnowledgeRetrievalUnavailableView,
  KnowledgeValidationFailedView,
} from '../../../../core/repositories/demo-repository';
import { provideKnowledgeTesting } from '../../knowledge.testing';
import { RetrievalPreviewPanelComponent } from './retrieval-preview-panel.component';

function render(knowledgeBaseId = 'knowledge-product-guide', testing = provideKnowledgeTesting('account-smb-admin')) {
  TestBed.configureTestingModule({
    imports: [RetrievalPreviewPanelComponent],
    providers: [...testing.providers],
  });
  const fixture = TestBed.createComponent(RetrievalPreviewPanelComponent);
  fixture.componentRef.setInput('knowledgeBaseId', knowledgeBaseId);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, repository: testing.repository };
}

async function settle(fixture: ReturnType<typeof render>['fixture']): Promise<void> {
  await fixture.whenStable();
  fixture.detectChanges();
}

function setQuestion(host: HTMLElement, value: string): void {
  const textarea = host.querySelector<HTMLTextAreaElement>('#knowledge-retrieval-question');
  if (!textarea) throw new Error('#knowledge-retrieval-question not found');
  textarea.value = value;
  textarea.dispatchEvent(new Event('input'));
}

function submit(host: HTMLElement): void {
  (host.querySelector('button[type="submit"]') as HTMLButtonElement).click();
}

function toggleIncludePending(host: HTMLElement): void {
  const checkbox = host.querySelector<HTMLInputElement>('.filter-toggle input[type="checkbox"]');
  if (!checkbox) throw new Error('include-pending checkbox not found');
  checkbox.click();
}

const READY_RESULT: KnowledgeRetrievalPreviewView = {
  passages: [
    {
      documentId: 'document-refund-faq-window',
      documentName: '收到商品幾天內可以退貨？',
      versionNumber: 1,
      versionState: 'effective',
      locationLabel: 'FAQ 內容',
      excerpt: '收到商品後 7 天內可以申請退貨。',
      score: 0.82,
      versionId: 'document-refund-faq-window:v1',
      chunkId: 'document-refund-faq-window:v1:u1:c1',
    },
  ],
  threshold: 0.3,
  belowThreshold: false,
};

const BELOW_THRESHOLD_RESULT: KnowledgeRetrievalPreviewView = {
  passages: [
    {
      documentId: 'document-guide-specs',
      documentName: '商品規格總表.pdf',
      versionNumber: 1,
      versionState: 'effective',
      locationLabel: '第 1 頁',
      excerpt: '這是「商品規格總表.pdf」第 1 個抽取單位的示範內容。',
      score: 0.05,
      versionId: 'document-guide-specs:v1',
      chunkId: 'document-guide-specs:v1:u1:c1',
    },
  ],
  threshold: 0.3,
  belowThreshold: true,
};

const PENDING_VERSION_RESULT: KnowledgeRetrievalPreviewView = {
  passages: [
    {
      documentId: 'document-guide-specs',
      documentName: '商品規格總表.pdf',
      versionNumber: 1,
      versionState: 'effective',
      locationLabel: '第 1 頁',
      excerpt: '這是「商品規格總表.pdf」第 1 個抽取單位（第 1 版）的內容。',
      score: 0.55,
      versionId: 'document-guide-specs:v1',
      chunkId: 'document-guide-specs:v1:u1:c1',
    },
    {
      documentId: 'document-guide-specs',
      documentName: '商品規格總表.pdf',
      versionNumber: 2,
      versionState: 'pending-review',
      locationLabel: '第 1 頁',
      excerpt: '這是「商品規格總表.pdf」第 1 個抽取單位（第 2 版，待確認）的內容。',
      score: 0.6,
      versionId: 'document-guide-specs:v2',
      chunkId: 'document-guide-specs:v2:u1:c1',
    },
  ],
  threshold: 0.3,
  belowThreshold: false,
};

describe('RetrievalPreviewPanelComponent', () => {
  it('does not submit a blank question, and shows a client-side hint instead', () => {
    const { fixture, host, repository } = render();
    const spy = vi.spyOn(repository, 'previewKnowledgeRetrieval');

    setQuestion(host, '   ');
    fixture.detectChanges();
    submit(host);
    fixture.detectChanges();

    expect(spy).not.toHaveBeenCalled();
    expect(host.querySelector('.action-error[role="alert"]')?.textContent).toContain('請輸入要試查的問題。');
  });

  it('lists the matching passages with document, version, location, excerpt and score (有結果)', async () => {
    const { fixture, host, repository } = render();
    vi.spyOn(repository, 'previewKnowledgeRetrieval').mockReturnValue(of({ status: 'ready', data: READY_RESULT }));

    setQuestion(host, '收到商品幾天內可以退貨？');
    fixture.detectChanges();
    submit(host);
    await settle(fixture);

    expect(host.querySelector('[data-testid="below-threshold-notice"]')).toBeNull();
    const row = host.querySelector('.passage-row');
    expect(row?.textContent).toContain('收到商品幾天內可以退貨？');
    expect(row?.textContent).toContain('第 1 版');
    expect(row?.textContent).toContain('FAQ 內容');
    expect(row?.textContent).toContain('收到商品後 7 天內可以申請退貨。');
    expect(row?.textContent).toContain('0.82');
  });

  it('shows the 查無結果 notice and still lists the closest passage when every score is below the threshold (低於門檻)', async () => {
    const { fixture, host, repository } = render();
    vi.spyOn(repository, 'previewKnowledgeRetrieval').mockReturnValue(
      of({ status: 'ready', data: BELOW_THRESHOLD_RESULT }),
    );

    setQuestion(host, '今天天氣如何？');
    fixture.detectChanges();
    submit(host);
    await settle(fixture);

    expect(host.querySelector('[data-testid="below-threshold-notice"]')?.textContent).toContain(
      '助理設定為只用組織資料時，這題會回答查無結果。',
    );
    expect(host.querySelector('.passage-row')?.textContent).toContain('商品規格總表.pdf');
    expect(host.querySelector('.passage-row')?.textContent).toContain('0.05');
  });

  it('marks a passage from a pending version, and only asks for it with 包含待確認版本 checked (含待確認版本)', async () => {
    const { fixture, host, repository } = render();
    const spy = vi
      .spyOn(repository, 'previewKnowledgeRetrieval')
      .mockReturnValue(of({ status: 'ready', data: PENDING_VERSION_RESULT }));

    setQuestion(host, '商品規格總表');
    toggleIncludePending(host);
    fixture.detectChanges();
    submit(host);
    await settle(fixture);

    expect(spy).toHaveBeenCalledWith('knowledge-product-guide', '商品規格總表', true);
    const rows = Array.from(host.querySelectorAll('.passage-row'));
    expect(rows).toHaveLength(2);
    const pendingRow = rows.find((row) => row.querySelector('[data-state="pending-review"]'));
    expect(pendingRow?.textContent).toContain('第 2 版');
    expect(pendingRow?.textContent).toContain('待確認');
  });

  it('shows the API message for a validation failure (422, e.g. a question over the length limit)', async () => {
    const { fixture, host, repository } = render();
    const refusal: KnowledgeValidationFailedView = { status: 'validation-failed', message: '問題最多 500 個字。' };
    vi.spyOn(repository, 'previewKnowledgeRetrieval').mockReturnValue(of(refusal));

    setQuestion(host, '問題');
    fixture.detectChanges();
    submit(host);
    await settle(fixture);

    expect(host.querySelector('.action-error[role="alert"]')?.textContent).toContain('問題最多 500 個字。');
  });

  it.each<KnowledgeRetrievalUnavailableReason>(['embedding-unavailable', 'embedding-not-configured'])(
    'shows the API message when the embedding model is unavailable (503 %s)',
    async (reason) => {
      const { fixture, host, repository } = render();
      const unavailable: KnowledgeRetrievalUnavailableView = {
        status: 'unavailable',
        reason,
        message: '嵌入模型暫時無法使用，請稍後重試。',
      };
      vi.spyOn(repository, 'previewKnowledgeRetrieval').mockReturnValue(of(unavailable));

      setQuestion(host, '退貨期限');
      fixture.detectChanges();
      submit(host);
      await settle(fixture);

      const alert = host.querySelector('.action-error[role="alert"]');
      expect(alert?.textContent).toContain('嵌入模型暫時無法使用，請稍後重試。');
      expect(alert?.getAttribute('data-reason')).toBe(reason);
    },
  );

  it('says so when the request cannot be sent at all', async () => {
    const { fixture, host, repository } = render();
    vi.spyOn(repository, 'previewKnowledgeRetrieval').mockReturnValue(throwError(() => new Error('offline')));

    setQuestion(host, '退貨期限');
    fixture.detectChanges();
    submit(host);
    await settle(fixture);

    expect(host.querySelector('.action-error[role="alert"]')?.textContent).toContain('目前無法試查');
  });

  it('ignores a second submit while the first is still in flight', () => {
    const { fixture, host, repository } = render();
    const spy = vi.spyOn(repository, 'previewKnowledgeRetrieval').mockReturnValue(of());

    setQuestion(host, '退貨期限');
    fixture.detectChanges();
    submit(host);
    fixture.detectChanges();
    submit(host);

    expect(spy).toHaveBeenCalledTimes(1);
  });
});
