import {
  ChangeDetectionStrategy,
  Component,
  input,
  output,
} from '@angular/core';

export type PanelState = 'loading' | 'empty' | 'error' | 'permission-denied';

@Component({
  selector: 'app-state-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section
      class="ui-panel"
      [attr.data-state]="state()"
      [attr.aria-busy]="state() === 'loading'"
    >
      <div
        [attr.role]="
          state() === 'error' || state() === 'permission-denied'
            ? 'alert'
            : 'status'
        "
        aria-atomic="true"
      >
        <h2 class="ui-panel-title">{{ title() }}</h2>
        @if (description()) {
          <p class="ui-panel-description">{{ description() }}</p>
        }
      </div>
      @if (recoveryLabel() && state() !== 'loading') {
        <button
          class="ui-button ui-panel-action"
          type="button"
          (click)="recover.emit()"
        >
          {{ recoveryLabel() }}
        </button>
      }
    </section>
  `,
  styles: `
    :host {
      display: block;
      min-inline-size: 0;
    }
    [data-state='error'] {
      border-inline-start: var(--space-1) solid var(--color-error);
    }
    [data-state='permission-denied'] {
      border-inline-start: var(--space-1) solid var(--color-warning);
    }
  `,
})
export class StatePanelComponent {
  readonly state = input.required<PanelState>();
  readonly title = input.required<string>();
  readonly description = input('');
  readonly recoveryLabel = input('');
  readonly recover = output<void>();
}
