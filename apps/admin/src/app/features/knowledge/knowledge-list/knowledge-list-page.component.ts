import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { DataTableCellDirective, DataTableColumn, DataTableComponent } from '@smart-agri/ui';
import { ADMIN_DATA_TABLE_LABELS } from '../../../shared/ui/data-table-labels';
import { fmtDateTime } from '../../../core/date-utils';
import type {
  KnowledgeBaseSummaryView,
  KnowledgeSharingScope,
} from '../../../core/domain/knowledge-base.model';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';
import { summarizeProcessing, type ProcessingSummary } from '../components/knowledge-processing';
import { SHARING_SCOPE_LABELS } from '../components/knowledge-labels';

@Component({
  selector: 'app-knowledge-list-page',
  imports: [RouterLink, PageHeaderComponent, StatePanelComponent, DataTableComponent, DataTableCellDirective],
  templateUrl: './knowledge-list-page.component.html',
  styleUrl: './knowledge-list-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class KnowledgeListPageComponent {
  protected readonly tableLabels = ADMIN_DATA_TABLE_LABELS;
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly router = inject(Router);

  protected readonly columns: DataTableColumn<KnowledgeBaseSummaryView>[] = [
    { key: 'name', label: '名稱', rowHeader: true },
    { key: 'purpose', label: '用途' },
    { key: 'content', label: '內容', exportSkip: true },
    { key: 'status', label: '處理狀態', exportSkip: true },
    { key: 'scope', label: '分享範圍', exportSkip: true },
    { key: 'assistants', label: '已連接助理', exportSkip: true },
    { key: 'updatedAt', label: '最近更新', exportSkip: true },
  ];

  protected readonly view = computed(() => {
    const accountId = this.session.activeAccountId();
    return accountId ? this.repository.listKnowledgeBaseSummaries(accountId) : null;
  });

  protected processing(item: KnowledgeBaseSummaryView): ProcessingSummary {
    return summarizeProcessing(item.statusCounts);
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
}
