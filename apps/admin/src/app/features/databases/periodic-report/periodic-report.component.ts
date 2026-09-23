import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { PeriodicReportView } from '../../../core/domain/database.model';

/**
 * 助理規則「定期回報」開啟時的回報面板。
 * 排程與摘要都由 repository 算好，這裡只負責顯示，不自行推導任何數字。
 */
@Component({
  selector: 'app-periodic-report',
  templateUrl: './periodic-report.component.html',
  styleUrl: './periodic-report.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PeriodicReportComponent {
  readonly report = input.required<PeriodicReportView>();
}
