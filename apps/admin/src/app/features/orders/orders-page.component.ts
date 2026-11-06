import { DOCUMENT } from '@angular/common';
import { afterNextRender, ChangeDetectionStrategy, Component, computed, DestroyRef, inject, Injector, signal } from '@angular/core';
import { catchError, forkJoin, of, type Subscription } from 'rxjs';
import {
  listOf,
  ORDERING_LIST_LABELS,
  ORDERING_LIST_VIEWS,
  shopClock,
  clockText,
  sortOrders,
  type OrderingListView,
  type OrderingOrderView,
  type OrderingSummaryView,
} from '../../core/domain/ordering.model';
import { pollWhile } from '../../core/repositories/repository-resource';
import {
  ORDERING_DENIED_MESSAGE,
  OrderingRepository,
  type OrderingActionRequest,
  type OrderingActionResult,
} from '../../core/repositories/ordering.repository';
import { ApiSessionService } from '../../core/session/api-session.service';
import { NAV_COUNTS_CHANGED_EVENT } from '../../layout/workspace-layout/nav-counts';
import { PageHeaderComponent } from '../../shared/ui/page-header/page-header.component';
import { StatePanelComponent } from '../../shared/ui/state-panel/state-panel.component';
import { OrderActionDialogComponent, type OrderDialogKind } from './order-action-dialog.component';
import { ORDER_ALERT_SOUND, rememberedSound, rememberSound } from './order-alert-sound';
import { OrderCardComponent, type OrderActionEvent } from './order-card.component';
import { ShopControlsComponent } from './shop-controls.component';

/** 每 10 秒讀一次 summary（§4.4：每個分頁每分鐘 6 次）。 */
export const ORDERING_POLL_MS = 10_000;
/** 「剩 N 分鐘」「N 分鐘前」每 30 秒重算一次。 */
const CLOCK_TICK_MS = 30_000;

type PageState = 'loading' | 'ready' | 'denied' | 'not-configured' | 'error';

/**
 * 提示音的狀態：`off` 沒開；`waiting` 記得開著、但這次還沒互動（瀏覽器還不准播）；`on` 可以播；`blocked` 瀏覽器擋下或不支援。
 */
type SoundStatus = 'off' | 'waiting' | 'on' | 'blocked';

interface OpenDialog {
  readonly kind: OrderDialogKind;
  readonly order: OrderingOrderView;
  readonly trigger: HTMLElement;
}

/**
 * 「點餐」頁（issue #399，`docs/plans/2026-11-06-online-ordering.md` §4.4）：店員處理線上訂單。
 *
 * - 每 10 秒讀 `summary`（分頁隱藏時暫停）；`latestOrderAt` 比上次新就是有新訂單：播提示音（開關開著、頁面互動過）、
 *   在 live region 報讀；`latestOrderAt`、`changedAt` 或 `shopVersion` 變了就重新讀三個清單。第一次讀到只記下、不響。
 * - 分頁標題在有待確認時加上「(N) 」；側欄徽章沿用 `NAV_COUNTS_CHANGED_EVENT`（數字變了才送出）。
 * - 按鈕只依訂單的 `actions[]` 顯示；`409` 時直接用回應裡目前的訂單取代畫面上的那一筆。
 * - 寬的畫面三欄並排，窄的畫面用三個切換鈕一次看一欄（容器查詢，側欄收合也跟著變）。
 */
