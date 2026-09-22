describe('admin-e2e', () => {
  beforeEach(() => cy.visit('/'));

  it('enters the AI assistant workspace from the public Demo path', () => {
    cy.location('pathname').should('eq', '/');
    cy.get('h1').should('be.visible').and('contain.text', '讓每一次服務回覆，都更有依據');
    cy.contains('a', '進入 Demo').click();
    cy.location('pathname').should('eq', '/login');
    cy.contains('button', 'SMB 管理者').click();
    cy.location('pathname').should('eq', '/app/home');
    cy.get('h1').should('be.visible').and('contain.text', 'AI 助理工作台');
  });
});
