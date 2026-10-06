import { loginToApi } from '../support/api-mode';

/**
 * M5b Slice 6（issue #234）：LINE 從擁有者填憑證、測試連線、啟用，到 LINE 使用者提問收到回答與暫停訊息。
 *
 * 需要假的 LINE 伺服器（tools/fake-line-server/server.mjs），API 的 `Line:ApiBaseUrl` 指到它，
 * 以及 `PublicChannels:PublicBaseUrl`（Webhook 網址的來源；CI 的 `e2e-api` job 與 apps/admin-e2e/README.md）。
 *
 * - 「LINE 平台」這一側全在 Node 裡（cypress.api.config.ts 的 `line*` task）：`lineAddBot` 讓假伺服器認得這次的
 *   Token、Secret 與官方帳號 ID；測試連線時假伺服器會真的送一個有簽章的測試事件到我們的 webhook。
 *   `lineSendText` 模擬 LINE 使用者傳訊息（task 以 Channel secret 對送出的位元組簽章），`lineMessages` 讀假伺服器
 *   收到的 reply／push。
 * - 後端收到 webhook 後在背景回答，所以讀 reply 要輪詢（`waitForReply`）。
 * - 每次執行都用新的 Token、Secret、官方帳號 ID 與 LINE 使用者，與其他 spec、上一次執行互不干擾；
 *   每個事件的 webhookEventId 都不同（後端依它去重）。
 * - 總共 3 個問題，遠低於預設的頻率限制（每位使用者每分鐘 6 題、每個群組每分鐘 10 題）。
 */
const FIXTURE = '../api/tests/fixtures/knowledge/return-policy.pdf';
const GROUNDED_QUESTION = '收到商品後七天內可申請退貨';
const PROCESSING_TIMEOUT = 90000;
const RUN_TIMEOUT = 90000;
const GUID = '[0-9a-f-]{36}';
const BOT_DISPLAY_NAME = '安心客服';
/** 假伺服器收到 reply 前的輪詢次數（每次間隔 1 秒）；Fake 模型通常幾秒內就回答。 */
const REPLY_POLL_ATTEMPTS = 45;

/** 假的 LINE 伺服器記錄的一次 reply 或 push（tools/fake-line-server/server.mjs 的 `/__control/messages`）。 */
interface RecordedLineMessage {
  readonly endpoint: 'reply' | 'push';
  readonly replyToken?: string | null;
  readonly to?: string | null;
  readonly messages: ReadonlyArray<{
    readonly type: string;
    readonly text?: string;
    readonly altText?: string;
    readonly contents?: { readonly type: string; readonly contents?: ReadonlyArray<{ readonly type: string; readonly body?: unknown }> };
  }>;
}

interface SentLineEvent {
  readonly status: number;
  readonly replyToken: string;
  readonly webhookEventId: string;
}

/** 跨 test 保留的資料（`rememberValue` task）。 */
interface LineRun {
  readonly suffix: number;
  readonly knowledgeId: string;
  readonly assistantId: string;
  /** 假伺服器給這個官方帳號的 userId：webhook 的 `destination`。 */
  readonly botUserId: string;
}

const RUN_KEY = 'line-api';

/** 在目前的 LINE 設定面板裡執行一組指令；每次呼叫都重新查詢，避免抓到儲存／測試後重新渲染掉的節點。 */
function inLineSetup(steps: () => void): void {
  cy.get('app-line-setup').within(steps);
}

function openLineSettings(assistantId: string): void {
  cy.visit(`/app/assistants/${assistantId}/publishing?channel=line`);
  cy.get('app-line-setup #line-officialAccountId', { timeout: 20000 }).should('exist');
}

/** 輪詢假伺服器，直到收到帶 `replyToken` 的 reply；回傳它（找不到就讓 test 失敗）。 */
function waitForReply(accessToken: string, replyToken: string, attempt = 0): Cypress.Chainable<RecordedLineMessage> {
  return cy.task<RecordedLineMessage[]>('lineMessages', accessToken, { log: false }).then((recorded) => {
    const reply = recorded.find((entry) => entry.endpoint === 'reply' && entry.replyToken === replyToken);
    if (reply !== undefined) return cy.wrap(reply, { log: false });
    expect(attempt, `fake LINE received the reply to ${replyToken}`).to.be.lessThan(REPLY_POLL_ATTEMPTS);
    cy.wait(1000, { log: false });
    return waitForReply(accessToken, replyToken, attempt + 1);
  });
}

