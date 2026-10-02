import { Location } from '@angular/common';
import { DetailLayoutComponent } from '@smart-agri/ui';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  signal,
  viewChild,
  type TemplateRef,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { map, of } from 'rxjs';
import type { AssistantConfigurationView } from '../../../core/domain/assistant.model';
import type { DeleteAssistantResult } from '../../../core/repositories/demo-repository';
import { ApiSessionService } from '../../../core/session/api-session.service';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { repositoryResource } from '../../../core/repositories/repository-resource';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';
import { AssistantPublishingComponent } from '../../publishing/assistant-publishing/assistant-publishing.component';
import { AssistantSettingsStore } from './assistant-settings.store';
import { AssistantOverviewTabComponent } from './tabs/overview-tab/assistant-overview-tab.component';
import { AssistantRulesTabComponent } from './tabs/rules-tab/assistant-rules-tab.component';
import { AssistantSourcesTabComponent } from './tabs/sources-tab/assistant-sources-tab.component';
import { AssistantAcceptanceTabComponent } from './tabs/acceptance-tab/assistant-acceptance-tab.component';

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
  { id: 'acceptance', label: '驗收', intro: '管理可保存的測試題組，重跑後查看逐題結果與歷史。', edits: false },
  { id: 'publishing', label: '發布', intro: '', edits: false },
  { id: 'activity', label: '使用紀錄', intro: '', edits: false },
];

@Component({
  selector: 'app-assistant-detail-page',
  imports: [
    RouterLink,
    MatDialogModule,
    DetailLayoutComponent,
    PageHeaderComponent,
    StatePanelComponent,
    AssistantPublishingComponent,
    AssistantOverviewTabComponent,
    AssistantSourcesTabComponent,
    AssistantRulesTabComponent,
    AssistantAcceptanceTabComponent,
  ],
  providers: [AssistantSettingsStore],
  templateUrl: './assistant-detail-page.component.html',
  styleUrl: './assistant-detail-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssistantDetailPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly repository = inject(DEMO_REPOSITORY);
  protected readonly settings = inject(AssistantSettingsStore);
  private readonly dialog = inject(MatDialog);
  private readonly destroyRef = inject(DestroyRef);
  private readonly deleteDialog = viewChild<TemplateRef<unknown>>('deleteDialog');
  /** API 模式（M3）還沒有匿名使用統計的端點。 */
  protected readonly apiMode = inject(ApiSessionService).apiMode;
  protected readonly deleting = signal(false);
  protected readonly deleteError = signal('');
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
  /**
   * 頁面標題與權限都以設定（`getAssistantSettings`，只有擁有者讀得到）為準：
   * 載入中、讀取失敗、沒有權限（含不存在）分開顯示。
   */
  protected readonly loadStatus = computed(() => this.settings.view().status);

  protected readonly configuration = computed<AssistantConfigurationView | null>(
    () => this.settings.savedSettings()?.configuration ?? null,
  );

  /** Outcome-only statistics from either the API or mock repository. */
  private readonly analyticsResource = repositoryResource({
    params: () => this.configuration()?.id ?? undefined,
    stream: () => this.repository.getAssistantAnalyticsSummary(this.assistantId()),
  });
  protected readonly analytics = computed(() => {
    const result = this.analyticsResource.view();
    return result.status === 'ready' || result.status === 'partial-failure' ? result.data : null;
  });
  protected replyKindLabel(kind: string): string {
    return ({ 'company-data': '組織資料', 'general-knowledge': '一般知識', 'no-result': '查無資料' } as Record<string, string>)[kind] ?? kind;
  }
  protected rejectionReasonLabel(reason: string): string {
    return ({ 'below-threshold': '相關度不足', 'citation-out-of-range': '引用超出範圍', 'no-citation': '缺少引用', 'cannot-answer': '無法回答', 'empty-answer': '空白回答' } as Record<string, string>)[reason] ?? reason;
  }

  protected returnToList(): void {
    void this.router.navigateByUrl('/app/assistants');
  }

  /** 刪除前一定要確認：所有成員與這個助理的對話都會一併刪除（M3 計畫決定 G）。 */
  protected confirmDelete(): void {
    const content = this.deleteDialog();
    if (!content) return;
    this.deleteError.set('');
    this.dialog.open(content, {
      width: 'min(32rem, calc(100vw - 2rem))',
      autoFocus: '#assistant-delete-cancel',
      restoreFocus: true,
      ariaLabelledBy: 'assistant-delete-title',
      ariaDescribedBy: 'assistant-delete-detail',
      role: 'alertdialog',
    });
  }

  protected closeDeleteDialog(): void {
    this.dialog.closeAll();
  }

  protected deleteConfirmed(): void {
    if (this.deleting()) return;
    this.deleting.set(true);
    this.deleteError.set('');
    this.repository
      .deleteAssistant(this.assistantId())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => this.deleted(result),
        error: () => {
          this.deleting.set(false);
          this.deleteError.set('目前無法刪除這個助理，請稍後再試。');
        },
      });
  }

  private deleted(result: DeleteAssistantResult): void {
    this.deleting.set(false);
    if (result.status === 'ready' || result.status === 'partial-failure') {
      this.closeDeleteDialog();
      void this.router.navigateByUrl('/app/assistants');
      return;
    }
    if (result.status === 'permission-denied') this.deleteError.set(result.message);
  }

}
