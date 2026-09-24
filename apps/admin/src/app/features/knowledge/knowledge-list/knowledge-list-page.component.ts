import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { DataTableComponent, DataTableHeadDirective, DataTableBodyDirective } from '@smart-agri/ui';
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
  imports: [RouterLink, PageHeaderComponent, StatePanelComponent, DataTableComponent, DataTableHeadDirective, DataTableBodyDirective],
  templateUrl: './knowledge-list-page.component.html',
  styleUrl: './knowledge-list-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class KnowledgeListPageComponent {
  protected readonly tableLabels = ADMIN_DATA_TABLE_LABELS;
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);

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
}
