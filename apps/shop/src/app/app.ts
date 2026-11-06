import { ChangeDetectionStrategy, Component, DOCUMENT, inject } from '@angular/core';

/** `/order/{slug}` 的店家代號；網址不是這個形狀時為 `null`。 */
export function slugFromPath(pathname: string): string | null {
  const match = /^\/order\/([a-z0-9-]+)(?:\/|$)/i.exec(pathname);
  return match ? match[1].toLowerCase() : null;
}

/**
 * 顧客點餐網頁（apps/shop，由 API 在 `/order/` 底下提供，online-ordering D1，#398）的佔位畫面：
 * 只確認建置、部署與路徑都接好了。點餐流程（取餐方式、菜單、購物車、結帳、訂單狀態頁）由前端票補上，
 * 公開 API 的契約見 `docs/plans/2026-11-06-online-ordering.md` §4.2。
 */
@Component({
  selector: 'app-root',
  template: `
    <main>
      <h1>線上點餐</h1>
      @if (slug) {
        <p>店家代號：{{ slug }}</p>
      }
      <p>點餐頁面準備中。</p>
    </main>
  `,
  styles: `
    main {
      max-width: 40rem;
      margin: 0 auto;
      padding: 1.5rem 1rem;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  protected readonly slug = slugFromPath(inject(DOCUMENT).location.pathname);
}
