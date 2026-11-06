import { computed, inject, Injectable, signal } from '@angular/core';
import { OrderingApi } from '../api/ordering-api';
import type { Menu, ShopInfo, Slot, Slots } from '../api/types';
import {
  EMPTY_CART,
  loadCart,
  newLineKey,
  priceLine,
  revalidate,
  sameLine,
  saveCart,
  type CartLine,
  type PricedCartLine,
  type ServiceChoice,
  type StoredCart,
} from '../domain/cart';
import {
  addMinutes,
  asapText,
  isCategoryAvailable,
  isItemAvailable,
  slotText,
  wallClock,
} from '../domain/time';
import { Announcer } from './announcer';

export type LoadState = 'idle' | 'loading' | 'ready' | 'not-found' | 'error';

/**
 * 目前這家店的點餐狀態（換店時 {@link load} 會清掉上一家店的資料）：店家資訊、菜單、可選時段、購物車與取餐方式。
 * 購物車與選好的取餐方式存在 localStorage（每家店一份），重新整理後仍在。
 */
@Injectable({ providedIn: 'root' })
export class ShopStore {
  private readonly api = inject(OrderingApi);
  private readonly announcer = inject(Announcer);

  readonly slug = signal('');
  readonly shop = signal<ShopInfo | null>(null);
  readonly shopState = signal<LoadState>('idle');
  readonly menu = signal<Menu | null>(null);
  readonly menuState = signal<LoadState>('idle');
  readonly slots = signal<Slots | null>(null);
  readonly cart = signal<StoredCart>(EMPTY_CART);
  /** 菜單改版後告訴顧客的話（移除了哪些品項、價格以新菜單為準）。 */
  readonly cartNotice = signal<string | null>(null);
  /** 伺服器說無法供應或選項不符的行：行的 key → 原因代碼。 */
  readonly lineProblems = signal<ReadonlyMap<string, string>>(new Map());

  /** 伺服器的現在時間（店家資訊或時段回應較新的那個）。 */
  readonly now = computed(() => {
    const times = [this.shop()?.now, this.slots()?.now].filter((value): value is string => !!value);
    return times.sort().at(-1) ?? new Date().toISOString();
  });

  readonly today = computed(() => {
    const shop = this.shop();
    return shop ? wallClock(this.now(), shop.timeZone).date : '';
  });

  /** 預約取餐有沒有任何可選（未滿）的時段。 */
  readonly hasScheduledSlots = computed(
    () =>
      !!this.shop()?.services.takeoutScheduled &&
      (this.slots()?.dates ?? []).some((date) => date.slots.some((slot) => !slot.isFull)),
  );

  /** 現在能不能點「儘快」。 */
  readonly asapAvailable = computed(
    () => !!this.shop()?.services.takeoutAsap && !!this.shop()?.asap,
  );

  /** 店家現在能不能接任何一種訂單。 */
  readonly canOrder = computed(() => {
    const shop = this.shop();
    if (!shop || shop.paused) return false;
    return this.asapAvailable() || this.hasScheduledSlots();
  });

  readonly service = computed(() => this.cart().service);

  /** 選好的預約時段（還在可選清單、而且未滿）。 */
  readonly chosenSlot = computed<{ date: string; slot: Slot } | null>(() => {
    const service = this.service();
    if (service?.type !== 'takeout-scheduled') return null;
    for (const date of this.slots()?.dates ?? []) {
      if (date.date !== service.date) continue;
      const slot = date.slots.find(
        (candidate) => candidate.value === service.slot && !candidate.isFull,
      );
      if (slot) return { date: date.date, slot };
    }
    return null;
  });

  /** 取餐方式與時間都還有效。 */
  readonly serviceReady = computed(() => {
    const service = this.service();
    if (!service) return false;
    return service.type === 'takeout-asap' ? this.asapAvailable() : this.chosenSlot() !== null;
  });

  /** 預計取餐時刻：決定分類是否供應、品項是否售完（同伺服器：儘快＝現在＋估計上限）。 */
  readonly pickupAt = computed<Date | null>(() => {
    const service = this.service();
    const shop = this.shop();
    if (!service || !shop) return null;
    if (service.type === 'takeout-asap') {
      return shop.asap ? addMinutes(this.now(), shop.asap.maxMinutes) : null;
    }
    const chosen = this.chosenSlot();
    return chosen ? new Date(chosen.slot.at) : null;
  });

