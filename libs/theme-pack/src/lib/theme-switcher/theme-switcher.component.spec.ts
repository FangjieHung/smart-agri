import { describe, it, expect, beforeEach } from 'vitest';
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
});
