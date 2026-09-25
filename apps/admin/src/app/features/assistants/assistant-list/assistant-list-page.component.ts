import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { AssistantConfigurationView, AssistantSummaryView } from '../../../core/domain/assistant.model';
import type { NamedAssistantDraftView } from '../../../core/domain/assistant-draft.model';
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

  /** 與 repository 的 `createNamedAssistantDraft` 同一條權限：沒有權限就不提供建立入口。 */
  protected readonly canCreateAssistant = computed(() => {
    const accountId = this.session.activeAccountId();
    const accounts = this.repository.listAccounts();
    if (!accountId || accounts.status !== 'ready') return false;
    return accounts.data.some(
      (account) => account.id === accountId && account.permissions.includes('manage-assistants'),
    );
  });

  protected readonly myItems = computed<readonly AssistantListItem[]>(() => {
    const accountId = this.session.activeAccountId();
    if (!accountId) return [];

    const assistants = this.repository.listAssistantConfigurations(accountId);
    const publishingChannels = this.repository.listPublishingChannels(accountId);

    if (assistants.status !== 'ready') return [];

    const channels = publishingChannels.status === 'ready' ? publishingChannels.data : [];

    return assistants.data.map((assistant) => ({
      assistant: this.toConfigurableSummary(assistant),
      channels: channels
        .filter((channel) => channel.assistantId === assistant.id && channel.status !== 'not-configured')
        .map((channel) => channel.name),
      recentActivity: assistant.status === 'draft' ? '設定尚未完成' : '今天更新',
    }));
  });

  protected readonly companyItems = computed<readonly AssistantListItem[]>(() => {
    const accountId = this.session.activeAccountId();
    if (!accountId) return [];
    const result = this.repository.listUsableAssistants(accountId);
    if (result.status !== 'ready' && result.status !== 'partial-failure') return [];
    return result.data.filter((assistant) => assistant.permission !== 'configure').map((assistant) => ({
      assistant,
      channels: [],
      recentActivity: '組織分享',
    }));
  });

  protected readonly drafts = computed<readonly NamedAssistantDraftView[]>(() => {
    const accountId = this.session.activeAccountId();
    if (!accountId) return [];
    const result = this.repository.listNamedAssistantDrafts(accountId);
    return result.status === 'ready' || result.status === 'partial-failure' ? result.data : [];
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
