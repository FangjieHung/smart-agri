import { loginToApi } from '../support/api-mode';

/**
 * M4 issue #148：助理連接數據庫並在對話中請求表單，對真實 API、PostgreSQL 與 Fake 模型跑。
 *
 * 1. 擁有者建立數據庫（客戶基本資料模板）與一個只有空知識庫的助理，在「資料來源」連接數據庫、
 *    在「回答與記錄」設定寫入對象與收集目的，再分享給內部同仁。
 * 2. 同仁在對話中要求填寫 → 表單請求顯示接收單位、目的與實際可查看者 → 取消：不送出任何資料。
 * 3. 再次填寫、明確同意後送出 → 真實回執出現在對話中（201，來源為對話）；重新整理仍在。
 * 4. 提交者本人在對話收據上撤回（#146）→ 收據顯示已撤回、不含內容，重新整理仍是已撤回；「我送出的資料」
 *    標示來源為助理對話。
 * 5. 權限撤回：擁有者解除連接後，重新讀取的舊表單請求顯示「目前無法使用」（每次讀取都重新授權），
 *    新的要求不再出現表單。送出端點在撤回後回 403 assistant-form 由後端整合測試涵蓋
 *    （`AssistantDatabaseFormEndpointsTests`，含分享／權限撤回）。
 *
 * 另外（#150）設定「每週」定期報表，並確認資料庫的「定期報表」頁籤列出排程；產生報表的排程、摘要與權限由後端整合測試
 * （`PeriodicReportEndpointsTests`）涵蓋，因為第一份報表要等一期結束。
 *
 * 名稱帶時間戳記，可以重覆執行；最後一個 `it` 刪除助理與知識庫（數據庫目前沒有刪除功能）。
 */

const GUID = '[0-9a-f-]{36}';
const FORM_QUESTION = '我想要填寫客戶資料';
const PURPOSE = '記錄客戶聯絡方式，方便客服回電。';
const STREAM_TIMEOUT = 20000;

function ask(question: string): void {
  cy.get('#chat-input').clear().type(question);
  cy.get('form.composer button[type="submit"]').click();
  cy.get('[role="log"]', { timeout: STREAM_TIMEOUT }).should('not.have.attr', 'aria-busy', 'true');
}

function fillCustomerForm(name: string): void {
  cy.get('#chat-field-field-customer-name').clear().type(name);
  cy.get('app-inline-form input[type="radio"][value="企業"]').check();
  cy.get('app-inline-form button[type="submit"]').click();
}

