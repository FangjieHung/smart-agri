import { describe, it, expect, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { ThemeService } from './theme.service';
import { DEFAULT_THEME } from './theme.token';

describe('ThemeService', () => {
  let svc: ThemeService;
  beforeEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    document.documentElement.removeAttribute('data-paradigm');
    TestBed.configureTestingModule({});
    svc = TestBed.inject(ThemeService);
  });

  it('setTheme 同時改 signal、dataset、localStorage', () => {
    svc.setTheme('midnight');
    expect(svc.theme()).toBe('midnight');
    expect(document.documentElement.dataset['theme']).toBe('midnight');
    expect(localStorage.getItem('cr.theme')).toBe('midnight');
  });

  it('setParadigm 同時改 signal、dataset、localStorage', () => {
    svc.setParadigm('material');
    expect(svc.paradigm()).toBe('material');
    expect(document.documentElement.dataset['paradigm']).toBe('material');
    expect(localStorage.getItem('cr.paradigm')).toBe('material');
  });

  it('init 從 localStorage 還原；無值用預設', () => {
    localStorage.setItem('cr.theme', 'midnight');
    svc.init();
    expect(svc.theme()).toBe('midnight');
    expect(svc.paradigm()).toBe('material'); // 無值 → 預設
  });
});
