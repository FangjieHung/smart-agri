import { ChangeDetectionStrategy, Component, computed, inject, output } from '@angular/core';
import { DAY_KEYS, openStatus, weekdayName } from '../domain/time';
import { ShopStore } from '../state/shop-store';
import { mapHref, telHref } from './format';
import { Sheet } from './sheet';

/** 店家資訊：電話、地址、每週可點餐時間與公告。 */
@Component({
  selector: 'app-info-sheet',
  imports: [Sheet],
  template: `
    @if (store.shop(); as shop) {
      <app-sheet labelledBy="info-title" [compact]="true" (dismissed)="dismissed.emit()">
        <div class="sheet__head">
          <h2 class="sheet__title" id="info-title" tabindex="-1">店家資訊</h2>
          <button type="button" class="sheet__close" aria-label="關閉" (click)="dismissed.emit()">
            ×
          </button>
        </div>
        <div class="sheet__scroll body">
          <dl class="facts">
            <dt>店名</dt>
            <dd>{{ shop.name }}</dd>
            <dt>電話</dt>
            <dd>
              <a [href]="telHref(shop.phone)">{{ shop.phone }}</a>
            </dd>
            @if (shop.address) {
              <dt>地址</dt>
              <dd>
                <a [href]="mapHref(shop.address)" target="_blank" rel="noopener noreferrer">{{
                  shop.address
                }}</a>
              </dd>
            }
            <dt>現在</dt>
            <dd>{{ status().label }}，{{ status().detail }}</dd>
          </dl>
          <h3 class="sub">線上點餐時間</h3>
          <table class="hours">
            <tbody>
              @for (day of hours(); track day.key) {
                <tr [class.today]="day.isToday">
                  <th scope="row">{{ day.name }}</th>
                  <td class="num">{{ day.text }}</td>
                </tr>
              }
            </tbody>
          </table>
          @if (shop.notices.checkout) {
            <h3 class="sub">結帳說明</h3>
            <p class="notice">{{ shop.notices.checkout }}</p>
          }
        </div>
      </app-sheet>
    }
  `,
  styles: `
    .body {
      padding: var(--space-4) var(--space-4) var(--space-5);
    }
    .facts {
      display: grid;
      grid-template-columns: max-content minmax(0, 1fr);
      gap: var(--space-2) var(--space-4);
    }
    .facts dt {
      color: var(--c-ink-2);
    }
    .facts a {
      display: inline-block;
      min-height: 1.5rem;
    }
    .sub {
      margin: var(--space-5) 0 var(--space-2);
      font-size: var(--fs-body);
    }
    .hours {
      width: 100%;
      border-collapse: collapse;
    }
    .hours th,
    .hours td {
      padding: var(--space-1) 0;
      border-bottom: 1px solid var(--c-line);
      text-align: left;
      font-weight: 400;
    }
    .hours th {
      width: 4em;
      color: var(--c-ink-2);
    }
    .today th,
    .today td {
      font-weight: 700;
      color: var(--c-ink);
    }
    .notice {
      white-space: pre-line;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class InfoSheet {
  protected readonly store = inject(ShopStore);
  readonly dismissed = output<void>();

  protected readonly telHref = telHref;
  protected readonly mapHref = mapHref;

  protected readonly status = computed(() => {
    const shop = this.store.shop();
    return shop ? openStatus(shop) : { open: false, label: '', detail: '' };
  });

  protected readonly hours = computed(() => {
    const shop = this.store.shop();
    if (!shop) return [];
    const todayIndex = (new Date(`${this.store.today()}T00:00:00Z`).getUTCDay() + 6) % 7;
    return DAY_KEYS.map((key, index) => {
      const periods = shop.orderingHours.find((day) => day.day === key)?.periods ?? [];
      return {
        key,
        name: weekdayName(key),
        isToday: index === todayIndex,
        text:
          periods.length === 0
            ? '不接單'
            : periods.map((period) => `${period.open}–${period.close}`).join('、'),
      };
    });
  });
}
