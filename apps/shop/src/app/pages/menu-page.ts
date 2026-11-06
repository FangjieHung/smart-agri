import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  DOCUMENT,
  inject,
  signal,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import type { MenuCategory, MenuItem, TableOrder } from '../api/types';
import { activeOrder } from '../domain/orders';
import { serviceKind, statusWords } from '../domain/status';
import {
  availabilityText,
  dayLabel,
  isCategoryAvailable,
  isItemAvailable,
  openStatus,
  wallClock,
} from '../domain/time';
import { Nav } from '../state/nav';
import { ShopStore } from '../state/shop-store';
import { InfoSheet } from '../ui/info-sheet';
import { ItemSheet } from '../ui/item-sheet';
import { mapHref, ntd, telHref } from '../ui/format';
import { PickupTicket } from '../ui/pickup-ticket';
import { TimeSheet } from '../ui/time-sheet';

/**
 * 點餐首頁：店家資訊、取餐時間、分類頁籤與菜單、底部的購物車列。掃桌號 QR code 進來（內用，#402）時不選時間，
 * 改顯示桌號與這桌已點的訂單（每次回到菜單都重讀）。
 */
@Component({
  selector: 'app-menu-page',
  imports: [NgTemplateOutlet, PickupTicket, TimeSheet, ItemSheet, InfoSheet],
  templateUrl: './menu-page.html',
  styleUrl: './menu-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class MenuPage {
  protected readonly store = inject(ShopStore);
  protected readonly nav = inject(Nav);
  private readonly document = inject(DOCUMENT);

  protected readonly ntd = ntd;
  protected readonly telHref = telHref;
  protected readonly mapHref = mapHref;
  protected readonly availabilityText = availabilityText;

  protected readonly timeOpen = signal(false);
  protected readonly timePrompt = signal(false);
  protected readonly infoOpen = signal(false);
  protected readonly openItem = signal<MenuItem | null>(null);
  protected readonly activeCategory = signal<string | null>(null);

  protected readonly status = computed(() => {
    const shop = this.store.shop();
    // 桌號模式（內用，#402）看內用的時間。
    const dineIn = this.store.isTable() ? shop?.dineIn : null;
    return shop ? openStatus(dineIn ? { ...shop, open: dineIn.open } : shop) : null;
  });

  protected readonly reminderPreview = computed(
    () => this.store.shop()?.notices.reminder?.split('\n')[0] ?? '',
  );

  protected readonly banner = computed(() => {
    const order = activeOrder(this.store.slug(), this.store.now());
    if (!order) return null;
    return { ...order, words: statusWords(order.status, null, serviceKind(order)).title };
  });

  /** 這桌目前桌次的訂單（內用，#402）。 */
  protected readonly session = computed(() =>
    this.store.isTable() ? (this.store.table()?.session ?? null) : null,
  );

  /** 判斷供應與售完用的時刻：選好的取餐時間，還沒選時用現在（伺服器時間）。 */
  private readonly pickup = computed(() => this.store.pickupAt() ?? new Date(this.store.now()));

  protected readonly categories = computed(() => {
    const menu = this.store.menu();
    const shop = this.store.shop();
    if (!menu || !shop) return [];
    const pickup = this.pickup();
    const today = this.store.today();
    return menu.categories.map((category) => {
      const offered = isCategoryAvailable(category.availability, pickup, shop.timeZone);
      return {
        category,
        offered,
        items: category.items.map((item) => ({
          item,
          reason: offered
            ? this.unavailableReason(item, pickup, shop.timeZone, today)
            : this.offText(),
        })),
      };
    });
  });

  /** 分類不在供應時段時的說明（內用沒有取餐時間，說「現在」）。 */
  protected readonly offText = computed(() =>
    this.store.isTable() ? '現在不供應' : '這個取餐時間不供應',
  );

  constructor() {
    const destroyRef = inject(DestroyRef);
    void this.init();

    afterNextRender(() => {
      const window = this.document.defaultView;
      if (!window) return;
      let frame = 0;
      const onScroll = () => {
        cancelAnimationFrame(frame);
        frame = requestAnimationFrame(() => this.syncActiveCategory());
      };
      window.addEventListener('scroll', onScroll, { passive: true });
      destroyRef.onDestroy(() => window.removeEventListener('scroll', onScroll));
    });
  }

  private async init(): Promise<void> {
    await Promise.all([this.store.loadMenu(), this.store.loadService()]);
    this.activeCategory.set(this.store.menu()?.categories[0]?.id ?? null);
    if (this.store.isTable() || !this.store.canOrder() || this.store.serviceReady()) return;
    if (this.store.asapAvailable()) {
      this.store.chooseService({ type: 'takeout-asap', date: null, slot: null });
    } else if (
      this.store.hasScheduledSlots() ||
      this.store.hasDeliverySlots() ||
      this.store.deliveryAsapAvailable()
    ) {
      this.timePrompt.set(true);
      this.timeOpen.set(true);
    }
  }

  private unavailableReason(
    item: MenuItem,
    pickup: Date,
    timeZone: string,
    today: string,
  ): string | null {
    if (isItemAvailable(item, pickup)) return null;
    if (item.state?.kind === 'sold-out-today') {
      if (!item.state.resetsAt || this.store.isTable()) return '今日售完';
      const back = wallClock(item.state.resetsAt, timeZone);
      return `今日售完，${dayLabel(back.date, today)} ${back.time} 起可預訂`;
    }
    return '暫停供應';
  }

  /** 這桌某筆訂單的狀態與品項（只有號碼、狀態、品名、份數與金額）。 */
  protected tableOrderStatus(order: TableOrder): string {
    return order.status ? statusWords(order.status, null, 'dine-in').title : '';
  }

  protected tableOrderItems(order: TableOrder): string {
    return order.items
      .map((item) => `${item.name}${item.quantity > 1 ? ` ×${item.quantity}` : ''}`)
      .join('、');
  }

  protected orderable(reason: string | null): boolean {
    return reason === null && this.store.canOrder();
  }

  protected openTime(): void {
    this.timePrompt.set(false);
    this.timeOpen.set(true);
  }

  protected closeTime(): void {
    this.timeOpen.set(false);
    this.timePrompt.set(false);
  }

  protected addToCart(line: Parameters<ShopStore['addLine']>[0]): void {
    this.store.addLine(line);
    this.openItem.set(null);
  }

  protected scrollToCategory(category: MenuCategory): void {
    const section = this.document.getElementById(`cat-${category.id}`);
    if (!section) return;
    const reduce = this.document.defaultView?.matchMedia?.(
      '(prefers-reduced-motion: reduce)',
    ).matches;
    section.scrollIntoView({ behavior: reduce ? 'auto' : 'smooth', block: 'start' });
    this.activeCategory.set(category.id);
    section.querySelector<HTMLElement>('h2')?.focus({ preventScroll: true });
  }

  /** 捲動時把頁籤切到畫面最上方那個分類，並讓頁籤列跟著捲到看得到。 */
  private syncActiveCategory(): void {
    const tabs = this.document.querySelector<HTMLElement>('.tabs');
    const offset = (tabs?.getBoundingClientRect().bottom ?? 0) + 8;
    let current: string | null = null;
    for (const section of Array.from(this.document.querySelectorAll<HTMLElement>('.category'))) {
      if (section.getBoundingClientRect().top <= offset)
        current = section.dataset['category'] ?? null;
    }
    current ??= this.store.menu()?.categories[0]?.id ?? null;
    if (current === this.activeCategory()) return;
    this.activeCategory.set(current);
    const tab = tabs?.querySelector<HTMLElement>(`[data-tab="${current}"]`);
    if (tab && tabs) {
      tabs.scrollTo({
        left: tab.offsetLeft - tabs.clientWidth / 2 + tab.clientWidth / 2,
        behavior: 'auto',
      });
    }
  }
}