@Component({
  selector: 'app-orders-page',
  imports: [PageHeaderComponent, StatePanelComponent, OrderCardComponent, OrderActionDialogComponent, ShopControlsComponent],
  templateUrl: './orders-page.component.html',
  styleUrl: './orders-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OrdersPageComponent {
  private readonly repository = inject(OrderingRepository);
  private readonly apiSession = inject(ApiSessionService);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private readonly sound = inject(ORDER_ALERT_SOUND)();

  protected readonly lists = ORDERING_LIST_VIEWS;
  protected readonly listLabels = ORDERING_LIST_LABELS;

  protected readonly state = signal<PageState>('loading');
  protected readonly stateMessage = signal('');
  protected readonly summary = signal<OrderingSummaryView | null>(null);
  protected readonly orders = signal<Readonly<Record<OrderingListView, readonly OrderingOrderView[] | null>>>({ pending: null, active: null, done: null });
  protected readonly listsFailed = signal(false);
  /** 窄的畫面目前看的那一欄。 */
  protected readonly current = signal<OrderingListView>('pending');

  protected readonly busyId = signal<string | null>(null);
  protected readonly dialog = signal<OpenDialog | null>(null);
  protected readonly dialogError = signal('');
  protected readonly notice = signal('');
  protected readonly error = signal('');
  protected readonly soundStatus = signal<SoundStatus>('off');

  /** 伺服器時間減裝置時間；「剩 N 分鐘」一律用伺服器時間。 */
  private clockOffset = 0;
  private readonly tick = signal(0);
  protected readonly now = computed(() => {
    this.tick();
    return Date.now() + this.clockOffset;
  });

  protected readonly timeZone = computed(() => this.summary()?.timeZone ?? 'Asia/Taipei');

  protected readonly counts = computed(() => {
    const orders = this.orders();
    const summary = this.summary();
    return {
      pending: orders.pending?.length ?? summary?.pendingCount ?? 0,
      active: orders.active?.length ?? summary?.activeCount ?? 0,
      done: orders.done?.length ?? 0,
    } satisfies Record<OrderingListView, number>;
  });

  /** 營業日顯示：「11/7（六）」。 */
  protected readonly businessDay = computed(() => {
    const date = this.summary()?.businessDate;
    if (!date) return '';
    const [, month, day] = date.split('-').map(Number);
    return `${month}/${day}（${'日一二三四五六'[new Date(`${date}T00:00:00Z`).getUTCDay()]}）`;
  });

  private readonly baseTitle: string;
  private poll: Subscription | null = null;
  private listsLoad: Subscription | null = null;
  private seenPending = new Set<string>();
  private dispatchedPending: number | null = null;
  private readonly unlockOnGesture = (event: Event) => {
    // 按的是提示音開關本身：交給開關（等待中按它是「關閉」）。
    if ((event.target as Element | null)?.closest?.('[data-sound-toggle]')) return;
    this.removeGestureListeners();
    void this.unlockSound(false);
  };

  constructor() {
    this.baseTitle = this.document.title.replace(/^\(\d+\) /, '');
    const destroyRef = inject(DestroyRef);
    const timer = setInterval(() => this.tick.update((value) => value + 1), CLOCK_TICK_MS);
    destroyRef.onDestroy(() => {
      clearInterval(timer);
      this.poll?.unsubscribe();
      this.listsLoad?.unsubscribe();
      this.removeGestureListeners();
      this.sound.close();
      this.document.title = this.baseTitle;
    });

    if (rememberedSound(this.document.defaultView)) {
      // 記得開著：等使用者在頁面上第一次點擊或按鍵（瀏覽器的規定）時解鎖。
      this.soundStatus.set('waiting');
      this.document.addEventListener('pointerdown', this.unlockOnGesture, true);
      this.document.addEventListener('keydown', this.unlockOnGesture, true);
    }

    if (!this.apiSession.permissions().includes('manage-orders')) {
      this.stateMessage.set(ORDERING_DENIED_MESSAGE);
      this.state.set('denied');
      return;
    }
    this.start();
  }

  /** 開始（或重試時重新開始）輪詢。 */
  protected start(): void {
    this.poll?.unsubscribe();
    this.state.set('loading');
    this.poll = pollWhile(() => this.repository.summary(), (view) => view.status === 'ready', {
      intervalMs: ORDERING_POLL_MS,
      document: this.document,
    }).subscribe({
      next: (view) => {
        if (view.status === 'ready') this.onSummary(view.data);
        else if (view.status === 'permission-denied' || view.status === 'not-configured') this.stop(view.status, view.message);
      },
      error: () => {
        if (this.summary() === null) this.state.set('error');
      },
    });
  }

  protected show(list: OrderingListView): void {
    this.current.set(list);
  }

  /** 〔開啟提示音〕是使用者手勢：在這裡解鎖音訊並試播一聲。 */
  protected toggleSound(): void {
    if (this.soundStatus() === 'on' || this.soundStatus() === 'waiting') {
      this.removeGestureListeners();
      this.soundStatus.set('off');
      rememberSound(this.document.defaultView, false);
      return;
    }
    rememberSound(this.document.defaultView, true);
    void this.unlockSound(true);
  }

  protected onAction(event: OrderActionEvent): void {
    this.notice.set('');
    this.error.set('');
    const { order, action, trigger } = event;
    if (action === 'accept' || action === 'decline' || action === 'cancel') {
      this.dialogError.set('');
      this.dialog.set({ kind: action, order, trigger });
      return;
    }
    this.run(order, { action, version: order.version }, trigger);
  }

  protected submitDialog(request: OrderingActionRequest): void {
    const open = this.dialog();
    if (open) this.run(open.order, request, open.trigger);
  }

  protected closeDialog(): void {
    const open = this.dialog();
    this.dialog.set(null);
    if (open) this.restoreFocus(open.order.id, open.trigger);
  }

  /** 店家控制改了暫停或檔位：直接用回傳的 summary（游標也一起更新，下一次輪詢不會誤以為有變化）。 */
  protected onSummaryChange(summary: OrderingSummaryView): void {
    this.summary.set(summary);
  }

  protected panelId(list: OrderingListView): string {
    return `orders-${list}`;
  }

  private onSummary(next: OrderingSummaryView): void {
    const previous = this.summary();
    this.clockOffset = Date.parse(next.now) - Date.now();
    this.tick.update((value) => value + 1);
    this.summary.set(next);
    this.state.set('ready');
    this.document.title = next.pendingCount > 0 ? `(${next.pendingCount}) ${this.baseTitle}` : this.baseTitle;
    if (this.dispatchedPending !== null && this.dispatchedPending !== next.pendingCount) {
      this.document.dispatchEvent(new Event(NAV_COUNTS_CHANGED_EVENT));
    }
    this.dispatchedPending = next.pendingCount;

    if (previous === null) {
      this.loadLists(false);
      return;
    }
    const newOrder = next.latestOrderAt !== null
      && (previous.latestOrderAt === null || Date.parse(next.latestOrderAt) > Date.parse(previous.latestOrderAt));
    if (newOrder && this.soundStatus() === 'on' && !this.sound.play()) this.soundStatus.set('blocked');
    if (newOrder || next.changedAt !== previous.changedAt || next.shopVersion !== previous.shopVersion) this.loadLists(newOrder);
  }

  /** 重新讀三個清單；新的一次會取消還沒回來的上一次（避免舊結果蓋掉動作之後的新結果）。 */
  private loadLists(announce: boolean): void {
    this.listsLoad?.unsubscribe();
    const read = (view: OrderingListView) => this.repository.orders(view).pipe(catchError(() => of(null)));
    this.listsLoad = forkJoin({ pending: read('pending'), active: read('active'), done: read('done') }).subscribe((results) => {
      const next = { ...this.orders() };
      let failed = false;
      for (const list of ORDERING_LIST_VIEWS) {
        const result = results[list];
        if (result === null) {
          failed = true;
        } else if (result.status === 'ready') {
          next[list] = sortOrders(list, result.data.orders);
        } else if (result.status === 'permission-denied' || result.status === 'not-configured') {
          this.stop(result.status, result.message);
          return;
        }
      }
      this.listsFailed.set(failed && Object.values(next).some((value) => value === null));
      this.orders.set(next);
      const pending = next.pending ?? [];
      const fresh = pending.filter((order) => !this.seenPending.has(order.id));
      pending.forEach((order) => this.seenPending.add(order.id));
      if (announce && fresh.length > 0) {
        this.notice.set(`新訂單：${fresh.map((order) => `${order.displayNumber}（${this.pickupBrief(order)}）`).join('、')}。`);
      }
    });
  }

  private run(order: OrderingOrderView, request: OrderingActionRequest, trigger: HTMLElement): void {
    if (this.busyId() !== null) return;
    this.busyId.set(order.id);
    this.dialogError.set('');
    this.repository.act(order.id, request).subscribe({
      next: (result) => this.afterAction(order, request, trigger, result),
      error: () => {
        this.busyId.set(null);
        const message = '目前無法處理這筆訂單，請稍後再試。';
        if (this.dialog()) this.dialogError.set(message);
        else this.error.set(message);
      },
    });
  }

  private afterAction(order: OrderingOrderView, request: OrderingActionRequest, trigger: HTMLElement, result: OrderingActionResult): void {
    this.busyId.set(null);
    switch (result.status) {
      case 'ready':
        this.place(result.data);
        this.notice.set(this.doneText(result.data, request));
        break;
      case 'conflict':
        this.place(result.order);
        this.notice.set(result.reason === 'order-changed'
          ? `${order.displayNumber} 這筆訂單剛被其他人處理了，畫面已更新成目前的狀態。`
          : `${order.displayNumber} 目前的狀態不能這樣處理（${result.message}），畫面已更新成目前的狀態。`);
        break;
      case 'validation-failed':
        if (this.dialog()) this.dialogError.set(result.message);
        else this.error.set(result.message);
        return;
      case 'permission-denied':
        this.error.set(result.message);
        break;
      case 'not-configured':
        this.dialog.set(null);
        this.stop('not-configured', result.message);
        return;
      default:
        return;
    }
    this.dialog.set(null);
    this.restoreFocus(order.id, trigger);
    this.loadLists(false);
    const pending = this.orders().pending?.length ?? 0;
    if (pending !== this.dispatchedPending) {
      this.dispatchedPending = pending;
      this.document.title = pending > 0 ? `(${pending}) ${this.baseTitle}` : this.baseTitle;
      this.document.dispatchEvent(new Event(NAV_COUNTS_CHANGED_EVENT));
    }
  }

  /** 把一筆訂單放進它現在所屬的清單（從原本的清單拿掉）。 */
  private place(order: OrderingOrderView): void {
    const target = listOf(order.status);
    const next = { ...this.orders() };
    for (const list of ORDERING_LIST_VIEWS) {
      const rows = (next[list] ?? []).filter((candidate) => candidate.id !== order.id);
      next[list] = list === target ? sortOrders(list, [...rows, order]) : next[list] === null ? null : rows;
    }
    this.orders.set(next);
  }

  private doneText(order: OrderingOrderView, request: OrderingActionRequest): string {
    const number = order.displayNumber;
    switch (request.action) {
      case 'accept':
        return `已接單 ${number}，預計 ${clockText(order.promisedAt ?? order.pickupAt, this.timeZone(), this.now())} 可取餐。`;
      case 'decline':
        return `已婉拒 ${number}。`;
      case 'ready':
        return `${number} 已標示為可取餐。`;
      case 'complete':
        return `${number} 已完成。`;
      case 'cancel':
        return `已取消 ${number}。`;
    }
  }

  private pickupBrief(order: OrderingOrderView): string {
    return order.estimate ? '外帶即時' : `外帶預約 ${shopClock(order.pickupAt, this.timeZone()).time}`;
  }

  /**
   * 對話框關閉或動作完成後的焦點：按鈕還在就回到按鈕；卡片搬到別欄（或按鈕不見了）就移到那張卡片；
   * 那一欄在窄的畫面沒有顯示時，移到目前這一欄的切換鈕（寬的畫面是欄標題）。
   */
  private restoreFocus(orderId: string, trigger: HTMLElement): void {
    afterNextRender(() => {
      if (trigger.isConnected && !(trigger as HTMLButtonElement).disabled) {
        trigger.focus();
        return;
      }
      const card = this.document.getElementById(`order-${orderId}`);
      card?.focus();
      if (card && this.document.activeElement === card) return;
      // 窄的畫面：目前這一欄的切換鈕；寬的畫面切換鈕隱藏，改用欄標題。
      const tab = this.document.querySelector<HTMLElement>(`[data-orders-tab="${this.current()}"]`);
      tab?.focus();
      if (tab && this.document.activeElement === tab) return;
      this.document.getElementById(`orders-${this.current()}-title`)?.focus();
    }, { injector: this.injector });
  }

  private stop(status: 'permission-denied' | 'not-configured', message: string): void {
    this.poll?.unsubscribe();
    this.listsLoad?.unsubscribe();
    this.document.title = this.baseTitle;
    this.stateMessage.set(message);
    this.state.set(status === 'permission-denied' ? 'denied' : 'not-configured');
  }

  private async unlockSound(test: boolean): Promise<void> {
    const unlocked = await this.sound.unlock();
    this.soundStatus.set(unlocked ? 'on' : 'blocked');
    if (unlocked && test) this.sound.play();
  }

  private removeGestureListeners(): void {
    this.document.removeEventListener('pointerdown', this.unlockOnGesture, true);
    this.document.removeEventListener('keydown', this.unlockOnGesture, true);
  }
}
