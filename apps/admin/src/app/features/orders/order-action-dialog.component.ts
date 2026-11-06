import { A11yModule } from '@angular/cdk/a11y';
import { afterNextRender, ChangeDetectionStrategy, Component, computed, ElementRef, input, output, signal, viewChild } from '@angular/core';
import {
  arrivalWord,
  clockText,
  ORDERING_CANCEL_REASONS,
  ORDERING_DECLINE_REASONS,
  ORDERING_NOTE_MAX_LENGTH,
  shopClock,
  shopInstant,
  staffNoteProblem,
  type OrderingCancelReason,
  type OrderingDeclineReason,
  type OrderingOrderView,
} from '../../core/domain/ordering.model';
import type { OrderingActionRequest } from '../../core/repositories/ordering.repository';

export type OrderDialogKind = 'accept' | 'decline' | 'cancel';

type AcceptChoice = 'keep' | 10 | 20 | 30 | 'custom';

const MINUTE = 60_000;
const DAY = 24 * 60 * MINUTE;

/**
 * 〔接單〕〔婉拒〕〔取消訂單〕的對話框（issue #399）。版面與行為同共用的確認對話框（遮罩、`role="dialog"`、
 * 焦點鎖定、Escape 關閉），但內容是表單、焦點先落在第一個選項，矮視窗時只有中間捲動。送出前在這裡先檢查與
 * 後端相同的規則；伺服器的 422 由頁面傳回 `error` 顯示。
 */
