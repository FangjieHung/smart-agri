import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import {
  actionLabel,
  cancelReasonLabel,
  clockText,
  declineReasonLabel,
  elapsedText,
  optionsText,
  ORDERING_SERVICE_LABELS,
  priceText,
  remainingText,
  statusLabel,
  type OrderingOrderAction,
  type OrderingOrderView,
} from '../../core/domain/ordering.model';

/** 待確認的訂單離自動取消不到這麼久時醒目顯示（#400）。 */
export const EXPIRY_WARNING_MS = 5 * 60_000;

export interface OrderActionEvent {
  readonly order: OrderingOrderView;
  readonly action: OrderingOrderAction;
  /** 按下的按鈕：對話框關閉後把焦點還給它（卡片已搬到別欄時改由頁面決定）。 */
  readonly trigger: HTMLElement;
}

/**
 * 一筆訂單（issue #399）：號碼、取餐方式與時間（以伺服器的現在時間算「剩 N 分鐘」）、顧客（電話可撥打）、明細與備註、
 * 總額、狀態、送出多久了；按鈕只依訂單的 `actions[]` 顯示。待確認的訂單另外顯示「HH:mm 前未確認將自動取消」（#400），
 * 個資已清除的舊訂單（取餐 90 天後）姓名是「（已刪除）」，沒有電話可撥。
 */
@Component({
  selector: 'app-order-card',
  templateUrl: './order-card.component.html',
  styleUrl: './order-card.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OrderCardComponent {
  readonly order = input.required<OrderingOrderView>();
  /** 伺服器的現在時間（毫秒）。 */
  readonly now = input.required<number>();
  readonly timeZone = input.required<string>();
  readonly busy = input(false);
  readonly act = output<OrderActionEvent>();

  protected readonly optionsText = optionsText;
  protected readonly priceText = priceText;

  protected readonly service = computed(() => ORDERING_SERVICE_LABELS[this.order().serviceType] ?? this.order().serviceType);
  protected readonly status = computed(() => statusLabel(this.order()));

  /** 取餐時間的說明。 */
  protected readonly when = computed(() => {
    const order = this.order();
    const clock = (at: string) => clockText(at, this.timeZone(), this.now());
    if (order.status === 'pending') {
      return order.estimate
        ? `儘快，約 ${order.estimate.minMinutes}–${order.estimate.maxMinutes} 分鐘（最晚 ${clock(order.pickupAt)}）`
        : `${clock(order.pickupAt)} 取餐`;
    }
    if (order.status === 'accepted' || order.status === 'ready') {
      const promised = order.promisedAt ?? order.pickupAt;
      const changed = !order.estimate && order.promisedAt !== null && Date.parse(order.promisedAt) !== Date.parse(order.pickupAt);
      return `預計 ${clock(promised)} 可取餐${changed ? `（顧客選 ${clock(order.pickupAt)}）` : ''}`;
    }
    return `${order.estimate ? '儘快取餐' : `${clock(order.pickupAt)} 取餐`}`;
  });

  /** 還在處理的訂單才顯示「剩 N 分鐘」。 */
  protected readonly remaining = computed(() => {
    const order = this.order();
    if (order.status !== 'pending' && order.status !== 'accepted' && order.status !== 'ready') return null;
    const target = order.status === 'pending' ? order.pickupAt : order.promisedAt ?? order.pickupAt;
    return { text: remainingText(target, this.now()), late: Date.parse(target) < this.now() };
  });

  protected readonly submitted = computed(() => elapsedText(this.order().createdAt, this.now()));

  /** 待確認的訂單什麼時候會自動取消（#400）；剩不到 5 分鐘時醒目顯示。 */
  protected readonly expiry = computed(() => {
    const order = this.order();
    if (order.status !== 'pending' || !order.expiresAt) return null;
    return {
      text: `${clockText(order.expiresAt, this.timeZone(), this.now())} 前未確認將自動取消`,
      soon: Date.parse(order.expiresAt) - this.now() < EXPIRY_WARNING_MS,
    };
  });

  /** 結束的訂單：什麼時候、誰、為什麼。 */
  protected readonly outcome = computed(() => {
    const order = this.order();
    const at = (iso: string) => clockText(iso, this.timeZone(), this.now());
    const who = (member: { displayName: string } | null) => (member ? `，${member.displayName}` : '');
    if (order.status === 'completed' && order.completed) return `${at(order.completed.at)} 完成${who(order.completed.by)}`;
    if (order.status === 'declined' && order.declined) {
      return `${at(order.declined.at)} 婉拒${who(order.declined.by)}：${declineReasonLabel(order.declined.reason)}`;
    }
    if (order.status === 'cancelled' && order.cancelled) {
      return order.cancelled.by === 'shop'
        ? `${at(order.cancelled.at)} 店家取消${who(order.cancelled.member)}：${cancelReasonLabel(order.cancelled.reason)}`
        : `${at(order.cancelled.at)} 顧客取消`;
    }
    if (order.status === 'expired') return `${at(order.statusChangedAt)} 逾時沒有接單，系統已自動婉拒`;
    if (order.accepted) {
      const ready = order.ready ? `；${at(order.ready.at)} 可取餐${who(order.ready.by)}` : '';
      return `${at(order.accepted.at)} 接單${who(order.accepted.by)}${ready}`;
    }
    return null;
  });

  /** 婉拒或店家取消時給顧客的說明。 */
  protected readonly note = computed(() => {
    const order = this.order();
    return order.status === 'declined' ? order.declined?.note ?? null : order.status === 'cancelled' ? order.cancelled?.note ?? null : null;
  });

  protected label(action: OrderingOrderAction): string {
    return actionLabel(action, this.order().serviceType);
  }

  protected emit(action: OrderingOrderAction, event: Event): void {
    this.act.emit({ order: this.order(), action, trigger: event.currentTarget as HTMLElement });
  }
}
