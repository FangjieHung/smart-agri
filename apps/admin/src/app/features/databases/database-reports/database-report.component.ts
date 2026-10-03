import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  DATABASE_REPORT_FREQUENCY_LABELS,
  type DatabaseFieldSumView,
} from '../../../core/domain/database.model';
import { fmtDateTime } from '../../../core/date-utils';
import { repositoryResource } from '../../../core/repositories/repository-resource';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';

/** 長條圖的一組：這一期與前一期的值，寬度只是把已算好的數字縮放到 0–100%，不產生新的數字。 */
interface BarRow {
  readonly key: string;
  readonly label: string;
  readonly current: { readonly display: string; readonly percent: number };
  readonly previous: { readonly display: string; readonly percent: number };
}

function percentOf(value: number, max: number): number {
  return max > 0 ? Math.max(0, Math.min(100, (value / max) * 100)) : 0;
}

function barRow(
  key: string,
  label: string,
  current: number,
  currentDisplay: string,
  previous: number,
  previousDisplay: string,
): BarRow {
  const max = Math.max(Math.abs(current), Math.abs(previous));
  return {
    key,
    label,
    current: { display: currentDisplay, percent: percentOf(Math.abs(current), max) },
    previous: { display: previousDisplay, percent: percentOf(Math.abs(previous), max) },
  };
}

/**
 * 一份定期報表（issue #150）：上半是統計——固定查詢 `period-summary` 的結果原樣保存的快照；下半是
 * 「AI 摘要」，另外保存並明確標示。統計與圖表的數字都是伺服器算好的，這裡只顯示：
 *
 * - 紀錄不足（這一期或前一期沒有紀錄）：統計照顯示，但不顯示變化、圖表與 AI 摘要，明說原因；
 * - 摘要失敗或被捨棄（含有統計沒有的數字）：統計與圖表不受影響，摘要區說明原因並可重試；
 * - 沒有產生的期間（助理不再連接、擁有者不能讀取）：只顯示原因。
 */
@Component({
  selector: 'app-database-report',
  imports: [StatePanelComponent],
  templateUrl: './database-report.component.html',
  styleUrl: './database-report.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DatabaseReportComponent {
  private readonly destroyRef = inject(DestroyRef);
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);

  readonly databaseId = input.required<string>();
  readonly reportId = input.required<string>();

  protected readonly frequencyLabels = DATABASE_REPORT_FREQUENCY_LABELS;

  private readonly resource = repositoryResource({
    params: () => {
      const accountId = this.session.activeAccountId();
      return accountId ? { accountId, databaseId: this.databaseId(), reportId: this.reportId() } : undefined;
    },
    stream: ({ databaseId, reportId }) => this.repository.getDatabaseReport(databaseId, reportId),
  });
  protected readonly view = this.resource.view;
  protected readonly report = computed(() => {
    const view = this.view();
    return view.status === 'ready' || view.status === 'partial-failure' ? view.data : null;
  });

  protected readonly retrying = signal(false);
  protected readonly retryFailed = signal(false);

  /** 長條圖：筆數與每個有值的數字欄位；只有兩期都有紀錄時才畫。 */
  protected readonly bars = computed<readonly BarRow[]>(() => {
    const report = this.report();
    const statistics = report?.statistics;
    if (!report || !statistics || report.report.dataState !== 'sufficient') return [];
    return [
      barRow(
        'record-count',
        '紀錄筆數',
        statistics.recordCount,
        `${statistics.recordCount} 筆`,
        statistics.previousRecordCount,
        `${statistics.previousRecordCount} 筆`,
      ),
      ...statistics.sums.map((sum: DatabaseFieldSumView) =>
        barRow(sum.fieldId, sum.label, sum.sum, sum.display, sum.previousSum, sum.previousDisplay),
      ),
    ];
  });

  protected generatedAt(iso: string): string {
    return fmtDateTime(iso);
  }

  protected reload(): void {
    this.resource.reload();
  }

  /** 重新產生 AI 摘要；統計不受影響。伺服器只讓失敗或被捨棄的摘要重來，連按兩次只會有一次模型呼叫。 */
  protected retry(): void {
    if (this.retrying()) return;
    this.retrying.set(true);
    this.retryFailed.set(false);
    this.repository
      .retryDatabaseReportSummary(this.databaseId(), this.reportId())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.retrying.set(false);
          this.resource.reload();
        },
        error: () => {
          this.retrying.set(false);
          this.retryFailed.set(true);
        },
      });
  }
}
