import { Location } from '@angular/common';
import { DetailLayoutComponent } from '@smart-agri/ui';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { map, of } from 'rxjs';
import type { AssistantConfigurationView } from '../../../core/domain/assistant.model';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';
import { AssistantPublishingComponent } from '../../publishing/assistant-publishing/assistant-publishing.component';
import { AssistantSettingsStore } from './assistant-settings.store';
import { AssistantOverviewTabComponent } from './tabs/overview-tab/assistant-overview-tab.component';
import { AssistantRulesTabComponent } from './tabs/rules-tab/assistant-rules-tab.component';
import { AssistantSourcesTabComponent } from './tabs/sources-tab/assistant-sources-tab.component';

interface AssistantTab {
  readonly id: string;
  readonly label: string;
  /** 頁籤標題下方的一句說明；三個編輯頁籤說的是「改了會怎樣」。 */
  readonly intro: string;
  /** 這個頁籤是不是會直接改動助理設定。 */
  readonly edits: boolean;
}

const TABS: readonly AssistantTab[] = [
  {
    id: 'overview',
    label: '概覽',
    intro: '助理是誰、幫忙做什麼、給誰用。改動會立刻套用到這個助理，不需要按儲存。',
    edits: true,
  },
  {
    id: 'data-sources',
    label: '資料來源',
    intro: '助理回答時可以引用哪些知識庫與資料庫。加入只會建立連接，不會複製原始資料。',
    edits: true,
  },
  {
    id: 'rules',
    label: '回答與記錄',
    intro: '回答範圍、找不到資料時的回覆，以及對話與資料要不要保存。',
    edits: true,
  },
  { id: 'test', label: '測試', intro: '', edits: false },
  { id: 'publishing', label: '發布', intro: '', edits: false },
  { id: 'activity', label: '使用紀錄', intro: '', edits: false },
];

@Component({
  selector: 'app-assistant-detail-page',
  imports: [
    RouterLink,
    DetailLayoutComponent,
    PageHeaderComponent,
    StatePanelComponent,
    AssistantPublishingComponent,
    AssistantOverviewTabComponent,
    AssistantSourcesTabComponent,
    AssistantRulesTabComponent,
  ],
  providers: [AssistantSettingsStore],
  templateUrl: './assistant-detail-page.component.html',
  styleUrl: './assistant-detail-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssistantDetailPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);
  protected readonly settings = inject(AssistantSettingsStore);
  private readonly routeParams = toSignal(this.route.paramMap, {
    initialValue: this.route.snapshot.paramMap,
  });

  /** 由建立精靈導向時帶入的一次性導覽狀態。 */
  protected readonly justCreated = (() => {
    const state: unknown = inject(Location).getState();
    return (
      typeof state === 'object' &&
      state !== null &&
      (state as Record<string, unknown>)['assistantCreated'] === true
    );
  })();

  /** 發布頁籤選取的管道（?channel=platform|website|line），由發布元件驗證。 */
  protected readonly publishingChannel = toSignal(
    (this.route.queryParamMap ?? of(null)).pipe(map((params) => params?.get('channel') ?? null)),
    { initialValue: this.route.snapshot.queryParamMap?.get('channel') ?? null },
  );

  protected readonly assistantId = computed(() => this.routeParams().get('id') ?? '');
  protected readonly tabs = TABS;
  private readonly selectedTabId = toSignal(
    this.route.paramMap.pipe(map((params) => params.get('tab'))),
    { initialValue: this.route.snapshot.paramMap.get('tab') },
  );
  protected readonly activeTab = computed<AssistantTab>(() => {
    const requestedTab = this.selectedTabId();
    return TABS.find((tab) => tab.id === requestedTab) ?? TABS[0];
  });
  protected readonly configuration = computed<AssistantConfigurationView | null>(() => {
    const accountId = this.session.activeAccountId();
    if (!accountId) return null;

    const result = this.repository.listAssistantConfigurations(accountId);
    if (result.status !== 'ready') return null;

    return result.data.find((assistant) => assistant.id === this.assistantId()) ?? null;
  });

  /** 匿名使用摘要：只有次數與比例，不含任何對話文字。 */
  protected readonly analytics = computed(() => {
    const accountId = this.session.activeAccountId();
    const configuration = this.configuration();
    if (!accountId || configuration === null) return null;
    const result = this.repository.getAssistantAnalytics(accountId, configuration.id);
    return result.status === 'ready' || result.status === 'partial-failure' ? result.data : null;
  });

  protected returnToList(): void {
    void this.router.navigateByUrl('/app/assistants');
  }

}
