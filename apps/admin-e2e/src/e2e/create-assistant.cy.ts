import { loginAs } from '../support/a11y';

function loginAsAdmin(): void {
  loginAs('SMB 管理者');
  cy.location('pathname').should('eq', '/app/home');
}

function connectSource(name: string): void {
  cy.contains('.source-row', name).within(() => {
    cy.contains('button', '加入').click();
    cy.get('button').should('have.attr', 'aria-pressed', 'true');
  });
}

/**
 * newAssistantDraftGuard 讓每次從「開始建立」進來都產生一份獨立草稿並轉址到
 * /app/assistants/drafts/<draftId>/<step>；不再有固定的 /app/assistants/new/<step> 網址。
 */
function draftStepPath(step: string): RegExp {
  return new RegExp(`^/app/assistants/drafts/draft-\\d+/${step}$`);
}

describe('create assistant wizard', () => {
  beforeEach(() => {
    cy.clearLocalStorage();
  });

  it('creates a customer-question assistant with two knowledge bases and two databases', () => {
    loginAsAdmin();
    cy.contains('a', '開始建立').click();
    cy.location('pathname').should('match', draftStepPath('purpose'));

    cy.contains('label', '回答客戶問題').click();
    cy.get('#assistant-name').should('have.value', '客戶問答助理');
    cy.get('details.advanced').should('not.have.attr', 'open');
    cy.get('#audience-internal').check();
    cy.get('#audience-external').check();
    cy.get('.autosave').should('contain', '已自動儲存');
    cy.contains('button', '下一步').click();

    cy.location('pathname').should('match', draftStepPath('sources'));
    cy.get('[aria-current="step"]').should('contain', '資料來源');
    connectSource('商品使用指南');
    connectSource('退換貨政策');
    connectSource('訂單資料庫');
    connectSource('客戶資料庫');
    cy.get('.source-summary').should('contain', '2 個知識庫、2 個資料庫');
    cy.contains('button', '下一步').click();

    cy.location('pathname').should('match', draftStepPath('rules'));
    cy.get('#scope-strict').should('be.checked');
    cy.contains('button', '下一步').click();

    cy.location('pathname').should('match', draftStepPath('test'));
    cy.contains('button', '建立助理').click();
    cy.contains('請至少試問一題').should('be.visible');
    cy.contains('.trial-question', '退貨').click();
    cy.get('.trial-answer').should('contain', '根據你的資料').and('contain', '退換貨政策');
    cy.contains('button', '建立助理').click();

    cy.location('pathname').should('match', /^\/app\/assistants\/assistant-created-\d+\/overview$/);
    cy.contains('h1', '客戶問答助理').should('be.visible');
    cy.contains('助理已建立').should('be.visible');

    cy.contains('a', '返回我的助理').click();
    cy.contains('客戶問答助理').should('be.visible');
  });

  it('resumes the mock draft after a page reload', () => {
    loginAsAdmin();
    cy.contains('a', '開始建立').click();
    cy.contains('label', '回答客戶問題').click();
    cy.get('#audience-internal').check();
    cy.contains('button', '下一步').click();
    cy.location('pathname').should('match', draftStepPath('sources'));
    connectSource('商品使用指南');
    cy.get('.autosave').should('contain', '已自動儲存');

    cy.reload();

    // Demo 身分保存在這個瀏覽器分頁，重新整理後留在原本的步驟。
    cy.location('pathname').should('match', draftStepPath('sources'));
    cy.get('.autosave').should('contain', '已載入先前的草稿');
    cy.contains('.source-row', '商品使用指南')
      .find('button')
      .should('have.attr', 'aria-pressed', 'true');
    cy.contains('button', '上一步').click();
    cy.get('#assistant-name').should('have.value', '客戶問答助理');
    cy.get('#audience-internal').should('be.checked');

    // 從首頁的「繼續最近的設定」也可以回到同一份草稿（連結文字已從「繼續未完成的設定」改名）。
    cy.visit('/app/home');
    cy.contains('a', '繼續最近的設定').click();
    cy.location('pathname').should('match', draftStepPath('purpose'));
    cy.get('#assistant-name').should('have.value', '客戶問答助理');
  });

  it('does not show one account\'s draft to another account', () => {
    loginAsAdmin();
    cy.contains('a', '開始建立').click();
    cy.contains('label', '回答客戶問題').click();

    loginAs('內部使用者');
    cy.contains('a', '繼續最近的設定').should('not.exist');
    // 沒有 manage-assistants 的帳號在首頁看不到建立入口（#54）；直接開網址時，
    // newAssistantDraftGuard 仍會轉址回我的助理列表。
    cy.contains('a', '開始建立').should('not.exist');
    cy.visit('/app/assistants/new/purpose');
    cy.location('pathname').should('eq', '/app/assistants');
    cy.contains('客戶問答助理').should('not.exist');
    cy.get('#assistant-name').should('not.exist');
  });
});
