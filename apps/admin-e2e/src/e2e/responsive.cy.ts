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
  // 建立表單改成對話框後，頁面上一直看得到的主要控制項是開啟對話框的「新增資料庫」按鈕
  // （對話框裡的標題與送出按鈕才叫「建立資料庫」，預設不在畫面上）。
  ['/app/databases', '新增資料庫'],
  ['/app/databases/database-customer-records/records', '收集紀錄'],
  ['/app/databases/database-customer-records/trends', '趨勢比較'],
  ['/app/databases/database-customer-records/access', '儲存資料管理者'],
  ['/app/channels', '助理發布設定'],
  ['/app/assistants/assistant-customer-service/publishing?channel=line', '儲存並檢查'],
  ['/app/settings', '團隊與權限'],
];

/** 整頁不得出現水平捲動。 */
function expectNoHorizontalOverflow(route: string, viewportWidth: number): void {
  cy.window().then((win) => {
    // Cypress 把應用程式跑在自己的 iframe 裡，iframe 有獨立的 viewport；
    // 先確認量到的寬度就是 cy.viewport 設的寬度，再拿它下結論。不和 clientWidth 比：
    // Linux（CI）的傳統捲軸會佔寬度，整頁有垂直捲動時 clientWidth 本來就比 innerWidth 小。
    expect(win.innerWidth, `${route} 的 innerWidth`).to.eq(viewportWidth);
  });
  cy.document().then((doc) => {
    const root = doc.documentElement;
    expect(
      root.scrollWidth,
      `${route} 在 ${root.clientWidth}px 下的 scrollWidth（超出的元素：${overflowingElements(doc)}）`,
    ).to.be.at.most(root.clientWidth);
  });
}

/** 列出右緣超出 viewport、而且不在自己的水平捲動區裡的元素，讓 CI 上的失敗訊息直接指出是誰。 */
function overflowingElements(doc: Document): string {
  const limit = doc.documentElement.clientWidth + 0.5;
  const win = doc.defaultView as Window;
  const insideScroller = (el: Element): boolean => {
    for (let node = el.parentElement; node && node !== doc.body; node = node.parentElement) {
      if (win.getComputedStyle(node).overflowX !== 'visible') return true;
    }
    return false;
  };
  const found = Array.from(doc.body.querySelectorAll('*'))
    .filter((el) => el.getBoundingClientRect().right > limit && !insideScroller(el))
    .slice(0, 5)
    .map((el) => {
      const classes = Array.from(el.classList).slice(0, 2).map((name) => `.${name}`).join('');
      return `${el.tagName.toLowerCase()}${classes} 右緣 ${Math.round(el.getBoundingClientRect().right)}px`;
    });
  return found.length > 0 ? found.join('、') : '無';
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
      expectNoHorizontalOverflow('/', PHONE[0]);

      cy.contains('a', '進入 Demo').click();
      cy.contains('h1', 'Demo 登入').should('be.visible');
      cy.get('#demo-username').should('be.visible');
      expectNoHorizontalOverflow('/login', PHONE[0]);
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
          expectNoHorizontalOverflow(route, PHONE[0]);
        });
      }

      it('fits the expanded team permission editor, which only exists after a click', () => {
        cy.visit('/app/settings');
        cy.contains('button', '變更 安心商行客服同仁 的權限').click();
        cy.get('.member__editor input[type="checkbox"]').should('have.length', 7);
        cy.window().its('innerWidth').should('eq', PHONE[0]);
        cy.contains('button', '儲存 安心商行客服同仁 的權限')
          .should('exist')
          .scrollIntoView()
          .should('be.visible');
        expectNoHorizontalOverflow('/app/settings（展開權限編輯器）', PHONE[0]);
      });

      it('switches the shell to the mobile header and drawer', () => {
        cy.visit('/app/home');

        cy.get('.menu-toggle').should('be.visible');
        cy.get('.app-sidenav').should('not.be.visible');

        cy.get('.menu-toggle').click();
        // 開啟動畫進行中時 Cypress 會把抽屜判定成被父層裁切；CI 機器較慢，先等動畫結束。
        cy.get('.app-sidenav', { timeout: 10000 })
          .should('have.class', 'mat-drawer-opened')
          .and('not.have.class', 'mat-drawer-animating');
        cy.get('.app-sidenav').should('be.visible');
        cy.contains('.app-sidenav a', '知識庫').click();

        cy.location('pathname').should('eq', '/app/knowledge');
        cy.get('.app-sidenav').should('not.be.visible');
        expectNoHorizontalOverflow('/app/knowledge', PHONE[0]);
      });

      it('keeps the wide records table usable by scrolling the table, not the page', () => {
        cy.visit('/app/databases/database-customer-records/trends');
        cy.get('table.comparison-table').should('exist');
        expectNoHorizontalOverflow('/app/databases/database-customer-records/trends', PHONE[0]);
      });
    });

    it('fits the end user chat and keeps the composer reachable', () => {
      cy.viewport(...PHONE);
      loginAs('外部客戶');
      cy.visit('/use/assistant-customer-service');
      cy.contains('h1', '客服助理').should('be.visible');
      cy.get('#chat-input').should('be.visible');
      cy.get('form.composer button[type="submit"]').should('be.visible');
      expectNoHorizontalOverflow('/use/assistant-customer-service', PHONE[0]);

      cy.get('#chat-input').type('收到商品後幾天內可以退貨？');
      cy.get('form.composer button[type="submit"]').click();
      cy.get('[role="log"] [data-kind="company-data"]').should('be.visible');
      expectNoHorizontalOverflow('/use/assistant-customer-service（回答後）', PHONE[0]);
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
      expectNoHorizontalOverflow('/app/home', DESKTOP[0]);
    });

    it('runs the core flows without horizontal overflow', () => {
      for (const [route, control] of ADMIN_ROUTES) {
        cy.visit(route);
        cy.contains(control).should('exist');
        expectNoHorizontalOverflow(route, DESKTOP[0]);
      }
    });
  });
});
