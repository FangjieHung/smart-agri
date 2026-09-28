/**
 * API 模式（cypress.api.config.ts）專用的登入：走真實的 `/login` 表單。
 *
 * 流程與使用者相同：`POST /api/v1/auth/login` 取得 Identity cookie → 前端導向
 * `/connect/authorize`（PKCE）→ API 導回 `/auth/callback` 換 token → `/app/home`。
 * `/api`、`/connect` 由 `nx serve admin --configuration=api` 的 proxy 轉給 API，和前端同源，
 * 所以不需要 `cy.origin`。
 *
 * 密碼是 `migrate` 建立示範帳號時的 `SEED_DEMO_PASSWORD`（cypress.api.config.ts 轉成
 * `cy.env(['demoPassword'])` 讀取的 Cypress 環境變數），不寫在 spec 裡，也不印到 Cypress 的指令紀錄。
 */
export function loginToApi(organizationCode: string, loginName: string): void {
  cy.env<{ demoPassword?: string }>(['demoPassword'], { log: false }).then(({ demoPassword }) => {
    if (typeof demoPassword !== 'string' || demoPassword === '') {
      throw new Error('SEED_DEMO_PASSWORD is not set: API-mode E2E signs in with the password migrate seeded.');
    }

    cy.visit('/login');
    cy.contains('h1', '登入').should('be.visible');
    // 資料庫裡有兩個以上的組織時，登入頁才要求組織代碼（`/api/v1/auth/login-options`）；
    // 欄位可能帶著上一次登入的組織代碼，先清空再輸入。
    cy.get('#login-organization-code').clear().type(organizationCode);
    cy.get('#demo-username').clear().type(loginName);
    cy.get('#demo-password').clear().type(demoPassword, { log: false });
    cy.contains('form button[type="submit"]', '登入').click();
    cy.location('pathname', { timeout: 20000 }).should('eq', '/app/home');
  });
}
