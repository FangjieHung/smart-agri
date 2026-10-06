import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RetentionPeriodComponent } from '../retention-period/retention-period.component';

/**
 * 系統設定的「對話保存」區塊（M6 計畫第 3 節 I）：放在「對話模型」之後。
 *
 * 區塊只負責標題與子區段的排列；每個子區段是自己的元件，各自讀取、各自處理錯誤：
 * - 保存期限（`app-retention-period`，issue #243）。
 * - 各助理已保存的對話（只有管理者；issue #242）：接在保存期限之後，不另開第二個區塊。
 */
@Component({
  selector: 'app-conversation-retention',
  imports: [RetentionPeriodComponent],
  templateUrl: './conversation-retention.component.html',
  styleUrl: './conversation-retention.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConversationRetentionComponent {}
