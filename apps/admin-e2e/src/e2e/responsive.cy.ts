import { loginAs } from '../support/a11y';

const PHONE: readonly [number, number] = [360, 780];
const DESKTOP: readonly [number, number] = [1280, 900];

/** 管理者可直接開啟的主要畫面，含每頁必須看得到的關鍵控制項。 */
const ADMIN_ROUTES: readonly (readonly [string, string])[] = [
  ['/app/home', '建立新助理'],
  ['/app/assistants', '建立新助理'],
  ['/app/assistants/new/purpose', '下一步'],
  ['/app/assistants/assistant-customer-service/overview', '客服助理'],
  ['/app/assistants/assistant-customer-service/data-sources', '搜尋資料來源'],
  ['/app/assistants/assistant-customer-service/rules', '找不到資料時'],
  ['/app/knowledge', '商品使用指南'],
  ['/app/knowledge/knowledge-product-guide/content', '加入示範文件'],
  ['/app/databases', '建立資料庫'],
  ['/app/databases/database-customer-records/records', '收集紀錄'],
  ['/app/databases/database-customer-records/trends', '趨勢比較'],
  ['/app/channels', '助理發布設定'],
  ['/app/assistants/assistant-customer-service/publishing?channel=line', '儲存並檢查'],
  ['/app/settings', '外觀設定'],
];

/** 整頁不得出現水平捲動。 */
function expectNoHorizontalOverflow(route: string): void {
  cy.window().then((win) => {
    // Cypress 把應用程式跑在自己的 iframe 裡，iframe 有獨立的 viewport；
    // 先確認量到的寬度就是 cy.viewport 設的寬度，再拿它下結論。
    expect(win.innerWidth, `${route} 的 innerWidth`).to.eq(
      win.document.documentElement.clientWidth,
    );
  });
  cy.document().then((doc) => {
    const root = doc.documentElement;
    expect(
      root.scrollWidth,
      `${route} 在 ${root.clientWidth}px 下的 scrollWidth`,
    ).to.be.at.most(root.clientWidth);
  });
}

describe('responsive layout', () => {
  beforeEach(() => {
    cy.clearLocalStorage();
  });

  describe('phone at 360px', () => {
    beforeEach(() => {
      cy.viewport(...PHONE);
    });

    it('fits the public landing and demo login without horizontal scrolling', () => {
      cy.visit('/');
      cy.contains('h1', '讓每一次服務回覆，都更有依據').should('be.visible');
      expectNoHorizontalOverflow('/');

      cy.contains('a', '進入 Demo').click();
      cy.contains('h1', '選擇 Demo 身分').should('be.visible');
      cy.contains('button', 'SMB 管理者').should('be.visible');
      expectNoHorizontalOverflow('/login');
    });

    describe('workspace as the SMB administrator', () => {
      beforeEach(() => {
        cy.viewport(...PHONE);
        loginAs('SMB 管理者');
      });

      for (const [route, control] of ADMIN_ROUTES) {
        it(`fits ${route} and keeps its main control reachable`, () => {
          cy.visit(route);
          cy.window().its('innerWidth').should('eq', PHONE[0]);
          cy.contains(control).should('exist').scrollIntoView().should('be.visible');
          expectNoHorizontalOverflow(route);
        });
      }

      it('switches the shell to the mobile header and drawer', () => {
        cy.visit('/app/home');

        cy.get('.menu-toggle').should('be.visible');
        cy.get('.app-sidenav').should('not.be.visible');

        cy.get('.menu-toggle').click();
        cy.get('.app-sidenav').should('be.visible');
        cy.contains('.app-sidenav a', '知識庫').click();

        cy.location('pathname').should('eq', '/app/knowledge');
        cy.get('.app-sidenav').should('not.be.visible');
        expectNoHorizontalOverflow('/app/knowledge');
      });

      it('keeps the wide records table usable by scrolling the table, not the page', () => {
        cy.visit('/app/databases/database-customer-records/trends');
        cy.get('table.comparison-table').should('exist');
        expectNoHorizontalOverflow('/app/databases/database-customer-records/trends');
      });
    });

    it('fits the end user chat and keeps the composer reachable', () => {
      cy.viewport(...PHONE);
      loginAs('外部客戶');
      cy.visit('/use/assistant-customer-service');
      cy.contains('h1', '客服助理').should('be.visible');
      cy.get('#chat-input').should('be.visible');
      cy.get('form.composer button[type="submit"]').should('be.visible');
      expectNoHorizontalOverflow('/use/assistant-customer-service');

      cy.get('#chat-input').type('收到商品後幾天內可以退貨？');
      cy.get('form.composer button[type="submit"]').click();
      cy.get('[role="log"] [data-kind="company-data"]').should('be.visible');
      expectNoHorizontalOverflow('/use/assistant-customer-service（回答後）');
    });
  });

  describe('desktop at 1280px', () => {
    beforeEach(() => {
      cy.viewport(...DESKTOP);
      loginAs('SMB 管理者');
    });

    it('shows the permanent side navigation and hides the mobile menu button', () => {
      cy.visit('/app/home');

      cy.get('.app-sidenav').should('be.visible').and('contain.text', 'AI 助理工作台');
      cy.get('.menu-toggle').should('not.be.visible');
      expectNoHorizontalOverflow('/app/home');
    });

    it('runs the core flows without horizontal overflow', () => {
      for (const [route, control] of ADMIN_ROUTES) {
        cy.visit(route);
        cy.contains(control).should('exist');
        expectNoHorizontalOverflow(route);
      }
    });
  });
});
