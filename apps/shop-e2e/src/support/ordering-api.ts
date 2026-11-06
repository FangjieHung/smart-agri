/**
 * 公開點餐 API（/api/v1/public/ordering/{slug}/…，設計文件 §4.2）的回放。
 *
 * src/fixtures/ 裡的 JSON 是 2026-11-06 從真實 API 錄下、原封不動的回應本文：自己的暫存資料庫、API 跑在 5271 埠，
 * `setup` 建立組織 wangji 後 `ordering-shop import` 匯入 deploy/ordering-shop.example.json（菜單第 1 版），
 * 再用 urllib 依序呼叫：店家、菜單、時段、送出（201）、查詢、取消（200）……；接著把牛肉麵改成 160 元、
 * 拿掉鮮肉餛飩湯匯入成第 2 版，錄下用第 1 版送出的 `409 menu-changed`、第 2 版的店家與菜單、第 2 版送出的 201。
 * 錄製時店家時間是 2026-11-06（五）22:32，休息中，所以首頁會先請顧客選預約時間；時段是 11/7、11/8。
 */
export const SLUG = 'wangji-noodles';
const API = `/api/v1/public/ordering/${SLUG}`;

const exact = (path: string) =>
  new RegExp(`/api/v1/public/ordering/${SLUG}${path.replace(/\//g, '\\/')}(\\?.*)?$`);

const problem = (fixture: string, statusCode: number) => ({
  statusCode,
  fixture,
  headers: { 'content-type': 'application/problem+json', 'cache-control': 'no-store' },
});

/** 菜單第 1 版的店家、菜單與時段。 */
export function interceptShopV1(): void {
  cy.intercept('GET', exact(''), { fixture: 'shop-v1.json' }).as('shop');
  cy.intercept('GET', exact('/menu'), { fixture: 'menu-v1.json' }).as('menu');
  cy.intercept('GET', exact('/slots'), { fixture: 'slots.json' }).as('slots');
}

/** 改成第 2 版（伺服器已經換菜單）。 */
export function interceptShopV2(): void {
  cy.intercept('GET', exact(''), { fixture: 'shop-v2.json' }).as('shop');
  cy.intercept('GET', exact('/menu'), { fixture: 'menu-v2.json' }).as('menu');
}

export { API, exact, problem };

/** 首頁一進來的「休息中，歡迎預訂」：選 11/7 12:30。 */
export function chooseRecordedSlot(): void {
  cy.get('dialog[open]').should('contain', '休息中，歡迎預訂');
  cy.contains('dialog[open] label.chip', '12:30').click();
  cy.get('[data-testid="confirm-time"]').click();
  cy.get('dialog[open]').should('not.exist');
  cy.get('[data-testid="pickup-when"]').should('have.text', '明天 12:30');
}

/** 送出訂單時在結帳頁填的聯絡資料。 */
export function fillContact(name: string, phone: string, email = ''): void {
  cy.get('#checkout-name').clear().type(name);
  cy.get('#checkout-phone').clear().type(phone);
  if (email) cy.get('#checkout-email').clear().type(email);
}
