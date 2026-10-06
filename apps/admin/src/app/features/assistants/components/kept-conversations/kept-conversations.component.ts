import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  ElementRef,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { rxResource, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import type { AccountId } from '../../../../core/domain/account.model';
import type { AssistantConversationSummaryView } from '../../../../core/domain/organization-settings.model';
import type { RepositoryView } from '../../../../core/repositories/demo-repository';
import { DEMO_REPOSITORY } from '../../../../core/repositories/tokens';
import { DemoSessionService } from '../../../../core/session/demo-session.service';
import { PurgeConversationsDialogComponent } from '../../../../shared/ui/purge-conversations-dialog/purge-conversations-dialog.component';

/** 非管理者看到的說明：取代舊的「要真正移除，請由對話的所有人…」。 */
export function keptConversationsNote(threadCount: number): string {
  return `既有的 ${threadCount} 串對話會保留到保存期限；要立即刪除，請聯絡管理者。`;
}

/**
 * 助理設定「保留使用者自己的對話紀錄」開關旁的已保存對話（issue #242，M6 計畫第 3 節 I）。只用在
 * 已建立的助理（「回答與記錄」頁籤）；建立精靈裡助理還不存在，不會建立這個元件、也不會讀 summary。
 *
 * - 管理者（`canPurge`）：已保存的串數與成員數，以及與系統設定相同的「立即刪除」與確認框。
 * - 其他讀得到設定的人（擁有者）：「既有的 N 串對話會保留到保存期限；要立即刪除，請聯絡管理者」。
 * - 讀不到（權限不足、連線失敗）時不顯示，只留表單本身的說明。
 */
@Component({
  selector: 'app-kept-conversations',
  imports: [PurgeConversationsDialogComponent],
  templateUrl: './kept-conversations.component.html',
  styleUrl: './kept-conversations.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class KeptConversationsComponent {
  readonly assistantId = input.required<string>();
  readonly assistantName = input.required<string>();

  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly session = inject(DemoSessionService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly purgeButton = viewChild<ElementRef<HTMLButtonElement>>('purgeButton');
  private readonly summaryText = viewChild<ElementRef<HTMLElement>>('summaryText');

  private readonly summaryResource = rxResource<
    RepositoryView<AssistantConversationSummaryView>,
    { readonly assistantId: string; readonly viewer: AccountId | undefined }
  >({
    params: () => ({ assistantId: this.assistantId(), viewer: this.session.activeAccountId() ?? undefined }),
    stream: ({ params }) => this.repository.getAssistantConversationSummary(params.assistantId),
    defaultValue: { status: 'loading' },
  });

  protected readonly summary = computed(() => {
    if (!this.summaryResource.hasValue()) return null;
    const result = this.summaryResource.value();
    return result.status === 'ready' || result.status === 'partial-failure' ? result.data : null;
  });

  protected readonly note = keptConversationsNote;
  protected readonly confirming = signal(false);
  protected readonly busy = signal(false);
  protected readonly feedback = signal('');
  protected readonly error = signal('');

  protected ask(): void {
    const summary = this.summary();
    if (summary === null || !summary.canPurge || summary.threadCount === 0 || this.busy()) return;
    this.error.set('');
    this.feedback.set('');
    this.confirming.set(true);
  }

  protected cancel(): void {
    if (this.busy()) return;
    this.confirming.set(false);
    this.purgeButton()?.nativeElement.focus();
  }

  protected confirm(): void {
    if (!this.confirming() || this.busy()) return;
    this.busy.set(true);
    this.feedback.set('正在刪除已保存的對話…');
    this.repository
      .purgeAssistantConversations(this.assistantId())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.finish();
          if (result.status === 'ready' || result.status === 'partial-failure') {
            this.feedback.set(`已刪除 ${result.data.deletedThreadCount} 串對話。`);
          } else {
            this.feedback.set('');
            if (result.status === 'permission-denied') this.error.set(result.message);
          }
          this.summaryResource.reload();
        },
        error: () => {
          this.finish();
          this.feedback.set('');
          this.error.set('目前無法刪除對話，請稍後再試。這次沒有刪除任何對話。');
        },
      });
  }

  /** 刪除後按鈕會因為沒有對話而停用，焦點交給串數的說明（`tabindex="-1"`）。 */
  private finish(): void {
    this.busy.set(false);
    this.confirming.set(false);
    this.summaryText()?.nativeElement.focus();
  }
}
