import type { Result } from 'axe-core';

/** 驗收門檻：critical 與 serious 一律不得出現（同 admin-e2e）。 */
export const BLOCKING_IMPACTS = ['critical', 'serious'] as const;

function reportViolations(violations: Result[]): void {
  cy.task(
    'a11yViolations',
    violations.map((violation) => ({
      rule: violation.id,
      impact: violation.impact,
      nodes: violation.nodes.length,
      target: violation.nodes
        .map((node) => node.target.join(' '))
        .join(' | ')
        .slice(0, 160),
      help: violation.help,
      why: (violation.nodes[0]?.failureSummary ?? '').replace(/\s+/g, ' ').slice(0, 200),
    })),
    { log: false },
  );
}

/** 面板的開啟動畫跑到結束再掃，避免 axe 取到轉場中的半透明顏色（同 admin-e2e #294）。 */
export function settleAnimations(): void {
  cy.document({ log: false }).then((doc) => {
    for (const animation of doc.getAnimations()) {
      if (animation.effect?.getComputedTiming().endTime === Infinity) continue;
      animation.finish();
    }
  });
}

/** 掃描目前頁面（或某個範圍）；只在 critical／serious 違規時失敗。 */
export function auditA11y(context?: string): void {
  settleAnimations();
  cy.injectAxe();
  cy.checkA11y(context, { includedImpacts: [...BLOCKING_IMPACTS] }, reportViolations);
}
