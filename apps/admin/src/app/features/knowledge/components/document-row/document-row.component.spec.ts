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
    expect((readonlyRow.nativeElement as HTMLElement).querySelector('button')).toBeNull();
  });

  it('disables both actions while a request for the row is in flight', () => {
    const fixture = render({ ...base, status: 'failed', issue: '無法開啟' });
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();
    const buttons = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button'));

    expect(buttons).toHaveLength(2);
    expect(buttons.every((button) => button.disabled)).toBe(true);
  });

  it('distinguishes FAQ items from documents', () => {
    const fixture = render({ ...base, kind: 'faq', name: '可以開立統編嗎？' });

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('FAQ');
  });
});
