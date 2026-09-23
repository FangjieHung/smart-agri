import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';

/**
 * 「對話與回報紀錄」入口頁。Demo 階段只說明各類紀錄分別保存在哪裡，
 * 不顯示跨助理的合併清單（對話屬於發起的帳號，不會在這裡揭露內容）。
 */
@Component({
  selector: 'app-dashboard-page',
  imports: [RouterLink, PageHeaderComponent],
  templateUrl: './dashboard-page.component.html',
  styleUrl: './dashboard-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DashboardPageComponent {}
