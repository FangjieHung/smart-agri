import { auditA11y, loginAs } from '../support/a11y';

/** 管理者可以直接開啟的主要畫面。 */
const ADMIN_ROUTES: readonly (readonly [string, string])[] = [
  ['/app/home', '首頁'],
  ['/app/assistants', '我的助理'],
  ['/app/assistants/new/purpose', '建立新助理'],
  ['/app/assistants/assistant-customer-service/overview', '客服助理'],
  ['/app/assistants/assistant-customer-service/data-sources', '搜尋資料來源'],
  ['/app/assistants/assistant-customer-service/rules', '找不到資料時'],
  ['/app/knowledge', '知識庫'],
  ['/app/knowledge/knowledge-product-guide/content', '商品使用指南'],
  ['/app/databases', '資料庫'],
  ['/app/databases/database-customer-records/records', '客戶資料庫'],
  ['/app/databases/database-customer-records/access', '誰可以查看收集紀錄'],
  ['/app/channels', '發布管道'],
  ['/app/assistants/assistant-customer-service/publishing?channel=line', '客服助理'],
  ['/app/settings', '團隊與權限'],
];

/** 用 CDP 模擬使用者的「減少動態效果」偏好。 */
function emulateReducedMotion(value: 'reduce' | 'no-preference'): void {
  cy.wrap(
    Cypress.automation('remote:debugger:protocol', {
      command: 'Emulation.setEmulatedMedia',
      params: { features: [{ name: 'prefers-reduced-motion', value }] },
    }),
    { log: false },
  );
}

