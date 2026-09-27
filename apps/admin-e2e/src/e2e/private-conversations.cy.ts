import { loginAs } from '../support/a11y';

function ask(question: string): void {
  cy.get('#chat-input').clear().type(question);
  cy.get('form.composer button[type="submit"]').click();
}

describe('private conversations and trustworthy answers', () => {
  beforeEach(() => {
    cy.clearLocalStorage();
  });

  it('separates company data with citations, general knowledge and no-result answers on a phone', () => {
    cy.viewport(390, 844);
    loginAs('外部客戶');
    // 沒有 manage-assistants 權限：首頁不提供「建立新助理」入口（issue #54）。
    cy.get('a[href="/app/assistants/new/purpose"]').should('not.exist');
    cy.get('section[aria-labelledby="usable-title"]').contains('a', '客服助理').click();
    cy.location('pathname').should('eq', '/use/assistant-customer-service');
    cy.contains('h1', '客服助理').should('be.visible');
    cy.get('.privacy-notice').should('contain', '助理建立者');
    cy.document().its('documentElement.scrollWidth').should('be.lte', 390);

    ask('收到商品後幾天內可以退貨？');
    cy.get('[role="log"] [data-kind="company-data"]')
      .should('contain', '根據你的資料')
      .and('contain', '7 天');
    cy.contains('button', '查看引用來源').click();
    cy.get('[role="dialog"]')
      .should('be.visible')
      .and('have.attr', 'aria-modal', 'true')
      .and('contain', '退換貨辦法 2026 版.pdf');
    cy.focused().should('have.class', 'drawer-close');
    cy.focused().type('{esc}');
    cy.get('[role="dialog"]').should('not.exist');
    cy.focused().should('contain', '查看引用來源');

    cy.contains('button.suggested-prompt', '皮革商品平常要怎麼保養？').click();
    cy.get('[role="log"] [data-kind="general-knowledge"]')
      .should('contain', '一般知識補充')
      .and('contain', '不是組織資料');

    ask('可以幫我訂下週的機票嗎？');
    cy.get('[role="log"] [data-kind="no-result"]')
      .should('contain', '查無資料')
      .find('ul.next-steps li')
      .should('have.length.greaterThan', 0);
  });

  it('keeps a conversation private from other accounts and from the assistant owner', () => {
    loginAs('外部客戶');
    cy.visit('/use/assistant-customer-service');
    ask('我的私人問題：退貨要幾天？');
    cy.get('[role="log"]').should('contain', '我的私人問題');

    loginAs('內部使用者');
    cy.visit('/use/assistant-customer-service');
    cy.contains('h1', '客服助理').should('be.visible');
    cy.get('[role="log"]').should('not.contain', '我的私人問題');

    loginAs('SMB 管理者');
    cy.visit('/use/assistant-customer-service');
    cy.contains('h1', '客服助理').should('be.visible');
    cy.get('[role="log"]').should('not.contain', '我的私人問題');

    cy.visit('/app/assistants/assistant-customer-service/activity');
    cy.get('.usage-summary').should('contain', '對話次數').and('contain', '19');
    cy.contains('看不到使用者的對話內容').should('be.visible');
    cy.contains('我的私人問題').should('not.exist');
  });

  it('does not reveal an assistant the account cannot use', () => {
    loginAs('外部客戶');
    cy.visit('/use/assistant-internal-onboarding');
    cy.contains('無法使用這個助理').should('be.visible');
    cy.contains('內部教育訓練助理').should('not.exist');
    cy.get('#chat-input').should('not.exist');
  });
});
