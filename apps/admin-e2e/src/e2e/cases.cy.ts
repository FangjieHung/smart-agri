import { loginAs } from '../support/a11y';

/**
 * 案件頁（issue #248，mock 模式）：內部同仁從側欄進入、只看得到自己能看的案件、建立案件
 * （選類型帶入承辦組與時限，時限早於現在時送出前就提示），以及看不到別人的案件。
 * mock 的範例：設備組（成員：客服同仁）、採購組（成員：管理者）；「冷藏庫溫度降不下來」由同仁建立、
 * 連結的對話已被刪除；「採購備用壓縮機」由管理者建立在採購組。
 */
describe('cases', () => {
  beforeEach(() => loginAs('內部使用者'));

  it('lists only the cases the employee may see and shows a deleted conversation as not openable', () => {
    cy.visit('/app/home');
    cy.contains('.app-sidenav a', '案件').click();
    cy.location('pathname').should('eq', '/app/cases');
    cy.contains('h1', '案件').should('be.visible');

    cy.contains('.case-item', '冷藏庫溫度降不下來').should('be.visible');
    cy.contains('.case-item', '採購備用壓縮機').should('not.exist');
    cy.contains('.case-item', '灌溉馬達異音').should('not.exist');

    cy.contains('.case-item', '冷藏庫溫度降不下來').click();
    cy.location('search').should('eq', '?case=case-cold-room');
    cy.get('[data-case-detail]').within(() => {
      cy.contains('h3', '冷藏庫溫度降不下來').should('be.visible');
      cy.contains('待受理').should('be.visible');
      cy.get('[data-case-link="thread"]').should('contain.text', '這個對話無法開啟');
      cy.get('[data-case-link="thread"] a').should('not.exist');
    });

    cy.get('.cases-filters select').eq(1).select('已結案');
    cy.contains('.case-item', '灌溉馬達異音').should('be.visible');
    cy.contains('.case-item', '冷藏庫溫度降不下來').should('not.exist');
  });

  it('creates a case from a type, refusing a due time in the past before sending', () => {
    cy.visit('/app/cases');
    cy.get('[data-case-create]').click();
    cy.get('[data-case-form]').within(() => {
      cy.get('#case-type').select('設備故障報修');
      cy.get('#case-group').find('option:selected').should('have.text', '設備組');
      cy.get('#case-due').invoke('val').should('match', /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/);

      cy.get('#case-due').clear().type('2020-01-01T09:00');
      cy.contains('#case-due-error', '時限不能早於現在。').should('be.visible');
      cy.get('#case-title').type('溫控器顯示錯誤代碼');
      cy.contains('button', '建立案件').click();
      cy.contains('#case-due-error', '時限不能早於現在。').should('be.visible');
    });
    cy.contains('.case-item', '溫控器顯示錯誤代碼').should('not.exist');

    cy.get('[data-case-form] #case-due').clear().type('2099-12-31T09:00');
    cy.get('#case-due-error').should('not.exist');
    cy.get('[data-case-form] #case-description').type('三號溫室溫控器顯示 E3。');
    cy.get('[data-case-form]').contains('button', '建立案件').click();

    cy.get('[data-case-detail]').within(() => {
      cy.contains('h3', '溫控器顯示錯誤代碼').should('be.visible');
      cy.contains('待受理').should('be.visible');
      cy.contains('設備組').should('be.visible');
      cy.contains('三號溫室溫控器顯示 E3。').scrollIntoView().should('be.visible');
      cy.contains('.case-history li', '建立案件').scrollIntoView().should('be.visible');
    });
    cy.contains('.case-item', '溫控器顯示錯誤代碼').scrollIntoView().should('be.visible');
  });

  it('does not show a case the employee may not see', () => {
    cy.visit('/app/cases?case=case-compressor-purchase');
    cy.get('[data-state="permission-denied"]')
      .should('be.visible')
      .and('contain.text', '無法查看這件案件')
      .and('contain.text', '你沒有這個案件的存取權限，或它已不存在。');
    cy.contains('採購備用壓縮機').should('not.exist');
  });
});
