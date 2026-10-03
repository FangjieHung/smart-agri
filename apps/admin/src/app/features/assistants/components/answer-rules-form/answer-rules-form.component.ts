import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { SettingRowComponent } from '@smart-agri/ui';
import {
  PERIODIC_REPORT_LABELS,
  type AssistantAnswerRules,
  type PeriodicReportSchedule,
} from '../../../../core/domain/assistant-draft.model';
import type { DatabaseId } from '../../../../core/domain/database.model';

export interface WritableDatabaseOption {
  readonly id: DatabaseId;
  readonly name: string;
}

export interface AnswerRulesErrors {
  readonly refusalMessage?: string | null;
  readonly dataWritePurpose?: string | null;
  readonly periodicReport?: string | null;
}

const REPORT_OPTIONS: readonly { readonly value: PeriodicReportSchedule; readonly label: string }[] = (
  ['off', 'weekly', 'monthly'] as const
).map((value) => ({ value, label: PERIODIC_REPORT_LABELS[value] }));

/** 回答與記錄規則的表單；建立精靈的步驟三與建立後的「回答與記錄」頁籤共用。 */
@Component({
  selector: 'app-answer-rules-form',
  imports: [SettingRowComponent],
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
  /**
   * 資料庫寫入可以設定嗎？API 模式的建立精靈還不能連接資料庫（#148 只開放建立後的設定），
   * 改成說明「後續開放」。
   */
  readonly databaseFeaturesAvailable = input(true);
  readonly changed = output<Partial<AssistantAnswerRules>>();

  protected readonly reportOptions = REPORT_OPTIONS;

  /** `aria-describedby`：有錯誤時帶上錯誤，已上線的助理另外帶上說明。 */
  protected periodicReportDescription(): string | null {
    const ids = [
      ...(this.errors().periodicReport ? ['periodic-report-error'] : []),
      ...(this.live() ? ['periodic-report-effect'] : []),
    ];
    return ids.length > 0 ? ids.join(' ') : null;
  }

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
