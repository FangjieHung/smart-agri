import {
  chooseRecordedSlot,
  exact,
  fillContact,
  interceptShopV1,
  problem,
  SLUG,
} from '../support/ordering-api';

describe('必選的選項', () => {
  beforeEach(() => {
    interceptShopV1();
    cy.visit(SLUG);
    chooseRecordedSlot();
  });

  it('必選群組沒選完不能加入購物車，按下去會標出並跳到沒選的群組', () => {
    cy.get('[data-item="beef-noodle"]').click();
    cy.get('[data-testid="add-to-cart"]').should('have.attr', 'aria-disabled', 'true').click();
    cy.get('.group--unmet').should('have.length', 2);
    cy.get('#item-size').should('have.class', 'group--unmet').and('contain', '請選擇份量');
    cy.focused().should('have.id', 'item-size');
    cy.get('dialog[open]').should('have.length', 1);

    cy.get('#item-size-regular').check();
    cy.get('#item-size').should('not.have.class', 'group--unmet');
    cy.get('[data-testid="add-to-cart"]').click();
    cy.focused().should('have.id', 'item-noodle');
    cy.get('[data-testid="cart-bar"]').should('not.exist');

    // 第二層：選了套餐卻按返回，套餐就不算選上。
    cy.get('#item-combo-set').check();
    cy.get('dialog[open]').should('have.length', 2);
    cy.get('dialog[open] [aria-label="返回，不儲存"]').click();
    cy.get('dialog[open]').should('have.length', 1);
    cy.get('#item-combo-set').should('not.be.checked');

    // 第二層的必選也要選完才能確定。
    cy.get('#item-combo-set').check();
    cy.contains('dialog[open] button', '確定').click();
    cy.get('dialog[open]').should('have.length', 2);
    cy.focused().should('have.id', 'opt-l2-set-side');
    cy.get('#opt-l2-set-side-seaweed').check();
    cy.get('#opt-l2-set-drink-black-tea').check();
    cy.contains('dialog[open] button', '確定').click();
    cy.get('dialog[open]').should('have.length', 1);

    cy.get('#item-noodle-wide').check();
    cy.get('[data-testid="add-to-cart"]')
      .should('have.attr', 'aria-disabled', 'false')
      .and('contain', 'NT$210')
      .click();
    cy.get('dialog[open]').should('not.exist');
    cy.get('[data-testid="cart-bar"]').should('contain', 'NT$210');
  });

  it('伺服器說選項不符時，標出那一行並請顧客回購物車修改', () => {
    cy.intercept('POST', exact('/orders'), problem('invalid-lines-422.json', 422)).as('submit');
    cy.get('[data-item="zhajiang"]').click();
    cy.get('#item-spicy-mild').check();
    cy.get('[data-testid="add-to-cart"]').click();
    cy.visit(`${SLUG}/checkout`);
    fillContact('林先生', '0933-222-111');
    cy.get('[data-testid="submit-order"]').click();
    cy.wait('@submit');
    cy.get('[data-testid="submit-failure"]').should('contain', '選項和目前的菜單不符');
    cy.contains('a', '回購物車修改').click();
    cy.get('.line--warn').should('contain', '選項和目前的菜單不符，請重新選擇');
  });

  it('欄位錯誤顯示在欄位旁', () => {
    cy.intercept('POST', exact('/orders'), problem('invalid-fields-422.json', 422)).as('submit');
    cy.get('[data-item="wonton-soup"]').click();
    cy.get('[data-testid="add-to-cart"]').click();
    cy.visit(`${SLUG}/checkout`);
    cy.get('[data-testid="submit-order"]').click();
    // 前端先擋下空白的姓名與電話，不送出。
    cy.get('#checkout-name-error').should('contain', '請填寫姓名');
    cy.focused().should('have.id', 'checkout-name');
    fillContact('林先生', '0933-222-11');
    cy.get('[data-testid="submit-order"]').click();
    cy.wait('@submit');
    cy.get('#checkout-phone-error').should('contain', '請填寫台灣的手機或市話號碼');
    cy.get('#checkout-phone').should('have.attr', 'aria-invalid', 'true');
  });
});
