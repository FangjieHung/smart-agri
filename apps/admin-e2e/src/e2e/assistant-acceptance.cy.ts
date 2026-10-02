import { loginAs } from '../support/a11y';

const ASSISTANT = 'assistant-customer-service';

describe('assistant acceptance in mock mode', () => {
  beforeEach(() => {
    cy.clearLocalStorage();
    loginAs('SMB 管理者');
  });

  it('creates cases, completes a run, and shows per-case failure and the assistant badge', () => {
    cy.visit(`/app/assistants/${ASSISTANT}/acceptance`);
    cy.contains('h4', '測試題組').should('be.visible');
    cy.contains('還沒有測試題').scrollIntoView().should('be.visible');

    cy.get('.case-form textarea').type('退貨期限是多久？');
    cy.get('.case-form button[type="submit"]').click();
    cy.contains('.case-card', '退貨期限是多久？').scrollIntoView().should('be.visible');

    cy.get('.case-form textarea').type('例外情況要怎麼處理？');
    cy.get('.case-form select').first().select('exception');
    cy.get('.case-form button[type="submit"]').click();
    cy.contains('.case-card', '例外情況要怎麼處理？').scrollIntoView().should('be.visible');

    cy.contains('button', '全部重跑').click();
    cy.contains('.run-row', '已完成', { timeout: 15000 }).should('contain', '失敗 1');
    cy.contains('.result-card', '例外情況要怎麼處理？').should('contain', '未通過');
    cy.get('.acceptance__summary h3').should('contain', '未通過');

    cy.get('a[href="/app/assistants"]').first().click();
    cy.contains('app-assistant-card', '客服助理').should('contain', '驗收未通過');

    cy.reload();
    cy.contains('app-assistant-card', '客服助理').should('contain', '驗收未通過');

    cy.contains('app-assistant-card', '客服助理').contains('查看設定').click();
    cy.contains('a', '驗收').click();
    cy.contains('.run-row', '已完成').click();
    cy.contains('.result-card', '例外情況要怎麼處理？')
      .contains('建立處理事項').click();
    cy.location('pathname').should('eq', '/app/issues');
    cy.contains('例外情況要怎麼處理？').should('be.visible');
  });
});
