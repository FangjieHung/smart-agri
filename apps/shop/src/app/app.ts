import { NgComponentOutlet } from '@angular/common';
import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  effect,
  inject,
  signal,
  untracked,
  type Type,
} from '@angular/core';
import { Announcer } from './state/announcer';
import { Nav, type PageTarget } from './state/nav';
import { ShopStore } from './state/shop-store';
import { telHref } from './ui/format';

/** 每個頁面一個延遲載入的 chunk：初始 bundle 只有這個外框、導覽與狀態。 */
const PAGES: Record<PageTarget, () => Promise<{ default: Type<unknown> }>> = {
  menu: () => import('./pages/menu-page'),
  cart: () => import('./pages/cart-page'),
  checkout: () => import('./pages/checkout-page'),
  status: () => import('./pages/status-page'),
};

const PAGE_TITLES: Record<PageTarget | 'table', string> = {
  menu: '線上點餐',
  cart: '購物車',
  checkout: '結帳',
  status: '訂單狀態',
  table: '線上點餐',
};

/**
 * 顧客點餐網頁（apps/shop，由 API 在 `/order/` 底下提供，online-ordering D1，#398）的外框：
 * 依網址載入店家與頁面、找不到店家時的說明，以及給螢幕報讀器的即時通知區。
 */
@Component({
  selector: 'app-root',
  imports: [NgComponentOutlet],
  template: `
    <div class="column">
      @if (
        !nav.route().slug || nav.route().page === 'not-found' || store.shopState() === 'not-found'
      ) {
        <main class="missing page-pad" data-testid="shop-not-found">
          <h1 id="page-title" tabindex="-1">找不到這家店的點餐頁面</h1>
          <p>
            網址可能打錯了，或這家店已經不提供線上點餐。請確認店家給您的連結，或直接打電話給店家訂餐。
          </p>
        </main>
      } @else if (store.shopState() === 'error') {
        <main class="missing page-pad">
          <h1 id="page-title" tabindex="-1">暫時無法連線</h1>
          <p>請確認網路連線後再試一次。</p>
          <button type="button" class="btn btn--primary" (click)="store.refreshShop()">
            重新整理
          </button>
        </main>
      } @else if (store.tableInvalid()) {
        <main class="missing page-pad" data-testid="table-invalid">
          <h1 id="page-title" tabindex="-1">這個桌號的 QR code 已失效，請洽店員</h1>
          <p>店家可能換了桌上的 QR code。請洽店員協助點餐，或重新掃描桌上的 QR code。</p>
          <button type="button" class="btn" (click)="leaveTable()">看菜單</button>
        </main>
      } @else if (store.shop() && page(); as component) {
        <main>
          <ng-container *ngComponentOutlet="component" />
        </main>
      } @else {
        <p class="loading page-pad" role="status">正在讀取…</p>
      }
      @if (store.shop(); as shop) {
        <footer class="foot page-pad">
          <p>
            訂單由{{ shop.name }}處理，有問題請來電
            <a [href]="telHref(shop.phone)">{{ shop.phone }}</a
            >。
          </p>
        </footer>
      }
    </div>
    <div class="visually-hidden" aria-live="polite" aria-atomic="true">
      {{ announcer.message() }}
    </div>
  `,
  styles: `
    .missing {
      display: grid;
      gap: var(--space-3);
      justify-items: start;
      padding-block: var(--space-6);
    }
    .missing h1 {
      font-size: var(--fs-title);
    }
    .loading {
      padding-block: var(--space-6);
      color: var(--c-ink-2);
    }
    .foot {
      padding-block: var(--space-5) var(--space-6);
      color: var(--c-ink-3);
      font-size: var(--fs-small);
      border-top: 1px solid var(--c-line);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  protected readonly nav = inject(Nav);
  protected readonly store = inject(ShopStore);
  protected readonly announcer = inject(Announcer);
  protected readonly telHref = telHref;
  private readonly document = inject(DOCUMENT);

  protected readonly page = signal<Type<unknown> | null>(null);
  private loadingFor = '';

  /** 失效的桌號頁按〔看菜單〕：改成一般點餐。 */
  protected leaveTable(): void {
    this.store.tableInvalid.set(false);
    this.nav.go(this.store.slug(), 'menu');
  }

  constructor() {
    effect(() => {
      const route = this.nav.route();
      untracked(() => {
        if (route.slug) void this.store.load(route.slug);
        if (route.page === 'not-found') {
          this.page.set(null);
          return;
        }
        if (route.page === 'table') {
          // 內用掃碼（#402）：記住桌號 token，把網址換成菜單頁（token 不留在網址列）。
          this.page.set(null);
          this.loadingFor = '';
          if (route.slug && this.store.adoptTable(route.slug, this.nav.hash()))
            this.nav.go(route.slug, 'menu', { replace: true });
          return;
        }
        const wanted = route.page;
        this.loadingFor = wanted;
        void PAGES[wanted]().then((module) => {
          if (this.loadingFor === wanted) this.page.set(module.default);
        });
      });
    });

    effect(() => {
      const shop = this.store.shop();
      const route = this.nav.route();
      const page = route.page === 'not-found' ? '找不到店家' : PAGE_TITLES[route.page];
      this.document.title = shop && route.page !== 'not-found' ? `${page}｜${shop.name}` : page;
    });

    // 換頁後把焦點放在新頁面的標題，螢幕報讀器會念出現在在哪一頁（第一次載入不搶焦點）。
    let first = true;
    afterRenderEffect(() => {
      this.nav.navigations();
      const page = this.page();
      if (!page) return;
      untracked(() => {
        if (first) {
          first = false;
          return;
        }
        this.document.getElementById('page-title')?.focus({ preventScroll: true });
      });
    });
  }
}
