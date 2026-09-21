describe('assistant-demo-e2e', () => {
  beforeEach(() => cy.visit('/'));

  it('loads the AI assistant workspace homepage', () => {
    cy.location('pathname').should('eq', '/');
    cy.get('h1').should('be.visible').and('contain.text', 'AI 助理工作台');
  });
});
