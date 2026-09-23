function loginAs(persona: string): void {
  cy.visit('/login');
  cy.contains('button', persona).click();
  cy.location('pathname').should('eq', '/app/home');
}

function fillOrderForm(): void {
  loginAs('外部客戶');
  cy.visit('/use/assistant-customer-service');
  cy.contains('button.suggested-prompt', '回報訂單問題').click();
  cy.get('[role="log"] [data-kind="form-request"]').contains('button', '填寫表單').click();

  cy.contains('button', '下一步：確認同意').click();
  cy.get('app-inline-form [role="alert"]').should('contain', '「訂單編號」為必填。');
  cy.get('#chat-field-field-order-number').should('have.attr', 'aria-invalid', 'true').type('DEMO-2001');
  cy.contains('app-inline-form label', '配送延遲').click();
  cy.get('#chat-field-field-reported-on').type('2026-09-21');
  cy.contains('button', '下一步：確認同意').click();
}

/** 已在對話頁時：填完表單、同意並送出一筆紀錄。 */
function submitOrder(orderNumber: string): void {
  cy.contains('button.suggested-prompt', '回報訂單問題').click();
  cy.get('[role="log"] [data-kind="form-request"]').last().contains('button', '填寫表單').click();
  cy.get('#chat-field-field-order-number').type(orderNumber);
  cy.contains('app-inline-form label', '配送延遲').click();
  cy.get('#chat-field-field-reported-on').type('2026-09-21');
  cy.contains('button', '下一步：確認同意').click();
  cy.get('#consent-agree').check();
  cy.contains('button', '同意並送出').click();
  cy.get('[role="log"] [data-kind="submission-receipt"]').last().should('contain', orderNumber);
}

describe('consented structured submission', () => {
  beforeEach(() => {
    cy.clearLocalStorage();
  });

  it('cannot submit before consenting and explains recipient, purpose, viewers and sensitive data', () => {
    fillOrderForm();

    cy.get('app-consent-confirmation').within(() => {
      cy.contains('dt', '接收單位').next('dd').should('contain', '安心商行');
      cy.contains('dt', '收集目的').next('dd').should('contain', '收集訂單問題回報');
      cy.contains('dt', '可查看者').next('dd').should('contain', '安心商行管理者');
      cy.get('.sensitive-notice').should('contain', '敏感');
      cy.root().should('contain', '撤回').and('contain', 'DEMO-2001');

      cy.contains('button', '同意並送出').should('be.disabled');
      cy.get('#consent-agree').check();
      cy.contains('button', '同意並送出').should('not.be.disabled').click();
    });

    cy.get('app-consent-confirmation').should('not.exist');
    cy.get('[role="log"] [data-kind="submission-receipt"]').should('contain', '已送出').and('contain', '安心商行');
  });

  it('shows the consented record only to the designated data manager', () => {
    fillOrderForm();
    cy.get('#consent-agree').check();
    cy.contains('button', '同意並送出').click();
    cy.get('[data-kind="submission-receipt"]').should('be.visible');

    loginAs('內部使用者');
    cy.visit('/app/databases/database-orders/records');
    cy.contains('無法查看這個資料庫').should('be.visible');
    cy.contains('DEMO-2001').should('not.exist');

    loginAs('SMB 管理者');
    cy.visit('/app/databases/database-orders/records');
    cy.get('#subject-select').find('option:selected').should('contain', '外部客戶（1 筆）');
    cy.get('ol.timeline > li')
      .should('have.length', 1)
      .first()
      .should('contain', 'DEMO-2001')
      .and('contain', '配送延遲')
      .and('contain', '助理對話');

    cy.visit('/use/assistant-customer-service');
    cy.contains('h1', '客服助理').should('be.visible');
    cy.contains('DEMO-2001').should('not.exist');
  });

  it('lets the submitter withdraw a record, which leaves the records and the trend', () => {
    loginAs('外部客戶');
    cy.visit('/use/assistant-customer-service');
    submitOrder('DEMO-3001');
    submitOrder('DEMO-3002');

    loginAs('SMB 管理者');
    cy.visit('/app/databases/database-orders/trends');
    cy.get('.trend-conclusion').should('contain', '2 筆');
    cy.get('.insufficient-records').should('not.exist');

    loginAs('外部客戶');
    cy.visit('/use/assistant-customer-service');
    cy.get('[role="log"] [data-kind="submission-receipt"]')
      .first()
      .within(() => {
        cy.get('.withdrawal-notice').should('contain', '撤回');
        cy.contains('button', '撤回這筆資料').click();
      });

    cy.get('[role="dialog"]')
      .should('have.attr', 'aria-modal', 'true')
      .and('contain', '無法復原');
    cy.focused().should('have.class', 'confirm-cancel');
    cy.contains('button.confirm-withdraw', '撤回').click();

    cy.get('[role="dialog"]').should('not.exist');
    cy.get('.withdraw-feedback').should('contain', '已撤回');
    // 撤回過的收據不再提供第二次撤回。
    cy.get('[role="log"] [data-kind="submission-receipt"]').first().within(() => {
      cy.get('.withdrawal-notice').should('contain', '已撤回');
      cy.contains('button', '撤回這筆資料').should('not.exist');
    });

    loginAs('SMB 管理者');
    cy.visit('/app/databases/database-orders/records');
    cy.get('ol.timeline > li').should('have.length', 1).and('contain', 'DEMO-3002');
    cy.contains('DEMO-3001').should('not.exist');
    cy.get('.withdrawn-list > li').should('have.length', 1).and('contain', '內容已移除');

    cy.contains('nav.tabs a', '趨勢比較').click();
    cy.get('.insufficient-records').should('contain', '累積 2 筆以上');
    cy.get('.trend-conclusion').should('not.exist');
  });

  it('gives the data manager no way to withdraw someone else’s record', () => {
    loginAs('外部客戶');
    cy.visit('/use/assistant-customer-service');
    submitOrder('DEMO-3003');

    loginAs('SMB 管理者');
    cy.visit('/app/databases/database-orders/records');
    cy.get('ol.timeline > li').should('have.length', 1).and('contain', 'DEMO-3003');
    cy.contains('button', '撤回').should('not.exist');
    cy.get('.subject-picker').should('contain', '撤回同意的紀錄');
  });
});
