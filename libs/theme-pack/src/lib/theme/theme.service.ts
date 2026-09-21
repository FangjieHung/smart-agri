import { Injectable, inject, signal } from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { DEFAULT_PARADIGM, DEFAULT_THEME, PARADIGM_KEY, THEME_KEY } from './theme.token';

@Injectable({ providedIn: 'root' })
export class ThemeService {
  private doc = inject(DOCUMENT);
  readonly paradigm = signal(DEFAULT_PARADIGM);
  readonly theme = signal(DEFAULT_THEME);

  init(): void {
    this.setParadigm(localStorage.getItem(PARADIGM_KEY) ?? DEFAULT_PARADIGM);
    this.setTheme(localStorage.getItem(THEME_KEY) ?? DEFAULT_THEME);
  }

  setParadigm(id: string): void {
    this.paradigm.set(id);
    this.doc.documentElement.dataset['paradigm'] = id;
    localStorage.setItem(PARADIGM_KEY, id);
  }

  setTheme(id: string): void {
    this.theme.set(id);
    this.doc.documentElement.dataset['theme'] = id;
    localStorage.setItem(THEME_KEY, id);
  }
}
