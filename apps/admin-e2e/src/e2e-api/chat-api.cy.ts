import { loginToApi } from '../support/api-mode';

/**
 * M3 Slice 14（issue #84）：用真實後端＋Fake 模型驗證第 1 節的業務閉環——建立知識庫、
 * 由精靈建立助理、在平台內分享給內部員工、以該員工身分對話並看到有引用的組織資料回覆。
 *
 * 步驟照人工驗收過的流程：
 * - PR #101（精靈與平台內分享改用 API）、PR #100（串流對話 UI）；
 * - PR #110（試問走真實流程，門檻與分數的行為）；
 * - PR #103（修正重新整理後顯示錯誤的迴歸）；
 * - PR #113（重試不會重複保存問題）。
 *
 * 與 `knowledge-api.cy.ts` 共用同一組 fixture，但只需要 `return-policy.pdf` 一份文件：
 * 這裡要驗證的是「對話拿到有引用的答案」，不必重覆知識庫批次上傳／確認生效的完整涵蓋（那份 spec
 * 已經蓋過三個檔案、批次流程與跨組織隔離）。
 *
 * 七個 `it` 依序共用同一個知識庫與助理（名稱都帶時間戳記，可以重覆執行），最後一個負責刪除。
 */

const FIXTURE_DIR = '../api/tests/fixtures/knowledge';
const FIXTURE = 'return-policy.pdf';

/** 與 knowledge-api.cy.ts 相同的門檻理由：Fake 嵌入不具語意，只有原句才會超過門檻。 */
const TRIAL_QUESTION = '收到商品後七天內可申請退貨';
const OUT_OF_SCOPE_QUESTION = '今天台北天氣如何';

const PROCESSING_TIMEOUT = 90000;
const STREAM_TIMEOUT = 20000;

const KNOWLEDGE_PATH = /^\/app\/knowledge\/([0-9a-f-]{36})\/content$/;
const ASSISTANT_OVERVIEW_PATH = /^\/app\/assistants\/([0-9a-f-]{36})\/overview$/;
const CHAT_PATH = /^\/app\/chat\/[0-9a-f-]{36}(\/[0-9a-f-]{36})?$/;

function ask(question: string): void {
  cy.get('#chat-input').clear().type(question);
  cy.get('form.composer button[type="submit"]').click();
}

