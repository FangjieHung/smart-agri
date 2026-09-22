import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
} from '@angular/core';

export type SourceType = 'knowledge-base' | 'database';

@Component({
  selector: 'app-source-type-icon',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="source-type">
      <span class="source-type__symbol" aria-hidden="true">{{ symbol() }}</span>
      <span class="source-type__label">{{ label() }}</span>
    </span>
  `,
  styleUrl: './source-type-icon.component.scss',
})
export class SourceTypeIconComponent {
  readonly type = input.required<SourceType>();
  readonly label = computed(() =>
    this.type() === 'knowledge-base' ? '知識庫' : '資料庫',
  );
  readonly symbol = computed(() =>
    this.type() === 'knowledge-base' ? '▤' : '▦',
  );
}
