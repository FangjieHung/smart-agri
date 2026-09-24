import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { type PublishingChannelView } from '../../../core/domain/publishing.model';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { EmptyStateComponent } from '../../../shared/ui/empty-state/empty-state.component';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';
import { ChannelCardComponent } from '../channel-card/channel-card.component';

/** 發布管道總覽：每個助理固定三張管道卡，單一管道故障只標記在該卡片。 */
@Component({
  selector: 'app-channel-overview-page',
  imports: [RouterLink, PageHeaderComponent, StatePanelComponent, EmptyStateComponent, ChannelCardComponent],
  templateUrl: './channel-overview-page.component.html',
  styleUrl: './channel-overview-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ChannelOverviewPageComponent {
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);

  protected readonly view = computed(() => {
    const accountId = this.session.activeAccountId();
    return accountId ? this.repository.listChannelOverview(accountId) : null;
  });

  protected actionLabel(channel: PublishingChannelView): string {
    if (channel.status === 'needs-attention') return '前往處理';
    if (channel.status === 'not-configured') return '開始設定';
    return '查看設定';
  }
}
