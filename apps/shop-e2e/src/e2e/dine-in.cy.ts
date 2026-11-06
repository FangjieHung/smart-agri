import { auditA11y } from '../support/a11y';
import { exact, problem, SLUG } from '../support/ordering-api';

/**
 * 內用掃碼（#402）。回應是 2026-11-07 用 #402 的 API 另外錄的一組（自己的暫存資料庫，號碼從 #001 起；店家匯入
 * deploy/ordering-shop.example.json，內用時間改成每天 00:00–23:59，所以半夜錄的時候能點內用；菜單同 menu-v1.json）。
 * 回應是回放的，網址 `#` 後面的桌號 token 用假的 43 字元字串即可（不要放真的 token）。
 */
const TABLE = 'FakeTable_Token-for-e2e-replay-0123456789ab';

/** 390 寬時整頁沒有橫向捲動。 */
function noSideScroll(): void {
  cy.document().then((doc) => {
    expect(doc.documentElement.scrollWidth).to.be.at.most(doc.documentElement.clientWidth);
  });
}

function interceptDineInShop(): void {
  cy.intercept('GET', exact(''), { fixture: 'dinein-shop.json' }).as('shop');
  cy.intercept('GET', exact('/menu'), { fixture: 'menu-v1.json' }).as('menu');
  cy.intercept('GET', exact('/slots'), (request) => {
    throw new Error(`桌號模式不該讀時段：${request.url}`);
  });
}

