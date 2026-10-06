import { loginToApi } from '../support/api-mode';

/**
 * M5a Slice 12（issue #204）：從擁有者設定官網嵌入、發布，到訪客在 API 提供的對話視窗問答。
 *
 * 需要 API 提供 widget（`Widget:RootPath` 指到 `nx build widget` 的產出、`Widget:EmbedScriptPath`
 * 指到 embed.js）與 `PublicChannels:PublicBaseUrl`（CI 的 `e2e-api` job 與 apps/admin-e2e/README.md）。
 *
 * - 訪客頁 `/use/{id}` 在 API 的來源（預設 http://localhost:5153，環境變數 `ADMIN_E2E_API_URL` 可覆寫），與 admin
 *   不同源；每個訪客 test 的第一個 `cy.visit` 就是 API，所以不需要 `cy.origin`。換來源時 Cypress 會在新來源
 *   重新載入 spec，模組變數會不見，所以跨 test 的資料存在 `rememberValue` task（cypress.api.config.ts）。
 *   Cypress 把受測頁面放在自己的 iframe 裡（`Sec-Fetch-Dest: iframe`），所以頁面照常提供。
 * - 訪客的工作階段只存在該分頁的 `sessionStorage`，Cypress 每個 test 開始會清掉；為了模擬「同一位訪客
 *   在擁有者暫停後再問一題」，先把第一個 test 的 `sessionStorage` 存下來，之後的訪客 test 在頁面載入前放回去。
 * - Cypress 會移除受測頁面的 CSP 標頭，所以 `frame-ancestors` 用 `cy.request` 檢查標頭；瀏覽器真正的阻擋
 *   留給 Slice 13 手動驗收。
 * - 用量上限以 `set-token-limit` CLI 調整（`cy.exec`，與 API 同一個資料庫設定）。用量快取 30 秒
 *   （`OrganizationTokenUsage.CacheDuration`，沒有設定可改），所以改完後輪詢 `/api/v1/organization/usage`
 *   直到狀態變了；最後一定恢復成部署預設值，並等到狀態回到正常，之後的 spec 不受影響。
 * - 訪客總共問 4 題、建立 1 個工作階段，遠低於預設的頻率限制（每位訪客每分鐘 6 題）。
 */
/** API 的網址，也是 `PublicChannels:PublicBaseUrl`（cypress.api.config.ts 的 `apiUrl`）；`before` 讀進來。 */
let API_URL = 'http://localhost:5153';
const FIXTURE = '../api/tests/fixtures/knowledge/return-policy.pdf';
const GROUNDED_QUESTION = '收到商品後七天內可申請退貨';
const DOMAIN = 'shop-e2e.example.com';
const PROCESSING_TIMEOUT = 90000;
const RUN_TIMEOUT = 90000;
const GUID = '[0-9a-f-]{36}';
/** `set-token-limit` 是 API 的一次性子指令；`cy.exec` 的工作目錄是 apps/admin-e2e。 */
const TOKEN_LIMIT_COMMAND = 'dotnet run --no-build --project ../api/src/SmartAgri.Api -- set-token-limit --organization anxin --tokens';
const USAGE_POLL_ATTEMPTS = 25;

type UsageState = 'normal' | 'near' | 'exceeded';

function setTokenLimit(tokens: string): void {
  cy.exec(`${TOKEN_LIMIT_COMMAND} ${tokens}`, { timeout: 120000 }).its('exitCode').should('eq', 0);
}

/** 重新整理首頁直到組織用量是 `state`（用量快取最多 30 秒才會看到新的上限）。呼叫前要先以擁有者登入。 */
function waitForUsageState(state: UsageState, attempt = 0): void {
  expect(attempt, `usage state ${state} polling attempts`).to.be.lessThan(USAGE_POLL_ATTEMPTS);
  cy.intercept('GET', '**/api/v1/organization/usage').as('usage');
  cy.visit('/app/home');
  cy.wait('@usage').then(({ response }) => {
    expect(response?.statusCode).to.eq(200);
    if ((response?.body as { state: UsageState }).state !== state) {
      cy.wait(2000);
      waitForUsageState(state, attempt + 1);
    }
  });
}