describe('assistant forms against the real API', () => {
  const suffix = Date.now();
  const databaseName = `E2E 客戶 ${suffix}`.slice(0, 40);
  const knowledgeName = `E2E 表單知識庫 ${suffix}`;
  const assistantName = `E2E 表單助理 ${suffix}`;
  let databaseId = '';
  let knowledgeId = '';
  let assistantId = '';

  it('connects a database to an assistant and sets what it collects and why', () => {
    loginToApi('anxin', 'admin');

    cy.visit('/app/databases');
    cy.get('button[page-header-actions]').contains('新增資料庫').click();
    cy.get('input[type="radio"][value="template-customer-profile"]').check({ force: true });
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

    // 建立精靈在 API 模式只列知識庫；資料庫從建立後的「資料來源」連接。
    cy.visit('/app/assistants/new/purpose');
    cy.contains('label', '回答客戶問題').click();
    cy.get('#assistant-name').clear().type(assistantName);
    cy.get('#audience-internal').check();
    cy.contains('button', '下一步').click();
    cy.contains('.source-row', knowledgeName, { timeout: 20000 }).contains('button', '加入').click();
    cy.contains('.source-row', databaseName).should('not.exist');
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
    cy.contains('.source-row', databaseName).find('button.source-toggle').should('have.attr', 'aria-pressed', 'true');

    cy.then(() => cy.visit(`/app/assistants/${assistantId}/rules`));
    cy.intercept('PATCH', /\/api\/v1\/assistants\/[^/]+\/settings$/).as('rules');
    cy.get('#data-write-database').select(databaseName);
    // 先選資料庫、還沒填目的：伺服器要求目的，畫面就地提示。
    cy.get('#data-write-purpose-error', { timeout: 20000 }).should('contain', '請說明收集目的');
    cy.get('#data-write-purpose').type(PURPOSE);
    cy.wait('@rules');
    cy.get('#data-write-purpose-error').should('not.exist');
    cy.reload();
    cy.get('#data-write-database').find('option:selected').should('have.text', databaseName);
    cy.get('#data-write-purpose').should('have.value', PURPOSE);

    // 定期報表（#150）：週期存到伺服器，重新整理仍在；資料庫的「定期報表」頁籤列出排程。第一份報表要等
    // 這一期結束才會產生（由背景工作排在期間結束時），所以這裡是「還沒有報表」，不是錯誤。
    cy.get('#periodic-report').select('每週一次');
    cy.wait('@rules').its('response.body.rules.periodicReport').should('eq', 'weekly');
    cy.reload();
    cy.get('#periodic-report').should('have.value', 'weekly');
    cy.then(() => cy.visit(`/app/databases/${databaseId}/reports`));
    cy.get('app-database-reports .schedules').should('contain', assistantName).and('contain', '每週報表');
    cy.get('app-database-reports').should('contain', '還沒有報表');

    // 數據庫詳情列出這個助理。
    cy.then(() => cy.visit(`/app/databases/${databaseId}/assistants`));
    cy.get('.assistant-list').should('contain', assistantName);

    cy.then(() => cy.visit(`/app/assistants/${assistantId}/publishing?channel=platform`));
    cy.get('app-platform-sharing').within(() => {
      cy.contains('label', '安心商行客服同仁').click();
      cy.contains('button', '儲存可使用的帳號').click();
      cy.get('[aria-live="polite"]').should('contain', '已更新可使用的帳號');
    });
  });

  it('asks for the form in the conversation; cancelling sends nothing, consenting returns a real receipt', () => {
    expect(assistantId).to.match(new RegExp(`^${GUID}$`));
    loginToApi('anxin', 'internal');
    cy.intercept('POST', /\/api\/v1\/assistants\/[^/]+\/chat\/forms\/[^/]+\/submissions$/).as('submit');
    cy.visit(`/app/chat/${assistantId}`);

    ask(FORM_QUESTION);
    cy.get('[role="log"] [data-kind="form-request"]').last().within(() => {
      cy.contains('button.form-start', databaseName).click();
    });

    // 取消：沒有送出任何請求。
    fillCustomerForm('取消的客戶');
    cy.get('app-consent-confirmation').within(() => {
      cy.contains('接收單位').next().should('contain', databaseName);
      cy.contains('收集目的').next().should('contain', PURPOSE);
      cy.contains('可查看者').next().should('contain', '安心商行管理者');
      cy.contains('button', '返回修改').click();
    });
    cy.get('app-inline-form').contains('button', '取消').click();
    cy.get('app-inline-form').should('not.exist');
    cy.get('@submit.all').should('have.length', 0);

    // 同意後送出：201、來源為對話，收據出現在對話裡。
    cy.get('[role="log"] [data-kind="form-request"]').last().find('button.form-start').click();
    fillCustomerForm('王小明');
    cy.get('button.consent-submit').should('be.disabled');
    cy.get('#consent-agree').check();
    cy.get('button.consent-submit').click();
    cy.wait('@submit').then(({ response }) => {
      expect(response?.statusCode).to.eq(201);
      expect(response?.body.receipt.source).to.eq('assistant-conversation');
      expect(response?.body.receipt.purpose).to.eq(PURPOSE);
    });
    cy.get('app-consent-confirmation').should('not.exist');
    cy.get('[role="log"] [data-kind="submission-receipt"]').last()
      .should('contain', '已送出')
      .and('contain', '王小明')
      .and('contain', '回執編號');

    cy.reload();
    cy.get('[role="log"] [data-kind="submission-receipt"]', { timeout: STREAM_TIMEOUT }).last().should('contain', '王小明');
  });

  it('lets the submitter withdraw from the in-chat receipt; the conversation reads it back withdrawn (#146)', () => {
    expect(assistantId).to.match(new RegExp(`^${GUID}$`));
    loginToApi('anxin', 'internal');
    cy.intercept('POST', /\/api\/v1\/submissions\/[^/]+\/withdrawal$/).as('withdraw');
    cy.visit(`/app/chat/${assistantId}`);

    cy.get('[role="log"] [data-kind="submission-receipt"]', { timeout: STREAM_TIMEOUT }).last().within(() => {
      cy.get('.withdrawal-notice').should('have.attr', 'data-status', 'available').and('contain', '回執編號');
      cy.get('button.withdraw').click();
    });
    cy.get('button.confirm-withdraw').click();
    cy.wait('@withdraw').its('response.statusCode').should('eq', 200);
    cy.get('.withdraw-feedback').should('contain', '已撤回這筆資料');
    cy.get('[role="log"] [data-kind="submission-receipt"]').last()
      .should('not.contain', '王小明')
      .find('.withdrawal-notice')
      .should('have.attr', 'data-status', 'withdrawn')
      .and('contain', '你已撤回這筆資料');

    // 重新讀取：對話表沒變，收據依提交 id 讀到已撤回的回執，不含內容、沒有撤回鍵。
    cy.reload();
    cy.get('[role="log"] [data-kind="submission-receipt"]', { timeout: STREAM_TIMEOUT }).last()
      .should('not.contain', '王小明')
      .within(() => {
        cy.get('.withdrawal-notice').should('have.attr', 'data-status', 'withdrawn');
        cy.get('button.withdraw').should('not.exist');
      });

    // 「我送出的資料」列出對話來源的提交，標示來源與已撤回。
    cy.visit('/app/activity');
    cy.contains('app-own-submissions li', databaseName, { timeout: STREAM_TIMEOUT })
      .should('have.attr', 'data-withdrawn', 'true')
      .and('contain', '來源：助理對話');
  });

  it('stops offering the form as soon as the database is disconnected', () => {
    expect(assistantId).to.match(new RegExp(`^${GUID}$`));
    loginToApi('anxin', 'admin');
    cy.visit(`/app/assistants/${assistantId}/data-sources`);
    cy.contains('.source-row', databaseName, { timeout: 20000 }).contains('button', '已連接').click();
    cy.contains('.source-row', databaseName).find('button.source-toggle').should('have.attr', 'aria-pressed', 'false');

    loginToApi('anxin', 'internal');
    cy.visit(`/app/chat/${assistantId}`);
    cy.get('[role="log"] [data-kind="form-request"]', { timeout: STREAM_TIMEOUT }).first()
      .should('contain', '目前無法使用')
      .find('button.form-start')
      .should('not.exist');
    ask(FORM_QUESTION);
    cy.get('[role="log"] app-chat-message').last().find('[data-kind]').should('not.have.attr', 'data-kind', 'form-request');

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
