import { auditA11y, loginAs } from '../support/a11y';

/**
 * Issue #171：對話中表單請求的體驗（mock 模式）。Mock 以「模型判斷」模式示範：助理有可用表單時，
 * 每題先收到 `form-check`，約 1.5 秒後才開始回答。
 *
 * ① 判斷中的等待狀態：只在 `form-check` 後出現，螢幕報讀器只聽到固定文字；減少動態效果時只顯示第一句。
 * ② 表單卡片的退路：× 與「不用了」不送出任何資料、訊息改成已關閉、焦點回到輸入框；
 *    「前往我送出的資料」有輸入時先確認。
 * ③ 「回報資料」固定入口：只有一份表單時直接開啟同一張卡片；已開啟時把焦點移進卡片；手機寬度放在輸入框上方。
 * 多份表單的選單（方向鍵／Esc）在 mock 種子只有一份表單，由元件測試涵蓋
 * （`chat-conversation-form-ux.component.spec.ts`）。
 */

const CHECKING_TEXT = '助理正在整理你的問題';

function emulateReducedMotion(value: 'reduce' | 'no-preference'): void {
  cy.wrap(
    Cypress.automation('remote:debugger:protocol', {
      command: 'Emulation.setEmulatedMedia',
      params: { features: [{ name: 'prefers-reduced-motion', value }] },
    }),
    { log: false },
  );
}

function askInChat(question: string): void {
  cy.get('#chat-input').type(question);
  cy.get('form.composer button[type="submit"]').click();
}

function openOfferedForm(): void {
  cy.contains('button.suggested-prompt', '回報訂單問題').click();
  cy.get('[role="log"] [data-kind="form-request"]')
    .last()
    .within(() => {
      cy.get('.form-offer').should('have.text', '我找到一份可能相關的表單。要現在開始填寫嗎？');
      cy.contains('button', '填寫表單').click();
    });
  cy.get('app-inline-form').should('be.visible');
}