describe('accessibility', () => {
  beforeEach(() => {
    cy.clearLocalStorage();
  });

  it('has no critical or serious violations on the public landing and demo login', () => {
    cy.visit('/');
    cy.contains('h1', '讓每一次服務回覆，都更有依據').should('be.visible');
    auditA11y();

    cy.visit('/login');
    cy.contains('h1', 'Demo 登入').should('be.visible');
    auditA11y();
  });

  it('has no critical or serious violations on the expired demo session state', () => {
    cy.visit('/app/home', {
      onBeforeLoad(win) {
        win.sessionStorage.setItem(
          'demo-session',
          JSON.stringify({ accountId: 'account-smb-admin', lastActiveAt: 0 }),
        );
      },
    });
    cy.contains('Demo 登入已逾時').should('be.visible');
    auditA11y();
  });

  describe('workspace routes as the SMB administrator', () => {
    beforeEach(() => loginAs('SMB 管理者'));

    for (const [route, marker] of ADMIN_ROUTES) {
      it(`has no critical or serious violations on ${route}`, () => {
        cy.visit(route);
        cy.contains(marker).should('be.visible');
        auditA11y();
      });
    }

    it('has no critical or serious violations on the loading, partial failure and permission states', () => {
      cy.visit('/app/knowledge?demoScenario=loading');
      cy.get('[data-state="loading"]').should('be.visible');
      auditA11y();

      cy.visit('/app/knowledge?demoScenario=partial-failure');
      cy.get('.partial-notice').should('be.visible');
      auditA11y();

      cy.visit('/app/knowledge?demoScenario=permission-denied');
      cy.get('[data-state="permission-denied"]').should('be.visible');
      auditA11y();
    });

    it('lets the keyboard skip the side navigation and land in the page content', () => {
      cy.visit('/app/assistants');

      cy.get('.skip-link').should('exist').focus();
      cy.focused().should('contain.text', '跳到主要內容').and('be.visible');
      cy.focused().click();
      cy.focused().should('have.id', 'main-content');
      cy.get('#main-content').should('contain.text', '我的助理');
    });

    it('points the LINE error summary at the field that needs fixing', () => {
      cy.visit('/app/assistants/assistant-customer-service/publishing?channel=line');
      cy.get('app-line-setup .error-summary')
        .scrollIntoView()
        .should('be.visible')
        .and('contain.text', 'Channel access token');
      auditA11y('app-line-setup');

      cy.get('app-line-setup .error-summary a').first().click();
      cy.focused().should('have.id', 'line-accessToken');
    });
  });

  describe('end user chat as the external customer', () => {
    beforeEach(() => loginAs('外部客戶'));

    it('has no critical or serious violations in the assistant chat', () => {
      cy.visit('/use/assistant-customer-service');
      cy.contains('h1', '客服助理').should('be.visible');
      auditA11y();

      cy.get('#chat-input').type('收到商品後幾天內可以退貨？');
      cy.get('form.composer button[type="submit"]').click();
      cy.get('[role="log"] [data-kind="company-data"]').should('be.visible');
      auditA11y();
    });

    it('traps focus inside the citation drawer and returns it on close', () => {
      cy.visit('/use/assistant-customer-service');
      cy.get('#chat-input').type('收到商品後幾天內可以退貨？');
      cy.get('form.composer button[type="submit"]').click();
      cy.contains('button', '查看引用來源').click();

      cy.get('[role="dialog"]')
        .should('have.attr', 'aria-modal', 'true')
        .and('have.attr', 'aria-labelledby');
      auditA11y();

      // CDK focus trap 的錨點包住對話框，焦點停在關閉鍵。
      cy.get('.cdk-focus-trap-anchor').should('have.length', 2);
      cy.focused().should('have.class', 'drawer-close');

      cy.focused().type('{esc}');
      cy.get('[role="dialog"]').should('not.exist');
      cy.focused().should('contain.text', '查看引用來源');
    });

    it('has no critical or serious violations in the withdrawal confirmation', () => {
      cy.visit('/use/assistant-customer-service');
      cy.contains('button.suggested-prompt', '回報訂單問題').click();
      cy.get('[role="log"] [data-kind="form-request"]').contains('button', '填寫表單').click();
      cy.get('#chat-field-field-order-number').type('DEMO-5001');
      cy.contains('app-inline-form label', '配送延遲').click();
      cy.get('#chat-field-field-reported-on').type('2026-09-21');
      cy.contains('button', '下一步：確認同意').click();
      cy.get('#consent-agree').check();
      cy.contains('button', '同意並送出').click();

      cy.get('[data-kind="submission-receipt"]').should('be.visible');
      auditA11y();

      cy.contains('button', '撤回這筆資料').click();
      cy.get('[role="dialog"]')
        .should('have.attr', 'aria-modal', 'true')
        .and('have.attr', 'aria-labelledby');
      cy.get('.cdk-focus-trap-anchor').should('have.length', 2);
      cy.focused().should('have.class', 'confirm-cancel');
      auditA11y();

      cy.focused().type('{esc}');
      cy.get('[role="dialog"]').should('not.exist');
      cy.focused().should('contain.text', '撤回這筆資料');
    });
  });

  describe('team settings as a non-admin persona', () => {
    it('has no critical or serious violations on the refused team panel', () => {
      loginAs('內部使用者');
      cy.visit('/app/settings');
      cy.contains('無法查看團隊設定').should('be.visible');
      auditA11y();
    });
  });

  describe('team permission editor expanded', () => {
    it('has no critical or serious violations with every checkbox rendered', () => {
      loginAs('SMB 管理者');
      cy.visit('/app/settings');
      cy.contains('button', '變更 安心商行客服同仁 的權限').click();
      cy.get('.member__editor input[type="checkbox"]').should('have.length', 7);
      auditA11y();
    });
  });

  describe('embedded chat as an anonymous visitor', () => {
    it('has no critical or serious violations without any demo persona', () => {
      cy.visit('/use/assistant-customer-service');
      cy.contains('h1', '客服助理').should('be.visible');
      auditA11y();

      cy.get('#chat-input').type('收到商品後幾天內可以退貨？');
      cy.get('form.composer button[type="submit"]').click();
      cy.get('[role="log"] [data-kind="company-data"]').should('be.visible');
      auditA11y();

      cy.visit('/use/assistant-customer-service?embed=1');
      cy.get('#chat-input').should('be.visible');
      auditA11y();
    });

    it('has no critical or serious violations on the refusal state', () => {
      cy.visit('/use/assistant-internal-onboarding');
      cy.contains('無法開啟這個助理').should('be.visible');
      auditA11y();
    });
  });

  it('removes transitions when the visitor prefers reduced motion', () => {
    loginAs('SMB 管理者');

    emulateReducedMotion('reduce');
    cy.visit('/app/home');
    cy.get('.ui-button').first().then(($button) => {
      const duration = getComputedStyle($button[0]).transitionDuration;
      expect(duration, 'reduced motion transition duration').to.match(/^0(\.\d+)?m?s$/);
    });

    emulateReducedMotion('no-preference');
  });
});
