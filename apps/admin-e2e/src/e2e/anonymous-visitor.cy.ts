import { loginAs } from '../support/a11y';

/** 已發布到官網嵌入的助理；未登入訪客只能開這一種。 */
const PUBLISHED = 'assistant-customer-service';
/** 只在帳號內使用、沒有任何對外管道的助理。 */
const INTERNAL_ONLY = 'assistant-internal-onboarding';

/** 未登入訪客的 id 存在這個分頁的 sessionStorage；換一個 id 就等於換一位訪客。 */
function visitAs(visitorId: string, url: string): void {
  cy.visit(url, {
    onBeforeLoad(win) {
      win.sessionStorage.setItem('demo-visitor', visitorId);
    },
  });
}

function ask(question: string): void {
  cy.get('#chat-input').clear().type(question);
  cy.get('form.composer button[type="submit"]').click();
}

function fillOrderForm(orderNumber: string): void {
  cy.contains('button.suggested-prompt', '回報訂單問題').click();
  cy.get('[role="log"] [data-kind="form-request"]').contains('button', '填寫表單').click();
  cy.get('#chat-field-field-order-number').type(orderNumber);
  cy.contains('app-inline-form label', '配送延遲').click();
  cy.get('#chat-field-field-reported-on').type('2026-09-21');
  cy.contains('button', '下一步：確認同意').click();
}

describe('anonymous visitor on the embedded chat page', () => {
  beforeEach(() => {
    cy.clearLocalStorage();
  });

  it('opens an externally published assistant without choosing a demo persona', () => {
    cy.visit(`/use/${PUBLISHED}`);

    cy.location('pathname').should('eq', `/use/${PUBLISHED}`);
    cy.contains('h1', '客服助理').should('be.visible');
    cy.get('.demo-notice').should('be.visible').and('contain', 'Demo');
    cy.get('.privacy-notice').should('contain', '關閉這個分頁');
    // 訪客看不到工作區：沒有側欄、沒有返回工作區的連結。
    cy.get('.app-shell').should('not.exist');
    cy.get('a[href^="/app"]').should('not.exist');
    cy.contains('返回首頁').should('not.exist');

    ask('收到商品後幾天內可以退貨？');
    cy.get('[role="log"] [data-kind="company-data"]').should('contain', '7 天');
  });

  it('keeps the visitor conversation invisible to every demo persona', () => {
    visitAs('visitor-e2e-first', `/use/${PUBLISHED}`);
    ask('我的訪客問題：退貨要幾天？');
    cy.get('[role="log"]').should('contain', '我的訪客問題');

    loginAs('SMB 管理者');
    cy.visit(`/use/${PUBLISHED}`);
    cy.contains('h1', '客服助理').should('be.visible');
    cy.get('[role="log"]').should('not.contain', '我的訪客問題');

    cy.visit(`/app/chat/${PUBLISHED}`);
    cy.get('.rail-empty').should('be.visible');
    cy.contains('我的訪客問題').should('not.exist');
  });

  it('keeps two visitors in the same browser apart', () => {
    visitAs('visitor-e2e-first', `/use/${PUBLISHED}`);
    ask('我的訪客問題：退貨要幾天？');
    cy.get('[role="log"]').should('contain', '我的訪客問題');

    visitAs('visitor-e2e-second', `/use/${PUBLISHED}`);
    cy.contains('h1', '客服助理').should('be.visible');
    cy.get('[role="log"] app-chat-message').should('have.length', 0);
    cy.contains('我的訪客問題').should('not.exist');
  });

  it('refuses an assistant with no external channel without leaking its name', () => {
    cy.visit(`/use/${INTERNAL_ONLY}`);

    cy.contains('無法開啟這個助理').should('be.visible');
    cy.contains('內部教育訓練助理').should('not.exist');
    cy.get('#chat-input').should('not.exist');
    cy.get('a[href^="/app"]').should('not.exist');
    cy.get('.ui-panel-action').should('not.exist');
  });

  it('shows no workspace chrome and no workspace links in embed mode', () => {
    cy.visit(`/use/${PUBLISHED}?embed=1`);

    cy.get('.chat-header').should('not.exist');
    cy.get('app-conversation-rail').should('not.exist');
    cy.get('.app-shell').should('not.exist');
    cy.get('a[href^="/app"]').should('not.exist');
    cy.get('h1').should('have.class', 'visually-hidden');
    cy.get('.demo-notice').should('contain', 'Demo');

    ask('收到商品後幾天內可以退貨？');
    cy.get('[role="log"] [data-kind="company-data"]').should('be.visible');
  });

  it('sends an anonymous consented submission to the data manager’s collection records', () => {
    cy.visit(`/use/${PUBLISHED}?embed=1`);
    fillOrderForm('DEMO-4001');

    cy.get('app-consent-confirmation').within(() => {
      cy.contains('dt', '接收單位').next('dd').should('contain', '安心商行');
      cy.contains('dt', '收集目的').next('dd').should('contain', '收集訂單問題回報');
      cy.contains('dt', '可查看者').next('dd').should('contain', '安心商行管理者');
      cy.get('.sensitive-notice').should('contain', '敏感');
      cy.contains('button', '同意並送出').should('be.disabled');
      cy.get('#consent-agree').check();
      cy.contains('button', '同意並送出').click();
    });
    cy.get('[role="log"] [data-kind="submission-receipt"]').should('contain', '已送出');

    loginAs('內部使用者');
    cy.visit('/app/databases/database-orders/records');
    cy.contains('無法查看這個資料庫').should('be.visible');
    cy.contains('DEMO-4001').should('not.exist');

    loginAs('SMB 管理者');
    cy.visit('/app/databases/database-orders/records');
    cy.get('#subject-select').find('option:selected').should('contain', '未登入訪客');
    cy.get('ol.timeline > li')
      .should('have.length', 1)
      .first()
      .should('contain', 'DEMO-4001')
      .and('contain', '助理對話');
    // 訪客沒有被冒認成任何 Demo 帳號。
    cy.get('#subject-select').should('not.contain', '外部客戶');
  });

  it('lets a visitor withdraw within the same tab session and says the record is lost with it', () => {
    visitAs('visitor-e2e-withdraw', `/use/${PUBLISHED}?embed=1`);
    fillOrderForm('DEMO-4002');
    cy.get('#consent-agree').check();
    cy.contains('button', '同意並送出').click();

    cy.get('[role="log"] [data-kind="submission-receipt"] .withdrawal-notice')
      .should('contain', '撤回')
      .and('contain', '分頁');
    cy.contains('button', '撤回這筆資料').click();
    cy.contains('button.confirm-withdraw', '撤回').click();
    cy.get('.withdraw-feedback').should('contain', '已撤回');

    loginAs('SMB 管理者');
    cy.visit('/app/databases/database-orders/records');
    cy.contains('DEMO-4002').should('not.exist');
    cy.get('.withdrawn-list > li').should('have.length', 1).and('contain', '內容已移除');
  });
});
