import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { Nav } from '../state/nav';
import { ShopStore } from '../state/shop-store';

/** 購物車、結帳、訂單頁的頁首：返回連結、頁面標題與店名。 */
@Component({
  selector: 'app-page-header',
  template: `
    <header class="head">
      <a
        class="btn btn--quiet back"
        [href]="'/order/' + store.slug() + (back() === 'cart' ? '/cart' : '')"
        (click)="nav.follow($event, store.slug(), back())"
        >‹ {{ backLabel() }}</a
      >
      <h1 class="title" id="page-title" tabindex="-1">{{ title() }}</h1>
      <p class="shop">{{ store.shop()?.name }}</p>
    </header>
  `,
  styles: `
    .head {
      padding: var(--space-2) var(--space-4) var(--space-3);
      border-bottom: 1px solid var(--c-line);
    }
    .back {
      margin-left: calc(-1 * var(--space-3));
    }
    .title {
      margin-top: var(--space-1);
      font-size: var(--fs-display);
      font-weight: 800;
    }
    .shop {
      color: var(--c-ink-2);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PageHeader {
  protected readonly store = inject(ShopStore);
  protected readonly nav = inject(Nav);
  readonly title = input.required<string>();
  readonly back = input<'menu' | 'cart'>('menu');
  readonly backLabel = input('回到菜單');
}
