import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  inject,
  input,
  linkedSignal,
  output,
  type AfterViewInit,
} from '@angular/core';
import type { ChatFormView } from '../../../core/domain/conversation.model';
import type {
  DatabaseFieldError,
  DatabaseFieldId,
  DatabaseFieldView,
  DatabaseTrialAnswers,
} from '../../../core/domain/database.model';

/** 對話中的表單：只收集填寫值並交給確認步驟，不會直接建立紀錄。 */
@Component({
  selector: 'app-inline-form',
  templateUrl: './inline-form.component.html',
  styleUrl: './inline-form.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class InlineFormComponent implements AfterViewInit {
  readonly form = input.required<ChatFormView>();
  readonly answers = input<DatabaseTrialAnswers>({});
  readonly errors = input<readonly DatabaseFieldError[]>([]);

  readonly review = output<DatabaseTrialAnswers>();
  readonly cancelled = output<void>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly values = linkedSignal<DatabaseTrialAnswers>(() => this.answers());
  protected readonly invalidIds = computed(
    () => new Set(this.errors().flatMap((error) => (error.fieldId === null ? [] : [error.fieldId]))),
  );

  ngAfterViewInit(): void {
    this.host.nativeElement.querySelector<HTMLElement>('input')?.focus();
  }

  protected inputId(field: DatabaseFieldView): string {
    return `chat-field-${field.id}`;
  }

  protected text(fieldId: DatabaseFieldId): string {
    const value = this.values()[fieldId];
    return typeof value === 'string' ? value : '';
  }

  protected checked(fieldId: DatabaseFieldId, option: string): boolean {
    const value = this.values()[fieldId];
    return Array.isArray(value) ? value.includes(option) : value === option;
  }

  protected scaleOptions(field: DatabaseFieldView): string[] {
    const scale = field.scale;
    if (scale === null) return [];
    return Array.from({ length: scale.max - scale.min + 1 }, (_, index) => String(scale.min + index));
  }

  protected setText(fieldId: DatabaseFieldId, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.values.update((current) => ({ ...current, [fieldId]: value }));
  }

  protected toggle(fieldId: DatabaseFieldId, option: string, event: Event): void {
    const isChecked = (event.target as HTMLInputElement).checked;
    this.values.update((current) => {
      const previous = current[fieldId];
      const list = Array.isArray(previous) ? previous : [];
      const next = isChecked ? [...list, option] : list.filter((item) => item !== option);
      return { ...current, [fieldId]: next };
    });
  }

  protected submit(event: Event): void {
    event.preventDefault();
    this.review.emit(this.values());
  }
}
