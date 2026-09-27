import { loginAs } from '../support/a11y';

/**
 * 知識庫的讀寫在 M2（issue #45）改成非同步契約：mock 模式仍同步完成，但畫面依請求生命週期
 * 呈現，所以斷言一律等「畫面上出現結果」，不假設點擊後立刻就緒。處理進度改成詳情頁每 3 秒
 * 重新讀取，mock 依經過時間計算（等待處理 2 秒、處理中 3 秒），等待「可使用」要放寬 timeout。
 */
const PROCESSING_TIMEOUT = 15000;

function loginAsAdmin(): void {
  loginAs('SMB 管理者');
  cy.location('pathname').should('eq', '/app/home');
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
    // 知識庫列表已從卡片改成可整列點開的 data-table（rowHeader 儲存格仍保留名稱連結）。
    cy.contains('tr', '商品使用指南')
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

  it('creates a private knowledge base, shows it in the list, and deletes it', () => {
    cy.visit('/app/knowledge');
    cy.contains('button', '建立知識庫').click();
    cy.get('#knowledge-name').should('be.focused');
    cy.get('.create-panel').contains('button', '建立知識庫').click();
    cy.get('#knowledge-create-error').should('contain', '請輸入知識庫名稱。');

    cy.get('#knowledge-name').type('門市作業手冊');
    cy.get('#knowledge-purpose').type('開店與結帳流程');
    cy.get('.create-panel').contains('button', '建立知識庫').click();
    cy.get('.create-panel').should('not.exist');
    cy.get('.feedback[role="status"]').should('contain', '已建立「門市作業手冊」');
    cy.contains('tr', '門市作業手冊')
      .should('contain', '只有我')
      .and('contain', '0 份文件')
      .within(() => cy.contains('a', '門市作業手冊').click());

    cy.location('pathname').should('match', /^\/app\/knowledge\/knowledge-created-\d+\/content$/);
    cy.get('.document-list .empty').should('contain', '這個知識庫還沒有文件或 FAQ。');

    cy.contains('button', '刪除知識庫').click();
    cy.get('.delete-panel').should('contain', '刪除知識庫「門市作業手冊」？').and('contain', '無法復原');
    cy.get('.delete-panel').contains('button', /^刪除$/).click();
    cy.location('pathname').should('eq', '/app/knowledge');
    cy.contains('tr', '商品使用指南').should('be.visible');
    cy.contains('門市作業手冊').should('not.exist');
  });

  it('retries a failed document and follows its progress without a refresh', () => {
    cy.visit('/app/knowledge/knowledge-product-guide/content');
    cy.get('.document-status[data-status="failed"]').closest('li').within(() => {
      cy.contains('button', '重新處理').click();
    });
    cy.get('.document-status[data-status="failed"]').should('not.exist');
    cy.get('.attention-summary').should('contain', '1 項需要處理');
    cy.get('.document-status[data-status="queued"]').should('have.length', 1);
    // 部分內容無法讀取的文件再處理一次也一樣，不提供重新處理。
    cy.get('.document-status[data-status="partially-readable"]').closest('li')
      .contains('button', '重新處理').should('not.exist');

    // 詳情頁每 3 秒重新讀取：等待處理 → 處理中 → 可使用。
    cy.get('.document-status[data-status="processing"]', { timeout: PROCESSING_TIMEOUT }).should('have.length', 1);
    cy.get('.document-status[data-status="ready"]', { timeout: PROCESSING_TIMEOUT }).should('have.length', 5);
    cy.get('.document-status[data-status="queued"], .document-status[data-status="processing"]').should('not.exist');
  });

  it('deletes one document after confirmation and keeps the others', () => {
    cy.visit('/app/knowledge/knowledge-product-guide/content');
    cy.get('.document-list > li').should('have.length', 6);
    cy.contains('.document-list > li', '舊版說明書掃描檔.pdf').contains('button', '刪除').click();
    cy.get('.delete-panel').should('contain', '刪除「舊版說明書掃描檔.pdf」？');
    cy.get('.delete-panel').contains('button', '取消').click();
    cy.get('.document-list > li').should('have.length', 6);

    cy.contains('.document-list > li', '舊版說明書掃描檔.pdf').contains('button', '刪除').click();
    cy.get('.delete-panel').contains('button', /^刪除$/).click();
    cy.get('.document-list > li').should('have.length', 5);
    cy.get('.document-list').should('not.contain', '舊版說明書掃描檔.pdf');
    cy.get('[role="status"][aria-live="polite"]').should('contain', '已刪除「舊版說明書掃描檔.pdf」');
    cy.get('.attention-summary').should('contain', '1 項需要處理');
  });

  it('changes the sharing scope with an explicit save', () => {
    cy.visit('/app/knowledge/knowledge-refund-policy/sharing');
    cy.get('fieldset legend').should('contain', '分享範圍');
    cy.contains('label', '指定帳號／團隊').click();
    cy.contains('button', '儲存分享設定').click();
    cy.get('[role="alert"]').should('contain', '至少選擇一個帳號');
    cy.contains('label', '安心商行客服同仁').click();
    cy.contains('button', '儲存分享設定').click();
    cy.get('.sharing-feedback').should('contain', '已儲存');

    cy.contains('nav a', '知識庫').click();
    cy.contains('tr', '退換貨政策').should('contain', '指定帳號／團隊');
  });

  it('does not reveal the name of a knowledge base the account cannot access', () => {
    cy.visit('/app/knowledge/knowledge-staff-notes/content');
    cy.contains('無法查看這個知識庫').should('be.visible');
    cy.contains('同仁個人筆記').should('not.exist');
    cy.get('nav.tabs').should('not.exist');
  });

  it('offers no create entry to an account without manage-data-sources', () => {
    loginAs('外部客戶');
    cy.visit('/app/knowledge');
    cy.contains('還沒有知識庫').should('be.visible');
    cy.contains('只有可管理資料來源的帳號可以建立知識庫。').should('be.visible');
    cy.contains('button', '建立知識庫').should('not.exist');
  });
});
