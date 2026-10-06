import { loginToApi } from '../support/api-mode';

/**
 * M6 Slice 7（issue #244）：組織設定的三條流程——換對話模型與重跑題組、保存期限清理、立即刪除已保存的對話——
 * 以及非管理者看到的唯讀設定。
 *
 * 需要 API 提供第二個 Fake 模型（CI 的 `e2e-api` job：`Ai__Chat__Models__0__Provider=Fake`、
 * `Ai__Chat__Models__0__Model=fake-chat-second`、`Ai__Chat__Models__0__Id=second`；本機做法見 apps/admin-e2e/README.md）。
 *
 * - 會改設定、會刪對話的步驟都在 對照組織（`control`）：其他 spec 只拿它檢查「看不到 安心商行 的資料」，
 *   不在那裡對話、不讀它的模型或保存期限；`retention-cleanup` 只清它自己的對話（`--organization control`），
 *   安心商行 的設定與對話完全不受影響，同一次 CI 的其他 spec 也就不受影響。
 * - 第一個 test 先把 對照組織 的設定恢復成預設（上一次失敗留下的狀態），最後一個 test 刪掉助理與知識庫、
 *   再恢復一次預設並確認：模型是部署預設、保存期限永久、沒有待生效的期限。
 * - 唯讀的檢查用 安心商行 的客服同仁（對照組織 只有管理者）。
 * - 保存期限縮短有 7 天緩衝期；`retention-cleanup --as-of <38 天後>`（只在 Development／Testing 可用）讓緩衝期
 *   已過、30 天的期限先生效，截止點是 38 − 30 = 8 天後，所以這時所有對話都算過期。
 * - 跨 test 的 id 存在 `rememberValue` task（cypress.api.config.ts）：與其他 API 模式 spec 一致，換來源重新載入
 *   spec 時也不會不見。
 */
const FIXTURE = '../api/tests/fixtures/knowledge/return-policy.pdf';
const GROUNDED_QUESTION = '收到商品後七天內可申請退貨';
const PROCESSING_TIMEOUT = 90000;
const RUN_TIMEOUT = 90000;
const STREAM_TIMEOUT = 20000;
const GUID = '[0-9a-f-]{36}';
const ORGANIZATION = 'control';
/** 部署預設的模型（`Ai:Chat`，id 沒設定時就是 Model）與第二個模型（`Ai:Chat:Models:0`）。 */
const DEFAULT_MODEL = { id: 'fake-chat-dev', model: 'fake-chat-dev' };
const SECOND_MODEL = { id: 'second', model: 'fake-chat-second' };
const RETENTION_FOREVER = 'forever';
const DAY_MS = 24 * 60 * 60 * 1000;
/** `retention-cleanup` 是 API 的一次性子指令；`cy.exec` 的工作目錄是 apps/admin-e2e。 */
const CLEANUP_COMMAND = `dotnet run --no-build --project ../api/src/SmartAgri.Api -- retention-cleanup --organization ${ORGANIZATION}`;

interface OrgSettingsRun {
  readonly suffix: number;
  readonly knowledgeId: string;
  readonly assistantId: string;
}

const RUN_KEY = 'org-settings-api';

/** 系統設定頁在捲動容器裡：操作或檢查前先捲到區塊，否則 `be.visible` 會因為被裁切而假失敗。 */
function visitSettings(): void {
  cy.visit('/app/settings');
  cy.get('[data-chat-model-panel]', { timeout: 20000 }).should('exist');
  cy.get('[data-conversation-retention]').should('exist');
}

function chooseChatModel(id: string): void {
  cy.get('#chat-model-select').scrollIntoView().select(id);
  cy.get('#chat-model-status').should('contain', '已改用');
  cy.get('#chat-model-select').should('have.value', id);
}

/**
 * 把 對照組織 的模型與保存期限恢復成預設（只改不是預設的那一項）。呼叫前要先以 對照組織 的管理者登入。
 * 選回永久：目前是 N 天就是延長（立即生效）；目前永久但有待生效的期限，就等同「改回」。
 */
