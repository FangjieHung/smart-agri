import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  ElementRef,
  inject,
  signal,
  viewChild,
  viewChildren,
} from '@angular/core';
import { rxResource, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import type { AccountId } from '../../../../core/domain/account.model';
import type { OrganizationRetentionAssistantView } from '../../../../core/domain/organization-settings.model';
import type { RepositoryView } from '../../../../core/repositories/demo-repository';
import { DEMO_REPOSITORY } from '../../../../core/repositories/tokens';
import { DemoSessionService } from '../../../../core/session/demo-session.service';
import { PurgeConversationsDialogComponent } from '../../../../shared/ui/purge-conversations-dialog/purge-conversations-dialog.component';
import { StatePanelComponent } from '../../../../shared/ui/state-panel/state-panel.component';

/**
 * 系統設定「對話保存」的第二個子區段：各助理已保存的對話（issue #242，M6 計畫第 3 節 I、第 7 節選擇 10）。
 *
 * - 只有管理者看得到：清單是管理者限定的 API，其他帳號得到 `organization-settings` permission-denied，
 *   這裡就什麼都不顯示（不是錯誤）。
 * - 每列：助理名稱、「保留使用者自己的對話紀錄」開關的狀態、已保存的對話串數與成員數、最後活動時間，
 *   以及「立即刪除」（沒有對話時停用）。只有數字，不含任何對話內容。
 * - 「立即刪除」開啟共用的確認框；刪除後重新讀取清單，結果以 `aria-live` 的狀態訊息告知，焦點回到該列的按鈕。
 */
@Component({
  selector: 'app-saved-conversations',
  imports: [DatePipe, StatePanelComponent, PurgeConversationsDialogComponent],
  templateUrl: './saved-conversations.component.html',
  styleUrl: './saved-conversations.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SavedConversationsComponent {
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly session = inject(DemoSessionService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly purgeButtons = viewChildren<ElementRef<HTMLButtonElement>>('purgeButton');
  private readonly heading = viewChild<ElementRef<HTMLElement>>('heading');

  private readonly listResource = rxResource<
    RepositoryView<readonly OrganizationRetentionAssistantView[]>,
    AccountId | undefined
  >({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: () => this.repository.listRetentionAssistants(),
    defaultValue: { status: 'loading' },
  });

  /** 讀取失敗（5xx、連線中斷）時為 null。 */
  protected readonly result = computed(() => (this.listResource.hasValue() ? this.listResource.value() : null));

  protected readonly rows = computed(() => {
    const result = this.result();
    return result?.status === 'ready' || result?.status === 'partial-failure' ? result.data : null;
  });

  /** 確認框開著時是要刪除的那一列。 */
  protected readonly purging = signal<OrganizationRetentionAssistantView | null>(null);
  protected readonly busy = signal(false);
  protected readonly feedback = signal('');
  protected readonly error = signal('');

  protected purgeLabel(row: OrganizationRetentionAssistantView): string {
    return `立即刪除「${row.assistantName}」已保存的對話`;
  }

  protected ask(row: OrganizationRetentionAssistantView): void {
    if (this.busy() || row.threadCount === 0) return;
    this.error.set('');
    this.feedback.set('');
    this.purging.set(row);
  }

  protected cancel(): void {
    if (this.busy()) return;
    const row = this.purging();
    this.purging.set(null);
    if (row !== null) this.focusRow(row.assistantId);
  }

  protected confirm(): void {
    const row = this.purging();
    if (row === null || this.busy()) return;
    this.busy.set(true);
    this.feedback.set(`正在刪除「${row.assistantName}」已保存的對話…`);
    this.repository
      .purgeAssistantConversations(row.assistantId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.finish();
          if (result.status === 'ready' || result.status === 'partial-failure') {
            this.feedback.set(`已刪除「${row.assistantName}」的 ${result.data.deletedThreadCount} 串對話。`);
          } else {
            this.feedback.set('');
            if (result.status === 'permission-denied') this.error.set(result.message);
          }
          this.listResource.reload();
        },
        error: () => {
          this.finish();
          this.feedback.set('');
          this.error.set('目前無法刪除對話，請稍後再試。這次沒有刪除任何對話。');
        },
      });
  }

  /**
   * 關掉確認框。刪除後該列的按鈕會因為沒有對話而停用、拿不到焦點，所以焦點交給子區段的標題
   * （`tabindex="-1"`），螢幕閱讀器接著讀到狀態訊息。
   */
  private finish(): void {
    this.busy.set(false);
    this.purging.set(null);
    this.heading()?.nativeElement.focus();
  }

  private focusRow(assistantId: string): void {
    this.purgeButtons()
      .find((button) => button.nativeElement.dataset['assistantId'] === assistantId)
      ?.nativeElement.focus();
  }
}
