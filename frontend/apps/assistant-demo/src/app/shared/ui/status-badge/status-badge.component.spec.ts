import { TestBed } from '@angular/core/testing';
import { StatusBadgeComponent } from './status-badge.component';

describe('StatusBadgeComponent', () => {
  it('communicates status with visible text as well as color', async () => {
    const fixture = TestBed.createComponent(StatusBadgeComponent);
    fixture.componentRef.setInput('label', '部分內容無法讀取');
    fixture.componentRef.setInput('tone', 'warning');
    await fixture.whenStable();
    const badge = fixture.nativeElement.querySelector(
      '.status-badge',
    ) as HTMLElement;
    expect(badge.textContent).toContain('部分內容無法讀取');
    expect(badge.getAttribute('data-tone')).toBe('warning');
    expect(badge.getAttribute('aria-hidden')).not.toBe('true');
  });
});
