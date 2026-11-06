import { DOCUMENT, Injectable, inject, signal } from '@angular/core';

/**
 * 點餐網頁的頁面：`/order/{slug}`、`/cart`、`/checkout`、`/o`（訂單狀態，token 在 `#` 後面）。`/table` 是內用桌號
 * QR code 的入口（#402，桌號 token 在 `#` 後面）：讀完 token 就換成菜單頁，所以不是可以前往的頁面。
 */
export type PageName = 'menu' | 'cart' | 'checkout' | 'status' | 'table' | 'not-found';

/** 可以前往的頁面。 */
export type PageTarget = Exclude<PageName, 'not-found' | 'table'>;

export interface Route {
  /** 店家代號（小寫）；網址沒有代號時是 `null`。 */
  readonly slug: string | null;
  readonly page: PageName;
}

const PAGES: Record<string, PageName> = {
  '': 'menu',
  cart: 'cart',
  checkout: 'checkout',
  o: 'status',
  table: 'table',
};

/** 從網址路徑（含 `/order/` 前綴）讀出店家代號與頁面。 */
export function parseRoute(pathname: string): Route {
  const match = /^\/order(?:\/([^/]*)(?:\/([^/]*))?)?\/?$/i.exec(pathname);
  if (!match) return { slug: null, page: 'not-found' };
  const slug = (match[1] ?? '').toLowerCase();
  if (!slug) return { slug: null, page: 'not-found' };
  if (!/^[a-z0-9-]{1,64}$/.test(slug)) return { slug: null, page: 'not-found' };
  const page = PAGES[(match[2] ?? '').toLowerCase()];
  return { slug, page: page ?? 'not-found' };
}

/** 某家店某一頁的網址路徑。 */
export function pagePath(slug: string, page: PageTarget): string {
  const suffix = { menu: '', cart: '/cart', checkout: '/checkout', status: '/o' }[page];
  return `/order/${slug}${suffix}`;
}

/**
 * 不帶 Angular Router 的極小導覽：只有五個頁面，自己處理 history 就能讓初始 bundle 少掉路由器的大小。
 * 頁面元件在 App 裡用動態 `import()` 延遲載入。
 */
@Injectable({ providedIn: 'root' })
export class Nav {
  private readonly document = inject(DOCUMENT);
  private readonly window = this.document.defaultView;

  readonly route = signal<Route>(parseRoute(this.document.location.pathname));
  readonly hash = signal(this.document.location.hash);
  /** 每次換頁加一，頁面可以據此把焦點移到標題。 */
  readonly navigations = signal(0);

  constructor() {
    this.window?.addEventListener('popstate', () => this.sync());
    this.window?.addEventListener('hashchange', () => this.hash.set(this.document.location.hash));
  }

  /** 換到某一頁；`hash` 不含 `#`。 */
  go(slug: string, page: PageTarget, options: { hash?: string; replace?: boolean } = {}): void {
    const url = `${pagePath(slug, page)}${options.hash ? `#${options.hash}` : ''}`;
    const history = this.window?.history;
    if (history) {
      if (options.replace) {
        history.replaceState(null, '', url);
      } else {
        history.pushState(null, '', url);
      }
    }
    this.sync();
    this.window?.scrollTo?.({ top: 0, left: 0, behavior: 'instant' as ScrollBehavior });
  }

  /** 連結的點擊：一般左鍵在本頁換頁；按住修飾鍵（開新分頁）時交給瀏覽器。 */
  follow(event: MouseEvent, slug: string, page: PageTarget, hash?: string): void {
    if (
      event.defaultPrevented ||
      event.button !== 0 ||
      event.metaKey ||
      event.ctrlKey ||
      event.shiftKey ||
      event.altKey
    ) {
      return;
    }
    event.preventDefault();
    this.go(slug, page, { hash });
  }

  private sync(): void {
    this.route.set(parseRoute(this.document.location.pathname));
    this.hash.set(this.document.location.hash);
    this.navigations.update((count) => count + 1);
  }
}
