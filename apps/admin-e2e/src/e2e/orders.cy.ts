import { auditA11y, loginAs } from '../support/a11y';

/**
 * 線上點餐的後台（issue #399，mock 模式）：王記麵店的示範訂單——待確認 #016（預約）與 #015（儘快）、處理中 #014、#013
 * 與外送 #010（#401）、今日完成 #012 與 #011。老闆與內部同仁有「處理線上點餐」；外部客戶沒有，側欄不顯示、頁面顯示沒有權限。
 */
describe('orders', () => {
  const navLink = '.app-sidenav a[href="/app/orders"]';
  const card = (number: string) => `[data-order="order-${number}"]`;

  beforeEach(() => {
    cy.viewport(1440, 900);
  });

  it('shows the pending number beside 點餐 and takes an order from accept to ready to complete', () => {
    loginAs('SMB 管理者');
    cy.get(`${navLink} .nav-count`).invoke('text').should((text) => expect(text.trim()).to.eq('2'));
    cy.get(navLink).should('have.attr', 'aria-label', '點餐，2 筆待確認').click();
    cy.location('pathname').should('eq', '/app/orders');
    cy.get('#main-content').contains('h1', '線上點餐').should('be.visible');
    cy.title().should('match', /^\(2\) /);

    cy.get(`[data-orders-column="pending"] ${card('016')}`).within(() => {
      cy.contains('外帶預約').should('be.visible');
      cy.contains('a', '0912-345-678').should('have.attr', 'href', 'tel:0912345678');
      cy.get('[data-action]').then((buttons) => expect([...buttons].map((button) => button.textContent?.trim())).to.deep.eq(['接單', '婉拒']));
      cy.get('[data-action="accept"]').click();
    });
    cy.get('[data-order-dialog="accept"]').should('be.visible').within(() => {
      cy.contains('h2', '接單 #016');
      cy.focused().should('have.attr', 'type', 'radio');
      cy.contains('label', '延後 10 分鐘').click();
      cy.get('[data-accept-result]').should('contain', '顧客會在訂單頁看到：預計');
      cy.get('[data-order-dialog-confirm]').click();
    });
    cy.get('[data-order-dialog]').should('not.exist');
    cy.get('[data-orders-notice]').should('contain', '已接單 #016');
    cy.get(`[data-orders-column="active"] ${card('016')}`).should('contain', '已接單');
    cy.get(`${navLink} .nav-count`).invoke('text').should((text) => expect(text.trim()).to.eq('1'));
    cy.title().should('match', /^\(1\) /);

    cy.get(`[data-orders-column="active"] ${card('016')} [data-action="ready"]`).should('have.text', '可取餐').click();
    cy.get(`[data-orders-column="active"] ${card('016')}`).should('contain', '可取餐');
    cy.get(`[data-orders-column="active"] ${card('016')} [data-action="complete"]`).click();
    cy.get(`[data-orders-column="done"] ${card('016')}`).should('contain', '已完成').find('[data-action]').should('not.exist');
    cy.get('[data-orders-notice]').should('contain', '#016 已完成。');
  });

  it('shows a delivery order with its zone, a map link for the address, the fee and 〔已出發〕 (#401)', () => {
    loginAs('SMB 管理者');
    cy.visit('/app/orders');
    cy.get('#main-content').contains('h1', '線上點餐').should('be.visible');
    cy.get(`#main-content [data-orders-column="active"] ${card('010')}`).scrollIntoView().should('be.visible').within(() => {
      cy.contains('.order__service', '外送預約').should('be.visible');
      cy.contains('.order__zone', '新營區其他里');
      cy.get('a[data-delivery-address]')
        .should('have.text', '台南市新營區中山路 1 號 3 樓')
        .and('have.attr', 'href', `https://www.google.com/maps/search/?api=1&query=${encodeURIComponent('台南市新營區中山路 1 號 3 樓')}`)
        .and('have.attr', 'target', '_blank')
        .and('have.attr', 'rel', 'noopener noreferrer');
      cy.get('[data-delivery-note]').should('have.text', '給外送的備註：請按電鈴');
      cy.get('[data-delivery-fee]').invoke('text').should((text) => expect(text.trim()).to.eq('運費 NT$30'));
      cy.get('.order__total').should('contain', 'NT$400');
      cy.get('.order__when').should('contain', '送達');
      cy.get('[data-action="ready"]').should('have.text', '已出發').click();
    });
    cy.get(`[data-orders-column="active"] ${card('010')} .order__status`).should('have.text', '已出發');
    cy.get('[data-orders-notice]').should('contain', '#010 已標示為已出發。');
  });

  it('declines with a reason and a note the customer will see', () => {
    loginAs('內部使用者');
    cy.visit('/app/orders');
    cy.get(`[data-orders-column="pending"] ${card('015')} [data-action="decline"]`).click();
    cy.get('[data-order-dialog="decline"]').within(() => {
      cy.contains('顧客會在訂單頁看到這段說明').should('be.visible');
      cy.get('[data-order-dialog-confirm]').click();
      cy.get('[role="alert"]').should('contain', '請選擇原因。');
      cy.contains('label', '太忙，無法接單').click();
      cy.get('[data-order-note]').type('中午訂單太多，抱歉！');
      cy.get('[data-order-dialog-confirm]').click();
    });
    cy.get('[data-order-dialog]').should('not.exist');
    cy.get(`[data-orders-column="done"] ${card('015')}`)
      .should('contain', '已婉拒')
      .and('contain', '太忙，無法接單')
      .and('contain', '給顧客的說明：中午訂單太多，抱歉！');
  });

  it('marks an item sold out for today and shows when it comes back', () => {
    loginAs('SMB 管理者');
    cy.visit('/app/orders');
    cy.get('#main-content').contains('summary', '品項供應').click();
    cy.get('[data-item="beef-noodle"]').should('contain', '供應中').within(() => {
      cy.contains('label', '今日售完').click();
    });
    cy.get('[data-item="beef-noodle"]')
      .should('have.attr', 'data-availability', 'sold-out-today')
      .and('contain', '明天 11:00 自動恢復供應');
    cy.get('[data-ordering-item-search]').type('滷味');
    cy.get('[data-item]').should('have.length', 1).and('have.attr', 'data-item', 'braised-platter');
  });

  it('shows one column at a time on a phone', () => {
    cy.viewport(390, 844);
    loginAs('SMB 管理者');
    cy.visit('/app/orders');
    cy.get('[data-orders-tab="pending"]').should('have.attr', 'aria-pressed', 'true');
    cy.get(card('016')).should('be.visible');
    cy.get(card('014')).should('not.be.visible');
    cy.get('[data-orders-tab="active"]').click();
    cy.get(card('014')).scrollIntoView().should('be.visible');
    cy.get(card('016')).should('not.be.visible');
  });

  it('hides 點餐 from an account without 處理線上點餐 and shows no permission on the page', () => {
    loginAs('外部客戶');
    cy.get('.app-sidenav').should('contain', '首頁');
    cy.get(navLink).should('not.exist');
    cy.visit('/app/orders');
    cy.get('#main-content').contains('無法使用線上點餐').should('be.visible');
  });

  it('has no critical or serious accessibility violations on the page and both dialogs', () => {
    loginAs('SMB 管理者');
    cy.visit('/app/orders');
    cy.get('#main-content').contains('button', '暫停接單').should('be.visible');
    cy.get(card('016')).should('be.visible');
    auditA11y();

    cy.get(`${card('016')} [data-action="accept"]`).click();
    cy.get('[data-order-dialog="accept"]').should('be.visible');
    auditA11y();
    cy.focused().trigger('keydown', { key: 'Escape' });
    cy.get('[data-order-dialog]').should('not.exist');
    cy.focused().should('have.attr', 'data-action', 'accept');

    cy.get(`${card('016')} [data-action="decline"]`).click();
    cy.get('[data-order-dialog="decline"]').should('be.visible');
    auditA11y();
  });
});
