import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { MetricComparisonView, TrackedSubjectView } from '../../../core/domain/database.model';
import { TrendChartComponent, type TrendChartPoint } from '../../../shared/ui/trend-chart/trend-chart.component';

/** 本次／上次／首次比較與趨勢圖；所有差異值與摘要都由 repository 預先算好。 */
@Component({
  selector: 'app-trend-view',
  imports: [TrendChartComponent],
  templateUrl: './trend-view.component.html',
  styleUrl: './trend-view.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TrendViewComponent {
  readonly subject = input.required<TrackedSubjectView>();

  protected readonly comparison = computed(() => this.subject().comparison);

  protected chartPoints(metric: MetricComparisonView): readonly TrendChartPoint[] {
    return metric.points.map((point) => ({ label: point.dateLabel, value: point.value, display: point.display }));
  }
}
