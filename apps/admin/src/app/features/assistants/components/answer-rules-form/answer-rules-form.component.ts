import { ChangeDetectionStrategy, Component, ElementRef, inject, input, output, signal } from '@angular/core';
import { SettingRowComponent } from '@smart-agri/ui';
import { fmtDateTime } from '../../../../core/date-utils';
import type { PeriodicReportAutoDisabledView } from '../../../../core/domain/assistant-settings.model';
import {
  PERIODIC_REPORT_LABELS,
  type AssistantAnswerRules,
  type PeriodicReportSchedule,
} from '../../../../core/domain/assistant-draft.model';
import type { DatabaseId } from '../../../../core/domain/database.model';
import { ConfirmDialogComponent } from '../../../../shared/ui/confirm-dialog/confirm-dialog.component';
import { KeptConversationsComponent } from '../kept-conversations/kept-conversations.component';

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
  imports: [SettingRowComponent, ConfirmDialogComponent, KeptConversationsComponent],
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
  /** 定期報表已自動停用（#179，只有建立後的設定頁會帶）：顯示原因與「重新啟用」。 */
  readonly autoDisabled = input<PeriodicReportAutoDisabledView | null>(null);
  /** 正在儲存時停用「重新啟用」按鈕，避免連按。 */
  readonly resuming = input(false);
  /**
   * 已建立的助理（「回答與記錄」頁籤才會帶）：開關旁顯示已保存的對話數與「立即刪除」或「請聯絡管理者」
   * （issue #242）。建立精靈裡助理還不存在，保持 `null`，不會讀取 summary。
   */
  readonly assistantId = input<string | null>(null);
  readonly assistantName = input('');
  readonly changed = output<Partial<AssistantAnswerRules>>();
  readonly resumeReport = output<void>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected readonly reportOptions = REPORT_OPTIONS;
  /** 已上線的助理關閉「保留使用者自己的對話紀錄」前的確認框（寫明已保存的對話不會刪除）。 */
  protected readonly confirmingKeepOff = signal(false);

  /** 已上線的助理先確認再關閉；開啟、或建立精靈裡（還沒有任何對話）直接套用。 */
  protected toggleKeepConversations(checked: boolean): void {
    if (!checked && this.live()) {
      this.confirmingKeepOff.set(true);
      return;
    }
    this.changed.emit({ keepOwnConversations: checked });
  }

  protected confirmKeepOff(): void {
    this.confirmingKeepOff.set(false);
    this.changed.emit({ keepOwnConversations: false });
    this.keepSwitch()?.focus();
  }

  /** 取消：開關回到開啟（畫面上的勾選已被使用者取消，設定值沒變，Angular 不會自己改回）。 */
  protected cancelKeepOff(): void {
    this.confirmingKeepOff.set(false);
    const toggle = this.keepSwitch();
    if (toggle) {
      toggle.checked = this.rules().keepOwnConversations;
      toggle.focus();
    }
  }

  private keepSwitch(): HTMLInputElement | null {
    return this.host.nativeElement.querySelector<HTMLInputElement>('#keep-conversations');
  }

  /** `aria-describedby`：有錯誤時帶上錯誤，已上線的助理另外帶上說明。 */
  protected periodicReportDescription(): string | null {
    const ids = [
      ...(this.errors().periodicReport ? ['periodic-report-error'] : []),
      ...(this.autoDisabled() !== null ? ['periodic-report-auto-disabled'] : []),
      ...(this.live() ? ['periodic-report-effect'] : []),
    ];
    return ids.length > 0 ? ids.join(' ') : null;
  }

  protected disabledAt(value: string): string {
    return fmtDateTime(value);
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
