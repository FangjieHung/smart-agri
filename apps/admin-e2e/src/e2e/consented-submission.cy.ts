import { loginAs } from '../support/a11y';

function fillOrderForm(): void {
  loginAs('外部客戶');
  cy.visit('/use/assistant-customer-service');
  cy.contains('button.suggested-prompt', '回報訂單問題').click();
  cy.get('[role="log"] [data-kind="form-request"]').contains('button', '填寫表單').click();

  cy.contains('button', '下一步：確認同意').click();
  cy.get('app-inline-form [role="alert"]').should('contain', '「訂單編號」為必填。');
  cy.get('#chat-field-field-order-number').should('have.attr', 'aria-invalid', 'true').type('DEMO-2001');
  cy.contains('app-inline-form label', '配送延遲').click();
  cy.get('#chat-field-field-reported-on').type('2026-09-21');
  cy.contains('button', '下一步：確認同意').click();
}

/** 已在對話頁時：填完表單、同意並送出一筆紀錄。 */
function submitOrder(orderNumber: string): void {
  cy.contains('button.suggested-prompt', '回報訂單問題').click();
  cy.get('[role="log"] [data-kind="form-request"]').last().contains('button', '填寫表單').click();
  cy.get('#chat-field-field-order-number').type(orderNumber);
  cy.contains('app-inline-form label', '配送延遲').click();
  cy.get('#chat-field-field-reported-on').type('2026-09-21');
  cy.contains('button', '下一步：確認同意').click();
  cy.get('#consent-agree').check();
  cy.contains('button', '同意並送出').click();
  cy.get('[role="log"] [data-kind="submission-receipt"]').last().should('contain', orderNumber);
}

