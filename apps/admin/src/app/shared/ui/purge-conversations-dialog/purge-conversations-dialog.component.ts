import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { HANDOFF_COPIES_KEPT } from '../../../core/domain/organization-settings.model';
import { ConfirmDialogComponent } from '../confirm-dialog/confirm-dialog.component';

/** 立即刪除的確認框裡，處理事項問答副本的說明。 */
export const PURGE_ISSUES_NOTE = `${HANDOFF_COPIES_KEPT}，不會一起刪除。`;

/** 必須勾選才能刪除的確認文字。 */
export const PURGE_ACKNOWLEDGEMENT = '我了解刪除後無法復原';

/**
 * 「立即刪除」已保存對話的確認框（issue #242，M6 計畫第 3 節 I）：系統設定「對話保存」的各助理清單與
 * 助理設定的開關旁共用。寫出助理名稱、對話串數、受影響的成員數與處理事項的說明；必須勾選
 * 「我了解刪除後無法復原」才能按「立即刪除」，不用輸入助理名稱。
 *
 * 只負責對話框；送出、錯誤訊息與關閉後的焦點由使用的元件處理。每次開啟都是新的元件，勾選不會延續到下一次。
 */
@Component({
  selector: 'app-purge-conversations-dialog',
  imports: [ConfirmDialogComponent],
  templateUrl: './purge-conversations-dialog.component.html',
  styleUrl: './purge-conversations-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PurgeConversationsDialogComponent {
  readonly assistantName = input.required<string>();
  readonly threadCount = input.required<number>();
  readonly accountCount = input.required<number>();
  /** 刪除中：兩個按鈕都停用。 */
  readonly busy = input(false);
  readonly headingLevel = input<2 | 3>(2);

  readonly cancelled = output<void>();
  readonly confirmed = output<void>();

  protected readonly acknowledged = signal(false);
  protected readonly issuesNote = PURGE_ISSUES_NOTE;
  protected readonly acknowledgement = PURGE_ACKNOWLEDGEMENT;

  protected readonly heading = computed(() => `立即刪除「${this.assistantName()}」已保存的對話？`);
  protected readonly detail = computed(
    () =>
      `會刪除 ${this.accountCount()} 位成員在這個助理上已保存的 ${this.threadCount()} 串對話，` +
      '連同其中的訊息與引用，刪除後無法復原。受影響的成員不會收到通知。',
  );

  protected acknowledge(event: Event): void {
    this.acknowledged.set((event.target as HTMLInputElement).checked);
  }

  protected confirm(): void {
    if (!this.acknowledged() || this.busy()) return;
    this.confirmed.emit();
  }
}
