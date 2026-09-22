import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { DatabaseRecordSource, TrackedSubjectView } from '../../../core/domain/database.model';
import { RECORD_SOURCE_LABELS } from '../database-labels';

/** 單一追蹤對象的紀錄時間軸（由新到舊），標示本次、上次與首次。 */
@Component({
  selector: 'app-records-table',
  templateUrl: './records-table.component.html',
  styleUrl: './records-table.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecordsTableComponent {
  readonly subject = input.required<TrackedSubjectView>();

  protected sourceLabel(source: DatabaseRecordSource): string {
    return RECORD_SOURCE_LABELS[source];
  }

  /** 只依排列位置標示本次／上次／首次，不涉及數值計算。 */
  protected position(index: number, total: number): string {
    if (index === 0) return '本次';
    if (index === 1) return '上次';
    if (index === total - 1) return '首次';
    return '';
  }
}
