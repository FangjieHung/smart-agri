/**
 * 公開點餐 API（/api/v1/public/ordering/{slug}/…，設計文件 §4.2）的回放。
 *
 * src/fixtures/ 裡的 JSON 是 2026-11-07 為了 #401（外送）重新從真實 API 錄下、原封不動的回應本文：自己的暫存
 * 資料庫 ordering401pub、API 跑在 5291 埠，開發種子 `anxin` 後 `ordering-shop import` 匯入
 * deploy/ordering-shop.example.json（菜單第 1 版，三個外送區域），再用 scratchpad/ordering/401rec/record_pub1.py
 * 依序呼叫：店家、菜單、時段、送出外帶預約（201，#001）、查詢、取消（200）……；外送：未達門檻（422）、
 * 不存在的區域（422）、沒填地址（422）、送出外送預約（201，#002：新營區其他里，餛飩湯＋牛肉麵套餐）、查詢、
 * 店員接單並按〔已出發〕後再查詢（ready）。接著把牛肉麵改成 160 元、拿掉鮮肉餛飩湯匯入成第 2 版
 * （record_pub2.py），錄下用第 1 版送出的 `409 menu-changed`、第 2 版的店家與菜單、第 2 版送出的 201（#003）。
 * 錄製時店家時間是 2026-11-07（六）02:2x，休息中，所以首頁會先請顧客選預約時間；外帶時段是 11/7、11/8，
 * 外送時段（外送自己的時間、最短 45 分鐘）是 11/7、11/8。
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

/** 首頁一進來的「休息中，歡迎預訂」：選外帶 11/7 12:30。 */
export function chooseRecordedSlot(): void {
  cy.get('dialog[open]').should('contain', '休息中，歡迎預訂');
  cy.contains('dialog[open] label.chip', '12:30').click();
  cy.get('[data-testid="confirm-time"]').click();
  cy.get('dialog[open]').should('not.exist');
  cy.get('[data-testid="pickup-when"]').should('have.text', '今天 12:30');
}

/** 送出訂單時在結帳頁填的聯絡資料。 */
export function fillContact(name: string, phone: string, email = ''): void {
  cy.get('#checkout-name').clear().type(name);
  cy.get('#checkout-phone').clear().type(phone);
  if (email) cy.get('#checkout-email').clear().type(email);
}
