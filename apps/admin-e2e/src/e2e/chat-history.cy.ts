import { auditA11y, loginAs } from '../support/a11y';

const ASSISTANT = 'assistant-customer-service';

/** 規則關閉「保存自己的對話」的助理；直接寫進 Demo 的 localStorage，不必跑一次建立精靈。 */
const EPHEMERAL_ASSISTANT = {
  id: 'assistant-created-1',
  ownerAccountId: 'account-smb-admin',
  name: '不留紀錄助理',
  purpose: '示範關閉保存對話',
  status: 'ready',
  audience: 'members-and-external-customers',
  sharedWithAccountIds: [],
  knowledgeBaseIds: ['knowledge-refund-policy'],
  databaseIds: [],
  keepOwnConversations: false,
};

function ask(question: string): void {
  cy.get('#chat-input').clear().type(question);
  cy.get('form.composer button[type="submit"]').click();
}

describe('chat history sidebar', () => {
  beforeEach(() => {
    cy.clearLocalStorage();
  });

  it('reaches the workspace chat from the side navigation and picks an assistant', () => {
    loginAs('SMB 管理者');
    cy.contains('.app-sidenav a', '和助理對話').click();
    cy.location('pathname').should('eq', '/app/chat');
    cy.get('.assistant-picker').contains('a', '客服助理').click();
    cy.location('pathname').should('eq', `/app/chat/${ASSISTANT}`);
    cy.contains('h1', '客服助理').should('be.visible');
    cy.get('app-conversation-rail').should('be.visible');
    auditA11y();
  });

  it('keeps several conversations, switches between them, renames and deletes one', () => {
    loginAs('外部客戶');
    cy.visit(`/app/chat/${ASSISTANT}`);
    cy.get('.rail-empty').should('be.visible');

    ask('收到商品後幾天內可以退貨？');
    cy.get('[role="log"] [data-kind="company-data"]').should('be.visible');
    cy.get('ul.thread-list > li').should('have.length', 1);
    cy.get('button.thread-open').should('contain', '退貨').and('have.attr', 'aria-current', 'true');
    auditA11y();

    cy.contains('button.new-conversation', '開新對話').click();
    cy.location('pathname').should('match', new RegExp(`^/app/chat/${ASSISTANT}/chat-thread-\\d+$`));
    cy.get('[role="log"] app-chat-message').should('have.length', 0);
    cy.get('ul.thread-list > li').should('have.length', 2);

    ask('皮革商品平常要怎麼保養？');
    cy.get('[role="log"] [data-kind="general-knowledge"]').should('be.visible');

    // 切回第一段對話：內容與標題都還在，切換會透過 live region 宣告。
    cy.contains('button.thread-open', '退貨').click();
    cy.get('[role="log"]').should('contain', '7 天').and('not.contain', '保養油');
    cy.get('[role="status"]').should('contain', '退貨');

    cy.contains('ul.thread-list > li', '退貨').find('button.thread-rename').click();
    cy.get('input.rename-input').clear().type('退貨與退款{enter}');
    cy.get('ul.thread-list').should('contain', '退貨與退款');

    cy.contains('ul.thread-list > li', '退貨與退款').find('button.thread-delete').click();
    cy.get('[role="dialog"]').should('have.attr', 'aria-modal', 'true').and('contain', '退貨與退款');
    cy.focused().should('have.class', 'confirm-cancel');
    auditA11y();
    cy.get('button.confirm-delete').click();
    cy.get('[role="dialog"]').should('not.exist');
    cy.get('ul.thread-list > li').should('have.length', 1);
    cy.get('ul.thread-list').should('not.contain', '退貨與退款');
  });

  it('never shows another account’s conversations in the rail', () => {
    loginAs('外部客戶');
    cy.visit(`/app/chat/${ASSISTANT}`);
    ask('我的私人問題：退貨要幾天？');
    cy.get('ul.thread-list > li').should('have.length', 1);

    loginAs('SMB 管理者');
    cy.visit(`/app/chat/${ASSISTANT}`);
    cy.get('.rail-empty').should('be.visible');
    cy.contains('我的私人問題').should('not.exist');
  });

  it('explains the missing history when the assistant does not save conversations', () => {
    loginAs('外部客戶');
    cy.window().then((win) =>
      win.localStorage.setItem('sme-demo:created-assistants', JSON.stringify([EPHEMERAL_ASSISTANT])),
    );
    cy.visit('/app/chat/assistant-created-1');

    cy.get('[data-state="history-off"]').should('contain', '不保存對話紀錄');
    cy.get('button.new-conversation').should('not.exist');
    cy.get('ul.thread-list').should('not.exist');
    auditA11y();

    ask('收到商品後幾天內可以退貨？');
    cy.get('[role="log"] app-chat-message').should('have.length', 2);
    cy.get('ul.thread-list').should('not.exist');

    // 重新整理就沒了：這段對話不會被保存。
    cy.reload();
    cy.get('[role="log"] app-chat-message').should('have.length', 0);
  });

  it('keeps /use single column and strips the page chrome when embedded', () => {
    loginAs('外部客戶');
    cy.visit(`/use/${ASSISTANT}`);
    cy.get('app-conversation-rail').should('not.exist');
    cy.get('.chat-header').should('be.visible');
    cy.contains('a', '返回首頁').should('be.visible');

    cy.visit(`/use/${ASSISTANT}?embed=1`);
    cy.get('.chat-header').should('not.exist');
    cy.contains('a', '返回首頁').should('not.exist');
    cy.get('app-conversation-rail').should('not.exist');
    cy.get('#chat-input').should('be.visible');
    ask('收到商品後幾天內可以退貨？');
    cy.get('[role="log"] [data-kind="company-data"]').should('be.visible');
    auditA11y();
  });

  it('collapses the rail into a sheet on a phone', () => {
    cy.viewport(390, 844);
    loginAs('外部客戶');
    cy.visit(`/app/chat/${ASSISTANT}`);
    ask('收到商品後幾天內可以退貨？');

    cy.get('#conversation-rail').should('not.be.visible');
    cy.get('button.rail-toggle').should('have.attr', 'aria-expanded', 'false').click();
    cy.get('#conversation-rail').should('be.visible');
    cy.get('button.thread-open').should('contain', '退貨');
    cy.document().its('documentElement.scrollWidth').should('be.lte', 390);
    auditA11y();
  });
});
