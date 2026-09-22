function loginAsAdmin(): void {
  cy.visit('/login');
  cy.contains('button', 'SMB 管理者').click();
  cy.location('pathname').should('eq', '/app/home');
}

describe('structured data and tracking', () => {
  beforeEach(() => {
    cy.clearLocalStorage();
    loginAsAdmin();
  });

  it('creates a database from a template, edits common fields and trial-fills the form', () => {
    cy.contains('nav a', '資料庫').click();
    cy.location('pathname').should('eq', '/app/databases');
    cy.contains('h1', '資料庫').should('be.visible');
    cy.contains('.database-card', '客戶資料庫').should('contain', '客服助理');

    cy.contains('fieldset legend', '你要收集什麼？').should('be.visible');
    cy.contains('label', '滿意度調查').click();
    cy.get('#database-name').should('have.value', '滿意度調查').clear().type('門市滿意度調查');
    cy.contains('button', '建立資料庫').click();

    cy.location('pathname').should('match', /^\/app\/databases\/database-created-\d+\/form$/);
    cy.contains('h1', '門市滿意度調查').should('be.visible');
    cy.get('nav.tabs a').then((tabs) => {
      expect(Array.from(tabs, (tab) => tab.textContent?.trim())).to.deep.equal([
        '表單設計',
        '收集紀錄',
        '趨勢比較',
        '已連接助理',
        '權限',
      ]);
    });
    cy.get('nav.tabs [aria-current="page"]').should('contain', '表單設計');

    cy.get('.field-editor').should('have.length', 3);
    cy.get('.field-editor').first().find('select option').should('have.length', 6);
    cy.get('#field-label-field-overall-satisfaction').clear().type('這次整體滿意度');

    cy.contains('button', '新增欄位').click();
    cy.get('.field-editor').should('have.length', 4);
    cy.get('.field-editor').last().within(() => {
      cy.get('select').select('數字');
      cy.get('input[id^="field-label-"]').type('消費金額');
      cy.get('input[id^="field-unit-"]').type('元');
      cy.contains('label', '必填').click();
      cy.contains('button', '上移').click();
    });
    cy.get('.field-editor').eq(2).find('input[id^="field-label-"]').should('have.value', '消費金額');

    cy.get('#field-label-field-suggestion').clear();
    cy.contains('button', '儲存表單').click();
    cy.get('app-form-designer [role="alert"]').should('contain', '還有欄位需要修正');
    cy.get('#field-label-field-suggestion').should('have.attr', 'aria-invalid', 'true').type('其他建議');
    cy.contains('button', '儲存表單').click();
    cy.get('.designer-feedback').should('contain', '表單已儲存');

    cy.contains('h3', '試填表單').should('be.visible');
    cy.contains('button', '送出試填').click();
    cy.get('app-form-trial').should('contain', '「這次整體滿意度」為必填。').and('contain', '「消費金額」為必填。');

    cy.contains('app-form-trial fieldset', '這次整體滿意度').within(() => cy.contains('label', '5').click());
    cy.contains('app-form-trial label', '消費金額').click();
    cy.focused().type('1200');
    cy.contains('app-form-trial label', '客服回應').click();
    cy.contains('button', '送出試填').click();
    cy.get('.trial-preview')
      .should('contain', '試填結果')
      .and('contain', '不會儲存')
      .and('contain', '5 / 5')
      .and('contain', '1,200 元')
      .and('contain', '客服回應');
  });

  it('shows the timeline and compares current, previous and first records', () => {
    cy.visit('/app/databases/database-customer-records');
    cy.location('pathname').should('eq', '/app/databases/database-customer-records/form');

    cy.contains('nav.tabs a', '收集紀錄').click();
    cy.get('#subject-select').should('have.value', 'subject-wang');
    cy.get('ol.timeline > li').should('have.length', 4);
    cy.get('ol.timeline > li').first().should('contain', '2026-09-15').and('contain', '本次');
    cy.get('ol.timeline > li').last().should('contain', '2026-06-15').and('contain', '首次');

    cy.get('#subject-select').select('林小姐（2 筆）');
    cy.location('search').should('eq', '?subject=subject-lin');
    cy.get('ol.timeline > li').should('have.length', 2);
    cy.contains('使用者已撤回同意').should('not.exist');

    cy.contains('nav.tabs a', '趨勢比較').click();
    cy.location('search').should('eq', '?subject=subject-lin');
    cy.get('#subject-select').select('王小姐（4 筆）');
    cy.contains('table.comparison-table tbody tr', '整體滿意度')
      .should('contain', '3 / 5')
      .and('contain', '5 / 5')
      .and('contain', '+2 分');
    cy.get('.trend-conclusion').should('contain', '整體滿意度：本次 5 / 5，較上次 +2 分，較首次 +2 分。');
    cy.get('app-trend-chart svg[role="img"]').should('have.length', 2);
    cy.get('app-trend-chart').first().find('tbody tr').should('have.length', 4);
  });

  it('does not show a trend conclusion before there are two records', () => {
    cy.visit('/app/databases/database-customer-records/trends');
    cy.get('#subject-select').select('陳先生（1 筆）');
    cy.get('.insufficient-records').should('contain', '累積 2 筆以上');
    cy.get('.trend-conclusion').should('not.exist');
    cy.get('app-trend-chart').should('not.exist');
  });

  it('does not reveal the name of a database the account cannot access', () => {
    cy.visit('/app/databases/database-staff-checkins/form');
    cy.contains('無法查看這個資料庫').should('be.visible');
    cy.contains('同仁排班回報').should('not.exist');
    cy.get('nav.tabs').should('not.exist');
  });
});
