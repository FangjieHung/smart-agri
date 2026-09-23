import { loginAs } from '../support/a11y';

/** 把已逾時的 Demo 工作階段寫進這個分頁，重現「登入逾時」狀態。 */
function visitWithExpiredSession(path: string): void {
  cy.visit(path, {
    onBeforeLoad(win) {
      win.sessionStorage.setItem(
        'demo-session',
        JSON.stringify({ accountId: 'account-smb-admin', lastActiveAt: 0 }),
      );
    },
  });
}

describe('demo states', () => {
  beforeEach(() => {
    cy.clearLocalStorage();
  });

  describe('成功與空白', () => {
    it('shows the ready state with real content for the owning account', () => {
      loginAs('SMB 管理者');
      cy.visit('/app/knowledge');

      cy.get('.knowledge-card').should('have.length.greaterThan', 0);
      cy.contains('.knowledge-card', '商品使用指南').should('be.visible');
      cy.get('[data-state="loading"]').should('not.exist');
      cy.get('.partial-notice').should('not.exist');
    });

    it('explains every empty list instead of showing a blank page', () => {
      loginAs('內部使用者');

      cy.visit('/app/assistants');
      cy.contains('尚未有可使用的助理').should('be.visible');
      cy.contains('a', '建立新助理').should('be.visible');

      cy.visit('/app/channels');
      cy.contains('目前沒有可設定發布管道的助理').should('be.visible');
      cy.get('app-channel-card').should('not.exist');

      loginAs('外部客戶');
      cy.visit('/app/knowledge');
      cy.contains('還沒有知識庫').should('be.visible');
      cy.get('.knowledge-card').should('not.exist');
    });
  });

  describe('載入中與部分成功', () => {
    beforeEach(() => loginAs('SMB 管理者'));

    it('announces the loading state while the data is not ready', () => {
      cy.visit('/app/knowledge?demoScenario=loading');

      cy.get('[data-state="loading"]')
        .should('be.visible')
        .and('have.attr', 'aria-busy', 'true')
        .and('contain.text', '正在載入知識庫');
      cy.get('.knowledge-card').should('not.exist');
    });

    it('keeps the usable items when part of the data cannot be read', () => {
      cy.visit('/app/knowledge?demoScenario=partial-failure');

      cy.get('.partial-notice')
        .should('be.visible')
        .and('have.attr', 'role', 'status')
        .and('contain.text', '部分知識庫同步暫時無法讀取')
        .and('contain.text', '其他知識庫仍可正常使用');
      // 部分失敗不會擋住其餘內容。
      cy.get('.knowledge-card').should('have.length.greaterThan', 0);
    });
  });

  describe('無結果', () => {
    it('answers with an explicit no-result state and next steps', () => {
      loginAs('外部客戶');
      cy.visit('/use/assistant-customer-service');

      cy.get('#chat-input').type('可以幫我訂下週的機票嗎？');
      cy.get('form.composer button[type="submit"]').click();

      cy.get('[role="log"] [data-kind="no-result"]')
        .should('be.visible')
        .and('contain.text', '查無資料')
        .find('ul.next-steps li')
        .should('have.length.greaterThan', 0);
    });
  });

  describe('權限不足', () => {
    it('does not reveal the name of a resource the account cannot open', () => {
      loginAs('SMB 管理者');

      cy.visit('/app/knowledge/knowledge-staff-notes/content');
      cy.contains('無法查看這個知識庫').should('be.visible');
      cy.contains('同仁個人筆記').should('not.exist');

      cy.visit('/app/databases/database-staff-checkins/form');
      cy.contains('無法查看這個資料庫').should('be.visible');
      cy.contains('同仁排班回報').should('not.exist');
    });

    it('shows a permission state for the whole list when the account has no access', () => {
      loginAs('SMB 管理者');
      cy.visit('/app/knowledge?demoScenario=permission-denied');

      cy.get('[data-state="permission-denied"]')
        .should('be.visible')
        .and('contain.text', '無法查看知識庫');
      cy.get('[data-state="permission-denied"] [role="alert"]').should('exist');
      cy.get('.knowledge-card').should('not.exist');
    });

    it('blocks configuring an assistant that belongs to another account', () => {
      loginAs('內部使用者');
      cy.visit('/app/assistants/assistant-customer-service/publishing?channel=line');

      cy.contains('你沒有這個助理的設定權限').should('be.visible');
      cy.contains('客服助理').should('not.exist');
      cy.get('app-line-setup').should('not.exist');
    });
  });

  describe('處理失敗與連線失敗', () => {
    beforeEach(() => loginAs('SMB 管理者'));

    it('marks a failed document without blocking the rest of the knowledge base', () => {
      cy.visit('/app/knowledge/knowledge-product-guide/content');

      cy.get('.document-status[data-status="failed"]')
        .scrollIntoView()
        .should('be.visible')
        .and('contain.text', '處理失敗');
      cy.get('.attention-summary').should('contain.text', '其餘 4 項仍可供助理使用');

      // 失敗的項目可以重新處理，其他項目不受影響。
      cy.get('.document-status[data-status="failed"]').closest('li').within(() => {
        cy.contains('button', '重新處理').click();
      });
      cy.get('.document-status[data-status="failed"]').should('not.exist');
      cy.get('.document-status[data-status="ready"]').should('have.length.greaterThan', 0);
    });

    it('marks only the disconnected channel and keeps the others published', () => {
      cy.visit('/app/channels?demoScenario=disconnected-channel');

      cy.contains('section.assistant-channels', '客服助理').within(() => {
        cy.contains('app-channel-card', '官網嵌入')
          .should('contain.text', '需要處理')
          .and('contain.text', '官網連線中斷')
          .and('contain.text', '其他管道不受影響');
        cy.contains('app-channel-card', '平台內分享').should('contain.text', '已發布');
      });
    });
  });

  describe('登入逾時', () => {
    it('explains an expired demo session instead of showing a blank page', () => {
      visitWithExpiredSession('/app/knowledge');

      cy.location('pathname').should('eq', '/login');
      cy.get('[role="alert"]')
        .should('be.visible')
        .and('contain.text', 'Demo 登入已逾時')
        .and('contain.text', '不是真實登入');

      // 可以直接重新選一個 Demo 身分繼續。
      cy.contains('button', 'SMB 管理者').click();
      cy.location('pathname').should('eq', '/app/home');
      cy.contains('Demo 登入已逾時').should('not.exist');
    });

    it('keeps an active demo session across a reload', () => {
      loginAs('SMB 管理者');
      cy.visit('/app/knowledge');
      cy.reload();

      cy.location('pathname').should('eq', '/app/knowledge');
      cy.contains('.knowledge-card', '商品使用指南').should('be.visible');
    });
  });

  describe('帳號切換', () => {
    it('leaves nothing the next account may not manage on screen', () => {
      loginAs('SMB 管理者');
      cy.visit('/app/knowledge');
      cy.contains('.knowledge-card', '商品使用指南').should('be.visible');
      cy.visit('/app/assistants');
      cy.contains('客服助理').should('be.visible');

      loginAs('內部使用者');

      // 換帳號後，管理畫面只剩下這個帳號自己的資料。
      cy.visit('/app/assistants');
      cy.contains('尚未有可使用的助理').should('be.visible');
      cy.contains('客服助理').should('not.exist');

      cy.visit('/app/knowledge');
      cy.contains('.knowledge-card', '同仁個人筆記').should('be.visible');
      cy.contains('商品使用指南').should('not.exist');
      cy.contains('退換貨政策').should('not.exist');

      cy.visit('/app/channels');
      cy.contains('目前沒有可設定發布管道的助理').should('be.visible');
      cy.get('app-channel-card').should('not.exist');
    });

    it('does not leak a private conversation to the next account in the same tab', () => {
      loginAs('外部客戶');
      cy.visit('/use/assistant-customer-service');
      cy.get('#chat-input').type('我的私人問題：退貨要幾天？');
      cy.get('form.composer button[type="submit"]').click();
      cy.get('[role="log"]').should('contain.text', '我的私人問題');

      loginAs('SMB 管理者');
      cy.visit('/use/assistant-customer-service');

      cy.contains('h1', '客服助理').should('be.visible');
      cy.get('[role="log"]').should('not.contain.text', '我的私人問題');
    });
  });
});
