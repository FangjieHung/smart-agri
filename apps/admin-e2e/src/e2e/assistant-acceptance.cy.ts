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

    // 另開案件（issue #252）：預先帶入標題與問題副本，處理事項以「非助理問題」結案，兩邊互相連結。
    cy.get('[data-issue-open-case]').scrollIntoView().click();
    cy.get('[role="dialog"]').should('be.visible').and('contain', '非助理問題');
    cy.get('#issue-case-title').should('have.value', '例外情況要怎麼處理？');
    cy.get('#issue-case-description').should('have.value', '例外情況要怎麼處理？');
    cy.get('#issue-case-type').select('設備故障報修');
    cy.get('#issue-case-group').find('option:selected').should('have.text', '設備組');
    cy.get('.confirm-open-case').click();
    cy.get('[role="dialog"]').should('not.exist');
    cy.get('[data-issue-case-notice]').should('contain', '非助理問題');
    cy.get('[data-issue-resolution-kind]').should('contain', '非助理問題');
    cy.get('[data-issue-open-case]').should('not.exist');
    cy.get('[data-issue-case-notice] a').click();
    cy.location('pathname').should('eq', '/app/cases');
    cy.contains('[data-case-detail]', '例外情況要怎麼處理？').should('be.visible');
    cy.get('[data-case-link="issue"] a').scrollIntoView().click();
    cy.location('pathname').should('eq', '/app/issues');
    cy.get('[data-issue-linked-case] a').should('have.attr', 'href').and('include', '/app/cases?case=');
  });
});
