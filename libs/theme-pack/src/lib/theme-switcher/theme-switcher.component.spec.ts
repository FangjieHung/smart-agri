import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { ThemeSwitcherComponent } from './theme-switcher.component';
import { ThemeService } from '../theme/theme.service';

describe('ThemeSwitcherComponent', () => {
  beforeEach(() => localStorage.clear());
  it('選配色會呼叫 ThemeService.setTheme', () => {
    const f = TestBed.createComponent(ThemeSwitcherComponent);
    const svc = TestBed.inject(ThemeService);
    f.detectChanges();
    f.componentInstance.onTheme('midnight');
    expect(svc.theme()).toBe('midnight');
  });

  describe('鍵盤可操作性', () => {
    afterEach(() => {
      // The CDK overlay renders menu content into document.body, outside the
      // fixture -- close/clean it up so it doesn't leak between tests.
      document.querySelectorAll('.cdk-overlay-container').forEach((el) => el.remove());
    });

    it('質地／配色的切換項目是可以 Tab 聚焦、Enter／Space 觸發的原生 <button>', () => {
      const f = TestBed.createComponent(ThemeSwitcherComponent);
      f.detectChanges();

      // Open the mat-menu so its content (rendered lazily into a CDK overlay
      // attached to document.body) actually exists in the DOM.
      const trigger: HTMLButtonElement = f.nativeElement.querySelector('.theme-fab');
      trigger.click();
      f.detectChanges();

      const pills = Array.from(document.querySelectorAll<HTMLButtonElement>('.theme-pill'));
      expect(pills.length).toBeGreaterThan(0);

      for (const pill of pills) {
        // Native semantics, not a custom widget: a real, non-disabled <button>
        // is guaranteed by the HTML spec to be part of the default Tab order
        // and to be activated by both Enter and Space when focused -- browsers
        // dispatch the exact same 'click' event for a mouse click as for an
        // Enter/Space activation, so there is nothing extra to wire up here.
        expect(pill.tagName).toBe('BUTTON');
        expect(pill.getAttribute('type')).toBe('button');
        expect(pill.disabled).toBe(false);
        expect(pill.tabIndex).not.toBe(-1);
      }

      // Tab-focusable: focusing the element actually moves DOM focus onto it.
      const firstPill = pills[0];
      firstPill.focus();
      expect(document.activeElement).toBe(firstPill);

      // Enter/Space-activatable: since jsdom (unlike real browsers) does not
      // implement the native "Enter/Space on a focused button fires the same
      // click event as a mouse click" default action, this exercises the
      // resulting click event directly on the focused element to prove the
      // component reacts correctly to that activation.
      const svc = TestBed.inject(ThemeService);
      const colorPill = pills.find((p) => p.textContent?.includes('Midnight'));
      expect(colorPill).toBeInstanceOf(HTMLButtonElement);
      const activatedPill = colorPill as HTMLButtonElement;
      activatedPill.focus();
      expect(document.activeElement).toBe(activatedPill);
      activatedPill.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
      f.detectChanges();
      expect(svc.theme()).toBe('midnight');
    });
  });
});
