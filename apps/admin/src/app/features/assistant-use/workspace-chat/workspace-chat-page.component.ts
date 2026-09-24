import { BreakpointObserver } from '@angular/cdk/layout';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { map } from 'rxjs/operators';
import type { AssistantSummaryView } from '../../../core/domain/assistant.model';
import type { ChatThreadId } from '../../../core/domain/conversation.model';
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
 * 左側是對話紀錄，右側是對話本身。對話元件與 `/use` 共用同一個實作。
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

  private readonly params = toSignal(this.route.paramMap, {
    initialValue: this.route.snapshot.paramMap,
  });
  /** 對話清單變更後遞增，讓側欄重新讀取。 */
  private readonly revision = signal(0);

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

  protected readonly threadsResult = computed(() => {
    this.revision();
    const accountId = this.session.activeAccountId();
    const assistantId = this.assistantId();
    if (accountId === null || assistantId === null) return null;

    return this.repository.listChatThreads(accountId, assistantId);
  });

  protected readonly threadList = computed(() => {
    const result = this.threadsResult();
    return result?.status === 'ready' || result?.status === 'partial-failure' ? result.data : null;
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

  protected readonly pickable = computed<readonly AssistantSummaryView[]>(() => {
    const accountId = this.session.activeAccountId();
    if (accountId === null) return [];
    const result = this.repository.listUsableAssistants(accountId);
    return result.status === 'ready' || result.status === 'partial-failure' ? result.data : [];
  });

  protected toggleRail(): void {
    this.railOpen.update((open) => !open);
  }

  protected onConversationChanged(threadId: ChatThreadId | null): void {
    this.revision.update((value) => value + 1);
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
    const accountId = this.session.activeAccountId();
    const assistantId = this.assistantId();
    if (accountId === null || assistantId === null) return;

    const result = this.repository.createChatThread(accountId, assistantId);
    this.revision.update((value) => value + 1);
    this.globalHistory.bump();
    if (result.status !== 'ready' && result.status !== 'partial-failure') return;
    const threadId = result.data.threadId;
    if (threadId !== null) this.openThread(threadId);
  }

  protected rename(change: { readonly id: string; readonly title: string }): void {
    const accountId = this.session.activeAccountId();
    const assistantId = this.assistantId();
    if (accountId === null || assistantId === null) return;

    const result = this.repository.renameChatThread(accountId, assistantId, change.id, change.title);
    this.renameError.set(result.status === 'validation-failed' ? result.message : '');
    this.revision.update((value) => value + 1);
    this.globalHistory.bump();
  }

  protected remove(threadId: string): void {
    const accountId = this.session.activeAccountId();
    const assistantId = this.assistantId();
    if (accountId === null || assistantId === null) return;

    this.repository.deleteChatThread(accountId, assistantId, threadId);
    this.renameError.set('');
    this.revision.update((value) => value + 1);
    this.globalHistory.bump();
    // 刪掉正在看的那一段就回到助理的最新對話，網址不再指向已刪除的 id。
    if (threadId === this.conversationId()) void this.router.navigate(['/app/chat', assistantId]);
  }

  protected returnHome(): void {
    void this.router.navigateByUrl('/app/home');
  }
}
