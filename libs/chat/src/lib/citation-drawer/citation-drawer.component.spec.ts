import { TestBed } from '@angular/core/testing';
import type { ChatCitationView } from '../chat-view.model';
import { CitationDrawerComponent } from './citation-drawer.component';

const CITATIONS: readonly ChatCitationView[] = [
  {
    id: 'citation-1',
    knowledgeBaseName: '退換貨政策',
    documentName: '退換貨辦法 2026 版.pdf',
    excerpt: '消費者於收受商品後七日內，得申請退貨，商品應保持原包裝完整。',
    updatedLabel: '2026-09-18',
  },
  {
    id: 'citation-2',
    knowledgeBaseName: '退換貨政策',
    documentName: '退款作業流程.docx',
    excerpt: '倉儲確認退貨商品後，於五個工作天內完成退款作業。',
    updatedLabel: '2026-09-18',
  },
];

function render() {
  TestBed.configureTestingModule({ imports: [CitationDrawerComponent] });
  const fixture = TestBed.createComponent(CitationDrawerComponent);
  document.body.appendChild(fixture.nativeElement);
  fixture.componentRef.setInput('citations', CITATIONS);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

describe('CitationDrawerComponent', () => {
  it('is a labelled modal dialog listing each source and excerpt', () => {
    const { host } = render();
    const dialog = host.querySelector('[role="dialog"]');

    expect(dialog?.getAttribute('aria-modal')).toBe('true');
    const labelId = dialog?.getAttribute('aria-labelledby') ?? '';
    expect(host.querySelector(`#${labelId}`)?.textContent).toContain('引用來源');
    expect(dialog?.textContent).toContain('退換貨政策');
    expect(dialog?.textContent).toContain('退換貨辦法 2026 版.pdf');
  });

  it('moves focus into the dialog and closes with Escape or the close button', async () => {
    const { fixture, host } = render();
    let closed = 0;
    fixture.componentInstance.closed.subscribe(() => (closed += 1));
    await fixture.whenStable();

    expect(host.contains(document.activeElement)).toBe(true);
    host.querySelector('[role="dialog"]')?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    host.querySelector<HTMLButtonElement>('button.drawer-close')?.click();
    expect(closed).toBe(2);
    fixture.destroy();
  });
});
