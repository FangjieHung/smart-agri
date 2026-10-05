import { loginAs } from '../support/a11y';

/**
 * 數據庫封存（issue #180，mock 模式）：擁有者確認後才封存 → 詳情標示已封存、表單連結暫停 → 預設清單看不到、
 * 「已封存」篩選看得到並標示 → 外部客戶的表單連結被拒、助理對話不再跳出表單 → 取消封存全部恢復。
 * API 模式的同一流程在 `e2e-api/database-api.cy.ts`；權限、冪等與每個入口的拒絕由後端整合測試涵蓋。
 */

function askInChat(question: string): void {
  cy.get('#chat-input').clear().type(question);
  cy.get('form.composer button[type="submit"]').click();
}

describe('database archive', () => {
  beforeEach(() => {
    cy.clearLocalStorage();
  });

  it('archives only after confirming, stops new submissions everywhere, and unarchiving restores them', () => {
    loginAs('SMB 管理者');
    cy.visit('/app/databases/database-orders/form');
    cy.contains('h1', '訂單資料庫').should('be.visible');
    cy.get('.archived-notice').should('not.exist');

    // 取消確認：什麼都不變。
    cy.get('#database-archive-toggle').should('contain', '封存資料庫').click();
    cy.get('[role="alertdialog"]')
      .should('contain', '封存「訂單資料庫」？')
      .and('contain', '不再接受新的提交')
      .and('contain', '資料不會刪除');
    cy.get('#database-archive-cancel').click();
    cy.get('[role="alertdialog"]').should('not.exist');
    cy.get('.archived-notice').should('not.exist');

    cy.get('#database-archive-toggle').click();
    cy.get('#database-archive-confirm').click();
    cy.get('[role="alertdialog"]').should('not.exist');
    cy.get('.archive-feedback').should('contain', '資料庫已封存');
    cy.get('.archived-notice').should('contain', '已封存').and('contain', '不再接受新的提交');
    cy.get('.database-meta').should('contain', '已封存，暫停填寫');
    cy.get('.database-meta a.form-link').should('not.exist');
    cy.get('#database-archive-toggle').should('contain', '取消封存');

    // 清單：預設只列使用中的；「已封存」篩選列出它並標示。
    cy.visit('/app/databases');
    cy.contains('.list-filter button', '使用中').should('have.attr', 'aria-pressed', 'true');
    cy.get('lib-data-table').should('contain', '客戶資料庫').and('not.contain', '訂單資料庫');
    cy.contains('.list-filter button', '已封存').click();
    cy.contains('.list-filter button', '已封存').should('have.attr', 'aria-pressed', 'true');
    cy.contains('tr', '訂單資料庫').find('app-status-badge').should('contain', '已封存');
    cy.get('lib-data-table').should('not.contain', '客戶資料庫');

    // 外部客戶：表單連結與不存在的表單相同的拒絕；對話中要求填寫也不再跳出表單。
    loginAs('外部客戶');
    cy.visit('/app/forms/database-orders');
    cy.contains('無法填寫這份表單').should('be.visible');
    cy.contains('訂單資料庫').should('not.exist');
    cy.visit('/use/assistant-customer-service');
    askInChat('我要回報訂單問題');
    cy.get('[role="log"] [data-kind="no-result"]').should('exist');
    cy.get('[role="log"] [data-kind="form-request"]').should('not.exist');

    // 取消封存：表單連結與對話中的表單都恢復。
    loginAs('SMB 管理者');
    cy.visit('/app/databases/database-orders/form');
    cy.get('#database-archive-toggle').should('contain', '取消封存').click();
    cy.get('[role="alertdialog"]').should('contain', '取消封存「訂單資料庫」？');
    cy.get('#database-archive-confirm').click();
    cy.get('.archived-notice').should('not.exist');
    cy.get('.database-meta a.form-link').should('have.attr', 'href', '/app/forms/database-orders');

    loginAs('外部客戶');
    cy.visit('/app/forms/database-orders');
    cy.contains('h1', '訂單資料庫').should('be.visible');
    cy.visit('/use/assistant-customer-service');
    askInChat('我要回報訂單問題');
    cy.get('[role="log"] [data-kind="form-request"]').should('exist');
  });

  it('shows a data manager that it is archived, read-only, without the archive action', () => {
    // 擁有者先把內部同仁也指定為資料管理者（mock 的指定存在 localStorage，與「權限」頁籤寫入的格式相同）。
    cy.visit('/login');
    cy.window().then((win) =>
      win.localStorage.setItem(
        'sme-demo:database-access:database-orders',
        JSON.stringify({
          version: 1,
          savedAt: '2026-10-05T02:00:00.000Z',
          savedBy: 'account-smb-admin',
          dataManagerAccountIds: ['account-smb-admin', 'account-internal-employee'],
        }),
      ),
    );
    loginAs('SMB 管理者');
    cy.visit('/app/databases/database-orders/form');
    cy.get('#database-archive-toggle').click();
    cy.get('#database-archive-confirm').click();
    cy.get('.archived-notice').should('be.visible');

    loginAs('內部使用者');
    cy.visit('/app/databases/database-orders/form');
    cy.contains('h1', '訂單資料庫').should('be.visible');
    cy.get('.archived-notice').should('contain', '已封存');
    cy.get('#database-archive-toggle').should('not.exist');
    // 紀錄照常可讀。
    cy.get('nav.tabs').contains('a', '收集紀錄').click();
    cy.contains('無法查看收集紀錄').should('not.exist');
  });
});
