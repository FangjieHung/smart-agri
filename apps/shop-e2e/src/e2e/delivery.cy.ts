import { auditA11y } from '../support/a11y';
import { exact, fillContact, interceptShopV1, SLUG } from '../support/ordering-api';

/** 外送（#401）：選區域與時間 → 未達門檻的提醒 → 再加點 → 填地址 → 送出 → 訂單頁顯示外送。 */
describe('外送：區域、門檻、地址、送出', () => {
  beforeEach(() => interceptShopV1());

  it('選新營區其他里，未滿 NT$200 不能結帳，加點後填地址送出，訂單頁顯示外送與地址', () => {
    cy.fixture('delivery-submit-201.json').then((submitted: { token: string }) => {
      cy.intercept('POST', exact('/orders'), {
        statusCode: 201,
        fixture: 'delivery-submit-201.json',
      }).as('submit');
      cy.intercept('GET', exact('/orders/current'), { fixture: 'delivery-order-pending.json' }).as(
        'order',
      );

      cy.visit(SLUG);
      cy.get('dialog[open]').should('contain', '休息中，歡迎預訂');
      cy.get('dialog[open] input[name="service"][value="delivery-scheduled"]').check();
      cy.get('[data-testid="zones"]')
        .should('contain', '新營區其他里')
        .and('contain', '運費 NT$30・滿 NT$200 外送')
        .and('contain', '免運費・滿 NT$150 外送');
      auditA11y();
      cy.get('dialog[open] input[name="zone"][value="xinying"]').check();
      cy.contains('dialog[open] label.chip', '12:00').click();
      cy.get('[data-testid="confirm-time"]').should('contain', '確定外送區域與時間').click();
      cy.get('dialog[open]').should('not.exist');
      cy.get('[data-testid="pickup-when"]').should('have.text', '今天 12:00');
      cy.get('[data-testid="pickup-zone"]').should('have.text', '送到 新營區其他里');

      cy.get('[data-item="wonton-soup"]').click();
      cy.get('[data-testid="add-to-cart"]').should('contain', 'NT$70').click();
      cy.get('[data-testid="cart-bar"]').click();
      cy.get('#page-title').should('have.text', '購物車');
      cy.get('[data-testid="delivery-shortfall"]').should('contain', '還差 NT$130 達外送門檻');
      cy.get('[data-testid="delivery-fee"]').should('have.text', 'NT$30');
      cy.get('[data-testid="go-checkout"]').should('have.attr', 'aria-disabled', 'true').click();
      cy.location('pathname').should('eq', `/order/${SLUG}/cart`);
      auditA11y();

      cy.contains('a', '新增餐點').click();
      cy.get('[data-item="beef-noodle"]').click();
      cy.get('#item-size-large').check();
      cy.get('#item-noodle-thin').check();
      cy.get('[aria-label="增加滷蛋"]').click().click();
      cy.get('#item-combo-set').check();
      cy.get('#opt-l2-set-side-cucumber').check();
      cy.get('#opt-l2-set-drink-winter-melon').check();
      cy.contains('dialog[open] button', '確定').click();
      cy.get('#item-comment').type('麵硬一點');
      cy.get('[data-testid="add-to-cart"]').should('contain', 'NT$270').click();
      cy.get('[data-testid="cart-bar"]').should('contain', 'NT$340').click();
      cy.get('[data-testid="delivery-shortfall"]').should('not.exist');
      cy.get('[data-testid="go-checkout"]').should('contain', 'NT$370').click();

      cy.location('pathname').should('eq', `/order/${SLUG}/checkout`);
      cy.get('[data-testid="checkout-fee"]').should('have.text', 'NT$30');
      cy.get('[data-testid="checkout-total"]').should('have.text', 'NT$370');
      cy.contains('.payment', '貨到付款');
      fillContact('王太太', '0955-123-456');
      cy.get('[data-testid="submit-order"]').click();
      cy.get('#checkout-address-error').should('contain', '請填寫外送地址');
      cy.focused().should('have.id', 'checkout-address');
      cy.get('#checkout-address').type('台南市新營區中山路 1 號 3 樓');
      cy.get('#checkout-deliveryNote').type('請按電鈴');
      auditA11y();
      cy.get('[data-testid="submit-order"]').click();

      cy.wait('@submit').then(({ request }) => {
        const body = request.body as Record<string, unknown>;
        expect(body).to.include({
          serviceType: 'delivery-scheduled',
          pickupDate: '2026-11-07',
          pickupSlot: '2026-11-07T12:00',
          deliveryZoneId: 'xinying',
          deliveryAddress: '台南市新營區中山路 1 號 3 樓',
          deliveryNote: '請按電鈴',
          menuVersion: 1,
        });
        expect((body['lines'] as { itemId: string }[]).map((line) => line.itemId)).to.deep.equal([
          'wonton-soup',
          'beef-noodle',
        ]);
        expect(JSON.stringify(body)).not.to.match(/price|total|fee/i);
      });

      cy.location('hash').should('eq', `#${submitted.token}`);
      cy.wait('@order');
      cy.get('[data-testid="order-number"]').should('contain.text', '#002');
      cy.get('[data-testid="order-status"]').should('contain.text', '等待店家確認');
      cy.get('[data-testid="order-pickup"]').should('have.text', '預計 今天 12:00 送達');
      cy.get('[data-testid="order-zone"]').should('have.text', '新營區其他里');
      cy.get('[data-testid="order-address"]').should('have.text', '台南市新營區中山路 1 號 3 樓');
      cy.get('[data-testid="order-fee"]').should('have.text', 'NT$30');
      cy.contains('dd', '貨到付款');
      auditA11y();
    });
  });

  it('店員按〔已出發〕後，訂單頁顯示外送已出發', () => {
    cy.fixture('delivery-order-ready.json').then(() => {
      cy.intercept('GET', exact('/orders/current'), { fixture: 'delivery-order-ready.json' });
      cy.visit(`${SLUG}/o#${'A'.repeat(43)}`);
      cy.get('[data-testid="order-status"]').should('contain.text', '外送已出發');
      cy.get('[aria-current="step"]').should('contain', '已出發');
      cy.get('[data-testid="order-address"]').should('contain', '中山路');
    });
  });
});
