import { auditA11y } from '../support/a11y';
import {
  chooseRecordedSlot,
  exact,
  fillContact,
  interceptShopV1,
  SLUG,
} from '../support/ordering-api';

describe('無障礙（axe：critical／serious 不得出現）', () => {
  beforeEach(() => interceptShopV1());

  it('菜單、選時間、商品選項（含第二層）、結帳、訂單頁', () => {
    cy.intercept('POST', exact('/orders'), { statusCode: 201, fixture: 'submit-201.json' });
    cy.intercept('GET', exact('/orders/current'), { fixture: 'order-pending.json' });

    cy.visit(SLUG);
    cy.get('dialog[open]').should('contain', '休息中，歡迎預訂');
    auditA11y();
    chooseRecordedSlot();
    cy.get('[data-item="beef-noodle"]').should('be.visible');
    auditA11y();

    cy.get('[data-item="beef-noodle"]').click();
    cy.get('[data-testid="add-to-cart"]').click();
    cy.get('.group--unmet').should('exist');
    auditA11y();
    cy.get('#item-size-regular').check();
    cy.get('#item-noodle-thin').check();
    cy.get('#item-combo-set').check();
    cy.get('dialog[open]').should('have.length', 2);
    auditA11y();
    cy.get('#opt-l2-set-side-cucumber').check();
    cy.get('#opt-l2-set-drink-black-tea').check();
    cy.contains('dialog[open] button', '確定').click();
    cy.get('[data-testid="add-to-cart"]').click();

    cy.get('[data-testid="cart-bar"]').click();
    cy.get('#page-title').should('have.text', '購物車');
    auditA11y();
    cy.get('[data-testid="go-checkout"]').click();
    cy.get('#page-title').should('have.text', '結帳');
    auditA11y();

    fillContact('陳小姐', '0912-345-678');
    cy.get('[data-testid="submit-order"]').click();
    cy.get('[data-testid="order-status"]').should('contain.text', '等待店家確認');
    auditA11y();
    cy.get('[data-testid="cancel-order"]').click();
    cy.get('dialog[open]').should('exist');
    auditA11y();
  });
});
