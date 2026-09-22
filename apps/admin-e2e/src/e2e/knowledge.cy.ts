function loginAsAdmin(): void {
  cy.visit('/login');
  cy.contains('button', 'SMB 管理者').click();
  cy.location('pathname').should('eq', '/app/home');
}

/** 以 SPA 導覽切換網址，保留 demo 登入狀態（重新整理會清除工作階段）。 */
function navigateInApp(path: string): void {
  cy.window().then((win) => {
    win.history.pushState({}, '', path);
    win.dispatchEvent(new PopStateEvent('popstate', { state: {} }));
  });
}

describe('knowledge bases', () => {
  beforeEach(() => {
    cy.clearLocalStorage();
    loginAsAdmin();
  });

  it('lists knowledge bases and opens the detail tabs', () => {
    cy.contains('nav a', '知識庫').click();
    cy.location('pathname').should('eq', '/app/knowledge');
    cy.contains('h1', '知識庫').should('be.visible');
    cy.contains('.knowledge-card', '商品使用指南')
      .should('contain', '2 項需要處理')
      .and('contain', '指定帳號／團隊')
      .within(() => cy.contains('a', '商品使用指南').click());

    cy.location('pathname').should('eq', '/app/knowledge/knowledge-product-guide/content');
    cy.get('nav.tabs [aria-current="page"]').should('contain', '內容');
    cy.get('.attention-summary').should('contain', '其餘 4 項仍可供助理使用');
    cy.get('.document-status[data-status="failed"]').should('contain', '處理失敗');
    cy.get('.document-status[data-status="partially-readable"]').should('contain', '部分內容無法讀取');

    cy.contains('nav.tabs a', '已連接助理').click();
    cy.location('pathname').should('eq', '/app/knowledge/knowledge-product-guide/assistants');
    cy.get('.assistant-list li').should('have.length', 2);
    cy.get('.assistant-list').should('contain', '客服助理').and('contain', '內部教育訓練助理');
  });

  it('simulates adding a document without uploading and keeps other items usable', () => {
    navigateInApp('/app/knowledge/knowledge-refund-policy');
    cy.location('pathname').should('eq', '/app/knowledge/knowledge-refund-policy/content');
    cy.contains('Demo：不會真正上傳檔案').should('be.visible');
    cy.get('input[type="file"]').should('not.exist');

    cy.get('.document-list > li').its('length').then((initialCount) => {
      cy.contains('button', '加入示範文件').click();
      cy.get('.document-list > li').should('have.length', initialCount + 1);
      cy.get('.document-list > li').last().find('.document-status')
        .should('have.attr', 'data-status', 'ready')
        .and('contain', '可使用');
      cy.get('.document-status[data-status="ready"]').should('have.length', initialCount + 1);
    });
  });

  it('retries a failed document', () => {
    navigateInApp('/app/knowledge/knowledge-product-guide/content');
    cy.get('.document-status[data-status="failed"]').closest('li').within(() => {
      cy.contains('button', '重新處理').click();
    });
    cy.get('.document-status[data-status="failed"]').should('not.exist');
    cy.get('.attention-summary').should('contain', '1 項需要處理');
  });

  it('changes the sharing scope with an explicit save', () => {
    navigateInApp('/app/knowledge/knowledge-refund-policy/sharing');
    cy.get('fieldset legend').should('contain', '分享範圍');
    cy.contains('label', '指定帳號／團隊').click();
    cy.contains('button', '儲存分享設定').click();
    cy.get('[role="alert"]').should('contain', '至少選擇一個帳號');
    cy.contains('label', '安心商行客服同仁').click();
    cy.contains('button', '儲存分享設定').click();
    cy.get('.sharing-feedback').should('contain', '已儲存');

    cy.contains('nav a', '知識庫').click();
    cy.contains('.knowledge-card', '退換貨政策').should('contain', '指定帳號／團隊');
  });

  it('does not reveal the name of a knowledge base the account cannot access', () => {
    navigateInApp('/app/knowledge/knowledge-staff-notes/content');
    cy.contains('無法查看這個知識庫').should('be.visible');
    cy.contains('同仁個人筆記').should('not.exist');
    cy.get('nav.tabs').should('not.exist');
  });
});
