import { loginToApi } from '../support/api-mode';

/**
 * M4 issue #149：在對話中查詢已連接數據庫的授權紀錄，對真實 API、PostgreSQL 與 Fake 模型跑。
 *
 * 1. 擁有者建立數據庫（定期回報模板）、一個只有空知識庫的助理，連接數據庫、分享給內部同仁，
 *    並把同仁指定為資料管理者（同仁的帳號本來就有「查看同意提交的紀錄」）。
 * 2. 外部客戶經表單連結送出一筆紀錄。
 * 3. 同仁在對話中問「近 30 天有幾筆紀錄？」：Fake 模型選擇固定查詢 `record-count`（近 30 天），
 *    回答標明資料來源、統計期間與伺服器算的數字（1 筆）；重新整理仍在。
 * 4. 資料不足：同仁指定一位只有一筆紀錄的追蹤對象做比較（`#query:` 指令替 Fake 模型選工具），
 *    得到「資料不足」，不畫任何數字。
 * 5. 無權限：擁有者撤銷同仁的指定 → 同一個問題得到「目前無法查詢」，不出現數據庫名稱或數字；
 *    重新讀取時，先前的回答也改為無法查詢（每次讀取都重新授權）。
 *
 * 其他拒絕（他組織、撤銷帳號權限、解除連接、定義外參數）、工具與模型失敗、不保存對話、
 * 用量紀錄由後端整合測試涵蓋（`ChatDatabaseQueryEndpointsTests`）。
 * 名稱帶時間戳記，可以重覆執行；最後一個 `it` 刪除助理與知識庫（數據庫目前沒有刪除功能）。
 */

const GUID = '[0-9a-f-]{36}';
const STREAM_TIMEOUT = 20000;

function ask(question: string): void {
  cy.get('#chat-input').clear().type(question, { parseSpecialCharSequences: false });
  cy.get('form.composer button[type="submit"]').click();
  cy.get('[role="log"]', { timeout: STREAM_TIMEOUT }).should('not.have.attr', 'aria-busy', 'true');
}

