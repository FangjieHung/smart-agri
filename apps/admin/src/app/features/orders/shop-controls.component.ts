import { ChangeDetectionStrategy, Component, computed, DestroyRef, effect, ElementRef, inject, input, output, signal, untracked, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import type { Observable } from 'rxjs';
import {
  clockText,
  ORDERING_ASAP_BANDS,
  ORDERING_AVAILABILITY_LABELS,
  priceText,
  shopClock,
  type OrderingAsapBand,
  type OrderingItemAvailability,
  type OrderingItemView,
  type OrderingMenuView,
  type OrderingSummaryView,
} from '../../core/domain/ordering.model';
import { OrderingRepository, type OrderingWriteResult } from '../../core/repositories/ordering.repository';
import { ConfirmDialogComponent } from '../../shared/ui/confirm-dialog/confirm-dialog.component';

const AVAILABILITIES: readonly OrderingItemAvailability[] = ['available', 'sold-out-today', 'unavailable'];

/**
 * 店家控制（issue #399，D7、D9）：暫停接單（開啟前確認）、即時單準備時間四檔、品項供應（供應中／今日售完／停售）。
 * 品項清單在展開時才讀取；其他分頁改過（`shopVersion` 變了）時重新讀取。改完把新的 summary 交給頁面。
 */
@Component({
  selector: 'app-shop-controls',
  imports: [ConfirmDialogComponent],
  templateUrl: './shop-controls.component.html',
  styleUrl: './shop-controls.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ShopControlsComponent {
  private readonly repository = inject(OrderingRepository);
  private readonly destroyRef = inject(DestroyRef);
  private readonly pauseSwitch = viewChild<ElementRef<HTMLButtonElement>>('pauseSwitch');

  readonly summary = input.required<OrderingSummaryView>();
  /** 伺服器的現在時間（毫秒）。 */
  readonly now = input.required<number>();
  readonly summaryChange = output<OrderingSummaryView>();

  protected readonly bands = ORDERING_ASAP_BANDS;
  protected readonly availabilities = AVAILABILITIES;
  protected readonly availabilityLabels = ORDERING_AVAILABILITY_LABELS;
  protected readonly priceText = priceText;

  protected readonly confirmingPause = signal(false);
  protected readonly busy = signal<string | null>(null);
  protected readonly feedback = signal('');
  protected readonly error = signal('');

  protected readonly itemsOpen = signal(false);
  protected readonly menu = signal<OrderingMenuView | null>(null);
  protected readonly itemsState = signal<'idle' | 'loading' | 'ready' | 'error'>('idle');
  protected readonly query = signal('');

  /** 摘要用：幾項今日售完、幾項停售（清單讀過才知道）。 */
  protected readonly counts = computed(() => {
    const items = this.menu()?.categories.flatMap((category) => category.items) ?? [];
    return {
      soldOut: items.filter((item) => item.availability === 'sold-out-today').length,
      unavailable: items.filter((item) => item.availability === 'unavailable').length,
    };
  });

  protected readonly categories = computed(() => {
    const menu = this.menu();
    if (!menu) return [];
    const query = normalize(this.query());
    return menu.categories
      .map((category) => ({ ...category, items: category.items.filter((item) => query === '' || normalize(item.name).includes(query)) }))
      .filter((category) => category.items.length > 0);
  });

  constructor() {
    // 其他分頁（或其他店員）改了暫停、檔位或品項：品項清單展開著就重新讀取。
    let seenVersion: number | null = null;
    effect(() => {
      const version = this.summary().shopVersion;
      if (seenVersion !== null && version !== seenVersion && untracked(() => this.itemsOpen())) untracked(() => this.loadItems());
      seenVersion = version;
    });
  }

  protected togglePause(): void {
    if (this.busy()) return;
    if (this.summary().paused) this.setPaused(false);
    else this.confirmingPause.set(true);
  }

  /** 確認框關閉：焦點回到開關。 */
  protected closePauseConfirm(): void {
    this.confirmingPause.set(false);
    this.pauseSwitch()?.nativeElement.focus();
  }

  protected setPaused(paused: boolean): void {
    if (this.confirmingPause()) this.closePauseConfirm();
    this.run('pause', this.repository.setPaused(paused), (summary) => {
      this.summaryChange.emit(summary);
      this.feedback.set(paused ? '已暫停接單。顧客暫時無法送出新訂單。' : '已恢復接單。');
    });
  }

  protected setBand(band: OrderingAsapBand, event: Event): void {
    const group = (event.target as HTMLElement).closest('fieldset');
    this.run('band', this.repository.setAsapBand(band), (summary) => {
      this.summaryChange.emit(summary);
      this.feedback.set(`即時單準備時間改為 ${this.bands.find(([value]) => value === band)?.[1]}。`);
    }, () => restoreRadios(group, this.summary().asapBand));
  }

  protected toggleItems(event: Event): void {
    const open = (event.target as HTMLDetailsElement).open;
    this.itemsOpen.set(open);
    if (open && this.itemsState() !== 'loading') this.loadItems();
  }

  protected loadItems(): void {
    if (this.menu() === null) this.itemsState.set('loading');
    this.repository.items().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (view) => {
        if (view.status === 'ready') {
          this.menu.set(view.data);
          this.itemsState.set('ready');
        } else if (view.status === 'permission-denied' || view.status === 'not-configured') {
          this.itemsState.set('error');
          this.error.set(view.message);
        }
      },
      error: () => this.itemsState.set(this.menu() === null ? 'error' : 'ready'),
    });
  }

  protected setItem(item: OrderingItemView, state: OrderingItemAvailability, event: Event): void {
    const group = (event.target as HTMLElement).closest('fieldset');
    this.run(`item:${item.id}`, this.repository.setItemState(item.id, state), (updated) => {
      this.menu.update((menu) => menu && {
        ...menu,
        categories: menu.categories.map((category) => ({
          ...category,
          items: category.items.map((candidate) => (candidate.id === updated.id ? updated : candidate)),
        })),
      });
      this.feedback.set(`「${updated.name}」改為${this.availabilityLabels[updated.availability]}${updated.resetsAt ? `，${this.reopenText(updated.resetsAt)}` : ''}。`);
    }, () => restoreRadios(group, item.availability));
  }

  /** 「明天 11:00 自動恢復供應」。 */
  protected reopenText(resetsAt: string): string {
    const timeZone = this.summary().timeZone;
    const day = shopClock(resetsAt, timeZone).date;
    const today = shopClock(this.now(), timeZone).date;
    const tomorrow = shopClock(this.now() + 24 * 60 * 60_000, timeZone).date;
    const when = day === today || day === tomorrow
      ? `${day === today ? '今天' : '明天'} ${shopClock(resetsAt, timeZone).time}`
      : clockText(resetsAt, timeZone, this.now());
    return `${when} 自動恢復供應`;
  }

  /** 送出一個變更；同時間只送一個，失敗時把畫面上的選項改回原本的值。 */
  private run<T>(key: string, request: Observable<OrderingWriteResult<T>>, done: (data: T) => void, failed?: () => void): void {
    if (this.busy()) {
      failed?.();
      return;
    }
    this.busy.set(key);
    this.feedback.set('');
    this.error.set('');
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (result) => {
        this.busy.set(null);
        if (result.status === 'ready') {
          done(result.data);
          return;
        }
        failed?.();
        if (result.status === 'item-not-found') this.loadItems();
        this.error.set('message' in result && result.message ? result.message : '目前無法儲存，請稍後再試。');
      },
      error: () => {
        this.busy.set(null);
        failed?.();
        this.error.set('目前無法儲存，請稍後再試。');
      },
    });
  }
}

/** 搜尋時不分全形半形與大小寫。 */
function normalize(text: string): string {
  return text.normalize('NFKC').toLowerCase().trim();
}

/**
 * 使用者點了另一個選項、但沒有存成功：`[checked]` 綁定的值沒有變，Angular 不會改回 DOM，所以直接把勾選改回去。
 */
function restoreRadios(group: HTMLFieldSetElement | null, value: string): void {
  group?.querySelectorAll<HTMLInputElement>('input[type="radio"]').forEach((radio) => (radio.checked = radio.value === value));
}
