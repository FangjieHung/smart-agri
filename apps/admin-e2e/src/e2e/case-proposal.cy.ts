import { auditA11y, loginAs } from '../support/a11y';

/**
 * Issue #254：助理提議開案（mock 模式）。擁有者在資料來源頁勾選「可提議的案件類型」後，內部帳號描述需要處理
 * 的事時，助理回一張提議卡片（類型、承辦組、時限，可修改標題與說明）；「建立案件」先開共用的確認視窗，確認後
 * 顯示案件連結並能在案件頁打開；「不用了」不建立案件。預設沒有可提議的類型，不會提議。
 */
const ASSISTANT = 'assistant-customer-service';

function askInChat(question: string): void {
  cy.get('#chat-input').type(question);
  cy.get('form.composer button[type="submit"]').click();
}

function proposableType(name: string) {
  return cy.get('app-proposable-case-types').contains('label', name).find('input[type="checkbox"]');
}

describe('case proposals in a conversation (#254)', () => {
  beforeEach(() => {
    cy.clearLocalStorage();
    loginAs('SMB 管理者');
  });

  it('proposes nothing by default; after the owner adds a type the asker confirms a case and opens it', () => {
    cy.visit(`/app/chat/${ASSISTANT}`);
    askInChat('二號冷藏庫溫度降不下來，需要報修');
    cy.get('[role="log"] app-chat-message', { timeout: 10000 }).should('have.length', 2);
    cy.get('app-case-proposal-card').should('not.exist');

    cy.visit(`/app/assistants/${ASSISTANT}/data-sources`);
    proposableType('設備故障報修').should('not.be.checked').check();
    proposableType('設備故障報修').should('be.checked');
    cy.reload();
    proposableType('設備故障報修').should('be.checked');

    cy.visit(`/app/chat/${ASSISTANT}`);
    askInChat('三號冷藏庫的壓縮機一直跳電，需要報修');
    // 對話紀錄是自己捲動的區塊：先捲到卡片再檢查可見（Cypress 的 be.visible 會被捲動容器裁切誤判）。
    cy.get('app-case-proposal-card', { timeout: 10000 }).scrollIntoView().should('be.visible').within(() => {
      cy.contains('設備故障報修').should('exist');
      cy.contains('設備組').should('exist');
      cy.contains('3 天').should('exist');
      cy.get('input[type="text"]').should('have.value', '三號冷藏庫的壓縮機一直跳電，需要報修');
    });
    cy.get('[role="log"] [data-kind="case-proposal"] .kind').should('contain', '建議開案');
    auditA11y('app-case-proposal-card');

    cy.get('app-case-proposal-card').within(() => {
      cy.get('input[type="text"]').clear().type('三號冷藏庫壓縮機跳電');
      cy.get('textarea').type('今天下午前請派人檢查。');
      cy.contains('button', '建立案件').click();
    });
    cy.get('[role="dialog"]').should('be.visible').within(() => {
      cy.contains('建立這件案件？').should('exist');
      cy.contains('三號冷藏庫壓縮機跳電').should('exist');
      cy.contains('今天下午前請派人檢查。').should('exist');
      cy.get('.confirm-case-proposal').click();
    });
    cy.get('[role="dialog"]').should('not.exist');
    cy.get('app-case-proposal-card').should('contain', '已建立案件「三號冷藏庫壓縮機跳電」');
    cy.get('app-case-proposal-card').contains('a', '查看案件').click();
    cy.location('pathname').should('eq', '/app/cases');
    cy.location('search').should('match', /^\?case=case-chat-/);
    cy.contains('三號冷藏庫壓縮機跳電').should('exist');
  });

  it('「不用了」 creates no case, and a removed type reads as 「無法建立」', () => {
    cy.visit(`/app/assistants/${ASSISTANT}/data-sources`);
    proposableType('設備故障報修').check();
    proposableType('設備故障報修').should('be.checked');

    cy.visit(`/app/chat/${ASSISTANT}`);
    askInChat('溫室風扇停了，需要報修');
    cy.get('app-case-proposal-card', { timeout: 10000 }).last().contains('button', '不用了').click();
    cy.get('app-case-proposal-card').last().should('contain', '已選擇不用了，沒有建立案件。').find('button').should('not.exist');
    cy.get('#chat-input').should('be.focused');

    askInChat('灌溉馬達有異音，請安排維修');
    cy.get('app-case-proposal-card', { timeout: 10000 }).should('have.length', 2);
    cy.visit(`/app/assistants/${ASSISTANT}/data-sources`);
    proposableType('設備故障報修').uncheck();
    proposableType('設備故障報修').should('not.be.checked');
    cy.visit(`/app/chat/${ASSISTANT}`);
    cy.get('app-case-proposal-card').last().should('contain', '無法建立').find('button').should('not.exist');
  });
});
