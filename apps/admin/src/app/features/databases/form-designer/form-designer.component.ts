import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  inject,
  input,
  linkedSignal,
  output,
  signal,
} from '@angular/core';
import {
  DATABASE_FIELD_TYPES,
  createDatabaseField,
  isChoiceFieldType,
  type DatabaseFieldError,
  type DatabaseFieldId,
  type DatabaseFieldType,
  type DatabaseFieldView,
  type DatabaseScaleRange,
} from '../../../core/domain/database.model';
import { FIELD_TYPE_LABELS } from '../database-labels';

/**
 * 表單設計：編輯欄位名稱、類型、必填、選項、量尺與排序。
 * 只保存在本元件的草稿中，按下「儲存表單」才交給頁面寫入 repository。
 */
@Component({
  selector: 'app-form-designer',
  templateUrl: './form-designer.component.html',
  styleUrl: './form-designer.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FormDesignerComponent {
  private readonly injector = inject(Injector);

  readonly fields = input.required<readonly DatabaseFieldView[]>();
  readonly errors = input<readonly DatabaseFieldError[]>([]);
  readonly feedback = input('');
  readonly save = output<readonly DatabaseFieldView[]>();

  protected readonly types = DATABASE_FIELD_TYPES;
  protected readonly typeLabels = FIELD_TYPE_LABELS;
  protected readonly draft = linkedSignal<readonly DatabaseFieldView[]>(() => this.fields());
  protected readonly liveMessage = signal('');

  protected isChoice(type: DatabaseFieldType): boolean {
    return isChoiceFieldType(type);
  }

  protected errorFor(fieldId: DatabaseFieldId): string {
    return this.errors().find((error) => error.fieldId === fieldId)?.message ?? '';
  }

  protected formErrors(): readonly string[] {
    return this.errors()
      .filter((error) => error.fieldId === null)
      .map((error) => error.message);
  }

  protected displayName(field: DatabaseFieldView): string {
    return field.label.trim() || '未命名欄位';
  }

  protected patch(index: number, change: Partial<DatabaseFieldView>): void {
    this.draft.update((fields) => fields.map((field, i) => (i === index ? { ...field, ...change } : field)));
  }

  protected patchScale(index: number, change: Partial<DatabaseScaleRange>): void {
    const current = this.draft()[index]?.scale;
    if (current) this.patch(index, { scale: { ...current, ...change } });
  }

  protected changeType(index: number, type: string): void {
    const next = DATABASE_FIELD_TYPES.find((candidate) => candidate === type);
    const field = this.draft()[index];
    if (next === undefined || field === undefined) return;

    const replaced = createDatabaseField(field.id, next, { label: field.label, required: field.required });
    const keepOptions = isChoiceFieldType(field.type) && isChoiceFieldType(next);
    this.patch(index, keepOptions ? { ...replaced, options: field.options } : replaced);
  }

  protected setOptions(index: number, text: string): void {
    this.patch(index, { options: text.split('\n') });
  }

  protected move(index: number, offset: -1 | 1): void {
    const fields = [...this.draft()];
    const target = index + offset;
    if (target < 0 || target >= fields.length) return;

    [fields[index], fields[target]] = [fields[target], fields[index]];
    this.draft.set(fields);
    this.liveMessage.set(`已將「${this.displayName(fields[target])}」${offset < 0 ? '上移' : '下移'}。`);
  }

  protected remove(index: number): void {
    const field = this.draft()[index];
    if (field === undefined) return;
    this.draft.update((fields) => fields.filter((_, i) => i !== index));
    this.liveMessage.set(`已刪除「${this.displayName(field)}」，儲存後生效。`);
  }

  protected add(): void {
    const existing = new Set<string>(this.draft().map((field) => field.id));
    let sequence = this.draft().length + 1;
    while (existing.has(`field-custom-${sequence}`)) sequence += 1;
    const id: DatabaseFieldId = `field-custom-${sequence}`;

    this.draft.update((fields) => [...fields, createDatabaseField(id, 'text')]);
    this.liveMessage.set('已新增一個文字欄位。');
    afterNextRender(() => document.getElementById(`field-label-${id}`)?.focus(), {
      injector: this.injector,
    });
  }

  protected submit(event: Event): void {
    event.preventDefault();
    this.save.emit(this.draft());
  }

  protected value(event: Event): string {
    return (event.target as HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement).value;
  }

  protected numberValue(event: Event): number {
    return Number(this.value(event));
  }

  protected optionsText(field: DatabaseFieldView): string {
    return field.options.join('\n');
  }

  protected checked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }
}
