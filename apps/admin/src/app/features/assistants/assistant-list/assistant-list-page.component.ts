import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { AssistantConfigurationView, AssistantSummaryView } from '../../../core/domain/assistant.model';
import type { NamedAssistantDraftView } from '../../../core/domain/assistant-draft.model';
import type { PublishingChannelView } from '../../../core/domain/publishing.model';
import type { RepositoryView } from '../../../core/repositories/demo-repository';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { ApiSessionService } from '../../../core/session/api-session.service';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';
import { AssistantCardComponent } from '../components/assistant-card/assistant-card.component';

interface AssistantListItem {
  readonly assistant: AssistantSummaryView;
  readonly channels: readonly string[];
  readonly recentActivity: string;
}

/** 讀取失敗（未預期的例外）時視為 `error`，與其餘三種 repository 狀態並列。 */
type SafeView<T> = RepositoryView<T> | { readonly status: 'error' };

/** 「我建立的」「組織建立的」各自要在畫面上呈現的整體狀態：loading／error／permission-denied 蓋過內容，其餘照常顯示（含 partial-failure 的資料）。 */
type SectionStatus = 'loading' | 'error' | 'permission-denied' | 'ready';

function sectionStatus(...views: readonly SafeView<unknown>[]): SectionStatus {
  if (views.some((view) => view.status === 'loading')) return 'loading';
  if (views.some((view) => view.status === 'error')) return 'error';
  if (views.some((view) => view.status === 'permission-denied')) return 'permission-denied';
  return 'ready';
}

function permissionMessage<T>(view: SafeView<T>): string | null {
  return view.status === 'permission-denied' ? view.message : null;
}

function partialFailureMessage<T>(view: SafeView<T>): string | null {
  return view.status === 'partial-failure' ? view.message : null;
}

function readyData<T>(view: SafeView<readonly T[]>): readonly T[] {
  return view.status === 'ready' || view.status === 'partial-failure' ? view.data : [];
}

@Component({
  selector: 'app-assistant-list-page',
  imports: [RouterLink, PageHeaderComponent, StatePanelComponent, AssistantCardComponent],
  templateUrl: './assistant-list-page.component.html',
  styleUrl: './assistant-list-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssistantListPageComponent {
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly apiSession = inject(ApiSessionService);

  /** 與 repository 的 `createNamedAssistantDraft` 同一條權限：沒有權限就不提供建立入口。 */
  protected readonly canCreateAssistant = computed(() =>
    this.apiSession.permissions().includes('manage-assistants'),
  );

  private readonly myAssistantsView = computed<SafeView<readonly AssistantConfigurationView[]>>(() => {
    const accountId = this.session.activeAccountId();
    if (!accountId) return { status: 'loading' };
    return this.safeRead(() => this.repository.listAssistantConfigurations(accountId));
  });

  private readonly draftsView = computed<SafeView<readonly NamedAssistantDraftView[]>>(() => {
    const accountId = this.session.activeAccountId();
    if (!accountId) return { status: 'loading' };
    const view = this.safeRead(() => this.repository.listNamedAssistantDrafts(accountId));
    // 沒有 manage-assistants 的帳號結構上本來就不能有草稿，這跟「建立新助理」入口用同一條權限判斷，
    // 不是需要另外中斷畫面的錯誤；維持顯示「還沒有建立助理」而不是權限不足面板。
    if (view.status === 'permission-denied' && view.reason === 'assistant-draft') {
      return { status: 'ready', data: [] };
    }
    return view;
  });

  private readonly channelsView = computed<SafeView<readonly PublishingChannelView[]>>(() => {
    const accountId = this.session.activeAccountId();
    if (!accountId) return { status: 'loading' };
    return this.safeRead(() => this.repository.listPublishingChannels(accountId));
  });

  private readonly companyView = computed<SafeView<readonly AssistantSummaryView[]>>(() => {
    const accountId = this.session.activeAccountId();
    if (!accountId) return { status: 'loading' };
    return this.safeRead(() => this.repository.listUsableAssistants(accountId));
  });

  /** 「我建立的」區塊的整體狀態；管道清單只用來補充頻道名稱，讀取失敗不擋住助理與草稿本身。 */
  protected readonly myState = computed<SectionStatus>(() =>
    sectionStatus(this.myAssistantsView(), this.draftsView()),
  );

  protected readonly myPermissionMessage = computed(
    () => permissionMessage(this.myAssistantsView()) ?? permissionMessage(this.draftsView()),
  );

  protected readonly myPartialFailureMessage = computed(
    () =>
      partialFailureMessage(this.myAssistantsView()) ??
      partialFailureMessage(this.draftsView()) ??
      partialFailureMessage(this.channelsView()),
  );

  protected readonly companyState = computed<SectionStatus>(() => sectionStatus(this.companyView()));

  protected readonly companyPermissionMessage = computed(() => permissionMessage(this.companyView()));

  protected readonly companyPartialFailureMessage = computed(() => partialFailureMessage(this.companyView()));

  protected readonly myItems = computed<readonly AssistantListItem[]>(() => {
    const assistants = readyData(this.myAssistantsView());
    const channels = readyData(this.channelsView());

    return assistants.map((assistant) => ({
      assistant: this.toConfigurableSummary(assistant),
      channels: channels
        .filter((channel) => channel.assistantId === assistant.id && channel.status !== 'not-configured')
        .map((channel) => channel.name),
      recentActivity: assistant.status === 'draft' ? '設定尚未完成' : '今天更新',
    }));
  });

  protected readonly companyItems = computed<readonly AssistantListItem[]>(() => {
    const assistants = readyData(this.companyView());
    return assistants.filter((assistant) => assistant.permission !== 'configure').map((assistant) => ({
      assistant,
      channels: [],
      recentActivity: '組織分享',
    }));
  });

  protected readonly drafts = computed<readonly NamedAssistantDraftView[]>(() => readyData(this.draftsView()));

  /** 同步呼叫理論上不會拋出例外，仍防禦性接住並顯示為 `error`，不讓整頁白畫面。 */
  private safeRead<T>(read: () => RepositoryView<T>): SafeView<T> {
    try {
      return read();
    } catch {
      return { status: 'error' };
    }
  }

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