function openWebsiteSettings(assistantId: string): void {
  cy.visit(`/app/assistants/${assistantId}/publishing?channel=website`);
  // 版面的內容區會自己捲動，面板常在可視範圍外（Cypress 判定為被裁切）；操作時 Cypress 會自動捲過去。
  cy.get('#website-title', { timeout: 20000 }).should('exist');
}

function askVisitor(question: string): void {
  cy.get('textarea#question').clear().type(question);
  cy.contains('button.send', '送出').click();
}

/** 跨 test 保留的資料：test 換到 API 的來源時 Cypress 會重新載入 spec，所以存在 `rememberValue` task。 */
interface EmbedRun {
  readonly suffix: number;
  readonly knowledgeId: string;
  readonly assistantId: string;
  /** 第一位訪客的 `sessionStorage`（工作階段 token 與前文），之後的訪客 test 放回去。 */
  readonly visitorStorage: Record<string, string>;
}

const RUN_KEY = 'website-embed-api';

describe('website embedding from publishing to a visitor conversation (API mode)', () => {
  const welcome = '您好，請問有什麼可以幫忙？';
  let run: EmbedRun = { suffix: 0, knowledgeId: '', assistantId: '', visitorStorage: {} };
  const knowledgeName = () => `E2E 官網知識庫 ${run.suffix}`;
  const assistantName = () => `E2E 官網助理 ${run.suffix}`;
  const displayName = () => `安心小幫手 ${run.suffix}`;

  function remember(changes: Partial<EmbedRun>): void {
    run = { ...run, ...changes };
    cy.task('rememberValue', { key: RUN_KEY, value: run }, { log: false });
  }

  beforeEach(() => {
    cy.env<{ apiUrl?: string }>(['apiUrl']).then(({ apiUrl }) => {
      if (typeof apiUrl === 'string' && apiUrl !== '') API_URL = apiUrl.replace(/\/$/, '');
    });
    cy.task<EmbedRun | null>('recallValue', RUN_KEY, { log: false }).then((saved) => {
      if (saved !== null) run = saved;
    });
  });

  function visitAsSameVisitor(): void {
    expect(Object.keys(run.visitorStorage), 'stored visitor session').to.have.length.greaterThan(0);
    cy.visit(`${API_URL}/use/${run.assistantId}?host=https://${DOMAIN}`, {
      onBeforeLoad(win) {
        for (const [key, value] of Object.entries(run.visitorStorage)) win.sessionStorage.setItem(key, value);
      },
    });
    cy.get('textarea#question', { timeout: 20000 }).should('be.visible');
  }

  it('prepares an assistant with a cited knowledge base', () => {
    remember({ suffix: Date.now(), knowledgeId: '', assistantId: '', visitorStorage: {} });
    loginToApi('anxin', 'admin');
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
    cy.get('.trial-answer', { timeout: 20000 }).first().should('have.attr', 'data-kind', 'company-data');
    cy.contains('button', '建立助理').click();
    cy.location('pathname', { timeout: 20000 }).should('match', new RegExp(`^/app/assistants/${GUID}/overview$`)).then((path) => {
      remember({ assistantId: path.split('/')[3] });
    });
  });

  it('saves the website settings, refuses to publish before acceptance passes, then publishes', () => {
    expect(run.assistantId).to.match(/^[0-9a-f-]{36}$/);
    loginToApi('anxin', 'admin');
    openWebsiteSettings(run.assistantId);

    cy.get('#website-display-name').clear().type(displayName());
    cy.get('#website-welcome').clear().type(welcome);
    cy.get('#website-domain-input').type(DOMAIN);
    cy.contains('.domain-row button', '加入網域').click();
    cy.get('ul.domain-list').should('contain', DOMAIN);
    cy.intercept('PUT', `**/api/v1/assistants/${run.assistantId}/publishing/website`).as('saveWebsite');
    cy.contains('form.settings button[type="submit"]', '儲存官網設定').click();
    cy.wait('@saveWebsite').its('response.statusCode').should('eq', 200);
    cy.get('form.settings .save-status').should('contain', '已儲存官網設定');

    // Saved but never published: the chat window is the fixed unavailable page that may not be framed.
    cy.request({ url: `${API_URL}/use/${run.assistantId}`, failOnStatusCode: false }).then((response) => {
      expect(response.status).to.eq(404);
      expect(response.headers['content-security-policy']).to.contain("frame-ancestors 'none'");
    });

    // Acceptance has never run: the gate refuses, names the reason and links to the acceptance page.
    cy.contains('.block--status button', '發布官網嵌入').click();
    cy.get('mat-dialog-container').should('contain', knowledgeName()).contains('button', '確認發布').click();
    cy.get('.block--status .error-summary li[data-reason="acceptance"]').should('exist');
    cy.get('.block--status .error-summary').contains('a', '前往驗收題組頁').click();
    cy.location('pathname').should('eq', `/app/assistants/${run.assistantId}/acceptance`);

    cy.get('.case-form textarea').type(GROUNDED_QUESTION);
    cy.get('.case-form button[type="submit"]').click();
    cy.contains('.case-card', GROUNDED_QUESTION).should('be.visible');
    cy.contains('.acceptance__summary button', '全部重跑').click();
    cy.contains('.run-row', '已完成', { timeout: RUN_TIMEOUT }).should('contain', '通過 1 / 失敗 0');

    openWebsiteSettings(run.assistantId);
    cy.contains('.block--status button', '發布官網嵌入').click();
    cy.get('mat-dialog-container').contains('button', '確認發布').click();
    cy.get('.block--status .action-status').should('contain', '已發布官網嵌入');
    cy.get('.block--status').should('have.attr', 'data-serving', 'serving');
    cy.get('pre.embed-code').should(
      'contain',
      `<script src="${API_URL}/embed.js" data-assistant="${run.assistantId}" async></script>`,
    );
  });

  it('serves the chat window only to the allowed domain (frame-ancestors)', () => {
    expect(run.assistantId).to.match(/^[0-9a-f-]{36}$/);
    cy.request({ url: `${API_URL}/use/${run.assistantId}`, headers: { 'Sec-Fetch-Dest': 'iframe' } }).then((response) => {
      expect(response.status).to.eq(200);
      expect(response.headers['cache-control']).to.eq('no-store');
      expect(response.headers['content-security-policy']).to.match(new RegExp(`frame-ancestors https://${DOMAIN.replace(/\./g, '\\.')}(;|$| )`));
      expect(response.headers['content-security-policy']).not.to.contain("frame-ancestors 'none'");
    });
    cy.request({ url: `${API_URL}/use/00000000-0000-4000-8000-000000000000`, failOnStatusCode: false }).then((response) => {
      expect(response.status).to.eq(404);
      expect(response.headers['content-security-policy']).to.contain("frame-ancestors 'none'");
      expect(response.body).to.contain('這個對話視窗目前無法使用');
    });
  });

  it('lets a visitor ask on the widget page and see a citation', () => {
    expect(run.assistantId).to.match(/^[0-9a-f-]{36}$/);
    cy.visit(`${API_URL}/use/${run.assistantId}?host=https://${DOMAIN}`);
    cy.contains('h1.title', displayName(), { timeout: 20000 }).should('be.visible');
    cy.get('p.welcome').should('contain', welcome);
    askVisitor(GROUNDED_QUESTION);
    cy.get('[role="log"] article[data-kind="company-data"]', { timeout: 30000 }).should('have.length', 1);
    cy.get('[role="log"]').should('not.have.attr', 'aria-busy');
    cy.contains('[role="log"] button.citation-toggle', '查看引用來源').click();
    cy.get('[role="dialog"]').should('contain', '引用來源').and('contain', knowledgeName()).and('contain', 'return-policy.pdf');
    cy.get('[role="dialog"]').contains('button', '關閉').click();
    cy.window().then((win) => {
      const visitorStorage: Record<string, string> = {};
      for (let index = 0; index < win.sessionStorage.length; index += 1) {
        const key = win.sessionStorage.key(index);
        if (key !== null && key.startsWith('smartagri-widget:')) visitorStorage[key] = win.sessionStorage.getItem(key) ?? '';
      }
      expect(Object.keys(visitorStorage)).to.include(`smartagri-widget:${run.assistantId}:session`);
      remember({ visitorStorage });
    });
  });

  it('lets the owner see the installation and pause the channel', () => {
    expect(run.assistantId).to.match(/^[0-9a-f-]{36}$/);
    loginToApi('anxin', 'admin');
    openWebsiteSettings(run.assistantId);
    // The visitor session reported the embedding page's https origin (passive installation detection).
    cy.get('ul.seen-list').should('contain', `最後一次在 ${DOMAIN} 偵測到`);
    cy.contains('.block--status button', '暫停服務').click();
    cy.get('.block--status .action-status').should('contain', '已暫停官網嵌入');
    cy.get('.block--status').should('have.attr', 'data-serving', 'paused');
  });

  it('tells the same visitor the service is paused on the next question', () => {
    visitAsSameVisitor();
    cy.get('[role="log"] article[data-kind="company-data"]').should('have.length', 1);
    askVisitor(GROUNDED_QUESTION);
    cy.contains('.paused', '目前暫停服務', { timeout: 20000 }).should('be.visible');
    cy.contains('.paused button', '再試一次').should('be.visible');
    cy.get('textarea#question').should('not.exist');
  });

  it('resumes the channel, then suspends it once the organization exceeds its token limit', () => {
    expect(run.assistantId).to.match(/^[0-9a-f-]{36}$/);
    loginToApi('anxin', 'admin');
    openWebsiteSettings(run.assistantId);
    cy.contains('.block--status button', '恢復服務').click();
    cy.get('.block--status .action-status').should('contain', '已恢復官網嵌入');
    cy.get('.block--status').should('have.attr', 'data-serving', 'serving');

    // Usage so far (wizard trial, acceptance run, the visitor's answer) is well over one token.
    setTokenLimit('1');
    waitForUsageState('exceeded');
    cy.get('.usage--banner[data-state="exceeded"]').should('be.visible').and('contain', '已超過上限')
      .and('contain', '官網與 LINE 等對外回覆已暫停');
    openWebsiteSettings(run.assistantId);
    cy.get('.block--status').should('have.attr', 'data-serving', 'suspended-quota')
      .and('contain', '自動暫停：本月用量已達上限');
  });

  it('tells the visitor the service is paused while the quota is exceeded', () => {
    visitAsSameVisitor();
    askVisitor(GROUNDED_QUESTION);
    cy.contains('.paused', '目前暫停服務', { timeout: 20000 }).should('be.visible');
    cy.contains('.paused button', '再試一次').should('be.visible');
  });

  it('restores the token limit and removes its assistant and knowledge base', () => {
    loginToApi('anxin', 'admin');
    setTokenLimit('default');
    waitForUsageState('normal');
    cy.get('.usage--banner').should('not.exist');

    expect(run.assistantId).to.match(/^[0-9a-f-]{36}$/);
    expect(run.knowledgeId).to.match(/^[0-9a-f-]{36}$/);
    cy.visit(`/app/assistants/${run.assistantId}/overview`);
    cy.contains('button', '刪除助理').click();
    cy.get('.delete-panel').contains('button', '刪除助理與所有對話').click();
    cy.location('pathname').should('eq', '/app/assistants');
    cy.visit(`/app/knowledge/${run.knowledgeId}/content`);
    cy.contains('button', '刪除知識庫').click();
    cy.get('.delete-panel').contains('button', /^刪除$/).click();
    cy.location('pathname').should('eq', '/app/knowledge');
    cy.request({ url: `${API_URL}/use/${run.assistantId}`, failOnStatusCode: false }).its('status').should('eq', 404);
  });
});
