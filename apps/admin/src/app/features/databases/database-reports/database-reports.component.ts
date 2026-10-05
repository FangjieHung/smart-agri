import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import {
  DATABASE_REPORT_FREQUENCY_LABELS,
  type DatabaseReportListItemView,
  type DatabaseReportSummaryStatus,
} from '../../../core/domain/database.model';
import { repositoryResource } from '../../../core/repositories/repository-resource';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';
import { StatusBadgeComponent, type StatusTone } from '../../../shared/ui/status-badge/status-badge.component';
import { DatabaseReportComponent } from './database-report.component';

const SUMMARY_BADGES: Readonly<Record<DatabaseReportSummaryStatus, { readonly label: string; readonly tone: StatusTone } | null>> = {
  'not-requested': null,
  pending: { label: 'AI 摘要產生中', tone: 'info' },
  ready: { label: '含 AI 摘要', tone: 'success' },
  failed: { label: 'AI 摘要失敗', tone: 'warning' },
  discarded: { label: 'AI 摘要已捨棄', tone: 'warning' },
};

/**
 * 資料庫的「定期報表」頁籤（issue #150）：這個資料庫上有哪些助理的排程、已產生的報表清單，以及選中的
 * 那一份。報表由助理擁有者設定的排程在每一期結束後產生，這裡只查看，沒有「立刻產生」。第一版只在
 * 站內查看，不寄 Email 或 LINE。
 */
@Component({
  selector: 'app-database-reports',
  imports: [DatabaseReportComponent, StatePanelComponent, StatusBadgeComponent],
  templateUrl: './database-reports.component.html',
  styleUrl: './database-reports.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DatabaseReportsComponent {
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);

  readonly databaseId = input.required<string>();

  protected readonly frequencyLabels = DATABASE_REPORT_FREQUENCY_LABELS;
  private readonly chosen = signal<string | null>(null);

  private readonly resource = repositoryResource({
    params: () => {
      const accountId = this.session.activeAccountId();
      return accountId ? { accountId, databaseId: this.databaseId() } : undefined;
    },
    stream: ({ databaseId }) => this.repository.listDatabaseReports(databaseId),
  });
  protected readonly view = this.resource.view;
  protected readonly list = computed(() => {
    const view = this.view();
    return view.status === 'ready' || view.status === 'partial-failure' ? view.data : null;
  });

  /** 選中的報表；沒選就是最新一份。 */
  protected readonly selected = computed<DatabaseReportListItemView | null>(() => {
    const reports = this.list()?.reports ?? [];
    return reports.find((report) => report.id === this.chosen()) ?? reports[0] ?? null;
  });

  protected choose(reportId: string): void {
    this.chosen.set(reportId);
  }

  protected reload(): void {
    this.resource.reload();
  }

  protected summaryBadge(report: DatabaseReportListItemView) {
    return report.status === 'generated' ? SUMMARY_BADGES[report.summaryStatus] : null;
  }
}