describe('form-request experience (#171)', () => {
  beforeEach(() => {
    cy.clearLocalStorage();
    loginAs('外部客戶');
  });

  afterEach(() => emulateReducedMotion('no-preference'));

  it('① shows the checking state only while the server checks, announced once as fixed text', () => {
    cy.visit('/use/assistant-customer-service');
    askInChat('收到商品後幾天內可以退貨？');

    cy.get('[app-form-check-status]').within(() => {
      cy.get('[role="status"]').should('contain.text', CHECKING_TEXT);
      cy.get('.phrases').should('have.attr', 'aria-hidden', 'true');
      cy.get('.phrase').should('have.length', 4).first().should('have.text', '正在整理問題重點…');
    });
    // 等待中只有「停止回答」可用，與既有的停止行為相同。
    cy.get('button.stop').should('be.visible');
    // 只掃等待狀態本身：串流中建議問題全部停用，`.suggestions` 捲動區沒有可聚焦內容是 #171 之前就有的狀況。
    auditA11y('[app-form-check-status]');

    // 判斷完成：等待狀態收起，換成回答。
    cy.get('[role="log"] [data-kind="company-data"]').should('be.visible');
    cy.get('[app-form-check-status]').should('not.exist');
    cy.get('[role="log"]').should('not.have.attr', 'aria-busy');
  });

  it('① with reduced motion shows only the first sentence, without the shimmer', () => {
    emulateReducedMotion('reduce');
    cy.visit('/use/assistant-customer-service');
    askInChat('收到商品後幾天內可以退貨？');

    cy.get('[app-form-check-status] .phrase').first().should('be.visible').and('have.css', 'color', 'rgb(82, 97, 112)')
      .and('have.css', 'animation-name', 'none');
    cy.get('[app-form-check-status] .phrase').eq(1).should('not.be.visible');
    cy.get('[role="log"] [data-kind="company-data"]').should('be.visible');
  });

  it('① stop during the check keeps only the question', () => {
    cy.visit('/use/assistant-customer-service');
    askInChat('收到商品後幾天內可以退貨？');
    cy.get('[app-form-check-status]').should('exist');
    cy.contains('button.stop', '停止回答').click();
    cy.get('[app-form-check-status]').should('not.exist');
    cy.contains('.run-note', '已停止回答，這則問題沒有回覆。').should('be.visible');
    cy.focused().should('have.id', 'chat-input');
  });

  it('② × closes the offered form without sending anything and returns focus to the input', () => {
    cy.visit('/use/assistant-customer-service');
    openOfferedForm();
    cy.focused().should('have.id', 'chat-field-field-order-number').type('DEMO-7101');
    auditA11y('app-inline-form');

    cy.get('app-inline-form button[aria-label="關閉表單，不填寫"]').should('have.css', 'width', '44px').click();
    cy.get('app-inline-form').should('not.exist');
    cy.focused().should('have.id', 'chat-input');
    cy.get('[role="log"] [data-kind="form-request"]').last().within(() => {
      cy.get('.form-dismissed').should('have.text', '已關閉表單，沒有送出任何資料。');
      cy.get('button.form-start').should('not.exist');
    });
    cy.get('.chat-body > [role="status"]').should('contain.text', '已關閉表單，沒有送出任何資料。');
    cy.get('[data-kind="submission-receipt"]').should('not.exist');

    // 可以繼續正常對話。
    askInChat('收到商品後幾天內可以退貨？');
    cy.get('[role="log"] [data-kind="company-data"]').should('be.visible');

    loginAs('SMB 管理者');
    cy.visit('/app/databases/database-orders/records');
    cy.contains('DEMO-7101').should('not.exist');
  });

  it('② 「不用了」 behaves the same', () => {
    cy.visit('/use/assistant-customer-service');
    openOfferedForm();
    cy.get('app-inline-form .actions button').then((buttons) => {
      expect([...buttons].map((button) => button.textContent?.trim())).to.deep.equal(['下一步：確認同意', '不用了']);
    });
    cy.get('app-inline-form').contains('button', '不用了').click();
    cy.get('app-inline-form').should('not.exist');
    cy.get('[role="log"] [data-kind="form-request"] .form-dismissed').should('be.visible');
    cy.focused().should('have.id', 'chat-input');
  });

  it('② confirms before leaving for 我送出的資料 with input, and goes straight there without', () => {
    cy.visit('/use/assistant-customer-service');
    openOfferedForm();
    cy.get('app-inline-form .withdraw-hint').should('contain.text', '想撤回已送出的資料？');
    cy.get('#chat-field-field-order-number').type('DEMO-7201');
    cy.contains('app-inline-form a', '前往我送出的資料').click();

    cy.get('[role="dialog"]')
      .should('contain.text', '離開會清除已填的內容')
      .and('have.attr', 'aria-modal', 'true');
    cy.get('[role="dialog"]').should('have.attr', 'aria-labelledby');
    cy.focused().should('have.text', '繼續填寫');
    auditA11y();
    cy.focused().type('{esc}');
    cy.get('[role="dialog"]').should('not.exist');
    cy.focused().should('have.text', '前往我送出的資料');
    cy.get('#chat-field-field-order-number').should('have.value', 'DEMO-7201');

    cy.contains('app-inline-form a', '前往我送出的資料').click();
    cy.contains('[role="dialog"] button', '離開').click();
    cy.location('pathname').should('eq', '/app/activity');

    // 沒有輸入：直接前往。
    cy.visit('/use/assistant-customer-service');
    openOfferedForm();
    cy.contains('app-inline-form a', '前往我送出的資料').click();
    cy.location('pathname').should('eq', '/app/activity');
  });

  it('③ the 回報資料 entry opens the only form directly, left of the input', () => {
    cy.visit('/use/assistant-customer-service');
    cy.get('form.composer').children().first().should('match', 'app-form-entry');
    cy.contains('form.composer button', '回報資料').should('not.have.attr', 'aria-haspopup');
    cy.contains('form.composer button', '回報資料').find('svg').should('have.attr', 'aria-hidden', 'true');
    cy.get('[role="log"]').then((log) => log.find('li').length).as('before');

    cy.contains('form.composer button', '回報資料').click();
    cy.get('app-inline-form h2').should('contain.text', '訂單資料庫');
    cy.focused().should('have.id', 'chat-field-field-order-number');

    // 已開啟時再按：焦點回到已開啟的卡片，不開第二張。
    cy.get('#chat-input').focus();
    cy.contains('form.composer button', '回報資料').click();
    cy.get('app-inline-form').should('have.length', 1);
    cy.focused().should('have.id', 'chat-field-field-order-number');

    // 從入口開的卡片同樣有退路；關閉後不在對話紀錄留下任何東西。
    cy.get('app-inline-form button[aria-label="關閉表單，不填寫"]').click();
    cy.get('app-inline-form').should('not.exist');
    cy.focused().should('have.id', 'chat-input');
    cy.get<number>('@before').then((before) => {
      cy.get('[role="log"]').should((log) => expect(log.find('li')).to.have.length(before));
    });
  });

  it('③ on a 360px phone sits above the input and only 停止回答 shows while waiting', () => {
    cy.viewport(360, 740);
    cy.visit('/use/assistant-customer-service');
    cy.get('form.composer app-form-entry').should('not.exist');
    cy.get('.composer-area .entry-row').contains('button', '回報資料').should('be.visible');
    cy.get('.composer-area').children().first().should('have.class', 'entry-row');
    cy.document().its('documentElement').then((root) => expect(root.scrollWidth).to.be.at.most(root.clientWidth));

    askInChat('收到商品後幾天內可以退貨？');
    cy.get('[app-form-check-status]').should('exist');
    cy.get('form.composer button[type="submit"]').should('not.exist');
    cy.contains('button.stop', '停止回答').should('be.visible');
    cy.get('[role="log"] [data-kind="company-data"]').should('be.visible');
    cy.get('form.composer button[type="submit"]').should('be.visible');

    cy.get('.entry-row').contains('button', '回報資料').click();
    cy.get('app-inline-form .actions button').each((button) => {
      expect(button.outerWidth()).to.be.closeTo(Cypress.$('app-inline-form .actions').width() ?? 0, 1);
    });
  });

  it('shows neither the checking state nor the entry for an assistant without forms', () => {
    loginAs('SMB 管理者');
    cy.visit('/use/assistant-internal-onboarding');
    cy.get('#chat-input').should('be.visible');
    cy.contains('button', '回報資料').should('not.exist');
    askInChat('商品怎麼保養？');
    // 沒有表單判斷：一送出就是「正在回答…」，不經過等待狀態。
    cy.get('app-streaming-reply').should('exist');
    cy.get('[app-form-check-status]').should('not.exist');
    cy.get('[role="log"]').should('not.have.attr', 'aria-busy');
  });
});
