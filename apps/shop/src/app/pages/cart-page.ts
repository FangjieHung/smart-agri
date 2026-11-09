import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import type { CartLine } from '../domain/cart';
import { lineProblemText } from '../domain/errors';
import { Nav } from '../state/nav';
import { ShopStore } from '../state/shop-store';
import { ntd } from '../ui/format';
import { ItemSheet } from '../ui/item-sheet';
import { PickupTicket } from '../ui/pickup-ticket';
import { TimeSheet } from '../ui/time-sheet';
import { PageHeader } from '../ui/page-header';

/** 購物車：改選項、份數、刪除、訂單備註、餐具，前往結帳。外送未達門檻時不能結帳（#401）。 */
@Component({
  selector: 'app-cart-page',
  imports: [PickupTicket, TimeSheet, ItemSheet, PageHeader],
  templateUrl: './cart-page.html',
  styleUrl: './cart-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class CartPage {
  protected readonly store = inject(ShopStore);
  protected readonly nav = inject(Nav);
  protected readonly ntd = ntd;
  protected readonly lineProblemText = lineProblemText;

  protected readonly timeOpen = signal(false);
  protected readonly editing = signal<CartLine | null>(null);

  protected readonly editingItem = computed(() => {
    const line = this.editing();
    const entry =
      line && this.store.pricedLines().find((candidate) => candidate.line.key === line.key);
    return entry?.priced?.item ?? null;
  });

  protected readonly blocked = computed(
    () => this.store.lineWarnings().size > 0 || this.store.shortfall() > 0,
  );

  constructor() {
    void this.store.loadMenu();
    void this.store.loadSlots();
  }

  protected saveEdit(line: Omit<CartLine, 'key'>): void {
    const editing = this.editing();
    if (editing) this.store.replaceLine(editing.key, line);
    this.editing.set(null);
  }

  protected checkout(event: MouseEvent): void {
    this.nav.follow(event, this.store.slug(), 'checkout');
  }
}
