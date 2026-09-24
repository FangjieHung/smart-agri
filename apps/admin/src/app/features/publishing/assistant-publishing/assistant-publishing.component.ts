import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { SettingRowComponent } from '@smart-agri/ui';
import {
  PUBLISHING_CHANNEL_TYPES,
  type PublishingChannelType,
} from '../../../core/domain/publishing.model';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';
import { ChannelCardComponent } from '../channel-card/channel-card.component';
import { LineSetupComponent } from '../line-setup/line-setup.component';
import { PlatformSharingComponent } from '../platform-sharing/platform-sharing.component';
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
  ],
  templateUrl: './assistant-publishing.component.html',
  styleUrl: './assistant-publishing.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssistantPublishingComponent {
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly session = inject(DemoSessionService);

  readonly assistantId = input.required<string>();
  /** 來自網址的管道參數，未經驗證；無效時顯示平台內分享。 */
  readonly channel = input<string | null>(null);

  private readonly revision = signal(0);
  protected readonly pauseStatus = signal('');
  protected readonly selectedType = computed<PublishingChannelType>(() => {
    const requested = this.channel();
    return PUBLISHING_CHANNEL_TYPES.find((type) => type === requested) ?? 'platform';
  });
  protected readonly result = computed(() => {
    this.revision();
    const accountId = this.session.activeAccountId();
    return accountId ? this.repository.getAssistantPublishing(accountId, this.assistantId()) : null;
  });
  protected readonly view = computed(() => {
    const result = this.result();
    return result?.status === 'ready' || result?.status === 'partial-failure' ? result.data : null;
  });
  protected readonly channels = computed(() => {
    const view = this.view();
    return view === null ? [] : PUBLISHING_CHANNEL_TYPES.map((type) => view[type].channel);
  });
  protected readonly deniedMessage = computed(() => {
    const result = this.result();
    return result?.status === 'permission-denied' ? result.message : '';
  });
  protected readonly selectedChannel = computed(() => this.view()?.[this.selectedType()].channel ?? null);

  protected refresh(): void {
    this.revision.update((value) => value + 1);
  }

  protected togglePause(): void {
    const accountId = this.session.activeAccountId();
    const channel = this.selectedChannel();
    if (!accountId || channel === null) return;
    const pause = channel.status !== 'paused';
    const result = this.repository.setPublishingChannelPaused(accountId, this.assistantId(), channel.type, pause);
    if (result.status === 'ready' || result.status === 'partial-failure') {
      this.pauseStatus.set(
        pause ? `已暫停${channel.name}，其他管道照常運作。` : `已恢復${channel.name}。`,
      );
      this.refresh();
    }
  }
}
