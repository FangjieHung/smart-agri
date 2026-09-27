import { TestBed } from '@angular/core/testing';
import type {
  KnowledgeDocumentStatus,
  KnowledgeDocumentView,
} from '../../../../core/domain/knowledge-base.model';
import { DocumentRowComponent } from './document-row.component';

function render(document: KnowledgeDocumentView, canManage = true) {
  TestBed.configureTestingModule({ imports: [DocumentRowComponent] });
  const fixture = TestBed.createComponent(DocumentRowComponent);
  fixture.componentRef.setInput('document', document);
  fixture.componentRef.setInput('canManage', canManage);
  fixture.detectChanges();
  return fixture;
}

const base: KnowledgeDocumentView = {
  id: 'document-test',
  kind: 'document',
  name: '保固條款.pdf',
  status: 'ready',
  issue: null,
  updatedAt: '2026-09-18T08:00:00.000Z',
  latestVersionId: 'document-test:v1',
};

function buttonNamed(host: HTMLElement, label: string): HTMLButtonElement | undefined {
  return Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.includes(label));
}

describe('DocumentRowComponent', () => {
  it.each([
    ['queued', '等待處理'],
    ['processing', '處理中'],
    ['ready', '可使用'],
    ['partially-readable', '部分內容無法讀取'],
    ['failed', '處理失敗'],
  ] as const)('labels the %s status with text and a symbol, not colour alone', (status, label) => {
    const fixture = render({ ...base, status: status as KnowledgeDocumentStatus });
    const badge = (fixture.nativeElement as HTMLElement).querySelector('.document-status');

    expect(badge?.textContent).toContain(label);
    expect(badge?.querySelector('[aria-hidden="true"]')?.textContent?.trim()).not.toBe('');
    expect(badge?.getAttribute('data-status')).toBe(status);
  });

  it('shows the failure reason and emits retry for a failed document', () => {
    const fixture = render({ ...base, status: 'failed', issue: '檔案已加密，無法開啟。' });
    const host = fixture.nativeElement as HTMLElement;
    const emitted: KnowledgeDocumentView[] = [];
    fixture.componentInstance.retry.subscribe((document) => emitted.push(document));

    expect(host.textContent).toContain('檔案已加密，無法開啟。');
    const button = buttonNamed(host, '重新處理') as HTMLButtonElement;
    expect(button.textContent).toContain('保固條款.pdf');
    button.click();

    expect(emitted.map((document) => document.latestVersionId)).toEqual(['document-test:v1']);
  });

  it.each(['ready', 'partially-readable', 'queued', 'processing'] as const)(
    'does not offer retry for a %s document (only failed versions can be retried)',
    (status) => {
      const host = render({ ...base, status }).nativeElement as HTMLElement;
      expect(buttonNamed(host, '重新處理')).toBeUndefined();
    },
  );

  it('offers delete to managers only, naming the document for screen readers', () => {
    const host = render(base).nativeElement as HTMLElement;
    const remove = buttonNamed(host, '刪除');
    expect(remove?.textContent).toContain('保固條款.pdf');
    TestBed.resetTestingModule();
    const readonlyRow = render({ ...base, status: 'failed', issue: '無法開啟' }, false);
    const readonlyHost = readonlyRow.nativeElement as HTMLElement;
    // 唯讀身分看不到重新處理／刪除，但版本與預覽（唯讀）仍然開得起來（issue #47）。
    expect(buttonNamed(readonlyHost, '重新處理')).toBeUndefined();
    expect(buttonNamed(readonlyHost, '刪除')).toBeUndefined();
    expect(buttonNamed(readonlyHost, '版本與預覽')).toBeDefined();
  });

  it('disables retry and remove while a request for the row is in flight, but not the review button', () => {
    const fixture = render({ ...base, status: 'failed', issue: '無法開啟' });
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    expect(buttonNamed(host, '重新處理')?.disabled).toBe(true);
    expect(buttonNamed(host, '刪除')?.disabled).toBe(true);
    expect(buttonNamed(host, '版本與預覽')?.disabled).toBeFalsy();
  });

  it('distinguishes FAQ items from documents', () => {
    const fixture = render({ ...base, kind: 'faq', name: '可以開立統編嗎？' });

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('FAQ');
  });

  it('shows effective status alongside processing status, not just "可使用" (issue #47)', () => {
    const inEffect = render({ ...base, inEffect: true, effectiveVersionNumber: 1 });
    expect((inEffect.nativeElement as HTMLElement).querySelector('.document-effect')?.textContent).toContain('已生效');

    TestBed.resetTestingModule();
    const pending = render({ ...base, latestVersionState: 'pending-review', inEffect: false });
    expect((pending.nativeElement as HTMLElement).querySelector('.document-effect')?.textContent).toContain('待確認');

    TestBed.resetTestingModule();
    const disabled = render({ ...base, disabled: true, inEffect: false });
    expect((disabled.nativeElement as HTMLElement).querySelector('.document-effect')?.textContent).toContain('已停用');
  });

  it('emits openReview when the name or the review button is activated', () => {
    const fixture = render(base);
    const emitted: KnowledgeDocumentView[] = [];
    fixture.componentInstance.openReview.subscribe((document) => emitted.push(document));
    buttonNamed(fixture.nativeElement as HTMLElement, '版本與預覽')?.click();
    expect(emitted).toHaveLength(1);
  });

  it('only lets a pending or scheduled version be selected for batch approval', () => {
    const fixture = render({ ...base, latestVersionState: 'pending-review' });
    fixture.componentRef.setInput('selectable', true);
    fixture.detectChanges();
    const checkbox = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(
      '.document-select input',
    );
    expect(checkbox?.disabled).toBe(false);

    TestBed.resetTestingModule();
    const effectiveFixture = render({ ...base, latestVersionState: 'effective' });
    effectiveFixture.componentRef.setInput('selectable', true);
    effectiveFixture.detectChanges();
    const effectiveCheckbox = (effectiveFixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(
      '.document-select input',
    );
    expect(effectiveCheckbox?.disabled).toBe(true);
  });
});
