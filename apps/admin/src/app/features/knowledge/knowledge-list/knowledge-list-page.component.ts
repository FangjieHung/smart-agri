import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  signal,
  TemplateRef,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { Router, RouterLink } from '@angular/router';
import { DataTableCellDirective, DataTableColumn, DataTableComponent } from '@smart-agri/ui';
import { ADMIN_DATA_TABLE_LABELS } from '../../../shared/ui/data-table-labels';
import { fmtDateTime } from '../../../core/date-utils';
import {
  KNOWLEDGE_BASE_NAME_MAX_LENGTH,
  KNOWLEDGE_BASE_PURPOSE_MAX_LENGTH,
  type KnowledgeBaseSummaryView,
  type KnowledgeSharingScope,
} from '../../../core/domain/knowledge-base.model';
import type { CreateKnowledgeBaseResult } from '../../../core/repositories/demo-repository';
import { repositoryResource } from '../../../core/repositories/repository-resource';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { ApiSessionService } from '../../../core/session/api-session.service';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';
import { summarizeProcessing, type ProcessingSummary } from '../components/knowledge-processing';
import { SHARING_SCOPE_LABELS } from '../components/knowledge-labels';

@Component({
  selector: 'app-knowledge-list-page',
  imports: [
    RouterLink,
    PageHeaderComponent,
    StatePanelComponent,
    DataTableComponent,
    DataTableCellDirective,
    MatDialogModule,
  ],
  templateUrl: './knowledge-list-page.component.html',
  styleUrl: './knowledge-list-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class KnowledgeListPageComponent {
  protected readonly tableLabels = ADMIN_DATA_TABLE_LABELS;
  protected readonly nameMaxLength = KNOWLEDGE_BASE_NAME_MAX_LENGTH;
  protected readonly purposeMaxLength = KNOWLEDGE_BASE_PURPOSE_MAX_LENGTH;
  private readonly session = inject(DemoSessionService);
  private readonly apiSession = inject(ApiSessionService);
  /** API 模式的知識庫回應沒有「已連接助理」（issue #81），這一欄改成提示到助理設定查看。 */
  protected readonly apiMode = this.apiSession.apiMode;
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  private readonly destroyRef = inject(DestroyRef);
  private readonly createDialog = viewChild<TemplateRef<unknown>>('createDialog');

  protected readonly columns: DataTableColumn<KnowledgeBaseSummaryView>[] = [
    { key: 'name', label: '名稱', rowHeader: true },
    { key: 'purpose', label: '用途' },
    { key: 'content', label: '內容', exportSkip: true },
    { key: 'status', label: '處理狀態', exportSkip: true },
    { key: 'scope', label: '分享範圍', exportSkip: true },
    { key: 'assistants', label: '已連接助理', exportSkip: true },
    { key: 'updatedAt', label: '最近更新', exportSkip: true },
  ];

  /** 還沒有 Demo 身分時停在載入中；換身分就重新讀取。 */
  private readonly list = repositoryResource({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: () => this.repository.listKnowledgeBaseSummaries(),
  });
  protected readonly view = this.list.view;

  /** 與 repository 的 `createKnowledgeBase` 同一條權限；API 模式讀 `/me`、mock 讀帳號清單。 */
  protected readonly canCreate = computed(() =>
    this.apiSession.permissions().includes('manage-data-sources'),
  );

  protected readonly name = signal('');
  protected readonly purpose = signal('');
  protected readonly createError = signal('');
  /** 送出中不能再按一次建立。 */
  protected readonly creating = signal(false);
  protected readonly feedback = signal('');

  protected processing(item: KnowledgeBaseSummaryView): ProcessingSummary {
    return summarizeProcessing(item.statusCounts, item.documentCount + item.faqCount);
  }

  protected scopeLabel(scope: KnowledgeSharingScope): string {
    return SHARING_SCOPE_LABELS[scope];
  }

  protected updatedAt(iso: string): string {
    return fmtDateTime(iso);
  }

  protected openDetail(item: KnowledgeBaseSummaryView): void {
    void this.router.navigate(['/app/knowledge', item.id, 'content']);
  }

  protected openCreateDialog(): void {
    const content = this.createDialog();
    if (!content) return;
    this.name.set('');
    this.purpose.set('');
    this.createError.set('');
    this.dialog.open(content, {
      width: 'min(36rem, calc(100vw - 2rem))',
      autoFocus: '#knowledge-name',
      restoreFocus: true,
      ariaLabelledBy: 'create-knowledge-title',
    });
  }

  protected closeCreateDialog(): void {
    this.dialog.closeAll();
  }

  protected setName(event: Event): void {
    this.name.set((event.target as HTMLInputElement).value);
    this.createError.set('');
  }

  protected setPurpose(event: Event): void {
    this.purpose.set((event.target as HTMLTextAreaElement).value);
    this.createError.set('');
  }

  protected create(event: Event): void {
    event.preventDefault();
    if (this.creating()) return;
    // 與後端相同的必填檢查先在前端擋下，省一次來回；其餘規則以 repository 的結果為準。
    if (this.name().trim().length === 0) {
      this.createError.set('請輸入知識庫名稱。');
      return;
    }

    this.creating.set(true);
    this.repository
      .createKnowledgeBase({ name: this.name(), purpose: this.purpose() })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => this.created(result),
        error: () => {
          this.creating.set(false);
          this.createError.set('目前無法建立知識庫，請稍後再試。');
        },
      });
  }

  private created(result: CreateKnowledgeBaseResult): void {
    this.creating.set(false);
    if (result.status === 'ready' || result.status === 'partial-failure') {
      this.closeCreateDialog();
      this.feedback.set(`已建立「${result.data.name}」，只有你看得到，可以在分享權限中開放給其他帳號。`);
      this.list.reload();
    } else if (result.status !== 'loading') {
      this.createError.set(result.message);
    }
  }
}
