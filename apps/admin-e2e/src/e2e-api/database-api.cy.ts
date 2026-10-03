import { loginToApi } from '../support/api-mode';

/**
 * API 模式的數據庫（M4 issue #142）：對真實 API 與 PostgreSQL 跑，不需要任何示範資料庫。
 * 從模板建立 → 清單與詳情顯示擁有者、用途、欄位與更新時間 → 沒有資料來源管理權限的帳號不能建立
 * → 另一個組織直接開網址看不到。
 *
 * 數據庫目前沒有刪除功能，每次執行以不同名稱建立一個新的，可以對同一個資料庫重跑。
 * 兩個 `it` 依序共用第一個建立的數據庫網址。
 */

const DATABASE_PATH = /^\/app\/databases\/[0-9a-f-]{36}\/form$/;

describe('databases against the real API', () => {
  const databaseName = `E2E 滿意度 ${Date.now()}`;
  let databasePath = '';

  it('creates a database from a template and shows its owner, purpose and initial form', () => {
    loginToApi('anxin', 'admin');

    cy.visit('/app/databases');
    cy.contains('h1', '數據庫').should('be.visible');
    cy.contains('正在載入資料庫').should('not.exist');
    cy.contains('目前無法載入資料庫').should('not.exist');

    cy.get('button[page-header-actions]').contains('新增資料庫').click();
    cy.get('fieldset.template-picker label.template-option').should('have.length', 5);
    cy.get('input[type="radio"][value="template-satisfaction"]').check({ force: true });
    cy.get('#database-name').should('have.value', '滿意度調查');

    // 可恢復的驗證錯誤：名稱留白不送出，改好後再建立。
    cy.get('#database-name').clear();
    cy.get('.create-panel button[type="submit"]').click();
    cy.get('#database-name-error').should('contain', '請輸入資料庫名稱。');
    cy.get('#database-name').should('have.attr', 'aria-invalid', 'true').type(databaseName);
    cy.get('.create-panel button[type="submit"]').click();

    cy.location('pathname', { timeout: 20000 })
      .should('match', DATABASE_PATH)
      .then((pathname) => {
        databasePath = pathname;
      });
    cy.contains('h1', databaseName).should('be.visible');
    cy.get('app-page-header').should('contain', '收集客戶對服務的評分與建議。');
    cy.get('.database-meta .database-owner').invoke('text').should('not.be.empty');
    cy.get('.database-meta').should('contain', '滿意度調查').and('contain', '3 個');
    cy.get('.database-meta time').should('have.attr', 'datetime');

    // 表單編輯（#143）尚未開放：初始表單唯讀顯示，與模板相同。
    cy.get('.upcoming-notice').should('contain', '將於後續版本開放');
    cy.get('app-form-designer').should('not.exist');
    cy.get('ol.field-summary > li').should('have.length', 3);
    cy.get('ol.field-summary > li[data-field-id="field-overall-satisfaction"]')
      .should('contain', '整體滿意度')
      .and('contain', '量尺')
      .and('contain', '必填');
    cy.get('ol.field-summary > li[data-field-id="field-liked-services"]').should('contain', '商品品質');

    cy.get('nav.tabs').contains('a', '收集紀錄').click();
    cy.get('.upcoming-notice').should('contain', '將於後續版本開放');

    cy.visit('/app/databases');
    cy.contains('tr', databaseName)
      .should('contain', '收集客戶對服務的評分與建議。')
      .and('contain', '3 個欄位・滿意度調查')
      .within(() => cy.get('time').should('have.attr', 'datetime'));
  });

  it('is not creatable without manage-data-sources and not visible to another organization', () => {
    expect(databasePath, 'the database created by the previous test').to.match(DATABASE_PATH);

    loginToApi('anxin', 'internal');
    cy.visit('/app/databases');
    cy.contains('正在載入資料庫').should('not.exist');
    cy.get('button[page-header-actions]').should('not.exist');
    cy.get('.notice').should('contain', '只有可管理資料來源的帳號可以建立資料庫');
    cy.contains(databaseName).should('not.exist');
    cy.visit(databasePath);
    cy.contains('無法查看這個資料庫').should('be.visible');
    cy.contains(databaseName).should('not.exist');

    loginToApi('control', 'admin');
    cy.visit('/app/databases');
    cy.contains('正在載入資料庫').should('not.exist');
    cy.contains(databaseName).should('not.exist');
    // 直接開網址：API 回 403 database，畫面不透露名稱或欄位。
    cy.visit(databasePath);
    cy.contains('無法查看這個資料庫').should('be.visible');
    cy.contains(databaseName).should('not.exist');
    cy.contains('整體滿意度').should('not.exist');
  });
});
