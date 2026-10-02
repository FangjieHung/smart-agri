import { loginToApi } from '../support/api-mode';

/** M3.5 Slice 11: one real API, PostgreSQL, worker and browser flow. Names are unique so a
 * failed run leaves diagnosable data; the final test removes the assistant and knowledge base. */
const FIXTURE = '../api/tests/fixtures/knowledge/return-policy.pdf';
const GROUNDED_QUESTION = '收到商品後七天內可申請退貨';
const MISSING_QUESTION = '今天台北天氣如何';
const PROCESSING_TIMEOUT = 90000;
const RUN_TIMEOUT = 90000;
const GUID = '[0-9a-f-]{36}';

function waitForLatestVersionReady(attempt = 0): void {
  expect(attempt, 'new document version processing attempts').to.be.lessThan(60);
  cy.contains('.document-row', 'return-policy.pdf').contains('button', '版本與預覽').click();
  cy.wait('@versionDetail').then(({ response }) => {
    expect(response?.statusCode).to.eq(200);
    const body = response?.body as { versions: { status: string }[] };
    const latest = body.versions[0];
    cy.get('mat-dialog-container').contains('button', '關閉').click();
    if (latest.status === 'queued' || latest.status === 'processing') {
      cy.wait(1000);
      waitForLatestVersionReady(attempt + 1);
    } else {
      expect(latest.status, 'new document version status').to.eq('ready');
    }
  });
}

function ask(question: string): void {
  cy.get('#chat-input').clear().type(question);
  cy.get('form.composer button[type="submit"]').click();
  cy.get('[role="log"]', { timeout: 20000 }).should('not.have.attr', 'aria-busy', 'true');
}

