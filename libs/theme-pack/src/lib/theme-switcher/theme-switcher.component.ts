import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { ThemeService } from '../theme/theme.service';
import { texture, COLOR_THEMES } from '../theme/theme.token';

@Component({
  selector: 'app-theme-switcher',
  imports: [MatButtonModule, MatMenuModule],
  template: `
    <button
      type="button"
      mat-fab
      color="primary"
      class="theme-fab"
      [matMenuTriggerFor]="themeMenu"
      aria-label="外觀設定"
    >
      <span class="material-symbols-rounded" aria-hidden="true">palette</span>
    </button>

    <mat-menu #themeMenu="matMenu" class="theme-menu-panel" xPosition="before" yPosition="above">
      <section class="theme-panel" aria-label="外觀設定" (click)="$event.stopPropagation()">
        <p class="theme-panel__title">外觀</p>

        <div class="theme-row">
          <span class="theme-row__label">質地</span>
          <div class="theme-row__options" role="group" aria-label="質地">
            @for (p of texture; track p.id) {
              <button
                type="button"
                class="theme-pill"
                [class.is-active]="theme.paradigm() === p.id"
                [attr.aria-pressed]="theme.paradigm() === p.id"
                (click)="onParadigm(p.id)"
              >
                {{ p.label }}
              </button>
            }
          </div>
        </div>

        <div class="theme-row">
          <span class="theme-row__label">配色</span>
          <div class="theme-row__options" role="group" aria-label="配色">
            @for (c of colorThemes; track c.id) {
              <button
                type="button"
                class="theme-pill"
                [class.is-active]="theme.theme() === c.id"
                [attr.aria-pressed]="theme.theme() === c.id"
                (click)="onTheme(c.id)"
              >
                {{ c.label }}
              </button>
            }
          </div>
        </div>
      </section>
    </mat-menu>
  `,
  styles: `
    :host {
      display: contents;
    }
    .theme-fab {
      position: fixed;
      right: 24px;
      bottom: 24px;
      z-index: 1000;
    }
    .theme-panel {
      display: flex;
      flex-direction: column;
      gap: 10px;
      padding: 4px 4px 8px;
      min-width: 220px;
    }
    .theme-panel__title {
      margin: 0;
      font-size: 0.72rem;
      font-weight: 700;
      letter-spacing: 0.06em;
      text-transform: uppercase;
      color: var(--mat-sys-on-surface-variant);
    }
    .theme-row {
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .theme-row__label {
      font-size: 0.75rem;
      color: var(--mat-sys-on-surface-variant);
    }
    .theme-row__options {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .theme-pill {
      flex: 1 1 auto;
      padding: 6px 10px;
      border: 0;
      border-radius: 999px;
      background: var(--mat-sys-surface-container-high);
      color: var(--mat-sys-on-surface-variant);
      font-size: 0.8rem;
      font-weight: 600;
      white-space: nowrap;
      cursor: pointer;
      transition:
        background 160ms ease,
        color 160ms ease;
    }
    .theme-pill:hover {
      background: var(--mat-sys-surface-container-highest);
      color: var(--mat-sys-on-surface);
    }
    .theme-pill.is-active {
      background: var(--mat-sys-primary);
      color: var(--mat-sys-on-primary);
    }
  `,
})
export class ThemeSwitcherComponent {
  readonly theme = inject(ThemeService);
  readonly texture = texture;
  readonly colorThemes = COLOR_THEMES;
  onParadigm(id: string) {
    this.theme.setParadigm(id);
  }
  onTheme(id: string) {
    this.theme.setTheme(id);
  }
}
