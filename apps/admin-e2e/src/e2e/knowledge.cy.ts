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

  // issue #46（M2 Slice 12）：批次上傳與逐檔結果。
  describe('batch upload', () => {
    beforeEach(() => cy.visit('/app/knowledge/knowledge-product-guide/content'));

    it('uploads a new file and shows it queued for processing', () => {
      cy.get('.document-list > li').should('have.length', 6);

      cy.get('input[type="file"]').selectFile(
        { contents: Cypress.Buffer.from('%PDF-1.4 test'), fileName: '新品錄影腳本.pdf', mimeType: 'application/pdf' },
        { force: true },
      );

      cy.get('.document-list > li').should('have.length', 7);
      cy.contains('.document-list > li', '新品錄影腳本.pdf').find('.document-status').should('have.attr', 'data-status', 'queued');
      cy.get('.upload-summary').should('contain', '成功 1 檔');
    });

    it('rejects an unsupported file at the front end without touching the document list', () => {
      cy.get('input[type="file"]').selectFile(
        { contents: Cypress.Buffer.from('not a real exe'), fileName: '安裝程式.exe', mimeType: 'application/octet-stream' },
        { force: true },
      );

      cy.get('.upload-item').should('contain', '只支援 PDF、Word（.docx）、Excel（.xlsx）、純文字（.txt）與 Markdown（.md）檔案。');
      cy.get('.document-list > li').should('have.length', 6);
    });

    it('rejects a duplicate name and offers uploading it as a new version instead', () => {
      cy.get('input[type="file"]').selectFile(
        { contents: Cypress.Buffer.from('%PDF-1.4 test'), fileName: '商品規格總表.pdf', mimeType: 'application/pdf' },
        { force: true },
      );

      cy.get('.upload-item').should('contain', '要更新它的內容，請改用「上傳新版本」');
      cy.contains('.upload-item', '商品規格總表.pdf').contains('button', '改為上傳新版本').should('be.visible');
      // 只是被拒絕，沒有多寫入一份同名文件。
      cy.get('.document-list > li').should('have.length', 6);
    });

    it('uploads five files at once, three at a time, and lets only the rejected one be retried', () => {
      // 內容長度各不相同：mock 用檔案大小模擬「內容重複」，同樣的內容會被判定成同一份檔案。
      const files = ['批次一.pdf', '批次二.pdf', '批次三.pdf', '批次四.pdf'].map((fileName, index) => ({
        contents: Cypress.Buffer.from(`%PDF-1.4 test ${'x'.repeat(index)}`),
        fileName,
        mimeType: 'application/pdf',
      }));
      cy.get('input[type="file"]').selectFile(
        [...files, { contents: Cypress.Buffer.from('not a real exe'), fileName: '批次五.exe', mimeType: 'application/octet-stream' }],
        { force: true },
      );

      cy.get('.upload-summary').should('contain', '成功 4 檔').and('contain', '失敗 1 檔');
      cy.contains('.upload-item', '批次五.exe').should('contain', '只支援');
      cy.get('.document-list > li').should('have.length', 10);

      cy.contains('.upload-item', '批次五.exe').contains('button', '重新上傳').click();
      cy.contains('.upload-item', '批次五.exe').should('contain', '只支援');
      cy.get('.document-list > li').should('have.length', 10);
    });
  });

  // issue #47（M2 Slice 13）：抽取預覽、排除段落與版本確認。
  describe('version confirmation, preview and emergency disable', () => {
    beforeEach(() => cy.visit('/app/knowledge/knowledge-refund-policy/content'));

    it('shows the seeded document as already in effect, alongside its processing status', () => {
      cy.contains('.document-list > li', '退換貨辦法 2026 版.pdf')
        .find('.document-effect')
        .should('contain', '已生效');
    });

    it('uploads a new version, previews it, excludes a paragraph, and confirms it effective', () => {
      // 上傳新版本：既有列表點「版本與預覽」開啟對話框上傳，這裡先用重複名稱模擬情境，
      // 改走既有的「改為上傳新版本」路徑，之後在對話框中操作確認生效與預覽排除。
      cy.get('input[type="file"]').selectFile(
        { contents: Cypress.Buffer.from('%PDF-1.4 v2'), fileName: '退換貨辦法 2026 版.pdf', mimeType: 'application/pdf' },
        { force: true },
      );
      cy.contains('.upload-item', '退換貨辦法 2026 版.pdf').contains('button', '改為上傳新版本').click();
      cy.get('.upload-summary').should('contain', '成功 1 檔');

      cy.contains('.document-list > li', '退換貨辦法 2026 版.pdf')
        .find('.document-effect')
        .should('contain', '待確認');

      cy.contains('.document-list > li', '退換貨辦法 2026 版.pdf').contains('button', '版本與預覽').click();
      cy.get('.review-dialog').should('be.visible');
      cy.get('.review-dialog').contains('待確認').should('be.visible');

      // 排除封面段落。
      cy.get('.review-dialog .chunk-row').first().find('input[type="checkbox"]').check();
      cy.get('.review-dialog .chunk-row').first().find('input[type="checkbox"]').should('be.checked');

      // 確認生效（立即生效，不填日期）。
      cy.get('.review-dialog').contains('button', '確認生效').click();
      cy.get('.review-dialog .version-list').should('contain', '已封存');
      cy.get('.review-dialog [data-effect="in-effect"]').should('contain', '第 2 版');

      cy.get('.review-dialog').contains('button', '關閉').click();
      cy.get('.review-dialog').should('not.exist');
      cy.contains('.document-list > li', '退換貨辦法 2026 版.pdf')
        .find('.document-effect')
        .should('contain', '已生效');
    });

    it('batch-confirms a pending version from the document list', () => {
      cy.get('input[type="file"]').selectFile(
        { contents: Cypress.Buffer.from('%PDF-1.4'), fileName: '新版退換貨辦法.pdf', mimeType: 'application/pdf' },
        { force: true },
      );
      cy.get('.upload-summary').should('contain', '成功 1 檔');

      cy.contains('.document-list > li', '新版退換貨辦法.pdf')
        .find('.document-select input')
        .should('not.be.disabled')
        .check();
      cy.contains('button', '批次確認生效（1）').click();

      cy.contains('.document-list > li', '新版退換貨辦法.pdf').find('.document-effect').should('contain', '已生效');
      cy.contains('button', '批次確認生效（0）').should('be.visible');
    });

    it('filters the list to documents awaiting approval only', () => {
      cy.get('input[type="file"]').selectFile(
        { contents: Cypress.Buffer.from('%PDF-1.4'), fileName: '待確認文件.pdf', mimeType: 'application/pdf' },
        { force: true },
      );
      cy.get('.upload-summary').should('contain', '成功 1 檔');

      cy.contains('.document-list > li', '退換貨辦法 2026 版.pdf').should('exist');
      cy.contains('label.filter-toggle', '只看待確認').find('input').check();
      cy.contains('.document-list > li', '待確認文件.pdf').should('exist');
      cy.contains('.document-list > li', '退換貨辦法 2026 版.pdf').should('not.exist');
    });

    it('requires a reason before an emergency disable, then disables and re-enables the document', () => {
      cy.contains('.document-list > li', '退換貨辦法 2026 版.pdf').contains('button', '版本與預覽').click();
      cy.get('.review-dialog').should('be.visible');
      cy.get('.review-dialog').contains('button', '緊急停用').click();
      cy.get('.review-dialog .action-error').should('contain', '請說明緊急停用的原因。');

      cy.get('#knowledge-disable-reason').type('疑似內容有誤，暫停使用');
      cy.get('.review-dialog').contains('button', '緊急停用').click();
      cy.get('.review-dialog').should('contain', '已停用').and('contain', '疑似內容有誤，暫停使用');

      cy.get('.review-dialog').contains('button', '恢復使用').click();
      cy.get('.review-dialog').should('contain', '已生效（第 1 版）');

      cy.get('.review-dialog').contains('button', '關閉').click();
      cy.contains('.document-list > li', '退換貨辦法 2026 版.pdf').find('.document-effect').should('contain', '已生效');
    });
  });

  // issue #48（M2 Slice 14）：檢索試查。
  describe('retrieval preview', () => {
    it('lists the matching passages for a question close to the seeded content (有結果)', () => {
      cy.visit('/app/knowledge/knowledge-refund-policy/retrieval');
      cy.get('nav.tabs [aria-current="page"]').should('contain', '試查');

      cy.get('#knowledge-retrieval-question').type('收到商品幾天內可以退貨？');
      cy.contains('button', '試查').click();

      cy.get('[data-testid="below-threshold-notice"]').should('not.exist');
      cy.get('.passage-row').should('have.length.at.least', 1);
      cy.get('.passage-row').first().should('contain', '收到商品幾天內可以退貨？').and('contain', '分數');
    });

    it('shows the 查無結果 notice for a question unrelated to anything in the knowledge base (低於門檻)', () => {
      cy.visit('/app/knowledge/knowledge-refund-policy/retrieval');

      cy.get('#knowledge-retrieval-question').type('今天適合出門郊遊嗎？');
      cy.contains('button', '試查').click();

      cy.get('[data-testid="below-threshold-notice"]').should('contain', '助理設定為只用組織資料時，這題會回答查無結果。');
    });

    it('only includes a pending version’s passages once 包含待確認版本 is checked (含待確認版本)', () => {
      cy.visit('/app/knowledge/knowledge-refund-policy/content');
      cy.get('input[type="file"]').selectFile(
        { contents: Cypress.Buffer.from('%PDF-1.4 v2'), fileName: '退換貨辦法 2026 版.pdf', mimeType: 'application/pdf' },
        { force: true },
      );
      cy.contains('.upload-item', '退換貨辦法 2026 版.pdf').contains('button', '改為上傳新版本').click();
      cy.get('.upload-summary').should('contain', '成功 1 檔');
      cy.contains('.document-list > li', '退換貨辦法 2026 版.pdf').find('.document-effect').should('contain', '待確認');
      // 這份文件的新版本要處理完成（可使用）後，才會被檢索到——不能只等「某一份」文件變成 ready，
      // 同一個知識庫裡其他既有文件本來就是 ready，會讓這個等待條件提早（錯誤地）通過。
      cy.contains('.document-list > li', '退換貨辦法 2026 版.pdf')
        .find('.document-status[data-status="ready"]', { timeout: PROCESSING_TIMEOUT })
        .should('exist');

      cy.contains('nav.tabs a', '試查').click();
      cy.get('#knowledge-retrieval-question').type('退換貨辦法');
      cy.contains('button', '試查').click();

      cy.get('.passage-row').should('have.length.at.least', 1);
      cy.get('.passage-row .version-state[data-state="pending-review"]').should('not.exist');

      cy.contains('label.filter-toggle', '包含待確認版本').find('input').check();
      cy.contains('button', '試查').click();

      cy.get('.passage-row .version-state[data-state="pending-review"]').should('have.length.at.least', 1);
      cy.get('.passage-row').filter(':contains("待確認")').should('contain', '第 2 版');
    });
  });
});