describe('assistant acceptance against the real API', () => {
  const suffix = Date.now();
  const knowledgeName = `E2E 驗收知識庫 ${suffix}`;
  const assistantName = `E2E 驗收助理 ${suffix}`;
  let knowledgeId = '';
  let assistantId = '';
  let memberThreadId = '';

  it('prepares a cited assistant and an eligible handler', () => {
    loginToApi('anxin', 'admin');
    cy.visit('/app/settings');
    cy.contains('.member', '安心商行管理者').contains('button', '變更 安心商行管理者 的權限').click();
    cy.contains('.member', '安心商行管理者').find('input[id$="handle-assistant-issues"]').check({ force: true });
    cy.contains('.member', '安心商行管理者').contains('button', '儲存 安心商行管理者 的權限').click();
    cy.contains('.member', '安心商行管理者').should('contain', '處理助理的處理事項');

    cy.visit('/app/knowledge');
    cy.get('button[page-header-actions]').contains('建立知識庫').click();
    cy.get('#knowledge-name').type(knowledgeName);
    cy.get('.create-panel').contains('button', '建立知識庫').click();
    cy.contains('tr', knowledgeName).contains('a', knowledgeName).click();
    cy.location('pathname').should('match', new RegExp(`^/app/knowledge/${GUID}/content$`)).then((path) => {
      knowledgeId = path.split('/')[3];
    });
    cy.get('.upload-panel input[type="file"]').selectFile(FIXTURE, { force: true });
    cy.get('.document-status[data-status="ready"]', { timeout: PROCESSING_TIMEOUT }).should('have.length', 1);
    cy.contains('.document-row', 'return-policy.pdf').find('.document-select input').check();
    cy.contains('.approval-toolbar button', '批次確認生效（1）').click();
    cy.get('.document-effect[data-effect="in-effect"]', { timeout: 20000 }).should('have.length', 1);

    cy.visit('/app/assistants/new/purpose');
    cy.contains('label', '回答客戶問題').click();
    cy.get('#assistant-name').clear().type(assistantName);
    cy.get('#audience-internal').check();
    cy.contains('button', '下一步').click();
    cy.contains('.source-row', knowledgeName, { timeout: 20000 }).contains('button', '加入').click();
    cy.contains('button', '下一步').click();
    cy.contains('button', '下一步').click();
    cy.get('#trial-question-input').type(GROUNDED_QUESTION);
    cy.get('.ask-submit').click();
    cy.get('.trial-answer', { timeout: 20000 }).first().should('have.attr', 'data-kind', 'company-data');
    cy.contains('button', '建立助理').click();
    cy.location('pathname', { timeout: 20000 }).should('match', new RegExp(`^/app/assistants/${GUID}/overview$`)).then((path) => {
      assistantId = path.split('/')[3];
    });
  });

  it('runs passing and failing cases, opens an issue and assigns it', () => {
    expect(assistantId).to.match(/^[0-9a-f-]{36}$/);
    loginToApi('anxin', 'admin');
    cy.visit(`/app/assistants/${assistantId}/acceptance`);
    cy.get('.case-form textarea').type(GROUNDED_QUESTION);
    cy.get('.case-form button[type="submit"]').click();
    cy.contains('.case-card', GROUNDED_QUESTION).should('be.visible');
    cy.get('.case-form textarea').type(MISSING_QUESTION);
    cy.get('.case-form select').eq(1).select('company-data');
    cy.get('.case-form button[type="submit"]').click();
    cy.get('.case-card').should('have.length', 2);
    cy.contains('.acceptance__summary button', '全部重跑').click();
    cy.contains('.run-row', '已完成', { timeout: RUN_TIMEOUT }).should('contain', '通過 1 / 失敗 1').click();
    cy.contains('.result-card', GROUNDED_QUESTION).should('contain', '通過');
    cy.contains('.result-card', MISSING_QUESTION).should('contain', '未通過')
      .contains('button', '建立處理事項').click();
    cy.location('pathname').should('eq', '/app/issues');
    cy.get('.issue-detail').should('contain', MISSING_QUESTION);
    cy.get('.issue-detail select').eq(1).select('安心商行管理者');
    cy.contains('.issue-detail button', '儲存變更').click();
    cy.get('.issue-facts').should('contain', '安心商行管理者');
  });

  it('automatically reruns acceptance when a new version is approved', () => {
    expect(assistantId).to.match(/^[0-9a-f-]{36}$/);
    expect(knowledgeId).to.match(/^[0-9a-f-]{36}$/);
    loginToApi('anxin', 'admin');
    cy.visit(`/app/knowledge/${knowledgeId}/content`);
    // A byte-identical file is correctly rejected as duplicate content. Add a harmless PDF
    // trailer comment so this has a distinct hash but the same filename and extracted text.
    cy.readFile(FIXTURE, null).then((contents) => {
      const revised = Cypress.Buffer.concat([contents, Cypress.Buffer.from('\n% M3.5 E2E second version\n')]);
      cy.get('.upload-panel input[type="file"]').selectFile(
        { contents: revised, fileName: 'return-policy.pdf', mimeType: 'application/pdf' },
        { force: true },
      );
    });
    cy.contains('.upload-panel button', '改為上傳新版本').click();
    cy.get('.upload-summary').should('contain', '成功 1 檔');
    cy.intercept('GET', /\/api\/v1\/knowledge-bases\/[^/]+\/documents\/[^/]+$/).as('versionDetail');
    waitForLatestVersionReady();
    cy.contains('.document-row', 'return-policy.pdf').find('.document-select input').check();
    cy.intercept('POST', /\/api\/v1\/knowledge-bases\/[^/]+\/versions\/approve$/).as('approveVersion');
    cy.contains('.approval-toolbar button', '批次確認生效（1）').click();
    cy.wait('@approveVersion').then(({ response }) => {
      expect(response?.statusCode, JSON.stringify(response?.body)).to.eq(200);
    });
    cy.visit(`/app/assistants/${assistantId}/acceptance`);
    // The worker may complete before navigation. The transient outdated state is asserted
    // in AssistantAutoRerunTests; here the browser verifies the eventual real rerun.
    cy.contains('.run-row', '自動重跑', { timeout: RUN_TIMEOUT }).should('exist');
    cy.contains('.run-row', '自動重跑', { timeout: RUN_TIMEOUT }).should('contain', '已完成');
  });

  // Pending explicit approval to share the isolated test assistant with another account.
  it.skip('lets a member hand off one answer while the owner sees only its issue snapshot', () => {
    expect(assistantId).to.match(/^[0-9a-f-]{36}$/);
    loginToApi('anxin', 'admin');
    cy.visit(`/app/assistants/${assistantId}/publishing?channel=platform`);
    cy.get('app-platform-sharing').within(() => {
      cy.contains('label', '安心商行客服同仁').click();
      cy.contains('button', '儲存可使用的帳號').click();
      cy.get('[aria-live="polite"]').should('contain', '已更新可使用的帳號');
    });
    loginToApi('anxin', 'internal');
    cy.visit(`/app/chat/${assistantId}`);
    ask(MISSING_QUESTION);
    cy.get('[role="log"] [data-kind="no-result"]', { timeout: 20000 }).last().should('be.visible');
    cy.location('pathname').then((path) => {
      memberThreadId = path.split('/')[4] ?? '';
    });
    cy.get('[role="log"] .handoff-trigger').last().click();
    cy.get('[role="dialog"]').should('contain', MISSING_QUESTION).and('contain', '對方看不到你的其他對話內容');
    cy.get('[role="dialog"]').contains('button', '確認轉交').click();
    cy.contains('[role="status"]', '已轉交這一則問答').should('be.visible');

    loginToApi('anxin', 'admin');
    cy.visit('/app/issues');
    cy.contains('.issue-item', MISSING_QUESTION).click();
    cy.get('.issue-detail').should('contain', '問題複本').and('contain', MISSING_QUESTION)
      .and('contain', '回覆複本');
    cy.get('.issue-detail').should('not.contain', '這筆問答來自不保存對話的助理');
    if (memberThreadId) {
      cy.visit(`/app/chat/${assistantId}/${memberThreadId}`);
      cy.contains('找不到這段對話').should('be.visible');
      cy.contains(MISSING_QUESTION).should('not.exist');
    }
  });

  it('removes its assistant and knowledge base', () => {
    expect(assistantId).to.match(/^[0-9a-f-]{36}$/);
    expect(knowledgeId).to.match(/^[0-9a-f-]{36}$/);
    loginToApi('anxin', 'admin');
    cy.visit(`/app/assistants/${assistantId}/overview`);
    cy.contains('button', '刪除助理').click();
    cy.get('.delete-panel').contains('button', '刪除助理與所有對話').click();
    cy.location('pathname').should('eq', '/app/assistants');
    cy.visit(`/app/knowledge/${knowledgeId}/content`);
    cy.contains('button', '刪除知識庫').click();
    cy.get('.delete-panel').contains('button', /^刪除$/).click();
    cy.location('pathname').should('eq', '/app/knowledge');
    cy.visit('/app/settings');
    cy.contains('.member', '安心商行管理者').contains('button', '變更 安心商行管理者 的權限').click();
    cy.contains('.member', '安心商行管理者').find('input[id$="handle-assistant-issues"]').uncheck({ force: true });
    cy.contains('.member', '安心商行管理者').contains('button', '儲存 安心商行管理者 的權限').click();
    cy.contains('.member', '安心商行管理者').find('input[id$="handle-assistant-issues"]').should('not.exist');
  });
});
