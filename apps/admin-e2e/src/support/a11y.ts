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

/**
 * 把頁面上還在跑的 CSS 動畫與轉場直接跳到結束狀態（issue #294）。
 * Material 對話框開啟時，內層容器有 150ms 的 opacity 轉場；axe 在轉場中取色，會把半透明的對話框
 * 和底下的遮罩混成中間色，報出不存在的色彩對比違規。無限循環的動畫（例如載入中的轉圈）不能 finish，略過。
 */
export function settleAnimations(): void {
  cy.document({ log: false }).then((doc) => {
    for (const animation of doc.getAnimations()) {
      if (animation.effect?.getComputedTiming().endTime === Infinity) continue;
      animation.finish();
    }
  });
}

/** 掃描目前頁面；只在 critical／serious 違規時失敗。掃描前先讓動畫結束，避免取到轉場中的顏色。 */
export function auditA11y(context?: string): void {
  settleAnimations();
  cy.injectAxe();
  cy.checkA11y(
    context,
    { includedImpacts: [...BLOCKING_IMPACTS] },
    reportViolations,
  );
}

/** admin 的配色主題（`libs/theme-pack` 的 `COLOR_THEMES`）；`midnight` 是深色主題。 */
export const COLOR_THEMES = ['verdant', 'midnight'] as const;
export type ColorTheme = (typeof COLOR_THEMES)[number];

/** 不重新載入頁面就切換配色（和 `ThemeService.setTheme` 一樣改 `<html data-theme>`），開著的對話框維持原狀。 */
export function useColorTheme(theme: ColorTheme): void {
  cy.document({ log: false }).then((doc) => {
    doc.documentElement.dataset['theme'] = theme;
  });
  cy.get('html').should('have.attr', 'data-theme', theme);
}

/** 在每個配色主題下各跑一次 `check`，最後切回預設的 `verdant`。 */
export function forEachColorTheme(check: (theme: ColorTheme) => void): void {
  for (const theme of COLOR_THEMES) {
    useColorTheme(theme);
    check(theme);
  }
  useColorTheme(COLOR_THEMES[0]);
}

type Rgb = readonly [number, number, number];

function parseRgb(color: string): { rgb: Rgb; alpha: number } {
  const match = /^rgba?\(([\d.]+),\s*([\d.]+),\s*([\d.]+)(?:,\s*([\d.]+))?\)$/.exec(color);
  if (!match) throw new Error(`not an rgb() color: ${color}`);
  return {
    rgb: [Number(match[1]), Number(match[2]), Number(match[3])],
    alpha: match[4] === undefined ? 1 : Number(match[4]),
  };
}

function luminance([r, g, b]: Rgb): number {
  const [lr, lg, lb] = [r, g, b].map((channel) => {
    const value = channel / 255;
    return value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * lr + 0.7152 * lg + 0.0722 * lb;
}

/** WCAG 2 的對比值（1–21）。 */
export function contrastRatio(foreground: Rgb, background: Rgb): number {
  const [light, dark] = [luminance(foreground), luminance(background)].sort((a, b) => b - a);
  return (light + 0.05) / (dark + 0.05);
}

/**
 * 實心按鈕的底色就是 `token` 這個 CSS 變數，字色與底色的對比至少 4.5:1（issue #261）。
 * 以 `getComputedStyle` 量真正畫出來的顏色；按鈕與祖先都不能是半透明（否則量到的不是畫面上的顏色）。
 */
export function expectFilledButton(selector: string, token: '--color-error' | '--color-accent'): void {
  settleAnimations();
  cy.get(selector).should(($button) => {
    const button = $button[0];
    const doc = button.ownerDocument;
    const win = doc.defaultView as Window;
    const probe = doc.createElement('span');
    probe.style.color = `var(${token})`;
    doc.body.append(probe);
    const tokenColor = win.getComputedStyle(probe).color;
    probe.remove();

    const style = win.getComputedStyle(button);
    for (let node: Element | null = button; node; node = node.parentElement) {
      expect(win.getComputedStyle(node).opacity, `opacity of ${node.tagName.toLowerCase()}`).to.eq('1');
    }
    const background = parseRgb(style.backgroundColor);
    const foreground = parseRgb(style.color);
    expect(style.backgroundColor, `${selector} background is ${token}`).to.eq(tokenColor);
    expect(background.alpha, `${selector} background is opaque`).to.eq(1);
    expect(foreground.alpha, `${selector} text is opaque`).to.eq(1);
    expect(contrastRatio(foreground.rgb, background.rgb), `${selector} contrast (${style.color} on ${style.backgroundColor})`).to.be.at.least(4.5);
  });
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
