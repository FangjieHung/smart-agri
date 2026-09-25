import { submitDemoLogin } from '../support/a11y';

describe('demo navigation', () => {
  it('takes a visitor from the public landing through demo account selection into the workspace', () => {
    cy.visit('/');
    cy.location('pathname').should('eq', '/');
    cy.contains('h1', '讓每一次服務回覆，都更有依據').should('be.visible');
    cy.contains('a', '進入 Demo').click();

    cy.location('pathname').should('eq', '/login');
    cy.contains('固定示範帳號，沒有連接正式驗證服務').should('be.visible');
    submitDemoLogin('SMB 管理者');

    cy.location('pathname').should('eq', '/app/home');
    cy.contains('.app-sidenav', 'AI 助理工作台').should('be.visible');
    cy.contains('h1', '首頁').should('be.visible');
    cy.contains('nav', '我的助理').should('be.visible');
  });
});
