import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import {
  clockText,
  countsInTableTotal,
  elapsedText,
  itemsBrief,
  priceText,
  statusLabel,
  type OrderingTableSessionView,
} from '../../core/domain/ordering.model';

export interface TableCheckoutEvent {
  readonly session: OrderingTableSessionView;
  /** 按下的〔已結帳〕：對話框取消時把焦點還給它。 */
  readonly trigger: HTMLElement;
}

/**
 * 「點餐」頁的內用桌次（issue #402）：每一桌還沒結帳的訂單（舊的在前，含已婉拒或取消的，劃掉、不算進合計）、
 * 開桌時間與合計，以及〔已結帳〕。還有待確認的訂單時〔已結帳〕停用並說明原因（伺服器也會以 `table-has-pending` 拒絕）。
 */
@Component({
  selector: 'app-table-sessions',
  templateUrl: './table-sessions.component.html',
  styleUrl: './table-sessions.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TableSessionsComponent {
  /** `null`：還在載入。 */
  readonly sessions = input.required<readonly OrderingTableSessionView[] | null>();
  /** 伺服器的現在時間（毫秒）。 */
  readonly now = input.required<number>();
  readonly timeZone = input.required<string>();
  /** 正在結帳的桌次 id（按鈕停用）。 */
  readonly busyId = input<string | null>(null);
  readonly checkout = output<TableCheckoutEvent>();

  protected readonly priceText = priceText;
  protected readonly itemsBrief = itemsBrief;
  protected readonly statusLabel = statusLabel;
  protected readonly counts = countsInTableTotal;

  protected clock(at: string): string {
    return clockText(at, this.timeZone(), this.now());
  }

  protected elapsed(at: string): string {
    return elapsedText(at, this.now());
  }

  protected emit(session: OrderingTableSessionView, event: Event): void {
    this.checkout.emit({ session, trigger: event.currentTarget as HTMLElement });
  }
}
