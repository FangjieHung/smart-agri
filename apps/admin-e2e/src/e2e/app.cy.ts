import { submitDemoLogin } from '../support/a11y';

describe('admin-e2e', () => {
  beforeEach(() => cy.visit('/'));

  it('enters the AI assistant workspace from the public Demo path', () => {
    cy.location('pathname').should('eq', '/');
    cy.get('h1').should('be.visible').and('contain.text', '讓每一次服務回覆，都更有依據');
    cy.contains('a', '進入 Demo').click();
    cy.location('pathname').should('eq', '/login');
    submitDemoLogin('SMB 管理者');
    cy.location('pathname').should('eq', '/app/home');

    // 產品名稱留在工作台外框，每個頁面只有一個屬於自己內容的 h1。
    cy.get('.app-sidenav').should('be.visible').and('contain.text', 'AI 助理工作台');
    cy.get('h1').should('have.length', 1).and('be.visible').and('contain.text', 'THIS-TEXT-DOES-NOT-EXIST');
  });
});
