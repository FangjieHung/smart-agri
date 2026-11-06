import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
  output,
} from '@angular/core';
import type { ServiceType, Zone } from '../api/types';
import { isDelivery } from '../domain/cart';
import { asapText, dateLabel, dayLabel, openStatus } from '../domain/time';
import { ntd } from './format';
import { ShopStore } from '../state/shop-store';
import { Sheet } from './sheet';

/**
 * 選取餐方式與時間：外帶儘快（「儘快，約 15–30 分鐘」）、外帶預約（日期＋時段，額滿的時段照列但不能選），
 * 以及店家有開的外送（#401）：先選外送區域（運費與最低消費），再選時間。只列店家開放的方式，現在不能用的
 * 照列但不能選。`prompt` 是一進頁面就請顧客先選時間的情況（休息中、只能預約）。
 */
interface ServiceOption {
  readonly type: ServiceType;
  readonly name: string;
  readonly hint: string;
  readonly available: boolean;
}

const NEAR_DAYS = ['今天', '明天', '後天'];

@Component({
  selector: 'app-time-sheet',
  imports: [Sheet],
  template: `
    <app-sheet labelledBy="time-title" (dismissed)="dismissed.emit()">
      <div class="sheet__head">
        <h2 class="sheet__title" id="time-title" tabindex="-1">{{ title() }}</h2>
        <button type="button" class="sheet__close" aria-label="關閉" (click)="dismissed.emit()">
          ×
        </button>
      </div>
      <div class="sheet__scroll body">
        @if (intro(); as text) {
          <p class="intro">{{ text }}</p>
        }
        @if (message()) {
          <p class="callout callout--danger" role="alert">{{ message() }}</p>
        }
        @if (options().length > 1) {
          <fieldset class="block">
            <legend class="block__legend">取餐方式</legend>
            <div class="services">
              @for (option of options(); track option.type) {
                <label class="service" [class.service--off]="!option.available">
                  <input
                    type="radio"
                    name="service"
                    [value]="option.type"
                    [checked]="type() === option.type"
                    [disabled]="!option.available"
                    (change)="setType(option.type)"
                  />
                  <span>
                    <span class="service__name">{{ option.name }}</span>
                    <span class="service__hint">{{ option.hint }}</span>
                  </span>
                </label>
              }
            </div>
          </fieldset>
        } @else if (options()[0]; as only) {
          @if (only.type === 'takeout-asap' || only.type === 'delivery-asap') {
            <p class="only">{{ only.name }}，{{ only.hint }}</p>
          }
        }

        @if (delivery()) {
          <fieldset class="block" data-testid="zones">
            <legend class="block__legend">外送區域</legend>
            @if (store.shop()?.delivery?.note; as note) {
              <p class="intro zone-note">{{ note }}</p>
            }
            <div class="services">
              @for (z of store.zones(); track z.id) {
                <label class="service">
                  <input
                    type="radio"
                    name="zone"
                    [value]="z.id"
                    [checked]="zone() === z.id"
                    (change)="zone.set(z.id)"
                  />
                  <span>
                    <span class="service__name">{{ z.name }}</span>
                    <span class="service__hint">{{ zoneTerms(z) }}</span>
                  </span>
                </label>
              }
            </div>
          </fieldset>
        }

        @if (scheduled()) {
          <fieldset class="block">
            <legend class="block__legend">日期</legend>
            <div class="chips chips--dates">
              @for (day of dates(); track day.date) {
                <label class="chip">
                  <input
                    type="radio"
                    name="pickup-date"
                    [value]="day.date"
                    [checked]="date() === day.date"
                    (change)="pickDate(day.date)"
                  />
                  <span class="chip__face">
                    <span class="chip__main">{{ dayWord(day.date) }}</span>
                    <span class="chip__sub">{{ daySub(day.date) }}</span>
                  </span>
                </label>
              }
            </div>
          </fieldset>
          @if (slotsOfDate(); as slots) {
            <fieldset class="block">
              <legend class="block__legend">時間</legend>
              <div class="chips chips--times">
                @for (slot of slots; track slot.value) {
                  <label class="chip" [class.chip--full]="slot.isFull">
                    <input
                      type="radio"
                      name="pickup-slot"
                      [value]="slot.value"
                      [checked]="chosenSlot() === slot.value"
                      [disabled]="slot.isFull"
                      (change)="chosenSlot.set(slot.value)"
                    />
                    <span class="chip__face">
                      <span class="chip__main num">{{ slot.time }}</span>
                      @if (slot.isFull) {
                        <span class="chip__sub">已滿</span>
                      } @else if (slot.isNextDay) {
                        <span class="chip__sub">隔天凌晨</span>
                      }
                    </span>
                  </label>
                }
              </div>
            </fieldset>
          }
        }
      </div>
      <div class="sheet__foot">
        <button
          type="button"
          class="btn btn--primary btn--block"
          data-testid="confirm-time"
          [attr.aria-disabled]="!valid()"
          (click)="confirm()"
        >
          {{ delivery() ? '確定外送區域與時間' : '確定取餐時間' }}
        </button>
      </div>
    </app-sheet>
  `,
  styles: `
    .body {
      padding: var(--space-4);
      display: grid;
      align-content: start;
      gap: var(--space-4);
    }
    .intro {
      color: var(--c-ink-2);
    }
    .block {
      margin: 0;
      padding: 0;
      border: 0;
      min-width: 0;
    }
    .block__legend {
      padding: 0;
      margin-bottom: var(--space-2);
      font-weight: 700;
    }
    .services {
      display: grid;
      gap: var(--space-2);
    }
    .service {
      display: flex;
      gap: var(--space-3);
      align-items: flex-start;
      padding: var(--space-3);
      border: 1px solid var(--c-line-strong);
      border-radius: var(--radius-m);
      cursor: pointer;
    }
    .service:has(input:checked) {
      border-color: var(--c-accent);
      background: var(--c-accent-wash);
      box-shadow: 0 0 0 1px var(--c-accent);
    }
    .service--off {
      cursor: not-allowed;
      color: var(--c-ink-3);
      border-style: dashed;
    }
    .service input {
      width: 1.25rem;
      height: 1.25rem;
      margin: 0.15rem 0 0;
      accent-color: var(--c-accent);
    }
    .service__name {
      display: block;
      font-weight: 600;
    }
    .service__hint {
      display: block;
      font-size: var(--fs-small);
      color: var(--c-ink-2);
    }
    .only {
      font-weight: 600;
    }
    .chips {
      display: grid;
      gap: var(--space-2);
    }
    .chips--dates {
      grid-template-columns: repeat(auto-fill, minmax(6.5rem, 1fr));
    }
    .chips--times {
      grid-template-columns: repeat(auto-fill, minmax(5.25rem, 1fr));
    }
    .chip {
      position: relative;
      display: block;
      cursor: pointer;
    }
    .chip input {
      position: absolute;
      inset: 0;
      opacity: 0;
      margin: 0;
      cursor: inherit;
    }
    .chip__face {
      display: grid;
      place-items: center;
      min-height: 3.25rem;
      padding: var(--space-1) var(--space-2);
      border: 1px solid var(--c-line-strong);
      border-radius: var(--radius-s);
      text-align: center;
      line-height: 1.3;
    }
    .chip__main {
      font-weight: 600;
    }
    .chip__sub {
      font-size: var(--fs-small);
      color: var(--c-ink-2);
    }
    .chip input:checked + .chip__face {
      border-color: var(--c-accent);
      background: var(--c-accent);
      color: #fff;
    }
    .chip input:checked + .chip__face .chip__sub {
      color: #fff;
    }
    .chip input:focus-visible + .chip__face {
      outline: 3px solid var(--c-focus);
      outline-offset: 2px;
    }
    .chip--full {
      cursor: not-allowed;
    }
    .chip--full .chip__face {
      border-style: dashed;
      background: var(--c-paper);
      color: var(--c-ink-3);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TimeSheet {
  protected readonly store = inject(ShopStore);

  /** 一進頁面就請顧客先選時間。 */
  readonly prompt = input(false);
  /** 送出時伺服器說時段不能用時的說明。 */
  readonly message = input<string | null>(null);
  readonly done = output<void>();
  readonly dismissed = output<void>();

  /** 店家開放的方式，依外帶儘快、外帶預約、外送儘快、外送預約排列。 */
  protected readonly options = computed<ServiceOption[]>(() => {
    const shop = this.store.shop();
    const services = shop?.services;
    if (!shop || !services) return [];
    const list: ServiceOption[] = [];
    if (services.takeoutAsap)
      list.push({
        type: 'takeout-asap',
        name: '外帶，儘快取餐',
        hint: this.asapHint(),
        available: this.store.asapAvailable(),
      });
    if (services.takeoutScheduled)
      list.push({
        type: 'takeout-scheduled',
        name: '外帶，預約取餐',
        hint: this.store.hasScheduledSlots() ? '選擇日期與時間' : '目前沒有可預約的時間',
        available: this.store.hasScheduledSlots(),
      });
    if (services.deliveryAsap && shop.delivery)
      list.push({
        type: 'delivery-asap',
        name: '外送（儘快）',
        hint:
          this.store.deliveryAsapAvailable() && shop.delivery.asap
            ? asapText(shop.delivery.asap)
            : '目前無法儘快外送',
        available: this.store.deliveryAsapAvailable(),
      });
    if (services.delivery && shop.delivery)
      list.push({
        type: 'delivery-scheduled',
        name: '外送（預約）',
        hint: this.store.hasDeliverySlots() ? '選擇外送區域與時間' : '目前沒有可預約的外送時間',
        available: this.store.hasDeliverySlots(),
      });
    return list;
  });

  protected readonly type = linkedSignal<ServiceType | null>(() => {
    const current = this.store.service()?.type;
    const available = this.options().filter((option) => option.available);
    if (current && available.some((option) => option.type === current)) return current;
    return available[0]?.type ?? null;
  });

  protected readonly delivery = computed(() => isDelivery(this.type()));

  protected readonly scheduled = computed(
    () => this.type() === 'takeout-scheduled' || this.type() === 'delivery-scheduled',
  );

  /** 選的外送區域（換方式時保留）。 */
  protected readonly zone = linkedSignal<string | null>(() => {
    const id = this.store.service()?.zoneId ?? null;
    return this.store.zones().some((zone) => zone.id === id) ? id : null;
  });

  protected readonly dates = computed(() =>
    this.store.datesFor(this.type()).filter((date) => date.slots.some((slot) => !slot.isFull)),
  );

  /** 這個方式原本選的時段（換到另一種方式時不沿用）。 */
  private readonly previous = computed(() =>
    this.store.service()?.type === this.type() ? this.store.chosenSlot() : null,
  );

  protected readonly date = linkedSignal<string | null>(() => {
    const chosen = this.previous();
    return chosen && this.dates().some((date) => date.date === chosen.date)
      ? chosen.date
      : (this.dates()[0]?.date ?? null);
  });

  protected readonly chosenSlot = linkedSignal<string | null>(
    () => this.previous()?.slot.value ?? null,
  );

  protected readonly slotsOfDate = computed(
    () => this.dates().find((date) => date.date === this.date())?.slots ?? null,
  );

  protected readonly title = computed(() => {
    const shop = this.store.shop();
    if (this.prompt() && shop && !shop.open.isOpen) return '休息中，歡迎預訂';
    return '取餐方式與時間';
  });

  protected readonly intro = computed(() => {
    const shop = this.store.shop();
    if (!this.prompt() || !shop) return null;
    const status = openStatus(shop);
    return status.open
      ? '現在無法儘快取餐，請先選擇預約的取餐時間，再開始點餐。'
      : `${status.detail}。現在可以先預訂，請選擇取餐時間。`;
  });

  protected readonly asapHint = computed(() => {
    const asap = this.store.shop()?.asap;
    return this.store.asapAvailable() && asap ? asapText(asap) : '目前無法儘快取餐';
  });

  protected readonly valid = computed(() => {
    const type = this.type();
    if (this.delivery() && !this.zone()) return false;
    if (type === 'takeout-asap') return this.store.asapAvailable();
    if (type === 'delivery-asap') return this.store.deliveryAsapAvailable();
    if (this.scheduled()) {
      return !!this.slotsOfDate()?.some((slot) => slot.value === this.chosenSlot() && !slot.isFull);
    }
    return false;
  });

  /** 外送區域的條件：「運費 NT$30・滿 NT$200 外送」。 */
  protected zoneTerms(zone: Zone): string {
    const fee = zone.fee > 0 ? `運費 ${ntd(zone.fee)}` : '免運費';
    const minimum =
      zone.minimumSubtotal > 0 ? `滿 ${ntd(zone.minimumSubtotal)} 外送` : '不限金額';
    return `${fee}・${minimum}`;
  }

  /** 日期按鈕的大字：今天、明天、後天，再後面寫星期。 */
  protected dayWord(date: string): string {
    const label = dayLabel(date, this.store.today());
    return NEAR_DAYS.includes(label)
      ? label
      : `週${'日一二三四五六'[new Date(`${date}T00:00:00Z`).getUTCDay()]}`;
  }

  /** 日期按鈕的小字：近三天寫「11/6（五）」，再後面只寫「11/10」（星期已經是大字）。 */
  protected daySub(date: string): string {
    const label = dateLabel(date);
    return NEAR_DAYS.includes(dayLabel(date, this.store.today()))
      ? label
      : label.replace(/（.）$/, '');
  }

  protected setType(type: ServiceType): void {
    this.type.set(type);
  }

  protected pickDate(date: string): void {
    this.date.set(date);
    if (!this.slotsOfDate()?.some((slot) => slot.value === this.chosenSlot()))
      this.chosenSlot.set(null);
  }

  protected confirm(): void {
    if (!this.valid()) {
      const target =
        this.delivery() && !this.zone()
          ? 'input[name="zone"]'
          : this.scheduled()
            ? 'input[name="pickup-slot"]:not(:disabled)'
            : 'input[name="service"]:not(:disabled)';
      globalThis.document?.querySelector<HTMLInputElement>(target)?.focus();
      return;
    }
    const type = this.type();
    if (!type) return;
    const zoneId = this.delivery() ? this.zone() : null;
    this.store.chooseService(
      this.scheduled()
        ? { type, date: this.date(), slot: this.chosenSlot(), zoneId }
        : { type, date: null, slot: null, zoneId },
    );
    this.done.emit();
  }
}