describe('LINE from enabling the channel to answering a question (API mode)', () => {
  let run: LineRun = { suffix: 0, knowledgeId: '', assistantId: '', botUserId: '' };
  const knowledgeName = () => `E2E LINE 知識庫 ${run.suffix}`;
  const assistantName = () => `E2E LINE 助理 ${run.suffix}`;
  // LINE 的格式：官方帳號 ID `@` 加 3–20 個英數字，Channel ID 10 位數字，Secret 32 位十六進位，Token 至少 40 字元。
  const officialAccountId = () => `@e2e${run.suffix}`;
  const channelId = () => String(run.suffix).slice(-10);
  const channelSecret = () => run.suffix.toString(16).padStart(32, 'a');
  const accessToken = () => `e2e-line-token-${run.suffix}-0123456789abcdefghijklmnopqrstuvwxyz`;
  const lineUserId = () => `U${run.suffix.toString(16).padStart(32, '0')}`;
  const lineGroupId = () => `C${run.suffix.toString(16).padStart(32, '0')}`;

  function remember(changes: Partial<LineRun>): void {
    run = { ...run, ...changes };
    cy.task('rememberValue', { key: RUN_KEY, value: run }, { log: false });
  }

  function sendText(text: string, group?: { groupId: string; mention: string }): Cypress.Chainable<SentLineEvent> {
    return cy.task<SentLineEvent>('lineSendText', {
      assistantId: run.assistantId,
      channelSecret: channelSecret(),
      destination: run.botUserId,
      userId: lineUserId(),
      text,
      ...group,
    });
  }

  beforeEach(() => {
    cy.task<LineRun | null>('recallValue', RUN_KEY, { log: false }).then((saved) => {
      if (saved !== null) run = saved;
    });
  });

  it('prepares an assistant with a cited knowledge base and a LINE official account', () => {
    remember({ suffix: Date.now(), knowledgeId: '', assistantId: '', botUserId: '' });
    cy.task<{ userId: string }>('lineAddBot', {
      accessToken: accessToken(),
      channelSecret: channelSecret(),
      basicId: officialAccountId(),
      displayName: BOT_DISPLAY_NAME,
    }).then(({ userId }) => {
      expect(userId).to.match(/^U[0-9a-f]{32}$/);
      remember({ botUserId: userId });
    });

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

  it('saves the credentials, passes the connection test, refuses to enable before acceptance passes, then enables', () => {
    expect(run.assistantId).to.match(/^[0-9a-f-]{36}$/);
    loginToApi('anxin', 'admin');
    openLineSettings(run.assistantId);

    inLineSetup(() => {
      cy.contains('button', '測試連線').should('be.disabled');
      // 打完字先確認欄位真的收到完整內容，再送出；否則掉字會讓之後的斷言誤報。
      cy.get('#line-officialAccountId').clear().type(officialAccountId()).should('have.value', officialAccountId());
      cy.get('#line-channelId').clear().type(channelId()).should('have.value', channelId());
      cy.get('#line-channelSecret').type(channelSecret(), { log: false }).should('have.value', channelSecret());
      cy.get('#line-accessToken').type(accessToken(), { log: false }).should('have.value', accessToken());
      cy.get('#line-welcomeMessage').clear().type('歡迎加入安心商行！');
      cy.intercept('PUT', `**/api/v1/assistants/${run.assistantId}/publishing/line`).as('saveLine');
      cy.contains('form.settings button[type="submit"]', '儲存').click();
    });
    cy.wait('@saveLine').its('response.statusCode').should('eq', 200);
    inLineSetup(() => {
      cy.get('form.settings .feedback').should('contain', '已儲存');
      // 憑證只寫不讀：只剩「已設定・末四碼」，畫面上沒有原文。
      cy.get('#line-channelSecret-status').should('contain', `末四碼 ${channelSecret().slice(-4)}`);
      cy.get('#line-accessToken-status').should('contain', `末四碼 ${accessToken().slice(-4)}`);
      // within() 的範圍就是 app-line-setup 本身，所以用 cy.root()，不能再 cy.get('app-line-setup')。
      cy.root().should('not.contain.text', accessToken()).and('not.contain.text', channelSecret());
      cy.get('.checklist li[data-state="pending"]').should('have.length', 3);
      cy.get('#line-webhook-url').should('contain', `/api/v1/line/webhook/${run.assistantId}`);
      cy.contains('button', '測試連線').should('be.enabled').click();
    });
    // 三項檢查：Token 屬於填寫的官方帳號、Webhook 網址設到假伺服器、假伺服器送來的有簽章測試事件通過驗證。
    inLineSetup(() => {
      cy.get('.checklist li[data-state="passed"]', { timeout: 20000 }).should('have.length', 3);
      cy.get('[role="status"]').should('contain', '三項檢查都通過');
      cy.contains('.block--status button', /^啟用$/).click();
    });

    // 驗收從沒跑過：啟用被拒，只列出驗收（連線已通過），並連到驗收題組頁。
    inLineSetup(() => {
      cy.get('.block--status .error-summary li[data-reason="acceptance"]').should('exist');
      cy.get('.block--status .error-summary li[data-reason="connection"]').should('not.exist');
      cy.get('.block--status').should('not.have.attr', 'data-serving', 'serving');
      cy.get('.block--status .error-summary').contains('a', '前往驗收題組頁').click();
    });
    cy.location('pathname').should('eq', `/app/assistants/${run.assistantId}/acceptance`);

    cy.get('.case-form textarea').type(GROUNDED_QUESTION);
    cy.get('.case-form button[type="submit"]').click();
    cy.contains('.case-card', GROUNDED_QUESTION).should('be.visible');
    cy.contains('.acceptance__summary button', '全部重跑').click();
    cy.contains('.run-row', '已完成', { timeout: RUN_TIMEOUT }).should('contain', '通過 1 / 失敗 0');

    openLineSettings(run.assistantId);
    inLineSetup(() => {
      cy.get('.checklist li[data-state="passed"]').should('have.length', 3);
      cy.contains('.block--status button', /^啟用$/).click();
    });
    inLineSetup(() => {
      cy.contains('.block--status .feedback', '已啟用 LINE 頻道').scrollIntoView().should('be.visible');
      cy.get('.block--status').should('have.attr', 'data-serving', 'serving');
    });
  });

  it('answers a LINE user with a text reply and a Flex card of its sources', () => {
    expect(run.assistantId).to.match(/^[0-9a-f-]{36}$/);
    expect(run.botUserId).to.match(/^U[0-9a-f]{32}$/);
    sendText(GROUNDED_QUESTION).then(({ status, replyToken }) => {
      expect(status, 'webhook status').to.eq(200);
      waitForReply(accessToken(), replyToken).then((reply) => {
        expect(reply.messages.map((message) => message.type)).to.deep.eq(['text', 'flex']);
        const [answer, sources] = reply.messages;
        // 組織資料的回答：引用標記改成可讀的「（來源 n）」，沒有 Markdown 的 [n]。
        expect(answer.text).to.be.a('string').and.match(/（來源 \d+(、\d+)*）/);
        expect(answer.text).not.to.match(/\[\d+\]/);
        expect(sources.altText).to.eq('參考來源：return-policy.pdf');
        expect(sources.contents?.type).to.eq('carousel');
        expect(sources.contents?.contents).to.have.length(1);
        const bubble = JSON.stringify(sources.contents?.contents?.[0]);
        expect(bubble).to.contain(knowledgeName()).and.contain('return-policy.pdf');
      });
    });
    // 在期限內就用 reply 回答：沒有補送（push）。
    cy.task<RecordedLineMessage[]>('lineMessages', accessToken()).then((recorded) => {
      expect(recorded.filter((entry) => entry.endpoint === 'push')).to.have.length(0);
    });
  });

  it('answers in a group when a member mentions the official account', () => {
    expect(run.assistantId).to.match(/^[0-9a-f-]{36}$/);
    const group = { groupId: lineGroupId(), mention: `@${BOT_DISPLAY_NAME}` };
    sendText(GROUNDED_QUESTION, group).then(({ status, replyToken }) => {
      expect(status, 'webhook status').to.eq(200);
      waitForReply(accessToken(), replyToken).then((reply) => {
        expect(reply.messages[0].type).to.eq('text');
        expect(reply.messages[0].text).to.match(/（來源 \d+(、\d+)*）/);
      });
    });
  });

  it('replies 「目前暫停服務」 once the owner pauses the LINE channel', () => {
    expect(run.assistantId).to.match(/^[0-9a-f-]{36}$/);
    loginToApi('anxin', 'admin');
    openLineSettings(run.assistantId);
    inLineSetup(() => {
      cy.contains('.block--status button', '暫停服務').click();
    });
    inLineSetup(() => {
      cy.contains('.block--status .feedback', '已暫停 LINE').scrollIntoView().should('be.visible');
      cy.get('.block--status').should('have.attr', 'data-serving', 'paused');
    });

    sendText(GROUNDED_QUESTION).then(({ status, replyToken }) => {
      expect(status, 'webhook status').to.eq(200);
      waitForReply(accessToken(), replyToken).then((reply) => {
        expect(reply.messages).to.deep.eq([{ type: 'text', text: '目前暫停服務' }]);
      });
    });
  });

  it('removes its assistant, knowledge base and fake LINE account', () => {
    expect(run.assistantId).to.match(/^[0-9a-f-]{36}$/);
    expect(run.knowledgeId).to.match(/^[0-9a-f-]{36}$/);
    loginToApi('anxin', 'admin');
    cy.visit(`/app/assistants/${run.assistantId}/overview`);
    cy.contains('button', '刪除助理').click();
    cy.get('.delete-panel').contains('button', '刪除助理與所有對話').click();
    cy.location('pathname').should('eq', '/app/assistants');
    cy.visit(`/app/knowledge/${run.knowledgeId}/content`);
    cy.contains('button', '刪除知識庫').click();
    cy.get('.delete-panel').contains('button', /^刪除$/).click();
    cy.location('pathname').should('eq', '/app/knowledge');

    // 助理刪除後，同一個 webhook 一律是固定的 401。
    sendText(GROUNDED_QUESTION).its('status').should('eq', 401);
    cy.task('lineReset', accessToken());
  });
});