function restoreOrganizationDefaults(): void {
  visitSettings();
  cy.get('#chat-model-select', { timeout: 20000 }).then(($select) => {
    if ($select.val() !== DEFAULT_MODEL.id) chooseChatModel(DEFAULT_MODEL.id);
  });
  cy.get('#retention-days-select').then(($select) => {
    if ($select.val() !== RETENTION_FOREVER) {
      cy.wrap($select).scrollIntoView().select('永久');
      cy.get('#retention-status').should('contain', '永久');
    }
  });
  cy.get('#chat-model-select').should('have.value', DEFAULT_MODEL.id);
  cy.get('#retention-days-select').should('have.value', RETENTION_FOREVER);
  cy.get('[data-retention-pending]').should('not.exist');
}

function ask(question: string): void {
  cy.get('#chat-input').clear().type(question);
  cy.get('form.composer button[type="submit"]').click();
  cy.get('[role="log"]', { timeout: STREAM_TIMEOUT }).should('not.have.attr', 'aria-busy', 'true');
  cy.get('[role="log"] [data-kind="company-data"]', { timeout: STREAM_TIMEOUT }).last().should('exist');
}

/** 以目前登入的帳號開助理的對話頁，問一題，確認左側「對話紀錄」有這一串。 */
function startSavedThread(assistantId: string): void {
  cy.visit(`/app/chat/${assistantId}`);
  ask(GROUNDED_QUESTION);
  cy.get('.thread-list .thread', { timeout: STREAM_TIMEOUT }).should('have.length', 1);
}

function expectNoSavedThreads(assistantId: string): void {
  cy.visit(`/app/chat/${assistantId}`);
  cy.get('.rail-empty', { timeout: 20000 }).should('contain', '還沒有任何對話');
  cy.get('.thread-list').should('not.exist');
}

/** 等驗收頁的重跑都結束（共 `count` 次，沒有排隊中或執行中的）。 */
function waitForRunsCompleted(count: number): void {
  cy.get('.run-row', { timeout: RUN_TIMEOUT }).should(($rows) => {
    expect($rows, 'test runs').to.have.length(count);
    expect($rows.text(), 'no queued or running test runs').not.to.match(/排隊中|執行中/);
  });
  cy.get('.run-row').each(($row) => expect($row.text()).to.contain('已完成'));
}

