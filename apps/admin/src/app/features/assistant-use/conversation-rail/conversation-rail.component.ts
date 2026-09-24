import { A11yModule } from '@angular/cdk/a11y';
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
import type { AssistantId } from '../../../core/domain/assistant.model';

export interface RecentChatThreadView extends ChatThreadSummaryView {
  readonly assistantId: AssistantId;
  readonly assistantName: string;
}

/**
 * 對話紀錄側欄：列出目前帳號與這個助理的所有對話，可開新對話、切換、改名與刪除。
 * 只顯示標題與訊息數，不含任何訊息文字；資料本身已由 repository 依帳號隔離。
 */
@Component({
  selector: 'app-conversation-rail',
  imports: [A11yModule],
  templateUrl: './conversation-rail.component.html',
  styleUrl: './conversation-rail.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConversationRailComponent {
  readonly threads = input<readonly ChatThreadSummaryView[]>([]);
  readonly activeThreadId = input<string | null>(null);
  readonly historyMode = input<ChatHistoryMode>('saved');
  readonly historyNotice = input('');
  readonly recentThreads = input<readonly RecentChatThreadView[] | null>(null);
  /** 改名被 repository 拒絕時由外層傳入的訊息。 */
  readonly renameError = input('');

  readonly newConversation = output<void>();
  readonly selectThread = output<string>();
  readonly renameThread = output<{ readonly id: string; readonly title: string }>();
  readonly deleteThread = output<string>();
  readonly selectRecentThread = output<RecentChatThreadView>();

  protected readonly renamingId = signal<string | null>(null);
  protected readonly pendingDelete = signal<ChatThreadSummaryView | null>(null);

  private readonly renameInput = viewChild<ElementRef<HTMLInputElement>>('renameInput');
  private readonly cancelButton = viewChild<ElementRef<HTMLButtonElement>>('cancelButton');
  private deleteTrigger: HTMLElement | null = null;

  constructor() {
    // 改名欄位與確認對話框一出現就把焦點帶過去，鍵盤操作不必再找。
    effect(() => {
      const input = this.renameInput()?.nativeElement;
      if (input === undefined) return;
      input.focus();
      input.select();
    });
    effect(() => this.cancelButton()?.nativeElement.focus());
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