describe('chat against the real API and a fake model', () => {
  const knowledgeBaseName = `E2E 對話知識庫 ${Date.now()}`;
  const assistantName = `E2E 對話助理 ${Date.now()}`;
  let knowledgeBaseId = '';
  let assistantId = '';

  it('creates and prepares a knowledge base for the assistant to use', () => {
    loginToApi('anxin', 'admin');

    cy.visit('/app/knowledge');
    cy.get('button[page-header-actions]').contains('建立知識庫').click();
    cy.get('#knowledge-name').type(knowledgeBaseName);
    cy.get('.create-panel').contains('button', '建立知識庫').click();
    cy.get('.create-panel').should('not.exist');
    cy.contains('tr', knowledgeBaseName).within(() => cy.contains('a', knowledgeBaseName).click());
    cy.location('pathname')
      .should('match', KNOWLEDGE_PATH)
      .then((pathname) => {
        const match = KNOWLEDGE_PATH.exec(pathname);
        expect(match, `knowledge base id in "${pathname}"`).not.to.eq(null);
        knowledgeBaseId = match?.[1] ?? '';
      });

    cy.get('.upload-panel input[type="file"]').selectFile(`${FIXTURE_DIR}/${FIXTURE}`, { force: true });
    cy.get('.upload-summary', { timeout: 30000 }).should('contain', '共 1 檔：成功 1 檔、失敗 0 檔。');

    cy.get('.document-list app-document-row').should('have.length', 1);
    cy.get('.document-status[data-status="ready"]', { timeout: PROCESSING_TIMEOUT }).should('have.length', 1);
    cy.get('.document-effect[data-effect="not-in-effect"]').should('have.length', 1);

    cy.contains('.document-row', FIXTURE).find('.document-select input').should('be.enabled').check();
    cy.contains('.approval-toolbar button', '批次確認生效（1）').click();
    cy.get('.document-effect[data-effect="in-effect"]', { timeout: 20000 })
      .should('have.length', 1)
      .and('contain', '已生效');
  });

  it('creates an assistant with the wizard that uses the knowledge base and tries a question', () => {
    expect(knowledgeBaseId, 'the knowledge base created by the previous test').to.match(/^[0-9a-f-]{36}$/);
    loginToApi('anxin', 'admin');

    cy.contains('a', '開始建立').click();
    cy.location('pathname').should('match', /^\/app\/assistants\/drafts\/[0-9a-f-]{36}\/purpose$/);

    cy.contains('label', '回答客戶問題').click();
    cy.get('#assistant-name').clear().type(assistantName);
    cy.get('#audience-internal').check();
    cy.get('.autosave').should('contain', '已自動儲存');
    cy.contains('button', '下一步').click();

    cy.location('pathname').should('match', /\/sources$/);
    cy.get('[aria-current="step"]').should('contain', '資料來源');
    cy.contains('.source-row', knowledgeBaseName, { timeout: 20000 }).within(() => {
      cy.contains('button', '加入').click();
      cy.get('button.source-toggle').should('have.attr', 'aria-pressed', 'true');
    });
    cy.get('.source-summary').should('contain', '1 個知識庫');
    cy.contains('button', '下一步').click();

    cy.location('pathname').should('match', /\/rules$/);
    // 「只用組織資料」：草稿預設就是這個規則，這裡只確認精靈沒有改變預設值。
    cy.get('#scope-strict').should('be.checked');
    cy.contains('button', '下一步').click();

    cy.location('pathname').should('match', /\/test$/);
    cy.get('#trial-question-input').type(TRIAL_QUESTION);
    cy.get('.ask-submit').click();
    cy.get('.trial-answer', { timeout: STREAM_TIMEOUT }).first().should('have.attr', 'data-kind', 'company-data');
    cy.get('.trial-answer')
      .first()
      .within(() => {
        cy.get('blockquote').should('exist');
        cy.contains('footer', FIXTURE).should('exist');
      });

    cy.contains('button', '建立助理').click();
    cy.location('pathname', { timeout: 20000 })
      .should('match', ASSISTANT_OVERVIEW_PATH)
      .then((pathname) => {
        const match = ASSISTANT_OVERVIEW_PATH.exec(pathname);
        expect(match, `assistant id in "${pathname}"`).not.to.eq(null);
        assistantId = match?.[1] ?? '';
      });
    cy.contains('h1', assistantName).should('be.visible');
    cy.contains('助理已建立').should('be.visible');
  });

  it('shares the assistant with internal employees on the platform-internal channel', () => {
    expect(assistantId, 'the assistant created by the previous test').to.match(/^[0-9a-f-]{36}$/);
    loginToApi('anxin', 'admin');

    cy.visit(`/app/assistants/${assistantId}/publishing?channel=platform`);
    cy.get('app-platform-sharing').within(() => {
      cy.contains('legend', '可使用的帳號');
      cy.contains('label', '安心商行客服同仁').click();
      cy.contains('button', '儲存可使用的帳號').click();
      cy.get('[aria-live="polite"]').should('contain', '已更新可使用的帳號');
    });
  });

  it('lets an internal employee reach the assistant from the home page and get a grounded, cited answer', () => {
    expect(assistantId, 'the assistant created earlier').to.match(/^[0-9a-f-]{36}$/);
    loginToApi('anxin', 'internal');

    cy.visit('/app/home');
    cy.get('section[aria-labelledby="usable-title"]').contains('a', assistantName).click();
    cy.location('pathname').should('match', CHAT_PATH).and('include', `/app/chat/${assistantId}`);
    cy.contains('h1', assistantName).should('be.visible');

    ask(TRIAL_QUESTION);
    cy.get('[role="log"]', { timeout: STREAM_TIMEOUT }).should('not.have.attr', 'aria-busy', 'true');
    cy.get('[role="log"] [data-kind="company-data"]', { timeout: STREAM_TIMEOUT })
      .scrollIntoView()
      .should('be.visible')
      .and('contain', '根據你的資料');

    cy.contains('button', '查看引用來源').click();
    cy.get('[role="dialog"]')
      .should('be.visible')
      .and('have.attr', 'aria-modal', 'true')
      .and('contain', FIXTURE);
    cy.focused().should('have.class', 'drawer-close');
    cy.focused().type('{esc}');
    cy.get('[role="dialog"]').should('not.exist');
  });

  it('reloads the conversation and the reply from the backend without showing an error (regression #103)', () => {
    expect(assistantId, 'the assistant created earlier').to.match(/^[0-9a-f-]{36}$/);
    loginToApi('anxin', 'internal');

    cy.visit(`/app/chat/${assistantId}`);
    cy.get('[role="log"] [data-kind="company-data"]', { timeout: STREAM_TIMEOUT })
      .scrollIntoView()
      .should('be.visible');

    cy.reload();

    cy.contains('目前無法開啟對話').should('not.exist');
    cy.get('[role="log"] [data-kind="company-data"]', { timeout: STREAM_TIMEOUT })
      .scrollIntoView()
      .should('be.visible')
      .and('contain', '根據你的資料');
    cy.contains('button', '查看引用來源').scrollIntoView().should('be.visible');
  });

  it('answers an out-of-scope question with no-result and no citations', () => {
    expect(assistantId, 'the assistant created earlier').to.match(/^[0-9a-f-]{36}$/);
    loginToApi('anxin', 'internal');

    cy.visit(`/app/chat/${assistantId}`);
    ask(OUT_OF_SCOPE_QUESTION);

    // 對話串沿用之前試問的歷史（同一位員工、同一個助理），所以只在「這則」查無資料回覆裡找
    // citation-toggle，不檢查整個對話紀錄——較早的組織資料回覆本來就有引用按鈕。
    cy.get('[role="log"] [data-kind="no-result"]', { timeout: STREAM_TIMEOUT })
      .last()
      .scrollIntoView()
      .should('be.visible')
      .and('contain', '查無資料')
      .find('.citation-toggle')
      .should('not.exist');
  });

  it('deletes the assistant (which also removes its conversations) and the knowledge base', () => {
    expect(assistantId, 'the assistant created earlier').to.match(/^[0-9a-f-]{36}$/);
    expect(knowledgeBaseId, 'the knowledge base created earlier').to.match(/^[0-9a-f-]{36}$/);
    loginToApi('anxin', 'admin');

    cy.visit(`/app/assistants/${assistantId}/overview`);
    cy.contains('button', '刪除助理').click();
    cy.get('.delete-panel')
      .should('contain', `刪除助理「${assistantName}」？`)
      .and('contain', '所有成員與這個助理的對話紀錄也會一併刪除');
    cy.get('.delete-panel').contains('button', '刪除助理與所有對話').click();
    cy.location('pathname', { timeout: 20000 }).should('eq', '/app/assistants');
    cy.contains(assistantName).should('not.exist');

    cy.visit(`/app/knowledge/${knowledgeBaseId}/content`);
    cy.contains('h1', knowledgeBaseName).should('be.visible');
    cy.contains('button', '刪除知識庫').click();
    cy.get('.delete-panel').should('contain', `刪除知識庫「${knowledgeBaseName}」？`);
    cy.get('.delete-panel').contains('button', /^刪除$/).click();
    cy.location('pathname').should('eq', '/app/knowledge');
    cy.contains(knowledgeBaseName).should('not.exist');
  });
});
