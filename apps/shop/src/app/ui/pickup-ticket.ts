import { ChangeDetectionStrategy, Component, inject, output } from '@angular/core';
import { ShopStore } from '../state/shop-store';

/**
 * 取餐單：一直顯示目前選的取餐方式與時間（外送另外寫送到哪一區，#401），旁邊是「更改」。掃桌號 QR code 進來
 * （內用，#402）時改成「內用・桌號 A1」，沒有時間可選、也沒有「更改」。版面像店裡的取餐聯，左邊直排「取餐」，
 * 虛線撕口隔開——這是整個點餐網頁唯一的裝飾，其他地方保持安靜。
 */
@Component({
  selector: 'app-pickup-ticket',
  template: `
    @if (store.isTable()) {
      <section class="ticket" aria-labelledby="ticket-label">
        <span class="ticket__stub" id="ticket-label" lang="zh-Hant-TW">內用</span>
        <div class="ticket__body">
          <span class="ticket__kind">掃碼點餐，不必選時間</span>
          <span class="ticket__when" data-testid="table-name">{{
            store.table() ? '內用・桌號 ' + store.table()?.name : '內用'
          }}</span>
        </div>
      </section>
    } @else {
      <section
        class="ticket"
        [class.ticket--empty]="!store.serviceLabel()"
        aria-labelledby="ticket-label"
      >
        <span class="ticket__stub" id="ticket-label" lang="zh-Hant-TW">{{
          store.isDelivery() ? '外送' : '取餐'
        }}</span>
        <div class="ticket__body" aria-live="polite">
          @if (store.serviceLabel(); as label) {
            <span class="ticket__kind">{{ label.kind }}</span>
            <span class="ticket__when" data-testid="pickup-when">{{ label.when }}</span>
            @if (label.zone) {
              <span class="ticket__kind" data-testid="pickup-zone">送到 {{ label.zone }}</span>
            }
          } @else {
            <span class="ticket__kind">還沒選取餐時間</span>
            <span class="ticket__when ticket__when--empty">選好時間才能送出訂單</span>
          }
        </div>
        <button
          type="button"
          class="btn ticket__action"
          [class.btn--primary]="!store.serviceLabel()"
          (click)="edit.emit()"
        >
          {{ store.serviceLabel() ? '更改' : '選擇時間'
          }}<span class="visually-hidden">{{
            store.isDelivery() ? '外送區域與時間' : '取餐時間'
          }}</span>
        </button>
      </section>
    }
  `,
  styles: `
    .ticket {
      --notch: 0.6rem;
      display: grid;
      grid-template-columns: 2.75rem minmax(0, 1fr) auto;
      align-items: center;
      gap: var(--space-3);
      padding-right: var(--space-3);
      border: 1px solid var(--c-line-strong);
      border-radius: var(--radius-m);
      background:
        radial-gradient(
            circle at 2.75rem 0,
            var(--c-surface) var(--notch),
            transparent calc(var(--notch) + 0.5px)
          )
          top / 100% 50% no-repeat,
        radial-gradient(
            circle at 2.75rem 100%,
            var(--c-surface) var(--notch),
            transparent calc(var(--notch) + 0.5px)
          )
          bottom / 100% 50% no-repeat,
        var(--c-accent-wash);
    }
    .ticket--empty {
      background:
        radial-gradient(
            circle at 2.75rem 0,
            var(--c-surface) var(--notch),
            transparent calc(var(--notch) + 0.5px)
          )
          top / 100% 50% no-repeat,
        radial-gradient(
            circle at 2.75rem 100%,
            var(--c-surface) var(--notch),
            transparent calc(var(--notch) + 0.5px)
          )
          bottom / 100% 50% no-repeat,
        var(--c-notice);
      border-color: var(--c-notice-line);
    }
    .ticket__stub {
      align-self: stretch;
      display: grid;
      place-items: center;
      writing-mode: vertical-rl;
      font-weight: 700;
      font-size: var(--fs-small);
      color: var(--c-accent-ink);
      border-right: 2px dashed var(--c-line-strong);
      padding-block: var(--space-3);
    }
    .ticket--empty .ticket__stub {
      color: var(--c-notice-ink);
    }
    .ticket__body {
      display: grid;
      padding-block: var(--space-3);
      min-width: 0;
    }
    .ticket__kind {
      font-size: var(--fs-small);
      color: var(--c-ink-2);
    }
    .ticket__when {
      font-size: var(--fs-lead);
      font-weight: 700;
      font-variant-numeric: tabular-nums;
    }
    .ticket__when--empty {
      font-size: var(--fs-body);
      color: var(--c-notice-ink);
    }
    .ticket__action {
      flex: none;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PickupTicket {
  protected readonly store = inject(ShopStore);
  readonly edit = output<void>();
}
