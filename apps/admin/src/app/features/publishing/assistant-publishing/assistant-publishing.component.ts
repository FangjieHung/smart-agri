import { ChangeDetectionStrategy, Component, computed, DestroyRef, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { SettingRowComponent } from '@smart-agri/ui';
import {
  isUnavailableChannel,
  PUBLISHING_CHANNEL_TYPES,
  type LineSetupView,
  type PublishingChannelType,
  type UnavailablePublishingChannelView,
} from '../../../core/domain/publishing.model';
import { repositoryResource } from '../../../core/repositories/repository-resource';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';
import { ChannelCardComponent } from '../channel-card/channel-card.component';
import { LineSetupComponent } from '../line-setup/line-setup.component';
import { PlatformSharingComponent } from '../platform-sharing/platform-sharing.component';
import { UsageNoticeComponent } from '../usage-notice/usage-notice.component';
import { WebsiteEmbedComponent } from '../website-embed/website-embed.component';

/** 單一助理的發布設定：三張管道卡與目前選取管道的設定面板，供助理詳情「發布」頁籤使用。 */
@Component({
  selector: 'app-assistant-publishing',
  imports: [
    RouterLink,
    SettingRowComponent,
    StatePanelComponent,
    ChannelCardComponent,
    PlatformSharingComponent,
    WebsiteEmbedComponent,
    LineSetupComponent,
    UsageNoticeComponent,
  ],
  templateUrl: './assistant-publishing.component.html',
  styleUrl: './assistant-publishing.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssistantPublishingComponent {
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly session = inject(DemoSessionService);
  private readonly destroyRef = inject(DestroyRef);

  readonly assistantId = input.required<string>();
  /** 來自網址的管道參數，未經驗證；無效時顯示平台內分享。 */
  readonly channel = input<string | null>(null);

  protected readonly pauseStatus = signal('');
  /** 暫停／恢復送出中：擋重複送出。 */
  protected readonly pausing = signal(false);
  protected readonly selectedType = computed<PublishingChannelType>(() => {
    const requested = this.channel();
    return PUBLISHING_CHANNEL_TYPES.find((type) => type === requested) ?? 'platform';
  });

  /** 非同步契約（issue #81）：寫入成功後 `reload()`，重新讀取期間保留上一份資料。 */
  private readonly publishing = repositoryResource({
    params: () => {
      const accountId = this.session.activeAccountId();
      return accountId === null ? undefined : { accountId, assistantId: this.assistantId() };
    },
    stream: ({ assistantId }) => this.repository.getAssistantPublishing(assistantId),
  });
  protected readonly result = this.publishing.view;
  protected readonly view = computed(() => {
    const result = this.result();
    return result.status === 'ready' || result.status === 'partial-failure' ? result.data : null;
  });
  protected readonly channels = computed(() => {
    const view = this.view();
    return view === null ? [] : PUBLISHING_CHANNEL_TYPES.map((type) => view[type].channel);
  });
  protected readonly deniedMessage = computed(() => {
    const result = this.result();
    return result.status === 'permission-denied' ? result.message : '';
  });
  protected readonly selectedChannel = computed(() => this.view()?.[this.selectedType()].channel ?? null);

  /** LINE 在 API 模式尚未開放時是 null，畫面改顯示說明。 */
  protected configurableLine(view: LineSetupView | UnavailablePublishingChannelView): LineSetupView | null {
    return isUnavailableChannel(view) ? null : view;
  }

  protected unavailableMessage(view: LineSetupView | UnavailablePublishingChannelView): string {
    return isUnavailableChannel(view) ? view.message : '';
  }

  protected refresh(): void {
    this.publishing.reload();
  }

  protected togglePause(): void {
    const channel = this.selectedChannel();
    if (channel === null || this.pausing()) return;
    const pause = channel.status !== 'paused';
    this.pausing.set(true);
    this.repository
      .setPublishingChannelPaused(this.assistantId(), channel.type, pause)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.pausing.set(false);
          if (result.status === 'ready' || result.status === 'partial-failure') {
            this.pauseStatus.set(
              pause ? `已暫停${channel.name}，其他管道照常運作。` : `已恢復${channel.name}。`,
            );
            this.refresh();
          } else if (result.status === 'permission-denied') {
            this.pauseStatus.set(result.message);
          }
        },
        error: () => {
          this.pausing.set(false);
          this.pauseStatus.set('目前無法變更管道狀態，請稍後再試。');
          this.refresh();
        },
      });
  }
}
