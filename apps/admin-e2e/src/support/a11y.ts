import type { Result } from 'axe-core';

/** Demo 的驗收門檻：critical 與 serious 一律不得出現。 */
export const BLOCKING_IMPACTS = ['critical', 'serious'] as const;

function reportViolations(violations: Result[]): void {
  cy.task(
    'a11yViolations',
    violations.map((violation) => ({
      rule: violation.id,
      impact: violation.impact,
      nodes: violation.nodes.length,
      target: violation.nodes.map((node) => node.target.join(' ')).join(' | ').slice(0, 160),
      help: violation.help,
      why: (violation.nodes[0]?.failureSummary ?? '').replace(/\s+/g, ' ').slice(0, 200),
    })),
    { log: false },
  );
}

/** 掃描目前頁面；只在 critical／serious 違規時失敗。 */
export function auditA11y(context?: string): void {
  cy.injectAxe();
  cy.checkA11y(
    context,
    { includedImpacts: [...BLOCKING_IMPACTS] },
    reportViolations,
  );
}

/** `/login` 頁 Demo 帳號清單上的角色名稱 → 登入帳號（mock 模式的密碼都是 1234）。 */
const DEMO_LOGIN_NAMES: Readonly<Record<string, string>> = {
  'SMB 管理者': 'admin',
  內部使用者: 'internal',
  外部客戶: 'customer',
};

/** 在目前的 `/login` 頁填入 Demo 帳號並送出；`persona` 是 Demo 帳號清單上的角色名稱。 */
export function submitDemoLogin(persona: string): void {
  const loginName = DEMO_LOGIN_NAMES[persona];
  if (!loginName) throw new Error(`unknown demo persona: ${persona}`);
  cy.get('#demo-username').clear().type(loginName);
  cy.get('#demo-password').clear().type('1234');
  cy.get('form.demo-login__form').contains('button', '登入').click();
}

/** 以 Demo 帳號登入並進入 `/app/home`。 */
export function loginAs(persona: string): void {
  cy.visit('/login');
  submitDemoLogin(persona);
  cy.location('pathname').should('eq', '/app/home');
}
