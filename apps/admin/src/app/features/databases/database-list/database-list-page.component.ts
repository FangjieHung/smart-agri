import { ChangeDetectionStrategy, Component, computed, inject, linkedSignal, signal, TemplateRef, viewChild } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { DataTableComponent, DataTableHeadDirective, DataTableBodyDirective } from '@smart-agri/ui';
import { ADMIN_DATA_TABLE_LABELS } from '../../../shared/ui/data-table-labels';
import { fmtDateTime } from '../../../core/date-utils';
import type { DatabaseSummaryView, DatabaseTemplateId, DatabaseTemplateView } from '../../../core/domain/database.model';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';

@Component({
  selector: 'app-database-list-page',
  imports: [RouterLink, PageHeaderComponent, StatePanelComponent, MatDialogModule, DataTableComponent, DataTableHeadDirective, DataTableBodyDirective],
  templateUrl: './database-list-page.component.html',
  styleUrl: './database-list-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DatabaseListPageComponent {
  protected readonly tableLabels = ADMIN_DATA_TABLE_LABELS;
  private readonly dialog = inject(MatDialog);
  private readonly createDialog = viewChild<TemplateRef<unknown>>('createDialog');
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly router = inject(Router);

  protected readonly view = computed(() => {
    const accountId = this.session.activeAccountId();
    return accountId ? this.repository.listDatabaseSummaries(accountId) : null;
  });

  protected readonly templates = computed<readonly DatabaseTemplateView[]>(() => {
    const accountId = this.session.activeAccountId();
    const result = accountId ? this.repository.listDatabaseTemplates(accountId) : null;
    return result?.status === 'ready' || result?.status === 'partial-failure' ? result.data : [];
  });

  protected readonly templateId = linkedSignal<DatabaseTemplateId | null>(() => this.templates()[0]?.id ?? null);
  protected readonly name = linkedSignal(
    () => this.templates().find((template) => template.id === this.templateId())?.name ?? '',
  );
  protected readonly error = signal('');

  protected openCreateDialog(): void {
    const content = this.createDialog();
    if (content) this.dialog.open(content, { width: 'min(42rem, calc(100vw - 2rem))', autoFocus: '#database-name', restoreFocus: true });
  }

  protected closeCreateDialog(): void { this.dialog.closeAll(); }

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
    const accountId = this.session.activeAccountId();
    const templateId = this.templateId();
    if (!accountId || templateId === null) return;

    const result = this.repository.createDatabaseFromTemplate(accountId, { templateId, name: this.name() });
    if (result.status === 'ready' || result.status === 'partial-failure') {
      this.closeCreateDialog();
      void this.router.navigate(['/app/databases', result.data.id, 'form']);
    } else if (result.status === 'validation-failed' || result.status === 'permission-denied') {
      this.error.set(result.message);
    }
  }

  protected records(item: DatabaseSummaryView): string {
    return item.recordCount === null
      ? '僅指定資料管理者可查看'
      : `${item.subjectCount ?? 0} 位對象・${item.recordCount} 筆紀錄`;
  }

  protected updatedAt(iso: string): string {
    return fmtDateTime(iso);
  }
}