  /** 畫面上的取餐時間：「外帶預約」「今天 12:30」。 */
  readonly serviceLabel = computed<{ kind: string; when: string } | null>(() => {
    const service = this.service();
    const shop = this.shop();
    if (!service || !shop || !this.serviceReady()) return null;
    if (service.type === 'takeout-asap') {
      return { kind: '外帶，儘快取餐', when: shop.asap ? asapText(shop.asap) : '' };
    }
    const chosen = this.chosenSlot();
    return chosen
      ? { kind: '外帶，預約取餐', when: slotText(chosen.date, chosen.slot, this.today()) }
      : null;
  });

  readonly pricedLines = computed<{ line: CartLine; priced: PricedCartLine | null }[]>(() => {
    const menu = this.menu();
    return this.cart().lines.map((line) => ({ line, priced: menu ? priceLine(menu, line) : null }));
  });

  readonly subtotal = computed(() =>
    this.pricedLines().reduce((sum, entry) => sum + (entry.priced?.lineTotal ?? 0), 0),
  );

  readonly itemCount = computed(() =>
    this.cart().lines.reduce((sum, line) => sum + line.quantity, 0),
  );

  /** 每個品項已加幾份（菜單上的「已加 N 份」）。 */
  readonly countByItem = computed(() => {
    const counts = new Map<string, number>();
    for (const line of this.cart().lines)
      counts.set(line.itemId, (counts.get(line.itemId) ?? 0) + line.quantity);
    return counts;
  });

  /**
   * 購物車裡在這個取餐時間不能點的行（行的 key → 原因代碼）：伺服器回報的，加上前端依菜單算出的
   * 今日售完、停售、分類不在供應時段。
   */
  readonly lineWarnings = computed<ReadonlyMap<string, string>>(() => {
    const map = new Map(this.lineProblems());
    const menu = this.menu();
    const shop = this.shop();
    const pickup = this.pickupAt();
    if (!menu || !shop || !pickup) return map;
    for (const line of this.cart().lines) {
      if (map.has(line.key)) continue;
      const category = menu.categories.find((candidate) =>
        candidate.items.some((item) => item.id === line.itemId),
      );
      const item = category?.items.find((candidate) => candidate.id === line.itemId);
      if (!category || !item) continue;
      if (!isItemAvailable(item, pickup)) {
        map.set(line.key, item.state?.kind ?? 'unavailable');
      } else if (!isCategoryAvailable(category.availability, pickup, shop.timeZone)) {
        map.set(line.key, 'category-unavailable');
      }
    }
    return map;
  });

  private loadedSlug = '';

  /** 換到某家店：讀店家資訊與這家店的購物車。 */
  async load(slug: string): Promise<void> {
    if (this.loadedSlug === slug && this.shopState() !== 'error') return;
    if (this.loadedSlug !== slug) {
      this.shop.set(null);
      this.menu.set(null);
      this.menuState.set('idle');
      this.slots.set(null);
      this.cartNotice.set(null);
      this.lineProblems.set(new Map());
    }
    this.loadedSlug = slug;
    this.slug.set(slug);
    this.cart.set(loadCart(slug));
    this.cartNotice.set(this.cart().notice ?? null);
    await this.refreshShop();
  }

  async refreshShop(): Promise<void> {
    this.shopState.set(this.shop() ? 'ready' : 'loading');
    const result = await this.api.shop(this.slug());
    if (result.ok) {
      this.shop.set(result.data);
      this.shopState.set('ready');
    } else {
      this.shopState.set(
        'problem' in result && result.problem.reason === 'shop-not-found' ? 'not-found' : 'error',
      );
    }
  }

  /** 讀菜單；版本和購物車不同時重新檢查購物車並告訴顧客。回傳是否成功。 */
  async loadMenu(): Promise<boolean> {
    if (!this.menu()) this.menuState.set('loading');
    const result = await this.api.menu(this.slug());
    if (!result.ok) {
      if (!this.menu()) this.menuState.set('error');
      return false;
    }
    this.applyMenu(result.data);
    this.menuState.set('ready');
    return true;
  }

  async loadSlots(): Promise<void> {
    const result = await this.api.slots(this.slug());
    if (result.ok) {
      this.slots.set(result.data);
      this.dropStaleService();
    }
  }

  /** 店家資訊、菜單、時段一起重新讀（例如送出時伺服器說時段或菜單變了）。 */
  async refreshAll(): Promise<void> {
    await Promise.all([this.refreshShop(), this.loadMenu(), this.loadSlots()]);
  }

