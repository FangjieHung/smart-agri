import { TestBed } from '@angular/core/testing';
import { SourceTypeIconComponent } from './source-type-icon.component';

describe('SourceTypeIconComponent', () => {
  it.each([
    ['knowledge-base', '知識庫'],
    ['database', '資料庫'],
  ])('labels %s visibly instead of relying on an icon', async (type, label) => {
    const fixture = TestBed.createComponent(SourceTypeIconComponent);
    fixture.componentRef.setInput('type', type);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('.source-type__label')?.textContent).toBe(
      label,
    );
    expect(
      element
        .querySelector('.source-type__symbol')
        ?.getAttribute('aria-hidden'),
    ).toBe('true');
  });
});
