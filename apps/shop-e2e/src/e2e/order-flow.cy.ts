import {
  chooseRecordedSlot,
  exact,
  fillContact,
  interceptShopV1,
  problem,
  SLUG,
} from '../support/ordering-api';

describe('外帶預約：點餐、送出、訂單頁、取消', () => {
  beforeEach(() => interceptShopV1());

  it('走完一次下單，訂單頁只用標頭帶 token，並可以取消', () => {
    cy.fixture('submit-201.json').then((submitted: { token: string }) => {
      const token = submitted.token;
      cy.intercept('POST', exact('/orders'), { statusCode: 201, fixture: 'submit-201.json' }).as(
        'submit',
      );
      cy.intercept('GET', exact('/orders/current'), { fixture: 'order-pending.json' }).as('order');
      cy.intercept('POST', exact('/orders/current/cancel'), { fixture: 'cancel-200.json' }).as(
        'cancel',
      );

      cy.visit(SLUG);
      chooseRecordedSlot();

      cy.get('[data-item="beef-noodle"]').click();
      cy.get('#item-size-large').check();
      cy.get('#item-noodle-thin').check();
      cy.get('[aria-label="增加滷蛋"]').click().click();
      cy.get('#item-combo-set').check();
      cy.get('dialog[open]').should('have.length', 2);
      cy.get('#opt-l2-set-side-cucumber').check();
      cy.get('#opt-l2-set-drink-winter-melon').check();
      cy.contains('dialog[open] button', '確定').click();
      cy.get('.opt__sub').should('contain', '涼拌小黃瓜、冬瓜檸檬');
      cy.get('#item-comment').type('麵硬一點');
      cy.get('[data-testid="add-to-cart"]').should('contain', 'NT$270').click();

      cy.get('[data-item="beef-noodle"]').should('contain', '已加 1 份');
      cy.get('[data-testid="cart-bar"]').should('contain', '1 份').and('contain', 'NT$270').click();
      cy.location('pathname').should('eq', `/order/${SLUG}/cart`);
      cy.get('#order-comment').type('會準時到');
      cy.get('[data-testid="go-checkout"]').click();

      cy.location('pathname').should('eq', `/order/${SLUG}/checkout`);
      cy.get('[data-testid="checkout-total"]').should('have.text', 'NT$270');
      fillContact('陳小姐', '0912-345-678', 'chen@example.com');
      cy.get('[data-testid="submit-order"]').click();

      cy.wait('@submit').then(({ request }) => {
        const body = request.body as Record<string, unknown>;
        expect(body).to.include({
          serviceType: 'takeout-scheduled',
          pickupDate: '2026-11-07',
          pickupSlot: '2026-11-07T12:30',
          menuVersion: 1,
          orderComment: '會準時到',
          tableware: false,
        });
        expect(body['customer']).to.deep.equal({
          name: '陳小姐',
          phone: '0912-345-678',
          email: 'chen@example.com',
        });
        expect(body['lines']).to.deep.equal([
          {
            itemId: 'beef-noodle',
            quantity: 1,
            comment: '麵硬一點',
            selections: [
              { groupId: 'size', optionId: 'large', count: 1, selections: null },
              { groupId: 'noodle', optionId: 'thin', count: 1, selections: null },
              { groupId: 'toppings', optionId: 'egg', count: 2, selections: null },
              {
                groupId: 'combo',
                optionId: 'set',
                count: 1,
                selections: [
                  { groupId: 'side', optionId: 'cucumber', count: 1, selections: null },
                  { groupId: 'drink', optionId: 'winter-melon', count: 1, selections: null },
                ],
              },
            ],
          },
        ]);
        expect(String(body['submissionKey'])).to.match(
          /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/,
        );
        expect(JSON.stringify(body)).not.to.match(/price|total/i);
      });

      cy.location('pathname').should('eq', `/order/${SLUG}/o`);
      cy.location('hash').should('eq', `#${token}`);
      cy.wait('@order').then(({ request }) => {
        expect(request.headers['x-ordering-token']).to.eq(token);
        expect(request.url).not.to.contain(token);
      });
      cy.get('[data-testid="order-number"]').should('contain.text', '#001');
      cy.get('[data-testid="order-status"]').should('contain.text', '等待店家確認');
      cy.get('[data-testid="order-pickup"]').should('contain', '明天 12:30');

      cy.get('[data-testid="cancel-order"]').click();
      cy.get('dialog[open]').should('contain', '要取消訂單 #001 嗎？');
      cy.get('[data-testid="confirm-cancel"]').click();
      cy.wait('@cancel').then(({ request }) => {
        expect(request.headers['x-ordering-token']).to.eq(token);
        expect(request.url).not.to.contain(token);
      });
      cy.get('[data-testid="order-status"]').should('contain.text', '訂單已取消');
      cy.get('[data-testid="cancel-order"]').should('not.exist');

      // 購物車清空了；回到菜單頁時，取消的訂單不再出現在橫幅。
      cy.contains('a', '回到菜單').click();
      cy.get('[data-testid="cart-bar"]').should('not.exist');
      cy.get('[data-testid="active-order"]').should('not.exist');
    });
  });

  it('找不到店家時說明怎麼辦', () => {
    cy.intercept(
      'GET',
      /\/api\/v1\/public\/ordering\/no-such-shop$/,
      problem('shop-not-found-404.json', 404),
    );
    cy.visit('no-such-shop');
    cy.get('[data-testid="shop-not-found"]').should('contain', '找不到這家店的點餐頁面');
  });

  it('沒有 token 或 token 不對時，訂單頁給出下一步', () => {
    cy.intercept('GET', exact('/orders/current'), problem('order-not-found-404.json', 404)).as(
      'order',
    );
    cy.visit(`${SLUG}/o#${'A'.repeat(43)}`);
    cy.wait('@order');
    cy.get('[data-testid="order-not-found"]').should('contain', '找不到這筆訂單');
    cy.visit(`${SLUG}/o`);
    cy.get('[data-testid="order-not-found"]').should('be.visible');
  });
});
