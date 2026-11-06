import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
  output,
} from '@angular/core';
import type { ServiceType } from '../api/types';
import { asapText, dateLabel, dayLabel, openStatus } from '../domain/time';
import { ShopStore } from '../state/shop-store';
import { Sheet } from './sheet';

/**
 * 選取餐方式與時間：外帶儘快（「儘快，約 15–30 分鐘」）或外帶預約（日期＋時段，額滿的時段照列但不能選）。
 * 只列店家開放、而且現在能用的方式。`prompt` 是一進頁面就請顧客先選時間的情況（休息中、只能預約）。
 */
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
        @if (offersBoth()) {
          <fieldset class="block">
            <legend class="block__legend">取餐方式</legend>
            <div class="services">
              <label class="service" [class.service--off]="!store.asapAvailable()">
                <input
                  type="radio"
                  name="service"
                  value="takeout-asap"
                  [checked]="type() === 'takeout-asap'"
                  [disabled]="!store.asapAvailable()"
                  (change)="setType('takeout-asap')"
                />
                <span>
                  <span class="service__name">外帶，儘快取餐</span>
                  <span class="service__hint">{{ asapHint() }}</span>
                </span>
              </label>
              <label class="service" [class.service--off]="!store.hasScheduledSlots()">
                <input
                  type="radio"
                  name="service"
                  value="takeout-scheduled"
                  [checked]="type() === 'takeout-scheduled'"
                  [disabled]="!store.hasScheduledSlots()"
                  (change)="setType('takeout-scheduled')"
                />
                <span>
                  <span class="service__name">外帶，預約取餐</span>
                  <span class="service__hint">{{
                    store.hasScheduledSlots() ? '選擇日期與時間' : '目前沒有可預約的時間'
                  }}</span>
                </span>
              </label>
            </div>
          </fieldset>
        } @else if (type() === 'takeout-asap') {
          <p class="only">外帶，{{ asapHint() }}</p>
        }

        @if (type() === 'takeout-scheduled') {
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
          確定取餐時間
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

  protected readonly offersBoth = computed(() => {
    const services = this.store.shop()?.services;
    return !!services?.takeoutAsap && !!services.takeoutScheduled;
  });

  protected readonly type = linkedSignal<ServiceType | null>(() => {
    const current = this.store.service()?.type;
    if (current === 'takeout-asap' && this.store.asapAvailable()) return current;
    if (current === 'takeout-scheduled' && this.store.hasScheduledSlots()) return current;
    if (this.store.asapAvailable()) return 'takeout-asap';
    return this.store.hasScheduledSlots() ? 'takeout-scheduled' : null;
  });

  protected readonly dates = computed(() =>
    (this.store.slots()?.dates ?? []).filter((date) => date.slots.some((slot) => !slot.isFull)),
  );

  protected readonly date = linkedSignal<string | null>(() => {
    const chosen = this.store.chosenSlot();
    return chosen?.date ?? this.dates()[0]?.date ?? null;
  });

  protected readonly chosenSlot = linkedSignal<string | null>(
    () => this.store.chosenSlot()?.slot.value ?? null,
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
    if (type === 'takeout-asap') return this.store.asapAvailable();
    if (type === 'takeout-scheduled') {
      return !!this.slotsOfDate()?.some((slot) => slot.value === this.chosenSlot() && !slot.isFull);
    }
    return false;
  });

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
        this.type() === 'takeout-scheduled'
          ? 'input[name="pickup-slot"]:not(:disabled)'
          : 'input[name="service"]:not(:disabled)';
      globalThis.document?.querySelector<HTMLInputElement>(target)?.focus();
      return;
    }
    const type = this.type();
    if (!type) return;
    this.store.chooseService(
      type === 'takeout-asap'
        ? { type, date: null, slot: null }
        : { type, date: this.date(), slot: this.chosenSlot() },
    );
    this.done.emit();
  }
}
