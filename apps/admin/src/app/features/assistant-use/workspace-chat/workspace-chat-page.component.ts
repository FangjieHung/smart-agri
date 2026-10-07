import { BreakpointObserver } from '@angular/cdk/layout';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { map } from 'rxjs/operators';
import type { AssistantSummaryView } from '../../../core/domain/assistant.model';
import type { ChatThreadId } from '../../../core/domain/conversation.model';
import { repositoryResource } from '../../../core/repositories/repository-resource';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { ChatHistoryRevisionService } from '../../../core/session/chat-history-revision.service';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';
import { ChatConversationComponent } from '../conversation/chat-conversation.component';
import { ConversationRailComponent } from '../conversation-rail/conversation-rail.component';

/** 與工作區側邊導覽同一個斷點：以下把對話紀錄收成可展開的面板。 */
const MOBILE_QUERY = '(max-width: 900px)';

/**
 * 工作區內的助理對話（`/app/chat/:assistantId[/:conversationId]`）：
 * 左側是對話紀錄，右側是對話本身。對話元件與 `/chat/:assistantId` 共用同一個實作。
 *
 * 對話串的讀取與管理是非同步契約（issue #79）：清單用 `repositoryResource` 讀取，
 * 寫入（開新對話、改名、刪除）改成訂閱，並用進行中旗標擋重複送出。
 */
@Component({
  selector: 'app-workspace-chat-page',
  imports: [
    RouterLink,
    PageHeaderComponent,
    StatePanelComponent,
    ChatConversationComponent,
    ConversationRailComponent,
  ],
  templateUrl: './workspace-chat-page.component.html',
  styleUrl: './workspace-chat-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkspaceChatPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly globalHistory = inject(ChatHistoryRevisionService);
  private readonly destroyRef = inject(DestroyRef);

  private readonly params = toSignal(this.route.paramMap, {
    initialValue: this.route.snapshot.paramMap,
  });

  protected readonly assistantId = computed(() => this.params().get('assistantId'));
  protected readonly conversationId = computed(() => this.params().get('conversationId'));
  protected readonly isMobile = toSignal(
    inject(BreakpointObserver)
      .observe([MOBILE_QUERY])
      .pipe(map((state) => state.matches)),
    { initialValue: false },
  );
  protected readonly railOpen = signal(false);
  protected readonly renameError = signal('');
  /** 有請求進行中時擋重複送出（開新對話、改名、刪除共用一個旗標，畫面上不會同時觸發兩個）。 */
  protected readonly busy = signal(false);

  private readonly threads = repositoryResource({
    params: () => {
      const assistantId = this.assistantId();
      return assistantId === null ? undefined : assistantId;
    },
    stream: (assistantId) => this.repository.listChatThreads(assistantId),
  });
  protected readonly threadsResult = this.threads.view;

  protected readonly threadList = computed(() => {
    const result = this.threadsResult();
    return result.status === 'ready' || result.status === 'partial-failure' ? result.data : null;
  });

  /** 沒有指定對話時，開啟的就是清單中最新的那一段。 */
  protected readonly activeThreadId = computed(
    () => this.conversationId() ?? this.threadList()?.threads[0]?.id ?? null,
  );

  protected readonly announcement = computed(() => {
    const active = this.activeThreadId();
    const thread = this.threadList()?.threads.find((candidate) => candidate.id === active);
    return thread === undefined ? '' : `已切換到對話：${thread.title}`;
  });

  /** 沒有指定助理時的選單（issue #81：非同步契約；API 模式是真實的助理 GUID）。 */
  private readonly usable = repositoryResource({
    params: () => (this.assistantId() === null ? (this.session.activeAccountId() ?? undefined) : undefined),
    stream: () => this.repository.listUsableAssistants(),
  });
  protected readonly pickableView = this.usable.view;

  protected readonly pickable = computed<readonly AssistantSummaryView[]>(() => {
    const result = this.usable.view();
    return result.status === 'ready' || result.status === 'partial-failure' ? result.data : [];
  });

  protected toggleRail(): void {
    this.railOpen.update((open) => !open);
  }

  protected onConversationChanged(threadId: ChatThreadId | null): void {
    this.threads.reload();
    this.globalHistory.bump();
    if (threadId !== null && threadId !== this.conversationId()) this.openThread(threadId);
  }

  protected openThread(threadId: string): void {
    this.renameError.set('');
    this.railOpen.set(false);
    const assistantId = this.assistantId();
    if (assistantId === null) return;
    void this.router.navigate(['/app/chat', assistantId, threadId]);
  }

  protected startNewConversation(): void {
    const assistantId = this.assistantId();
    if (assistantId === null || this.busy()) return;

    this.busy.set(true);
    this.repository
      .createChatThread(assistantId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.busy.set(false);
          this.threads.reload();
          this.globalHistory.bump();
          if (result.status !== 'ready' && result.status !== 'partial-failure') return;
          const threadId = result.data.threadId;
          if (threadId !== null) this.openThread(threadId);
        },
        error: () => this.busy.set(false),
      });
  }

  protected rename(change: { readonly id: string; readonly title: string }): void {
    const assistantId = this.assistantId();
    if (assistantId === null || this.busy()) return;

    this.busy.set(true);
    this.repository
      .renameChatThread(assistantId, change.id, change.title)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.busy.set(false);
          this.renameError.set(result.status === 'validation-failed' ? result.message : '');
          this.threads.reload();
          this.globalHistory.bump();
        },
        error: () => this.busy.set(false),
      });
  }

  protected remove(threadId: string): void {
    const assistantId = this.assistantId();
    if (assistantId === null || this.busy()) return;

    this.busy.set(true);
    this.repository
      .deleteChatThread(assistantId, threadId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.renameError.set('');
          this.threads.reload();
          this.globalHistory.bump();
          // 刪掉正在看的那一段就回到助理的最新對話，網址不再指向已刪除的 id。
          if (threadId === this.conversationId()) void this.router.navigate(['/app/chat', assistantId]);
        },
        error: () => this.busy.set(false),
      });
  }

  protected returnHome(): void {
    void this.router.navigateByUrl('/app/home');
  }
}