describe('organization settings: chat model, retention and purging conversations (API mode)', () => {
  let run: OrgSettingsRun = { suffix: 0, knowledgeId: '', assistantId: '' };
  const knowledgeName = () => `E2E 組織設定知識庫 ${run.suffix}`;
  const assistantName = () => `E2E 組織設定助理 ${run.suffix}`;

  function remember(changes: Partial<OrgSettingsRun>): void {
    run = { ...run, ...changes };
    cy.task('rememberValue', { key: RUN_KEY, value: run }, { log: false });
  }

  beforeEach(() => {
    cy.task<OrgSettingsRun | null>('recallValue', RUN_KEY, { log: false }).then((saved) => {
      if (saved !== null) run = saved;
    });
  });

  it('prepares an assistant with a cited knowledge base in the control organization', () => {
    remember({ suffix: Date.now(), knowledgeId: '', assistantId: '' });
    loginToApi(ORGANIZATION, 'admin');
    restoreOrganizationDefaults();

    cy.visit('/app/knowledge');
    cy.get('button[page-header-actions]').contains('建立知識庫').click();
    cy.get('#knowledge-name').type(knowledgeName());
    cy.get('.create-panel').contains('button', '建立知識庫').click();
    cy.contains('tr', knowledgeName()).contains('a', knowledgeName()).click();
    cy.location('pathname').should('match', new RegExp(`^/app/knowledge/${GUID}/content$`)).then((path) => {
      remember({ knowledgeId: path.split('/')[3] });
    });
    cy.get('.upload-panel input[type="file"]').selectFile(FIXTURE, { force: true });
    cy.get('.document-status[data-status="ready"]', { timeout: PROCESSING_TIMEOUT }).should('have.length', 1);
    cy.contains('.document-row', 'return-policy.pdf').find('.document-select input').check();
    cy.contains('.approval-toolbar button', '批次確認生效（1）').click();
    cy.get('.document-effect[data-effect="in-effect"]', { timeout: 20000 }).should('have.length', 1);

    cy.visit('/app/assistants/new/purpose');
    cy.contains('label', '回答客戶問題').click();
    cy.get('#assistant-name').clear().type(assistantName());
    cy.get('#audience-internal').check();
    cy.contains('button', '下一步').click();
    cy.contains('.source-row', knowledgeName(), { timeout: 20000 }).contains('button', '加入').click();
    cy.contains('button', '下一步').click();
    cy.contains('button', '下一步').click();
    cy.get('#trial-question-input').type(GROUNDED_QUESTION);
    cy.get('.ask-submit').click();
    cy.get('.trial-answer', { timeout: STREAM_TIMEOUT }).first().should('have.attr', 'data-kind', 'company-data');
    cy.contains('button', '建立助理').click();
    cy.location('pathname', { timeout: 20000 }).should('match', new RegExp(`^/app/assistants/${GUID}/overview$`)).then((path) => {
      remember({ assistantId: path.split('/')[3] });
    });
  });

  it('flags the acceptance results after the chat model changes, until the test set is rerun', () => {
    expect(run.assistantId).to.match(/^[0-9a-f-]{36}$/);
    loginToApi(ORGANIZATION, 'admin');
    const acceptancePath = `/app/assistants/${run.assistantId}/acceptance`;

    cy.visit(acceptancePath);
    cy.get('.case-form textarea').type(GROUNDED_QUESTION);
    cy.get('.case-form button[type="submit"]').click();
    cy.contains('.case-card', GROUNDED_QUESTION).should('be.visible');
    cy.contains('.acceptance__summary button', '全部重跑').click();
    waitForRunsCompleted(1);
    cy.get('[data-model-changed]').should('not.exist');

    visitSettings();
    cy.get('#chat-model-select').should('have.value', DEFAULT_MODEL.id);
    chooseChatModel(SECOND_MODEL.id);
    cy.get('[data-chat-model-last-change]').should('contain', '上次變更：對照組織管理者');

    cy.visit(acceptancePath);
    waitForRunsCompleted(1);
    cy.get('[data-model-changed]').scrollIntoView().should('be.visible')
      .and('contain', `上次測試使用模型 ${DEFAULT_MODEL.model}，現在是 ${SECOND_MODEL.model}`)
      .and('contain', '建議重跑題組');

    cy.contains('.acceptance__summary button', '全部重跑').click();
    waitForRunsCompleted(2);
    cy.get('[data-model-changed]').should('not.exist');
    // 重新整理後仍然沒有提示：最新一次重跑記錄的就是現在的模型。
    cy.reload();
    waitForRunsCompleted(2);
    cy.get('[data-model-changed]').should('not.exist');

    // 恢復部署預設，後面的對話與清理都用預設模型。
    visitSettings();
    chooseChatModel(DEFAULT_MODEL.id);
  });

  it('shows the chat model and the retention read-only to an internal member', () => {
    loginToApi('anxin', 'internal');
    visitSettings();
    cy.get('[data-chat-model-panel]').scrollIntoView().within(() => {
      cy.contains('h2', '對話模型').should('be.visible');
      cy.contains(`目前使用：${DEFAULT_MODEL.model}`).should('be.visible');
      // 部署提供兩個模型，所以說明的是「只有管理者可以變更」，不是「只提供一個」。
      cy.contains('只有管理者可以變更對話模型。').should('be.visible');
      cy.get('select').should('not.exist');
    });
    cy.get('[data-conversation-retention]').scrollIntoView().within(() => {
      cy.contains('保存期限：永久').should('be.visible');
      cy.contains('只有管理者可以變更保存期限').should('be.visible');
      cy.get('select').should('not.exist');
      cy.get('button').should('not.exist');
    });
    // 各助理已保存的對話數只有管理者看得到。
    cy.get('[data-saved-conversations]').should('not.exist');
  });

  it('shortens the retention to 30 days, shows the buffer, and the cleanup 38 days later removes the old thread', () => {
    expect(run.assistantId).to.match(/^[0-9a-f-]{36}$/);
    loginToApi(ORGANIZATION, 'admin');
    startSavedThread(run.assistantId);

    visitSettings();
    cy.get('#retention-days-select').scrollIntoView().should('have.value', RETENTION_FOREVER).select('30 天');
    cy.get('[role="dialog"]').should('be.visible').within(() => {
      cy.contains('把保存期限縮短為 30 天？').should('be.visible');
      cy.contains('7 天緩衝期').should('be.visible');
      cy.get('[data-retention-preview-count]').should('contain', '大約會刪除').and('contain', '串對話');
      cy.contains('button', '縮短為 30 天').click();
    });
    cy.get('[role="dialog"]').should('not.exist');
    cy.get('#retention-status').should('contain', '已改為 30 天').and('contain', '起生效');
    cy.get('[data-retention-pending]').scrollIntoView().should('be.visible')
      .and('contain', '目前是 永久')
      .and('contain', '起改為 30 天')
      .and('contain', '在那之前不會刪除任何對話');

    // 緩衝期內對話還在。
    cy.visit(`/app/chat/${run.assistantId}`);
    cy.get('.thread-list .thread', { timeout: 20000 }).should('have.length', 1);

    const asOf = new Date(Date.now() + 38 * DAY_MS).toISOString();
    cy.exec(`${CLEANUP_COMMAND} --as-of ${asOf}`, { timeout: 120000, failOnNonZeroExit: false }).then((result) => {
      expect(result.exitCode, result.stderr).to.eq(0);
      expect(result.stdout).to.contain('保存期限已生效：永久 → 30 天');
      expect(result.stdout).to.match(/保存 30 天，截止點 \S+：刪除 [1-9]\d* 串對話/);
    });

    expectNoSavedThreads(run.assistantId);
    visitSettings();
    cy.get('#retention-days-select').scrollIntoView().should('have.value', '30');
    cy.get('[data-retention-pending]').should('not.exist');

    // 恢復永久（延長立即生效，不需確認），後面的對話不會被清理。
    cy.get('#retention-days-select').select('永久');
    cy.get('#retention-status').should('contain', '已改為 永久，立即生效');
    cy.get('#retention-days-select').should('have.value', RETENTION_FOREVER);
  });

  it('purges the assistant\'s saved conversations from the settings list after the acknowledgement', () => {
    expect(run.assistantId).to.match(/^[0-9a-f-]{36}$/);
    loginToApi(ORGANIZATION, 'admin');
    startSavedThread(run.assistantId);

    visitSettings();
    cy.get(`[data-assistant-row="${run.assistantId}"]`, { timeout: 20000 }).scrollIntoView().within(() => {
      cy.contains(assistantName()).should('be.visible');
      cy.get('[data-keep-state]').should('contain', '開啟');
      cy.get('[data-thread-count]').should('contain', '已保存 1 串對話（1 位成員）');
      cy.contains('button', '立即刪除').click();
    });
    cy.get('[role="dialog"]').should('be.visible').within(() => {
      cy.contains(`立即刪除「${assistantName()}」已保存的對話？`).should('be.visible');
      cy.get('.confirm-purge').should('be.disabled');
      cy.get('#purge-acknowledge').check();
      cy.get('.confirm-purge').should('be.enabled').click();
    });
    cy.get('[role="dialog"]').should('not.exist');
    cy.get('[data-purge-status]').should('contain', `已刪除「${assistantName()}」的 1 串對話`);
    cy.get(`[data-assistant-row="${run.assistantId}"] [data-thread-count]`).should('contain', '已保存 0 串對話');

    expectNoSavedThreads(run.assistantId);
  });

  it('removes its assistant and knowledge base and leaves the organization on the defaults', () => {
    expect(run.assistantId).to.match(/^[0-9a-f-]{36}$/);
    expect(run.knowledgeId).to.match(/^[0-9a-f-]{36}$/);
    loginToApi(ORGANIZATION, 'admin');
    cy.visit(`/app/assistants/${run.assistantId}/overview`);
    cy.contains('button', '刪除助理').click();
    cy.get('.delete-panel').contains('button', '刪除助理與所有對話').click();
    cy.location('pathname', { timeout: 20000 }).should('eq', '/app/assistants');
    cy.visit(`/app/knowledge/${run.knowledgeId}/content`);
    cy.contains('button', '刪除知識庫').click();
    cy.get('.delete-panel').contains('button', /^刪除$/).click();
    cy.location('pathname').should('eq', '/app/knowledge');

    restoreOrganizationDefaults();
  });
});
