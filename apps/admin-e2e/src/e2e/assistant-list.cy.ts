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

    // 沒有「manage-assistants」權限：自己名下沒有任何助理，只有組織分享給你「使用」的那份。
    cy.get('section[aria-labelledby="my-assistants-title"]').should('contain', '你還沒有建立助理。');
    cy.get('section[aria-labelledby="my-assistants-title"]').should('not.contain', '客服助理');

    // 分享來的助理只能「開始使用」，看不到擁有者才有的「查看設定」管理入口。
    cy.get('section[aria-labelledby="company-assistants-title"]').within(() => {
      cy.contains('客服助理').should('be.visible');
      cy.contains('a', '開始使用').should('be.visible');
      cy.contains('a', '查看設定').should('not.exist');
    });
  });

  it('sends the create action to its own newly created draft', () => {
    loginAs('SMB 管理者');
    cy.contains('a', '建立新助理').first().click();

    // 每次從「建立新助理」進來都會產生一份獨立草稿（newAssistantDraftGuard），
    // 所以落地網址是 /app/assistants/drafts/<draftId>/purpose，不是固定的 /new/purpose。
    cy.location('pathname').should('match', /^\/app\/assistants\/drafts\/draft-\d+\/purpose$/);
    cy.contains('h1', '建立新助理').should('be.visible');
  });
});