describe('database queries in a conversation against the real API', () => {
  const suffix = Date.now();
  const databaseName = `E2E 回報 ${suffix}`.slice(0, 40);
  const knowledgeName = `E2E 查詢知識庫 ${suffix}`;
  const assistantName = `E2E 查詢助理 ${suffix}`;
  let databaseId = '';
  let knowledgeId = '';
  let assistantId = '';
  let subjectId = '';

  it('connects a database to an assistant shared with an internal employee designated to read its records', () => {
    loginToApi('anxin', 'admin');

    cy.visit('/app/databases');
    cy.get('button[page-header-actions]').contains('新增資料庫').click();
    cy.get('input[type="radio"][value="template-periodic-report"]').check({ force: true });
    cy.get('#database-name').clear().type(databaseName);
    cy.get('.create-panel button[type="submit"]').click();
    cy.location('pathname', { timeout: 20000 })
      .should('match', new RegExp(`^/app/databases/${GUID}/form$`))
      .then((path) => {
        databaseId = path.split('/')[3];
      });

    cy.visit('/app/knowledge');
    cy.get('button[page-header-actions]').contains('建立知識庫').click();
    cy.get('#knowledge-name').type(knowledgeName);
    cy.get('.create-panel').contains('button', '建立知識庫').click();
    cy.contains('tr', knowledgeName).contains('a', knowledgeName).click();
    cy.location('pathname').should('match', new RegExp(`^/app/knowledge/${GUID}/content$`)).then((path) => {
      knowledgeId = path.split('/')[3];
    });

    cy.visit('/app/assistants/new/purpose');
    cy.contains('label', '回答客戶問題').click();
    cy.get('#assistant-name').clear().type(assistantName);
    cy.get('#audience-internal').check();
    cy.contains('button', '下一步').click();
    cy.contains('.source-row', knowledgeName, { timeout: 20000 }).contains('button', '加入').click();
    cy.contains('button', '下一步').click();
    cy.contains('button', '下一步').click();
    cy.get('#trial-question-input').type('營業時間');
    cy.get('.ask-submit').click();
    cy.get('.trial-answer', { timeout: STREAM_TIMEOUT }).should('exist');
    cy.contains('button', '建立助理').click();
    cy.location('pathname', { timeout: 20000 })
      .should('match', new RegExp(`^/app/assistants/${GUID}/overview$`))
      .then((path) => {
        assistantId = path.split('/')[3];
      });

    cy.then(() => cy.visit(`/app/assistants/${assistantId}/data-sources`));
    cy.intercept('PUT', /\/api\/v1\/assistants\/[^/]+\/sources\/database\/[^/]+$/).as('connect');
    cy.contains('.source-row', databaseName, { timeout: 20000 }).contains('button', '加入').click();
    cy.wait('@connect').its('response.statusCode').should('eq', 200);

    cy.then(() => cy.visit(`/app/assistants/${assistantId}/publishing?channel=platform`));
    cy.get('app-platform-sharing').within(() => {
      cy.contains('label', '安心商行客服同仁').click();
      cy.contains('button', '儲存可使用的帳號').click();
      cy.get('[aria-live="polite"]').should('contain', '已更新可使用的帳號');
    });

    cy.then(() => cy.visit(`/app/databases/${databaseId}/access`));
    cy.contains('h3', '誰可以查看收集紀錄').should('be.visible');
    cy.contains('.choice', '內部同仁').find('input[type="checkbox"]').check();
    cy.contains('button', '儲存資料管理者').click();
    cy.get('[aria-live="polite"]').should('contain', '已更新資料管理者');
  });

  it('receives one record from an external customer through the form link', () => {
    expect(databaseId).to.match(new RegExp(`^${GUID}$`));
    loginToApi('anxin', 'customer');
    cy.intercept('POST', `/api/v1/databases/${databaseId}/submissions`).as('submit');
    cy.visit(`/app/forms/${databaseId}`);
    cy.contains('h1', databaseName).should('be.visible');
    cy.contains('app-inline-form label', '回報日期').click();
    cy.focused().type('2026-10-01');
    cy.contains('app-inline-form label', '本期完成數量').click();
    cy.focused().clear().type('5');
    cy.contains('button', '下一步：確認同意').click();
    cy.get('#consent-agree').check();
    cy.contains('button', '同意並送出').click();
    cy.wait('@submit').its('response.statusCode').should('eq', 201);
  });

  it('answers a designated member with the server’s count, its period and its source, and keeps it on reload', () => {
    expect(assistantId).to.match(new RegExp(`^${GUID}$`));
    loginToApi('anxin', 'internal');
    cy.visit(`/app/chat/${assistantId}`);

    ask('近30天有幾筆紀錄？');
    cy.get('[role="log"] [data-kind="database-query"]').last().within(() => {
      cy.get('.query-source').should('contain', databaseName).and('contain', '紀錄筆數').and('contain', '近 30 天');
      cy.get('.query-figures tbody tr').first().should('contain', '有效紀錄筆數').and('contain', '1 筆');
    });
    // 查詢回答不能轉人工。
    cy.get('[role="log"] li').last().find('.handoff-trigger').should('not.exist');

    cy.reload();
    cy.get('[role="log"] [data-kind="database-query"]', { timeout: STREAM_TIMEOUT }).last().should('contain', databaseName).and('contain', '1 筆');
  });

  it('says there are too few records to compare one subject', () => {
    expect(databaseId).to.match(new RegExp(`^${GUID}$`));
    loginToApi('anxin', 'internal');
    cy.intercept('GET', `/api/v1/databases/${databaseId}/tracking`).as('tracking');
    cy.visit(`/app/databases/${databaseId}/records`);
    cy.wait('@tracking').then((interception) => {
      subjectId = String(interception.response?.body?.subjects?.[0]?.subject?.id ?? '');
      expect(subjectId).to.match(new RegExp(`^${GUID}$`));
    });

    cy.then(() => {
      cy.visit(`/app/chat/${assistantId}`);
      // 真實模型會自己選工具；Fake 模型需要 `#query:` 指令才會指定追蹤對象。
      const directive = JSON.stringify({ name: 'database_subject_comparison', arguments: { databaseId, subjectId } });
      ask(`這位追蹤對象的趨勢統計？ #query:${directive}`);
    });
    cy.get('[role="log"] [data-kind="database-query"]').last().within(() => {
      cy.root().should('contain', '資料不足');
      cy.get('.query-notice').should('contain', '目前只有 1 筆紀錄');
      cy.get('.query-figures').should('not.exist');
    });
  });

  it('refuses without naming the database once the designation is revoked, also for the earlier answer', () => {
    expect(databaseId).to.match(new RegExp(`^${GUID}$`));
    loginToApi('anxin', 'admin');
    cy.visit(`/app/databases/${databaseId}/access`);
    cy.contains('.choice', '內部同仁').find('input[type="checkbox"]').uncheck();
    cy.contains('button', '儲存資料管理者').click();
    cy.get('[aria-live="polite"]').should('contain', '已更新資料管理者');

    loginToApi('anxin', 'internal');
    cy.visit(`/app/chat/${assistantId}`);
    ask('近30天有幾筆紀錄？');
    cy.get('[role="log"] [data-kind="database-query"]').last().within(() => {
      cy.root().should('contain', '目前無法查詢');
      cy.get('.query-source').should('not.exist');
      cy.get('.query-figures').should('not.exist');
    });
    cy.reload();
    cy.get('[role="log"] [data-kind="database-query"]', { timeout: STREAM_TIMEOUT }).should('have.length.at.least', 3);
    cy.get('[role="log"]').should('not.contain', databaseName).and('not.contain', '1 筆');
  });

  it('removes its assistant and knowledge base', () => {
    expect(assistantId).to.match(new RegExp(`^${GUID}$`));
    loginToApi('anxin', 'admin');
    cy.visit(`/app/assistants/${assistantId}/overview`);
    cy.contains('button', '刪除助理').click();
    cy.get('.delete-panel').contains('button', '刪除助理與所有對話').click();
    cy.location('pathname').should('eq', '/app/assistants');
    cy.visit(`/app/knowledge/${knowledgeId}/content`);
    cy.contains('button', '刪除知識庫').click();
    cy.get('.delete-panel').contains('button', /^刪除$/).click();
    cy.location('pathname').should('eq', '/app/knowledge');
  });
});
