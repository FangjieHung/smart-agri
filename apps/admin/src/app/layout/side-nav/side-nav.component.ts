import { Component, computed, inject, input, output } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ConnectedPosition, OverlayModule } from '@angular/cdk/overlay';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink, RouterLinkActive } from '@angular/router';
import { map, startWith } from 'rxjs';
import { ApiSessionService } from '../../core/session/api-session.service';
import { DemoSessionService } from '../../core/session/demo-session.service';
import { ZH_TW } from '../../core/i18n/zh-tw';
import { NavEntry, NavGroup, isNavGroup } from './nav-item.model';
import { repositoryResource } from '../../core/repositories/repository-resource';
import { DEMO_REPOSITORY } from '../../core/repositories/tokens';
import { ChatHistoryRevisionService } from '../../core/session/chat-history-revision.service';
import { ConversationRailComponent, type RecentChatThreadView } from '../../features/assistant-use/conversation-rail/conversation-rail.component';

@Component({
  selector: 'app-side-nav',
  imports: [
    RouterLink,
    RouterLinkActive,
    MatButtonModule,
    MatMenuModule,
    MatTooltipModule,
    OverlayModule,
    ConversationRailComponent,
  ],
  templateUrl: './side-nav.component.html',
  styleUrl: './side-nav.component.scss',
})
export class SideNavComponent {
  protected readonly t = ZH_TW;
  protected readonly isNavGroup = isNavGroup;
  private readonly apiSession = inject(ApiSessionService);
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly router = inject(Router);
  private readonly chatRevision = inject(ChatHistoryRevisionService);
  private readonly routeRevision = toSignal(this.router.events.pipe(map(() => this.router.url), startWith(this.router.url)));

  /** API 模式顯示 `/me` 的顯示名稱；mock 模式維持原本的固定文字。 */
  protected readonly userName = computed(() => this.apiSession.displayName() ?? this.t.layout.adminUser);

  /**
   * 最近 10 個對話串（issue #79）：Mock 與 API 模式都走 `listRecentChatThreads`——
   * Mock 內部仍是逐助理組裝，API 模式直接打 `GET /api/v1/chat/recent-conversations`，
   * 呼叫端不必分辨是哪一種。換路由或對話有異動（`ChatHistoryRevisionService`）時重讀。
   */
  private readonly recent = repositoryResource({
    params: () => {
      this.routeRevision();
      this.chatRevision.revision();
      return this.session.activeAccountId() ?? undefined;
    },
    stream: () => this.repository.listRecentChatThreads(),
  });

  protected readonly recentChats = computed<readonly RecentChatThreadView[]>(() => {
    const result = this.recent.view();
    if (result.status !== 'ready' && result.status !== 'partial-failure') return [];
    return result.data.map((thread) => ({
      id: thread.threadId,
      title: thread.title,
      messageCount: thread.messageCount,
      updatedAt: thread.updatedAt,
      assistantId: thread.assistantId,
      assistantName: thread.assistantName,
    }));
  });

  readonly navItems = input.required<NavEntry[]>();
  readonly collapsed = input.required<boolean>();
  readonly isMobile = input.required<boolean>();
  readonly openGroupLabel = input.required<string | null>();
  readonly currentGroupLabel = input.required<string | null>();
  readonly flyoutPositions = input.required<ConnectedPosition[]>();

  readonly toggleCollapse = output<void>();
  readonly toggleGroup = output<string>();
  readonly navClick = output<void>();
  readonly groupDetach = output<void>();

  protected isGroupActive(group: NavGroup): boolean {
    return this.currentGroupLabel() === group.label;
  }

  /** mock 模式清除 Demo 身分回登入頁；API 模式另外走 end-session 結束伺服器端的登入。 */
  protected logout(): void {
    void this.apiSession.logout();
  }

  protected openRecentChat(thread: RecentChatThreadView): void {
    void this.router.navigate(['/app/chat', thread.assistantId, thread.id]);
    this.navClick.emit();
  }
}
