import { loginToApi } from '../support/api-mode';

/**
 * API 模式的知識庫整合流程（M2 Slice 17，issue #51）：對真實 API、PostgreSQL（pgvector）與
 * 背景工作 worker 跑，嵌入模型是 `Fake`。步驟照 PR #86／#98／#102／#109 的手動驗收：
 * 建立 → 批次上傳 3 份 fixture → 等處理完成 → 批次確認生效 → 試查命中 → 另一個組織看不到 → 刪除。
 *
 * 三個 `it` 依序共用同一個知識庫（名稱每次不同），最後一個負責刪除，讓 spec 可以對同一個資料庫重跑。
 */

/** 與 API 整合測試同一組檔案，不另外複製；路徑相對於 apps/admin-e2e（Cypress 的專案根目錄）。 */
const FIXTURE_DIR = '../api/tests/fixtures/knowledge';
const FIXTURES = ['faq.md', 'product-guide.docx', 'return-policy.pdf'] as const;

/**
 * `Fake` 嵌入是字元雜湊、不具語意，要用 return-policy.pdf 第 2 頁的原句才會高於門檻（PR #109）；
 * 改寫過的問句會落在門檻以下。
 */
const TRIAL_QUESTION = '收到商品後七天內可申請退貨';

/** 背景工作每份文件要抽文字、切段、嵌入；詳情頁每 3 秒重新讀取一次。 */
const PROCESSING_TIMEOUT = 90000;

const KNOWLEDGE_PATH = /^\/app\/knowledge\/[0-9a-f-]{36}\/content$/;

describe('knowledge bases against the real API', () => {
  const knowledgeBaseName = `E2E 知識庫 ${Date.now()}`;
  let knowledgeBasePath = '';

  it('creates a knowledge base, uploads, processes, approves and retrieves from it', () => {
    loginToApi('anxin', 'admin');

    cy.visit('/app/knowledge');
    cy.get('button[page-header-actions]').contains('建立知識庫').click();
    cy.get('#knowledge-name').type(knowledgeBaseName);
    cy.get('.create-panel').contains('button', '建立知識庫').click();
    cy.get('.create-panel').should('not.exist');
    cy.get('.feedback[role="status"]').should('contain', `已建立「${knowledgeBaseName}」`);
    cy.contains('tr', knowledgeBaseName)
      .should('contain', '只有我')
      .within(() => cy.contains('a', knowledgeBaseName).click());
    cy.location('pathname')
      .should('match', KNOWLEDGE_PATH)
      .then((pathname) => {
        knowledgeBasePath = pathname;
      });
    cy.get('.document-list .empty').should('contain', '這個知識庫還沒有文件或 FAQ。');

    // 批次上傳：一次選 3 個檔案，上傳面板最多同時送 3 個。
    cy.get('.upload-panel input[type="file"]').selectFile(
      FIXTURES.map((name) => `${FIXTURE_DIR}/${name}`),
      { force: true },
    );
    cy.get('.upload-summary', { timeout: 30000 }).should('contain', '共 3 檔：成功 3 檔、失敗 0 檔。');

    // 等 worker 處理完：輪詢畫面上的狀態，不固定 sleep。
    cy.get('.document-list app-document-row').should('have.length', FIXTURES.length);
    cy.get('.document-status[data-status="ready"]', { timeout: PROCESSING_TIMEOUT }).should(
      'have.length',
      FIXTURES.length,
    );
    cy.get('.document-effect[data-effect="not-in-effect"]').should('have.length', FIXTURES.length);

    // 批次確認生效：逐列勾選（每次勾選都會重新渲染清單），再一次送出。
    for (const name of FIXTURES) {
      cy.contains('.document-row', name).find('.document-select input').should('be.enabled').check();
    }
    cy.contains('.approval-toolbar button', `批次確認生效（${FIXTURES.length}）`).click();
    cy.get('.document-effect[data-effect="in-effect"]', { timeout: 20000 })
      .should('have.length', FIXTURES.length)
      .each((effect) => expect(effect.text().trim()).to.eq('已生效'));

    // 試查：只查已生效的版本。product-guide.docx 的退換貨小節也有同一句話，所以不假設哪份文件排第一，
    // 而是找 return-policy.pdf 分數最高的段落（清單依分數排序），核對頁碼、原文與門檻。
    cy.contains('nav.tabs a', '試查').click();
    cy.location('pathname').should('match', /\/retrieval$/);
    cy.get('#knowledge-retrieval-question').type(TRIAL_QUESTION);
    cy.get('.retrieval-form').contains('button', '試查').click();
    cy.get('.retrieval-result .action-error').should('not.exist');
    cy.get('.passage-row').should('have.length.greaterThan', 0);
    cy.get('[data-testid="below-threshold-notice"]').should('not.exist');
    cy.contains('.passage-row', 'return-policy.pdf').within(() => {
      cy.get('.passage-document').should('contain', 'return-policy.pdf・第 1 版');
      cy.get('.version-state').should('have.attr', 'data-state', 'effective');
      cy.get('.passage-location').invoke('text').invoke('trim').should('eq', '第 2 頁');
      cy.get('.passage-excerpt').should('contain', TRIAL_QUESTION);
      cy.get('.passage-score')
        .invoke('text')
        .then((text) => {
          const match = /分數：([\d.]+)（門檻 ([\d.]+)）/.exec(text);
          expect(match, `score text "${text}"`).not.to.eq(null);
          expect(Number(match?.[1]), 'score').to.be.at.least(Number(match?.[2]));
        });
    });
  });

  it('is not visible to another organization', () => {
    expect(knowledgeBasePath, 'the knowledge base created by the previous test').to.match(KNOWLEDGE_PATH);
    loginToApi('control', 'admin');

    cy.visit('/app/knowledge');
    cy.contains('h1', '知識庫').should('be.visible');
    cy.contains('正在載入知識庫').should('not.exist');
    cy.contains('目前無法載入知識庫').should('not.exist');
    cy.contains(knowledgeBaseName).should('not.exist');

    // 直接開網址：API 回 404／403，畫面不透露名稱。
    cy.visit(knowledgeBasePath);
    cy.contains('無法查看這個知識庫').should('be.visible');
    cy.contains(knowledgeBaseName).should('not.exist');
  });

  it('is deleted by its owner so the spec can run again', () => {
    expect(knowledgeBasePath, 'the knowledge base created by the first test').to.match(KNOWLEDGE_PATH);
    loginToApi('anxin', 'admin');

    cy.visit(knowledgeBasePath);
    cy.contains('h1', knowledgeBaseName).should('be.visible');
    cy.contains('button', '刪除知識庫').click();
    cy.get('.delete-panel').should('contain', `刪除知識庫「${knowledgeBaseName}」？`);
    cy.get('.delete-panel').contains('button', /^刪除$/).click();
    cy.location('pathname').should('eq', '/app/knowledge');
    cy.contains('正在載入知識庫').should('not.exist');
    cy.contains(knowledgeBaseName).should('not.exist');
  });
});
