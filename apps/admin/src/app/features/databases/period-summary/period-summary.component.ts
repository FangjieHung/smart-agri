import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import {
  DATABASE_PERIOD_OPTIONS,
  type DatabasePeriodName,
  type TrackedSubjectView,
} from '../../../core/domain/database.model';
import { repositoryResource } from '../../../core/repositories/repository-resource';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';

/**
 * 一位追蹤對象的期間統計：選一個期間，看這一期與前一期的有效紀錄筆數與每個數字欄位的加總。
 * 數字、單位與差異標籤都由 repository（API 模式是伺服器的固定查詢 `period-summary`）算好，這裡只顯示；
 * 沒有紀錄時筆數與加總是 0，明說「這一期沒有紀錄」，不顯示成錯誤。
 */
@Component({
  selector: 'app-period-summary',
  imports: [StatePanelComponent],
  templateUrl: './period-summary.component.html',
  styleUrl: './period-summary.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PeriodSummaryComponent {
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);

  readonly databaseId = input.required<string>();
  readonly subject = input.required<TrackedSubjectView>();

  protected readonly options = DATABASE_PERIOD_OPTIONS;
  protected readonly period = signal<DatabasePeriodName>('last-30-days');

  private readonly resource = repositoryResource({
    params: () => {
      const accountId = this.session.activeAccountId();
      return accountId
        ? { accountId, databaseId: this.databaseId(), subjectId: this.subject().id, period: this.period() }
        : undefined;
    },
    stream: ({ databaseId, subjectId, period }) =>
      this.repository.getDatabasePeriodSummary(databaseId, { period, subjectId }),
  });
  protected readonly view = this.resource.view;
  protected readonly summary = computed(() => {
    const view = this.view();
    return view.status === 'ready' || view.status === 'partial-failure' ? view.data : null;
  });

  protected select(event: Event): void {
    this.period.set((event.target as HTMLSelectElement).value as DatabasePeriodName);
  }

  protected reload(): void {
    this.resource.reload();
  }
}
