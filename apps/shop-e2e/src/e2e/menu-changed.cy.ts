import { chooseRecordedSlot, exact, fillContact, problem, SLUG } from '../support/ordering-api';

describe('送出時菜單已經改版', () => {
  it('重新讀菜單、移除下架的品項、以新價格重算，顧客確認後用同一個送出鍵再送', () => {
    let version = 1;
    cy.intercept('GET', exact(''), (req) =>
      req.reply({ fixture: version === 1 ? 'shop-v1.json' : 'shop-v2.json' }),
    );
    cy.intercept('GET', exact('/menu'), (req) =>
      req.reply({ fixture: version === 1 ? 'menu-v1.json' : 'menu-v2.json' }),
    );
    cy.intercept('GET', exact('/slots'), { fixture: 'slots.json' });
    cy.intercept('POST', exact('/orders'), (req) => {
      if (version === 1) {
        version = 2;
        req.reply(problem('menu-changed-409.json', 409));
      } else {
        req.reply({ statusCode: 201, fixture: 'submit-v2-201.json' });
      }
    }).as('submit');
    cy.intercept('GET', exact('/orders/current'), { fixture: 'order-v2-pending.json' }).as('order');

    cy.visit(SLUG);
    chooseRecordedSlot();
    cy.get('[data-item="beef-noodle"]').click();
    cy.get('#item-size-regular').check();
    cy.get('#item-noodle-wide').check();
    cy.get('[data-testid="add-to-cart"]').click();
    cy.get('[data-item="wonton-soup"]').click();
    cy.get('[aria-label="增加份數"]').click();
    cy.get('[data-testid="add-to-cart"]').should('contain', 'NT$140').click();
    cy.get('[data-testid="cart-bar"]').should('contain', '3 份').and('contain', 'NT$290').click();
    cy.get('[data-testid="go-checkout"]').click();
    fillContact('林先生', '0933-222-111');
    cy.get('[data-testid="submit-order"]').click();

    cy.wait('@submit').its('request.body.menuVersion').should('eq', 1);
    cy.get('[data-testid="submit-failure"]').should('contain', '菜單剛剛更新了');
    cy.contains('.callout', '鮮肉餛飩湯').should('contain', '先移除了');
    cy.get('[data-testid="checkout-total"]').should('have.text', 'NT$160');
    cy.get('.summary__line').should('have.length', 1);

    cy.get('@submit.all').then((calls) => {
      const firstKey = (calls[0] as unknown as { request: { body: { submissionKey: string } } })
        .request.body.submissionKey;
      cy.get('[data-testid="submit-order"]').click();
      cy.wait('@submit').then(({ request }) => {
        expect(request.body.menuVersion).to.eq(2);
        expect(request.body.lines).to.have.length(1);
        expect(request.body.submissionKey).to.eq(firstKey);
      });
    });
    cy.location('pathname').should('eq', `/order/${SLUG}/o`);
    cy.get('[data-testid="order-number"]').should('contain.text', '#002');
  });
});