  private applyMenu(menu: Menu): void {
    this.menu.set(menu);
    const cart = this.cart();
    if (cart.lines.length === 0) {
      if (cart.menuVersion !== menu.version)
        this.updateCart({ ...cart, menuVersion: menu.version });
      return;
    }
    if (cart.menuVersion === menu.version) return;
    const { cart: next, removed } = revalidate(cart, menu);
    const notice =
      removed.length > 0
        ? `菜單已經更新，購物車裡的「${removed.join('」「')}」已經沒有供應或選項改了，我們先移除了。其他品項的金額以新菜單為準。`
        : '菜單已經更新，購物車的金額以新菜單重新計算，請確認後再送出。';
    this.updateCart({ ...next, notice });
    this.cartNotice.set(notice);
    this.announcer.announce(notice);
  }

  /** 選好的時段已經不在清單（過了、額滿）時清掉，讓顧客重選。 */
  private dropStaleService(): void {
    const service = this.service();
    if (service?.type === 'takeout-scheduled' && !this.chosenSlot()) {
      this.updateCart({ ...this.cart(), service: null });
    }
  }

  /** 顧客看過菜單改版的說明。 */
  dismissNotice(): void {
    this.cartNotice.set(null);
    this.updateCart({ ...this.cart(), notice: null });
  }

  chooseService(service: ServiceChoice): void {
    this.updateCart({ ...this.cart(), service });
    const label = this.serviceLabel();
    if (label) this.announcer.announce(`取餐時間：${label.when}`);
  }

  clearService(): void {
    this.updateCart({ ...this.cart(), service: null });
  }

  /** 加入購物車；同品項、同選項、同備註的行合併份數。 */
  addLine(line: Omit<CartLine, 'key'>): void {
    const cart = this.cart();
    const same = cart.lines.find((existing) => sameLine(existing, line));
    const lines = same
      ? cart.lines.map((existing) =>
          existing === same
            ? { ...existing, quantity: Math.min(99, existing.quantity + line.quantity) }
            : existing,
        )
      : [...cart.lines, { ...line, key: newLineKey() }];
    this.updateCart({ ...cart, lines, menuVersion: this.menu()?.version ?? cart.menuVersion });
    this.announcer.announce(
      `已加入購物車：${line.name ?? ''} ${line.quantity} 份，購物車共 ${this.itemCount()} 份，小計 NT$${this.subtotal().toLocaleString('en-US')}`,
    );
  }

  /** 修改購物車的某一行（改選項、份數、備註）。 */
  replaceLine(key: string, line: Omit<CartLine, 'key'>): void {
    const cart = this.cart();
    this.updateCart({
      ...cart,
      lines: cart.lines.map((existing) => (existing.key === key ? { ...line, key } : existing)),
    });
    this.clearLineProblem(key);
    this.announcer.announce(`已更新購物車，小計 NT$${this.subtotal().toLocaleString('en-US')}`);
  }

  setQuantity(key: string, quantity: number): void {
    const cart = this.cart();
    const next = Math.max(1, Math.min(99, Math.trunc(quantity)));
    this.updateCart({
      ...cart,
      lines: cart.lines.map((line) => (line.key === key ? { ...line, quantity: next } : line)),
    });
    this.announcer.announce(`份數 ${next}，小計 NT$${this.subtotal().toLocaleString('en-US')}`);
  }

  removeLine(key: string): void {
    const cart = this.cart();
    const removed = cart.lines.find((line) => line.key === key);
    this.updateCart({ ...cart, lines: cart.lines.filter((line) => line.key !== key) });
    this.clearLineProblem(key);
    this.announcer.announce(
      `已移除${removed?.name ? `「${removed.name}」` : ''}，小計 NT$${this.subtotal().toLocaleString('en-US')}`,
    );
  }

  setOrderComment(orderComment: string): void {
    this.updateCart({ ...this.cart(), orderComment });
  }

  setTableware(tableware: boolean): void {
    this.updateCart({ ...this.cart(), tableware });
  }

  /** 送出成功後清空購物車；取餐方式與時間留著，再點一次時不必重選。 */
  clearCart(): void {
    this.updateCart({
      ...EMPTY_CART,
      menuVersion: this.menu()?.version ?? 0,
      service: this.cart().service,
    });
    this.lineProblems.set(new Map());
    this.cartNotice.set(null);
  }

  markLineProblems(problems: readonly { line: number; code: string }[]): void {
    const lines = this.cart().lines;
    const map = new Map<string, string>();
    for (const problem of problems) {
      const line = lines[problem.line];
      if (line) map.set(line.key, problem.code);
    }
    this.lineProblems.set(map);
  }

  private clearLineProblem(key: string): void {
    if (!this.lineProblems().has(key)) return;
    const map = new Map(this.lineProblems());
    map.delete(key);
    this.lineProblems.set(map);
  }

  private updateCart(cart: StoredCart): void {
    this.cart.set(cart);
    saveCart(this.slug(), cart);
  }
}
