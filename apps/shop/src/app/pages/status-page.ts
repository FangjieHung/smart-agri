import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  DOCUMENT,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { OrderingApi } from '../api/ordering-api';
import type { ChosenOption, Order } from '../api/types';
import { isFinal, recentOrders, tokenFromHash, updateRecentStatus } from '../domain/orders';
import { progressIndex, PROGRESS_STEPS, reasonText, statusWords } from '../domain/status';
import { addMinutes, pickupText, wallClock } from '../domain/time';
import { Announcer } from '../state/announcer';
import { Nav } from '../state/nav';
import { ShopStore } from '../state/shop-store';
import { ntd, telHref } from '../ui/format';
import { PageHeader } from '../ui/page-header';
import { Sheet } from '../ui/sheet';

/** 狀態頁每隔多久重新查一次（設計文件 §4.2：每 10 秒，最終狀態後停止）。 */
export const POLL_MS = 10_000;

/**
 * 訂單狀態頁 `/order/{slug}/o#<token>`：token 只從網址 `#` 後面讀，查詢時只放在 `X-Ordering-Token` 標頭。
 * 每 10 秒更新一次（頁面在背景時暫停），進入最終狀態後停止；店家還沒確認前可以取消（先確認）。
 */
@Component({
  selector: 'app-status-page',
  imports: [PageHeader, Sheet],
  templateUrl: './status-page.html',
  styleUrl: './status-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class StatusPage {
  protected readonly store = inject(ShopStore);
  protected readonly nav = inject(Nav);
  private readonly api = inject(OrderingApi);
  private readonly announcer = inject(Announcer);
  private readonly document = inject(DOCUMENT);

  protected readonly ntd = ntd;
  protected readonly telHref = telHref;
  protected readonly steps = PROGRESS_STEPS;

  protected readonly token = computed(() => tokenFromHash(this.nav.hash()));
  protected readonly order = signal<Order | null>(null);
  protected readonly state = signal<'loading' | 'ready' | 'not-found' | 'error'>('loading');
  protected readonly confirmCancel = signal(false);
  protected readonly cancelling = signal(false);
  protected readonly cancelError = signal<string | null>(null);
  protected readonly recent = computed(() => recentOrders(this.store.slug()));

  protected readonly words = computed(() => {
    const order = this.order();
    return order?.status ? statusWords(order.status, order.cancelledBy ?? null) : null;
  });

  /** 店家婉拒或取消的原因（#399）。 */
  protected readonly reason = computed(() => {
    const order = this.order();
    return order ? reasonText(order) : null;
  });

  protected readonly progress = computed(() => {
    const status = this.order()?.status;
    return status ? progressIndex(status) : null;
  });

  protected readonly pickup = computed(() => {
    const order = this.order();
    const shop = this.store.shop();
    if (!order || !shop) return '';
    // 接單時店家給的時間（#399）：儘快取餐是預計好的時間，預約取餐可能比顧客選的晚。
    const promisedAt = order.promisedAt ?? null;
    if (promisedAt) {
      const promised = pickupText(promisedAt, shop.timeZone, this.store.now());
      if (order.estimate) return `預計 ${promised} 可取餐（儘快取餐）`;
      return Date.parse(promisedAt) === Date.parse(order.pickupAt)
        ? promised
        : `${promised}（店家調整了取餐時間）`;
    }
    if (order.estimate) {
      const from = wallClock(
        addMinutes(order.createdAt, order.estimate.minMinutes),
        shop.timeZone,
      ).time;
      const to = wallClock(order.pickupAt, shop.timeZone).time;
      return `預計 ${from}–${to} 可取餐（儘快取餐）`;
    }
    return pickupText(order.pickupAt, shop.timeZone, this.store.now());
  });

  private timer: ReturnType<typeof setTimeout> | undefined;
  private destroyed = false;

  constructor() {
    const destroyRef = inject(DestroyRef);
    const onVisibility = () => {
      if (this.document.visibilityState === 'visible' && this.shouldPoll()) void this.refresh();
    };
    this.document.addEventListener('visibilitychange', onVisibility);
    destroyRef.onDestroy(() => {
      this.destroyed = true;
      clearTimeout(this.timer);
      this.document.removeEventListener('visibilitychange', onVisibility);
    });

    effect(() => {
      const token = this.token();
      untracked(() => {
        clearTimeout(this.timer);
        this.order.set(null);
        if (!token) {
          this.state.set('not-found');
          return;
        }
        this.state.set('loading');
        void this.refresh();
      });
    });
  }

  private shouldPoll(): boolean {
    const order = this.order();
    return (
      !this.destroyed && !!this.token() && (order === null || !isFinal(order.status ?? 'pending'))
    );
  }

  private async refresh(): Promise<void> {
    const token = this.token();
    if (!token) return;
    clearTimeout(this.timer);
    const result = await this.api.order(this.store.slug(), token);
    if (this.destroyed || token !== this.token()) return;
    if (result.ok) {
      this.apply(result.data, token);
    } else if ('problem' in result && result.problem.reason === 'order-not-found') {
      this.state.set('not-found');
      return;
    } else if (!this.order()) {
      this.state.set('error');
    }
    this.schedule();
  }

  private schedule(): void {
    clearTimeout(this.timer);
    if (!this.shouldPoll()) return;
    this.timer = setTimeout(() => {
      if (this.document.visibilityState === 'hidden') return;
      void this.refresh();
    }, POLL_MS);
  }

  private apply(order: Order, token: string): void {
    const previous = this.order()?.status;
    this.order.set(order);
    this.state.set('ready');
    updateRecentStatus(this.store.slug(), token, order);
    if (previous && previous !== order.status && order.status) {
      this.announcer.announce(
        `訂單 ${order.displayNumber}：${statusWords(order.status, order.cancelledBy ?? null).title}`,
      );
    }
  }

  protected retry(): void {
    this.state.set('loading');
    void this.refresh();
  }

  protected async cancel(): Promise<void> {
    const token = this.token();
    if (!token || this.cancelling()) return;
    this.cancelling.set(true);
    this.cancelError.set(null);
    const result = await this.api.cancel(this.store.slug(), token);
    this.cancelling.set(false);
    this.confirmCancel.set(false);
    if (result.ok) {
      this.apply(result.data, token);
      this.announcer.announce(`訂單 ${result.data.displayNumber} 已取消`);
      clearTimeout(this.timer);
      return;
    }
    if ('problem' in result && result.problem.reason === 'not-cancellable') {
      this.cancelError.set('店家已經處理這筆訂單，無法取消；如需更改請直接來電店家。');
    } else if ('problem' in result && result.problem.reason === 'rate-limited') {
      this.cancelError.set('操作太頻繁了，請稍等一下再試。');
    } else {
      this.cancelError.set('取消沒有成功，請確認網路連線後再試一次，或來電店家。');
    }
    this.announcer.announce(this.cancelError() ?? '');
    void this.refresh();
  }

  /** 明細裡一行的選項說明。 */
  protected optionsText(options: readonly ChosenOption[]): string {
    return options
      .map((option) => {
        const inner = this.optionsText(option.options);
        return `${option.optionName}${option.count > 1 ? ` ×${option.count}` : ''}${inner ? `（${inner}）` : ''}`;
      })
      .join('、');
  }

  protected statusOf(entry: { status: Order['status'] }): string {
    return entry.status ? statusWords(entry.status).title : '';
  }
}
