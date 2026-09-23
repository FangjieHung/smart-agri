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

export function loginAs(persona: string): void {
  cy.visit('/login');
  cy.contains('button', persona).click();
  cy.location('pathname').should('eq', '/app/home');
}
