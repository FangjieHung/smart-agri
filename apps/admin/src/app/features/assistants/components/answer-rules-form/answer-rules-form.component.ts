import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type {
  AssistantAnswerRules,
  PeriodicReportSchedule,
} from '../../../../core/domain/assistant-draft.model';
import type { DatabaseId } from '../../../../core/domain/database.model';

export interface WritableDatabaseOption {
  readonly id: DatabaseId;
  readonly name: string;
}

export interface AnswerRulesErrors {
  readonly refusalMessage?: string | null;
  readonly dataWritePurpose?: string | null;
}

const REPORT_OPTIONS: readonly { readonly value: PeriodicReportSchedule; readonly label: string }[] = [
  { value: 'off', label: '不需要' },
  { value: 'weekly', label: '每週一次' },
  { value: 'monthly', label: '每月一次' },
];

/** 回答與記錄規則的表單；建立精靈的步驟三與建立後的「回答與記錄」頁籤共用。 */
@Component({
  selector: 'app-answer-rules-form',
  templateUrl: './answer-rules-form.component.html',
  styleUrl: '../assistant-form.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AnswerRulesFormComponent {
  readonly rules = input.required<AssistantAnswerRules>();
  /** 只有已連接到這個助理的資料庫可以當寫入對象。 */
  readonly writableDatabases = input<readonly WritableDatabaseOption[]>([]);
  readonly errors = input<AnswerRulesErrors>({});
  /**
   * 這是已經有人在用的助理嗎？是的話要說明改動會如何影響現有的對話，
   * 而不是只描述「之後」的行為。
   */
  readonly live = input(false);
  readonly changed = output<Partial<AssistantAnswerRules>>();

  protected readonly reportOptions = REPORT_OPTIONS;

  protected text(event: Event): string {
    return (event.target as HTMLInputElement | HTMLTextAreaElement).value;
  }

  protected checked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  protected selectWriteTarget(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    const target =
      this.writableDatabases().find((database) => database.id === value)?.id ?? null;
    this.changed.emit({ dataWriteDatabaseId: target });
  }

  protected selectReport(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.changed.emit({
      periodicReport:
        REPORT_OPTIONS.find((option) => option.value === value)?.value ?? 'off',
    });
  }
}