@Component({
  selector: 'app-order-action-dialog',
  imports: [A11yModule],
  templateUrl: './order-action-dialog.component.html',
  styleUrl: './order-action-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OrderActionDialogComponent {
  readonly kind = input.required<OrderDialogKind>();
  readonly order = input.required<OrderingOrderView>();
  /** 伺服器的現在時間（毫秒）。 */
  readonly now = input.required<number>();
  readonly timeZone = input.required<string>();
  readonly busy = input(false);
  /** 伺服器拒絕（422）或連線失敗的訊息。 */
  readonly error = input('');

  readonly submitted = output<OrderingActionRequest>();
  readonly closed = output<void>();

  protected readonly noteMax = ORDERING_NOTE_MAX_LENGTH;
  protected readonly choice = signal<AcceptChoice>('keep');
  protected readonly customTime = signal('');
  protected readonly reason = signal<string | null>(null);
  protected readonly note = signal('');
  protected readonly localError = signal('');

  private readonly dialog = viewChild.required<ElementRef<HTMLElement>>('dialog');

  constructor() {
    // 對話框一出現（已經放進 DOM 之後）就把焦點帶到第一個選項（接單是「維持原時間」）。
    afterNextRender(() => this.dialog().nativeElement.querySelector('input')?.focus());
  }

  protected readonly title = computed(() => {
    const number = this.order().displayNumber;
    return this.kind() === 'accept' ? `接單 ${number}` : this.kind() === 'decline' ? `婉拒 ${number}` : `取消訂單 ${number}`;
  });

  /** 「取餐」或「送達」（外送單，#401）。 */
  protected readonly word = computed(() => arrivalWord(this.order().serviceType));

  /** 「預計可取餐時間」或「預計送達時間」。 */
  protected readonly timeName = computed(() => (this.word() === '取餐' ? '預計可取餐時間' : '預計送達時間'));

  protected readonly detail = computed(() => {
    const order = this.order();
    if (this.kind() === 'accept') {
      const word = this.word();
      return order.estimate
        ? `顧客希望儘快${word}（約 ${order.estimate.minMinutes}–${order.estimate.maxMinutes} 分鐘）。請選${this.timeName()}。`
        : `顧客選的${word}時間是 ${this.clock(Date.parse(order.pickupAt))}。可以維持，或改成較晚的時間。`;
    }
    return this.kind() === 'decline'
      ? '請選擇婉拒的原因。顧客會在訂單頁看到原因與你寫的說明。'
      : '已接單的訂單取消後無法復原。顧客會在訂單頁看到原因與你寫的說明。';
  });

  protected readonly confirmLabel = computed(() => (this.kind() === 'accept' ? '接單' : this.kind() === 'decline' ? '婉拒' : '取消訂單'));

  /** 「現在」取整到下一分鐘：快速選項與即時單的下限。 */
  private readonly nowMinute = computed(() => Math.ceil(this.now() / MINUTE) * MINUTE);

  /** 不改時間時顧客會看到的時間：預約單是顧客選的時間；儘快單是估計上限，已經過了就是現在。 */
  private readonly keepAt = computed(() => Math.max(Date.parse(this.order().pickupAt), this.order().estimate ? this.nowMinute() : 0));

  protected readonly acceptChoices = computed(() => {
    const order = this.order();
    const keep = this.keepAt();
    const asap = order.estimate !== null;
    const quick = ([10, 20, 30] as const).map((minutes) => {
      const at = (asap ? this.nowMinute() : Date.parse(order.pickupAt)) + minutes * MINUTE;
      return { key: minutes as AcceptChoice, text: asap ? `${minutes} 分鐘後（${this.clock(at)}）` : `延後 ${minutes} 分鐘（${this.clock(at)}）` };
    });
    const keepText = asap
      ? Date.parse(order.pickupAt) > this.now() ? `${this.clock(keep)}（顧客看到的最晚時間）` : `現在（${this.clock(keep)}）`
      : `維持 ${this.clock(keep)}`;
    return [{ key: 'keep' as AcceptChoice, text: keepText }, ...quick];
  });

  /** 選到的時間（毫秒）或錯誤訊息。 */
  private readonly promised = computed<{ at: number } | { problem: string }>(() => {
    const order = this.order();
    const choice = this.choice();
    if (choice === 'keep') return { at: this.keepAt() };
    if (choice !== 'custom') return { at: (order.estimate ? this.nowMinute() : Date.parse(order.pickupAt)) + choice * MINUTE };
    const time = this.customTime();
    if (!/^\d{2}:\d{2}$/.test(time)) return { problem: '請輸入時間。' };
    const earliest = order.estimate ? this.now() - MINUTE : Date.parse(order.pickupAt);
    let at = shopInstant(shopClock(earliest, this.timeZone()).date, time, this.timeZone());
    // 比下限早半天以上，是指隔天（例如 23:50 接單、選 00:20）。
    if (at < earliest && earliest - at > DAY / 2) at += DAY;
    const what = this.word() === '取餐' ? '預計取餐時間' : '預計送達時間';
    if (at < earliest) return { problem: order.estimate ? `${what}不能早於現在。` : `${what}不能早於顧客選的時間（${this.clock(earliest)}）。` };
    if (at > Math.max(Date.parse(order.pickupAt), this.now()) + DAY) return { problem: `${what}最多延後 24 小時。` };
    return { at };
  });

  protected readonly promisedText = computed(() => {
    const promised = this.promised();
    return 'at' in promised ? `顧客會在訂單頁看到：預計 ${this.clock(promised.at)} ${this.word() === '取餐' ? '可取餐' : '送達'}。` : '';
  });

  protected readonly reasons = computed<readonly (readonly [string, string])[]>(() =>
    this.kind() === 'decline' ? ORDERING_DECLINE_REASONS : ORDERING_CANCEL_REASONS,
  );

  protected readonly noteRequired = computed(() => this.reason() === 'other');

  protected pick(choice: AcceptChoice): void {
    this.choice.set(choice);
    this.localError.set('');
    if (choice === 'custom' && this.customTime() === '') this.customTime.set(shopClock(this.keepAt(), this.timeZone()).time);
  }

  protected pickReason(reason: string): void {
    this.reason.set(reason);
    this.localError.set('');
  }

  protected close(): void {
    if (!this.busy()) this.closed.emit();
  }

  protected submit(): void {
    if (this.busy()) return;
    const order = this.order();
    if (this.kind() === 'accept') {
      const promised = this.promised();
      if ('problem' in promised) {
        this.localError.set(promised.problem);
        return;
      }
      // 不改時間時送 null，由伺服器決定（儘快單已過估計時間時是「現在」）。
      const promisedAt = this.choice() === 'keep' ? null : new Date(promised.at).toISOString();
      this.submitted.emit({ action: 'accept', version: order.version, promisedAt });
      return;
    }
    const reason = this.reason();
    if (reason === null) {
      this.localError.set('請選擇原因。');
      return;
    }
    const problem = staffNoteProblem(this.note(), reason === 'other');
    if (problem) {
      this.localError.set(problem);
      return;
    }
    const note = this.note().trim() || null;
    this.submitted.emit(
      this.kind() === 'decline'
        ? { action: 'decline', version: order.version, reason: reason as OrderingDeclineReason, note }
        : { action: 'cancel', version: order.version, reason: reason as OrderingCancelReason, note },
    );
  }

  private clock(at: number): string {
    return clockText(at, this.timeZone(), this.now());
  }
}
