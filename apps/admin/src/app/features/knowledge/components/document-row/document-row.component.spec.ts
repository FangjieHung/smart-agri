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
};

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
    const emitted: string[] = [];
    fixture.componentInstance.retry.subscribe((id) => emitted.push(id));

    expect(host.textContent).toContain('檔案已加密，無法開啟。');
    const button = host.querySelector('button') as HTMLButtonElement;
    expect(button.textContent).toContain('重新處理');
    expect(button.textContent).toContain('保固條款.pdf');
    button.click();

    expect(emitted).toEqual(['document-test']);
  });

  it('does not offer retry for usable documents or for viewers who cannot manage', () => {
    expect((render(base).nativeElement as HTMLElement).querySelector('button')).toBeNull();
    TestBed.resetTestingModule();
    const readonlyRow = render({ ...base, status: 'failed', issue: '無法開啟' }, false);
    expect((readonlyRow.nativeElement as HTMLElement).querySelector('button')).toBeNull();
  });

  it('distinguishes FAQ items from documents', () => {
    const fixture = render({ ...base, kind: 'faq', name: '可以開立統編嗎？' });

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('FAQ');
  });
});
