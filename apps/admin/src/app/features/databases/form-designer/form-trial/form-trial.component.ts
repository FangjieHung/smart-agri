import { ChangeDetectionStrategy, Component, computed, input, linkedSignal, output } from '@angular/core';
import type {
  DatabaseFieldId,
  DatabaseFieldView,
  DatabaseTrialAnswer,
  DatabaseTrialAnswers,
} from '../../../../core/domain/database.model';
import type { PreviewDatabaseEntryResult } from '../../../../core/repositories/demo-repository';

/** 試填：依已儲存的表單呈現輸入控制項，送出後由 repository 驗證並回傳預覽。 */
@Component({
  selector: 'app-form-trial',
  templateUrl: './form-trial.component.html',
  styleUrl: './form-trial.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FormTrialComponent {
  readonly fields = input.required<readonly DatabaseFieldView[]>();
  readonly result = input<PreviewDatabaseEntryResult | null>(null);
  readonly trial = output<DatabaseTrialAnswers>();

  /** 表單結構改變時清空試填答案。 */
  protected readonly answers = linkedSignal<readonly DatabaseFieldView[], Partial<Record<DatabaseFieldId, DatabaseTrialAnswer>>>({
    source: this.fields,
    computation: () => ({}),
  });

  protected readonly errors = computed(() => {
    const result = this.result();
    return result?.status === 'validation-failed' ? result.errors : [];
  });

  protected readonly message = computed(() => {
    const result = this.result();
    return result?.status === 'validation-failed' || result?.status === 'permission-denied' ? result.message : '';
  });

  protected readonly preview = computed(() => {
    const result = this.result();
    return result?.status === 'ready' || result?.status === 'partial-failure' ? result.data : null;
  });

  protected errorFor(fieldId: DatabaseFieldId): string {
    return this.errors().find((error) => error.fieldId === fieldId)?.message ?? '';
  }

  protected scaleSteps(field: DatabaseFieldView): readonly number[] {
    const scale = field.scale;
    if (scale === null || scale.max < scale.min) return [];
    return Array.from({ length: scale.max - scale.min + 1 }, (_, index) => scale.min + index);
  }

  protected inputType(field: DatabaseFieldView): string {
    return field.type === 'number' || field.type === 'date' ? field.type : 'text';
  }

  protected textAnswer(fieldId: DatabaseFieldId): string {
    const answer = this.answers()[fieldId];
    return typeof answer === 'string' ? answer : '';
  }

  protected isChosen(fieldId: DatabaseFieldId, option: string): boolean {
    const answer = this.answers()[fieldId];
    return Array.isArray(answer) ? answer.includes(option) : answer === option;
  }

  protected setText(fieldId: DatabaseFieldId, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.answers.update((answers) => ({ ...answers, [fieldId]: value }));
  }

  protected toggle(field: DatabaseFieldView, option: string, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.answers.update((answers) => {
      const current = answers[field.id];
      const chosen = new Set(Array.isArray(current) ? current : []);
      if (checked) chosen.add(option);
      else chosen.delete(option);
      return { ...answers, [field.id]: field.options.filter((candidate) => chosen.has(candidate)) };
    });
  }

  protected submit(event: Event): void {
    event.preventDefault();
    this.trial.emit({ ...this.answers() });
  }
}
