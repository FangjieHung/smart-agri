import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  linkedSignal,
  signal,
  TemplateRef,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { DataTableCellDirective, DataTableColumn, DataTableComponent } from '@smart-agri/ui';
import { ADMIN_DATA_TABLE_LABELS } from '../../../shared/ui/data-table-labels';
import { fmtDateTime } from '../../../core/date-utils';
import type {
  DatabaseListFilter,
  DatabaseSummaryView,
  DatabaseTemplateId,
  DatabaseTemplateView,
} from '../../../core/domain/database.model';
import type { CreateDatabaseResult } from '../../../core/repositories/demo-repository';
import { repositoryResource } from '../../../core/repositories/repository-resource';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';
import { StatusBadgeComponent } from '../../../shared/ui/status-badge/status-badge.component';

/** 與後端 `Database.NameMaxLength`、mock 的檢查相同。 */
export const DATABASE_NAME_MAX_LENGTH = 40;

const CREATE_FAILED_MESSAGE = '目前無法建立資料庫，你輸入的內容仍保留，請稍後再試一次。';

@Component({
  selector: 'app-database-list-page',
  imports: [
    RouterLink,
    PageHeaderComponent,
    StatePanelComponent,
    StatusBadgeComponent,
    MatDialogModule,
    DataTableComponent,
    DataTableCellDirective,
  ],
  templateUrl: './database-list-page.component.html',
  styleUrl: './database-list-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DatabaseListPageComponent {
  protected readonly tableLabels = ADMIN_DATA_TABLE_LABELS;
  protected readonly nameMaxLength = DATABASE_NAME_MAX_LENGTH;
  private readonly dialog = inject(MatDialog);
  private readonly destroyRef = inject(DestroyRef);
  private readonly createDialog = viewChild<TemplateRef<unknown>>('createDialog');
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly router = inject(Router);

  protected readonly columns: DataTableColumn<DatabaseSummaryView>[] = [
    { key: 'name', label: '名稱', rowHeader: true },
    { key: 'purpose', label: '用途' },
    { key: 'owner', label: '擁有者', exportSkip: true },
    { key: 'form', label: '表單', exportSkip: true },
    { key: 'records', label: '收集紀錄', exportSkip: true },
    { key: 'assistants', label: '已連接助理', exportSkip: true },
    { key: 'updatedAt', label: '最近更新', exportSkip: true },
  ];

  /** 預設只列使用中的；「已封存」只列封存的（#180）。 */
  protected readonly filter = signal<DatabaseListFilter>('active');

  /** 還沒有 Demo 身分時停在載入中；換身分或篩選就重新讀取。 */
  private readonly list = repositoryResource({
    params: () => {
      const accountId = this.session.activeAccountId();
      return accountId ? { accountId, filter: this.filter() } : undefined;
    },
    stream: ({ filter }) => this.repository.listDatabaseSummaries(filter),
  });
  protected readonly view = this.list.view;

  /** 沒有資料來源管理權限時是 permission-denied：不顯示建立入口，只說明原因。 */
  private readonly templateList = repositoryResource({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: () => this.repository.listDatabaseTemplates(),
  });
  protected readonly templatesView = this.templateList.view;

  protected readonly templates = computed<readonly DatabaseTemplateView[]>(() => {
    const result = this.templatesView();
    return result.status === 'ready' || result.status === 'partial-failure' ? result.data : [];
  });
  protected readonly canCreate = computed(() => this.templates().length > 0);

  protected readonly templateId = linkedSignal<DatabaseTemplateId | null>(() => this.templates()[0]?.id ?? null);
  protected readonly name = linkedSignal(
    () => this.templates().find((template) => template.id === this.templateId())?.name ?? '',
  );
  protected readonly error = signal('');
  /** 送出中不能再按一次建立，也不能關閉對話框以免結果無處顯示。 */
  protected readonly creating = signal(false);

  protected openCreateDialog(): void {
    const content = this.createDialog();
    if (!content) return;
    this.error.set('');
    this.dialog.open(content, {
      width: 'min(42rem, calc(100vw - 2rem))',
      autoFocus: '#database-name',
      restoreFocus: true,
      ariaLabelledBy: 'create-title',
    });
  }

  protected closeCreateDialog(): void {
    if (this.creating()) return;
    this.dialog.closeAll();
  }

  protected showFilter(filter: DatabaseListFilter): void {
    this.filter.set(filter);
  }

  protected reloadTemplates(): void {
    this.templateList.reload();
  }

  protected chooseTemplate(template: DatabaseTemplateView): void {
    this.templateId.set(template.id);
    this.error.set('');
  }

  protected setName(event: Event): void {
    this.name.set((event.target as HTMLInputElement).value);
    this.error.set('');
  }

  protected create(event: Event): void {
    event.preventDefault();
    const templateId = this.templateId();
    if (this.creating() || templateId === null) return;
    // 與後端相同的必填檢查先在前端擋下，省一次來回；其餘規則以 repository 的結果為準。
    if (this.name().trim().length === 0) {
      this.error.set('請輸入資料庫名稱。');
      return;
    }

    this.creating.set(true);
    this.repository
      .createDatabaseFromTemplate({ templateId, name: this.name() })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => this.created(result),
        error: () => {
          this.creating.set(false);
          this.error.set(CREATE_FAILED_MESSAGE);
        },
      });
  }

  private created(result: CreateDatabaseResult): void {
    this.creating.set(false);
    if (result.status === 'ready' || result.status === 'partial-failure') {
      this.dialog.closeAll();
      this.filter.set('active');
      this.list.reload();
      void this.router.navigate(['/app/databases', result.data.id, 'form']);
    } else if (result.status === 'validation-failed' || result.status === 'permission-denied') {
      this.error.set(result.message);
    }
  }

  /** 筆數只有可讀紀錄的人拿得到（API 模式也一樣，#177）；其他人是 null。 */
  protected records(item: DatabaseSummaryView): string {
    return item.recordCount === null
      ? '僅指定資料管理者可查看'
      : `${item.subjectCount ?? 0} 位對象・${item.recordCount} 筆紀錄`;
  }

  /** mock 與 API 模式一樣：只顯示 repository 給的名稱（API 只含呼叫者自己的助理，#148），空的是「尚未連接」。 */
  protected assistants(item: DatabaseSummaryView): string {
    return item.connectedAssistantNames.join('、') || '尚未連接';
  }

  protected updatedAt(iso: string): string {
    return fmtDateTime(iso);
  }

  protected openDetail(item: DatabaseSummaryView): void {
    void this.router.navigate(['/app/databases', item.id, 'form']);
  }
}
