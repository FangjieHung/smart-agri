import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  effect,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import type {
  ChatHistoryMode,
  ChatThreadSummaryView,
} from '../../../core/domain/conversation.model';
import { ConfirmDialogComponent } from '../../../shared/ui/confirm-dialog/confirm-dialog.component';

/**
 * 對話紀錄側欄：列出目前帳號與這個助理的所有對話，可開新對話、切換、改名與刪除。
 * 只顯示標題與訊息數，不含任何訊息文字；資料本身已由 repository 依帳號隔離。
 */
@Component({
  selector: 'app-conversation-rail',
  imports: [ConfirmDialogComponent],
  templateUrl: './conversation-rail.component.html',
  styleUrl: './conversation-rail.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConversationRailComponent {
  readonly threads = input<readonly ChatThreadSummaryView[]>([]);
  readonly activeThreadId = input<string | null>(null);
  readonly historyMode = input<ChatHistoryMode>('saved');
  readonly historyNotice = input('');
  /** 改名被 repository 拒絕時由外層傳入的訊息。 */
  readonly renameError = input('');
  /** 有寫入請求進行中（開新對話、改名、刪除）：擋掉「開新對話」的重複送出。 */
  readonly busy = input(false);

  readonly newConversation = output<void>();
  readonly selectThread = output<string>();
  readonly renameThread = output<{ readonly id: string; readonly title: string }>();
  readonly deleteThread = output<string>();

  protected readonly renamingId = signal<string | null>(null);
  protected readonly pendingDelete = signal<ChatThreadSummaryView | null>(null);

  private readonly renameInput = viewChild<ElementRef<HTMLInputElement>>('renameInput');
  private deleteTrigger: HTMLElement | null = null;

  constructor() {
    // 改名欄位一出現就把焦點帶過去，鍵盤操作不必再找（刪除確認框由 ConfirmDialogComponent 自己帶到「取消」）。
    effect(() => {
      const input = this.renameInput()?.nativeElement;
      if (input === undefined) return;
      input.focus();
      input.select();
    });
  }

  protected startRename(threadId: string): void {
    this.renamingId.set(threadId);
  }

  protected cancelRename(): void {
    this.renamingId.set(null);
  }

  protected submitRename(event: Event, threadId: string): void {
    event.preventDefault();
    const title = this.renameInput()?.nativeElement.value ?? '';
    this.renamingId.set(null);
    this.renameThread.emit({ id: threadId, title });
  }

  protected askDelete(thread: ChatThreadSummaryView, event: Event): void {
    this.deleteTrigger = event.currentTarget as HTMLElement;
    this.pendingDelete.set(thread);
  }

  protected cancelDelete(): void {
    this.pendingDelete.set(null);
    this.deleteTrigger?.focus();
    this.deleteTrigger = null;
  }

  protected confirmDelete(): void {
    const pending = this.pendingDelete();
    this.pendingDelete.set(null);
    this.deleteTrigger = null;
    if (pending !== null) this.deleteThread.emit(pending.id);
  }
}
