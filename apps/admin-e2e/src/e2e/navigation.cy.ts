describe('demo navigation', () => {
  it('takes a visitor from the public landing through demo account selection into the workspace', () => {
    cy.visit('/');
    cy.location('pathname').should('eq', '/');
    cy.contains('h1', '讓每一次服務回覆，都更有依據').should('be.visible');
    cy.contains('a', '進入 Demo').click();

    cy.location('pathname').should('eq', '/login');
    cy.contains('Demo 帳號切換，不是真實驗證').should('be.visible');
    cy.contains('button', 'SMB 管理者').click();

    cy.location('pathname').should('eq', '/app/home');
    cy.contains('h1', 'AI 助理工作台').should('be.visible');
    cy.contains('nav', '我的助理').should('be.visible');
  });
});