describe('consented structured submission', () => {
  beforeEach(() => {
    cy.clearLocalStorage();
  });

  it('cannot submit before consenting and explains recipient, purpose, viewers and sensitive data', () => {
    fillOrderForm();

    cy.get('app-consent-confirmation').within(() => {
      cy.contains('dt', '接收單位').next('dd').should('contain', '安心商行');
      cy.contains('dt', '收集目的').next('dd').should('contain', '收集訂單問題回報');
      cy.contains('dt', '可查看者').next('dd').should('contain', '安心商行管理者');
      cy.get('.sensitive-notice').should('contain', '敏感');
      cy.root().should('contain', '撤回').and('contain', 'DEMO-2001');

      cy.contains('button', '同意並送出').should('be.disabled');
      cy.get('#consent-agree').check();
      cy.contains('button', '同意並送出').should('not.be.disabled').click();
    });

    cy.get('app-consent-confirmation').should('not.exist');
    cy.get('[role="log"] [data-kind="submission-receipt"]').should('contain', '已送出').and('contain', '安心商行');
  });

  it('shows the consented record only to the designated data manager', () => {
    fillOrderForm();
    cy.get('#consent-agree').check();
    cy.contains('button', '同意並送出').click();
    cy.get('[data-kind="submission-receipt"]').should('be.visible');

    loginAs('內部使用者');
    cy.visit('/app/databases/database-orders/records');
    cy.contains('無法查看這個資料庫').should('be.visible');
    cy.contains('DEMO-2001').should('not.exist');

    loginAs('SMB 管理者');
    cy.visit('/app/databases/database-orders/records');
    cy.get('#subject-select').find('option:selected').should('contain', '外部客戶（1 筆）');
    cy.get('ol.timeline > li')
      .should('have.length', 1)
      .first()
      .should('contain', 'DEMO-2001')
      .and('contain', '配送延遲')
      .and('contain', '助理對話');

    cy.visit('/use/assistant-customer-service');
    cy.contains('h1', '客服助理').should('be.visible');
    cy.contains('DEMO-2001').should('not.exist');
  });

  it('cancelling the inline form sends nothing (#148)', () => {
    fillOrderForm();
    cy.get('app-consent-confirmation').contains('button', '返回修改').click();
    cy.get('app-inline-form').contains('button', '取消').click();
    cy.get('app-inline-form').should('not.exist');
    cy.get('[data-kind="submission-receipt"]').should('not.exist');

    loginAs('SMB 管理者');
    cy.visit('/app/databases/database-orders/records');
    cy.contains('DEMO-2001').should('not.exist');
  });

  it('refuses a form whose database was disconnected after it was offered, recording nothing (#148)', () => {
    loginAs('外部客戶');
    cy.visit('/use/assistant-customer-service');
    cy.contains('button.suggested-prompt', '回報訂單問題').click();
    cy.get('[role="log"] [data-kind="form-request"]').should('be.visible');

    // 擁有者解除助理與訂單資料庫的連接。
    loginAs('SMB 管理者');
    cy.visit('/app/assistants/assistant-customer-service/data-sources');
    cy.contains('.source-row', '訂單資料庫').contains('button', '已連接').click();
    cy.contains('.source-row', '訂單資料庫').find('button.source-toggle').should('have.attr', 'aria-pressed', 'false');

    // 先前顯示的表單送出時被拒，畫面說明原因、不建立紀錄；新的要求不再出現表單。
    loginAs('外部客戶');
    cy.visit('/use/assistant-customer-service');
    cy.get('[role="log"] [data-kind="form-request"]').last().contains('button', '填寫表單').click();
    cy.get('#chat-field-field-order-number').type('DEMO-4001');
    cy.contains('app-inline-form label', '配送延遲').click();
    cy.get('#chat-field-field-reported-on').type('2026-09-21');
    cy.contains('button', '下一步：確認同意').click();
    cy.get('app-inline-form [role="alert"]').should('contain', '目前無法使用');
    cy.get('app-inline-form').contains('button', '取消').click();
    // 解除連接後建議問題不再提供回報入口；手動輸入也不會得到表單。
    cy.contains('button.suggested-prompt', '回報訂單問題').should('not.exist');
    cy.get('[role="log"] app-chat-message').its('length').then((before) => {
      cy.get('#chat-input').type('我要回報訂單問題');
      cy.get('form.composer button[type="submit"]').click();
      // 等使用者訊息與助理回覆都出現後才檢查，回覆不是表單請求。
      cy.get('[role="log"] app-chat-message').should('have.length', before + 2);
    });
    cy.get('[role="log"] app-chat-message').last().find('[data-kind]').should('not.have.attr', 'data-kind', 'form-request');
    cy.get('[role="log"] [data-kind="form-request"]').should('have.length', 1);

    loginAs('SMB 管理者');
    cy.visit('/app/databases/database-orders/records');
    cy.contains('DEMO-4001').should('not.exist');
  });

  it('lets the submitter withdraw a record, which leaves the records and the trend', () => {
    loginAs('外部客戶');
    cy.visit('/use/assistant-customer-service');
    submitOrder('DEMO-3001');
    submitOrder('DEMO-3002');

    loginAs('SMB 管理者');
    cy.visit('/app/databases/database-orders/trends');
    cy.get('.trend-conclusion').should('contain', '2 筆');
    cy.get('.insufficient-records').should('not.exist');

    loginAs('外部客戶');
    cy.visit('/use/assistant-customer-service');
    cy.get('[role="log"] [data-kind="submission-receipt"]')
      .first()
      .within(() => {
        cy.get('.withdrawal-notice').should('contain', '撤回');
        cy.contains('button', '撤回這筆資料').click();
      });

    cy.get('[role="dialog"]')
      .should('have.attr', 'aria-modal', 'true')
      .and('contain', '無法復原');
    cy.focused().should('have.class', 'confirm-cancel');
    cy.contains('button.confirm-withdraw', '撤回').click();

    cy.get('[role="dialog"]').should('not.exist');
    cy.get('.withdraw-feedback').should('contain', '已撤回');
    // 撤回過的收據不再提供第二次撤回。
    cy.get('[role="log"] [data-kind="submission-receipt"]').first().within(() => {
      cy.get('.withdrawal-notice').should('contain', '已撤回');
      cy.contains('button', '撤回這筆資料').should('not.exist');
    });

    loginAs('SMB 管理者');
    cy.visit('/app/databases/database-orders/records');
    cy.get('ol.timeline > li').should('have.length', 1).and('contain', 'DEMO-3002');
    cy.contains('DEMO-3001').should('not.exist');
    cy.get('.withdrawn-list > li').should('have.length', 1).and('contain', '內容已移除');

    cy.contains('nav.tabs a', '趨勢比較').click();
    cy.get('.insufficient-records').should('contain', '累積 2 筆以上');
    cy.get('.trend-conclusion').should('not.exist');
  });

  it('submits through the standalone form link only after explicit consent, once, with a receipt (issue #145)', () => {
    loginAs('外部客戶');
    cy.visit('/app/forms/database-orders');
    cy.contains('h1', '訂單資料庫').should('be.visible');
    cy.get('.intro').should('contain', '安心商行（訂單資料庫）').and('contain', '安心商行管理者');
    cy.get('.sensitive-notice').should('contain', '敏感');

    // 拒絕路徑：必填沒填不會進到同意步驟，也不會送出。
    cy.contains('button', '下一步：確認同意').click();
    cy.get('app-inline-form [role="alert"]').should('contain', '「訂單編號」為必填。');
    cy.get('app-consent-confirmation').should('not.exist');

    cy.get('#chat-field-field-order-number').type('DEMO-4501');
    cy.contains('app-inline-form label', '商品瑕疵').click();
    cy.get('#chat-field-field-reported-on').type('2026-09-22');
    cy.contains('button', '下一步：確認同意').click();

    cy.get('app-consent-confirmation').within(() => {
      cy.contains('dt', '接收單位').next('dd').should('contain', '安心商行（訂單資料庫）');
      cy.contains('dt', '可查看者').next('dd').should('contain', '安心商行管理者');
      cy.root().should('contain', 'DEMO-4501');
      cy.contains('button', '同意並送出').should('be.disabled');
      cy.get('#consent-agree').check();
      cy.contains('button', '同意並送出').click();
    });

    cy.get('[data-kind="submission-receipt"]')
      .should('contain', '已送出')
      .and('contain', 'DEMO-4501')
      .and('contain', '表單連結');
    cy.get('.receipt-number').invoke('text').should('match', /^R-\d{8}-\d{10}$/);
    cy.location('search').should('match', /^\?receipt=/);
    // 重新整理仍是同一張回執。
    cy.reload();
    cy.get('[data-kind="submission-receipt"]').should('contain', 'DEMO-4501');

    // 指定資料管理者在收集紀錄看得到，來源是表單連結。
    loginAs('SMB 管理者');
    cy.visit('/app/databases/database-orders/records');
    cy.get('ol.timeline > li').should('have.length', 1).first().should('contain', 'DEMO-4501').and('contain', '表單連結');

    // 沒有填寫授權表單權限的帳號打不開表單，也看不到名稱。
    loginAs('內部使用者');
    cy.visit('/app/forms/database-orders');
    cy.contains('無法填寫這份表單').should('be.visible');
    cy.contains('訂單資料庫').should('not.exist');
  });

  it('gives the data manager no way to withdraw someone else’s record', () => {
    loginAs('外部客戶');
    cy.visit('/use/assistant-customer-service');
    submitOrder('DEMO-3003');

    loginAs('SMB 管理者');
    cy.visit('/app/databases/database-orders/records');
    cy.get('ol.timeline > li').should('have.length', 1).and('contain', 'DEMO-3003');
    cy.contains('button', '撤回').should('not.exist');
    cy.get('.subject-picker').should('contain', '撤回同意的紀錄');
  });
});