describe('內用掃碼：桌號網址、點餐、訂單頁、繼續加點', () => {
  beforeEach(() => {
    cy.viewport(390, 844);
    interceptDineInShop();
  });

  it('掃碼進菜單（不選時間）、送出兩筆，〔繼續加點〕後看到這桌已點的訂單', () => {
    expect(TABLE).to.have.length(43);
    cy.fixture('dinein-submit-201.json').then((first: { token: string }) => {
      cy.fixture('dinein-submit2-201.json').then((second: { token: string }) => {
        cy.intercept('GET', exact('/table'), { fixture: 'dinein-table.json' }).as('table');
        cy.intercept('POST', exact('/orders'), {
          statusCode: 201,
          fixture: 'dinein-submit-201.json',
        }).as('submit');
        cy.intercept('GET', exact('/orders/current'), { fixture: 'dinein-order-pending.json' }).as(
          'order',
        );

        cy.visit(`${SLUG}/table#${TABLE}`);
        // token 不留在網址列，換成菜單頁；只放在 X-Ordering-Table 標頭。
        cy.location('pathname').should('eq', `/order/${SLUG}`);
        cy.location('hash').should('eq', '');
        cy.wait('@table').then(({ request }) => {
          expect(request.headers['x-ordering-table']).to.eq(TABLE);
          expect(request.url).not.to.contain(TABLE);
        });
        cy.window().then((win) => {
          expect(win.sessionStorage.getItem(`smart-agri-shop:table:${SLUG}`)).to.eq(TABLE);
          expect(JSON.stringify({ ...win.localStorage })).not.to.contain(TABLE);
        });
        cy.get('[data-testid="table-name"]').should('have.text', '內用・桌號 A1');
        cy.get('dialog[open]').should('not.exist');
        cy.get('[data-testid="table-session"]').should('not.exist');
        cy.get('[data-testid="table-closed"]').should('not.exist');
        noSideScroll();
        auditA11y();

        cy.get('[data-item="beef-noodle"]').click();
        cy.get('#item-size-regular').check();
        cy.get('#item-noodle-thin').check();
        cy.get('[data-testid="add-to-cart"]').should('contain', 'NT$150').click();
        cy.get('[data-testid="cart-bar"]').click();

        cy.get('#page-title').should('have.text', '購物車');
        cy.get('[data-testid="table-name"]').should('have.text', '內用・桌號 A1');
        cy.get('input[name="tableware"]').should('not.exist');
        cy.get('#order-comment').type('不要香菜');
        cy.contains('.sum__note', '用餐後請至櫃台結帳，可用現金或 LINE Pay');
        noSideScroll();
        cy.get('[data-testid="go-checkout"]').should('contain', 'NT$150').click();

        cy.get('#page-title').should('have.text', '結帳');
        cy.get('label[for="checkout-name"]').should('contain', '稱呼').and('contain', '選填');
        cy.get('#checkout-phone').should('not.exist');
        cy.get('#checkout-email').should('not.exist');
        cy.contains('.payment', '用餐後請至櫃台結帳，可用現金或 LINE Pay');
        cy.contains('.privacy', '稱呼與備註');
        cy.get('#checkout-name').type('王先生');
        noSideScroll();
        auditA11y();
        cy.get('[data-testid="submit-order"]').click();

        cy.wait('@submit').then(({ request }) => {
          expect(request.headers['x-ordering-table']).to.eq(TABLE);
          expect(request.url).not.to.contain(TABLE);
          const body = request.body as Record<string, unknown>;
          expect(body).to.include({
            serviceType: 'dine-in',
            pickupDate: null,
            pickupSlot: null,
            menuVersion: 1,
            orderComment: '不要香菜',
            tableware: null,
          });
          expect(body['customer']).to.deep.equal({ name: '王先生', phone: null, email: null });
          expect(body['lines']).to.deep.equal([
            {
              itemId: 'beef-noodle',
              quantity: 1,
              comment: null,
              selections: [
                { groupId: 'size', optionId: 'regular', count: 1, selections: null },
                { groupId: 'noodle', optionId: 'thin', count: 1, selections: null },
              ],
            },
          ]);
          expect(JSON.stringify(body)).not.to.contain(TABLE);
        });

        cy.location('hash').should('eq', `#${first.token}`);
        cy.wait('@order').then(({ request }) => {
          expect(request.headers['x-ordering-table']).to.eq(undefined);
          expect(request.url).not.to.contain(first.token);
        });
        cy.get('[data-testid="order-number"]').should('contain.text', '#001');
        cy.get('[data-testid="order-status"]').should('contain.text', '等待店家確認');
        cy.get('[data-testid="order-table"]').should('have.text', '內用・桌號 A1');
        cy.get('[data-testid="order-pickup"]').should('not.exist');
        cy.get('[data-testid="order-payment"]').should(
          'contain.text',
          '用餐後請至櫃台結帳，可用現金或 LINE Pay',
        );
        cy.get('[data-testid="cancel-order"]').should('exist');
        noSideScroll();
        auditA11y();

        // 〔繼續加點〕：回到菜單，桌號還在，重讀這桌已點的訂單。
        cy.intercept('GET', exact('/table'), { fixture: 'dinein-table-session.json' }).as('table');
        cy.intercept('POST', exact('/orders'), {
          statusCode: 201,
          fixture: 'dinein-submit2-201.json',
        }).as('submit');
        cy.intercept('GET', exact('/orders/current'), {
          fixture: 'dinein-order2-pending.json',
        }).as('order');
        cy.get('[data-testid="order-more"]').should('have.text', '繼續加點').click();
        cy.location('pathname').should('eq', `/order/${SLUG}`);
        cy.wait('@table');
        cy.get('[data-testid="table-name"]').should('have.text', '內用・桌號 A1');
        cy.get('[data-testid="table-session"]').should('contain', '這桌已點 1 筆，共 NT$150');
        cy.get('[data-testid="table-session"] summary').click();
        cy.get('[data-testid="table-session"]')
          .should('contain', '#001')
          .and('contain', '等待店家確認')
          .and('contain', '紅燒牛肉麵');
        noSideScroll();
        auditA11y();

        cy.get('[data-item="wonton-soup"]').click();
        cy.get('[aria-label="增加份數"]').click();
        cy.get('[data-testid="add-to-cart"]').should('contain', 'NT$140').click();
        cy.get('[data-testid="cart-bar"]').click();
        cy.get('[data-testid="go-checkout"]').click();
        cy.get('#page-title').should('have.text', '結帳');
        cy.get('[data-testid="submit-order"]').click();
        cy.wait('@submit').then(({ request }) => {
          expect(request.headers['x-ordering-table']).to.eq(TABLE);
          const body = request.body as Record<string, unknown>;
          expect(body['customer']).to.deep.equal({ name: null, phone: null, email: null });
          expect(body['lines']).to.deep.equal([
            { itemId: 'wonton-soup', quantity: 2, comment: null, selections: null },
          ]);
        });
        cy.location('hash').should('eq', `#${second.token}`);
        cy.get('[data-testid="order-number"]').should('contain.text', '#002');
        cy.get('[data-testid="order-table"]').should('have.text', '內用・桌號 A1');

        cy.intercept('GET', exact('/table'), { fixture: 'dinein-table-session2.json' }).as('table');
        cy.get('[data-testid="order-more"]').click();
        cy.wait('@table');
        cy.get('[data-testid="table-session"]').should('contain', '這桌已點 2 筆，共 NT$290');
      });
    });
  });

  it('店家接單、已出餐、〔已結帳〕後，訂單頁的說法', () => {
    const status = `${SLUG}/o#${'S'.repeat(43)}`;
    cy.intercept('GET', exact('/orders/current'), { fixture: 'dinein-order-accepted.json' });
    cy.visit(status);
    cy.get('[data-testid="order-status"]').should('contain.text', '店家已接單，餐點準備中');
    cy.get('[aria-current="step"]').should('contain', '準備中');
    cy.get('[data-testid="order-table"]').should('have.text', '內用・桌號 A1');
    // 這個分頁沒有掃過桌號：沒有〔繼續加點〕。
    cy.get('[data-testid="order-more"]').should('not.exist');

    cy.intercept('GET', exact('/orders/current'), { fixture: 'dinein-order-ready.json' });
    cy.reload();
    cy.get('[data-testid="order-status"]').should('contain.text', '已出餐');

    cy.intercept('GET', exact('/orders/current'), { fixture: 'dinein-order-completed.json' });
    cy.reload();
    cy.get('[data-testid="order-status"]').should('contain.text', '已結帳，謝謝光臨');
    noSideScroll();
    auditA11y();
  });

  it('失效的桌號 QR code：「這個桌號的 QR code 已失效，請洽店員」', () => {
    cy.intercept('GET', exact('/table'), problem('dinein-table-not-found-404.json', 404)).as(
      'table',
    );
    cy.visit(`${SLUG}/table#${TABLE}`);
    cy.wait('@table');
    cy.get('[data-testid="table-invalid"] h1').should(
      'have.text',
      '這個桌號的 QR code 已失效，請洽店員',
    );
    cy.window().then((win) => {
      expect(win.sessionStorage.getItem(`smart-agri-shop:table:${SLUG}`)).to.eq(null);
    });
    noSideScroll();
    auditA11y();
  });

  it('送出時桌號才失效：同樣請顧客洽店員', () => {
    cy.intercept('GET', exact('/table'), { fixture: 'dinein-table.json' });
    cy.intercept(
      'POST',
      exact('/orders'),
      problem('dinein-submit-table-not-found-404.json', 404),
    ).as('submit');
    cy.visit(`${SLUG}/table#${TABLE}`);
    cy.get('[data-item="wonton-soup"]').click();
    cy.get('[data-testid="add-to-cart"]').click();
    cy.get('[data-testid="cart-bar"]').click();
    cy.get('[data-testid="go-checkout"]').click();
    cy.get('[data-testid="submit-order"]').click();
    cy.wait('@submit');
    cy.get('[data-testid="table-invalid"]').should(
      'contain',
      '這個桌號的 QR code 已失效，請洽店員',
    );
  });
});
