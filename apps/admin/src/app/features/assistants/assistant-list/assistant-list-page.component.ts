import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { AssistantConfigurationView, AssistantSummaryView } from '../../../core/domain/assistant.model';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { AssistantCardComponent } from '../components/assistant-card/assistant-card.component';

interface AssistantListItem {
  readonly assistant: AssistantSummaryView;
  readonly channels: readonly string[];
  readonly recentActivity: string;
}

@Component({
  selector: 'app-assistant-list-page',
  imports: [RouterLink, PageHeaderComponent, AssistantCardComponent],
  templateUrl: './assistant-list-page.component.html',
  styleUrl: './assistant-list-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssistantListPageComponent {
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);

  protected readonly items = computed<readonly AssistantListItem[]>(() => {
    const accountId = this.session.activeAccountId();
    if (!accountId) return [];

    const assistants = this.repository.listAssistantConfigurations(accountId);
    const publishingChannels = this.repository.listPublishingChannels(accountId);

    if (assistants.status !== 'ready') return [];

    const channels = publishingChannels.status === 'ready' ? publishingChannels.data : [];

    return assistants.data.map((assistant) => ({
      assistant: this.toConfigurableSummary(assistant),
      channels: channels
        .filter((channel) => channel.assistantId === assistant.id)
        .map((channel) => channel.name),
      recentActivity: assistant.status === 'draft' ? '設定尚未完成' : '今天更新',
    }));
  });

  private toConfigurableSummary(
    assistant: AssistantConfigurationView,
  ): AssistantSummaryView {
    return {
      id: assistant.id,
      name: assistant.name,
      purpose: assistant.purpose,
      status: assistant.status,
      audience: assistant.audience,
      permission: 'configure',
    };
  }
}
