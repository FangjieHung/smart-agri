import { TestBed } from '@angular/core/testing';
import { replyFor } from '../assistant-use.testing';
import { CitationDrawerComponent } from './citation-drawer.component';

function render() {
  const reply = replyFor('收到商品後幾天內可以退貨？');
  if (reply.kind !== 'company-data') throw new Error('expected company data');
  TestBed.configureTestingModule({ imports: [CitationDrawerComponent] });
  const fixture = TestBed.createComponent(CitationDrawerComponent);
  document.body.appendChild(fixture.nativeElement);
  fixture.componentRef.setInput('citations', reply.citations);
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
