import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import type { AssistantConfigurationView } from '../../../core/domain/assistant.model';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';

interface AssistantTab {
  readonly id: string;
  readonly label: string;
  readonly placeholder: string;
}

const TABS: readonly AssistantTab[] = [
  { id: 'overview', label: '概覽', placeholder: '概覽內容將在下一階段完成' },
  { id: 'data-sources', label: '資料來源', placeholder: '資料來源內容將在下一階段完成' },
  { id: 'rules', label: '回答與記錄', placeholder: '回答與記錄內容將在下一階段完成' },
  { id: 'test', label: '測試', placeholder: '測試內容將在下一階段完成' },
  { id: 'publishing', label: '發布', placeholder: '發布內容將在下一階段完成' },
  { id: 'activity', label: '使用紀錄', placeholder: '使用紀錄內容將在下一階段完成' },
];

@Component({
  selector: 'app-assistant-detail-page',
  imports: [RouterLink, PageHeaderComponent, StatePanelComponent],
  templateUrl: './assistant-detail-page.component.html',
  styleUrl: './assistant-detail-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssistantDetailPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly routeParams = toSignal(this.route.paramMap, {
    initialValue: this.route.snapshot.paramMap,
  });

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

  protected returnToList(): void {
    void this.router.navigateByUrl('/app/assistants');
  }

}
