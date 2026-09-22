import {
  ChangeDetectionStrategy,
  Component,
  input,
  output,
} from '@angular/core';

@Component({
  selector: 'app-empty-state',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="ui-panel">
      <h2 class="ui-panel-title">{{ title() }}</h2>
      @if (description()) {
        <p class="ui-panel-description">{{ description() }}</p>
      }
      @if (actionLabel()) {
        <button
          class="ui-button ui-panel-action"
          type="button"
          (click)="primaryAction.emit()"
        >
          {{ actionLabel() }}
        </button>
      }
    </section>
  `,
  styles: ':host { display: block; min-inline-size: 0; }',
})
export class EmptyStateComponent {
  readonly title = input.required<string>();
  readonly description = input('');
  readonly actionLabel = input('');
  readonly primaryAction = output<void>();
}
