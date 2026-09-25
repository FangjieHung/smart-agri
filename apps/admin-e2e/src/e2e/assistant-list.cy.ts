import { loginAs } from '../support/a11y';

describe('assistant list', () => {
  it('shows the signed-in account only its permitted assistants and opens the detail tabs', () => {
    loginAs('SMB 管理者');
    cy.contains('a', '我的助理').click();

    cy.location('pathname').should('eq', '/app/assistants');
    cy.contains('h1', '我的助理').should('be.visible');
    cy.contains('客服助理').should('be.visible');
    cy.contains('a', '查看設定').click();

    cy.location('pathname').should('eq', '/app/assistants/assistant-customer-service/overview');
    cy.contains('a', '資料來源').click();
    cy.location('pathname').should('eq', '/app/assistants/assistant-customer-service/data-sources');
    cy.get('app-source-connection-list').should('be.visible');
    cy.get('.source-summary').should('contain', '已連接 3 個知識庫、2 個資料庫');
  });

  it('keeps another account\'s assistant configuration out of the management list', () => {
    loginAs('內部使用者');
    cy.contains('a', '我的助理').click();

    cy.contains('尚未有可使用的助理').should('be.visible');
    cy.contains('客服助理').should('not.exist');
  });

  it('sends the create action to its dedicated wizard route', () => {
    loginAs('SMB 管理者');
    cy.contains('a', '建立新助理').first().click();

    cy.location('pathname').should('eq', '/app/assistants/new/purpose');
    cy.contains('h1', '建立新助理').should('be.visible');
  });
});
