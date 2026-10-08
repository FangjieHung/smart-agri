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
  if (match) {
    return {
      rgb: [Number(match[1]), Number(match[2]), Number(match[3])],
      alpha: match[4] === undefined ? 1 : Number(match[4]),
    };
  }
  // color-mix() 與相對色的計算值是 `color(srgb r g b / a)`（0–1），換算成和 rgb() 相同的 0–255。
  const srgb = /^color\(srgb ([\d.e-]+) ([\d.e-]+) ([\d.e-]+)(?: \/ ([\d.]+))?\)$/.exec(color);
  if (!srgb) throw new Error(`not an rgb() color: ${color}`);
  const [r, g, b] = [srgb[1], srgb[2], srgb[3]].map((channel) => Math.round(Number(channel) * 255));
  return { rgb: [r, g, b], alpha: srgb[4] === undefined ? 1 : Number(srgb[4]) };
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

interface MeasuredColors {
  readonly color: string;
  readonly background: string;
}

/** 元素的字色，以及往上找到的第一個不透明底色（元素自己沒有底色時，畫面上看到的是祖先的底色）。 */
function measureColors(element: Element): MeasuredColors {
  const win = element.ownerDocument.defaultView as Window;
  for (let node: Element | null = element; node; node = node.parentElement) {
    expect(win.getComputedStyle(node).opacity, `opacity of ${node.tagName.toLowerCase()}`).to.eq('1');
  }
  let background = 'rgba(0, 0, 0, 0)';
  for (let node: Element | null = element; node; node = node.parentElement) {
    const color = win.getComputedStyle(node).backgroundColor;
    if (parseRgb(color).alpha === 1) {
      background = color;
      break;
    }
  }
  return { color: win.getComputedStyle(element).color, background };
}

/**
 * 對話框的顏色跟著配色主題走（issue #320）：`parts` 是「名稱 → 選擇器」，每個元素量實際畫出的字色與底色。
 * - 每個主題下，字色與底色的對比都至少 4.5:1；
 * - 淺色與深色主題量到的字色、底色都不同；淺色主題是淺底的部分，深色主題的底色要是暗色（相對亮度 < 0.1）。
 *
 * 以前 admin 的 `--color-*` 只定義在 `:root`，深色主題下對話框和淺色一模一樣，對比照樣通過，
 * 所以只檢查對比抓不到；這裡同時比較兩個主題量到的顏色。
 */
export function expectColorsFollowTheme(parts: Readonly<Record<string, string>>): void {
  const measured: Partial<Record<ColorTheme, Record<string, MeasuredColors>>> = {};
  forEachColorTheme((theme) => {
    settleAnimations();
    cy.document({ log: false }).should((doc) => {
      const colors: Record<string, MeasuredColors> = {};
      for (const [name, selector] of Object.entries(parts)) {
        const element = doc.querySelector(selector);
        if (!element) throw new Error(`${name} (${selector}) not found`);
        const part = measureColors(element);
        const ratio = contrastRatio(parseRgb(part.color).rgb, parseRgb(part.background).rgb);
        expect(ratio, `${theme} ${name} contrast (${part.color} on ${part.background})`).to.be.at.least(4.5);
        colors[name] = part;
      }
      measured[theme] = colors;
    });
  });
  cy.then(() => {
    const [light, dark] = [measured.verdant, measured.midnight];
    if (!light || !dark) throw new Error('both color themes must be measured');
    // 量到的顏色印到終端機（cypress.config.ts 的 a11yColors），失敗時看得到兩個主題各是什麼顏色。
    const rows = Object.keys(parts).map((name) => ({ name, verdant: light[name], midnight: dark[name] }));
    return cy.task('a11yColors', rows, { log: false }).then(() => ({ light, dark }));
  }).then(({ light, dark }) => {
    for (const name of Object.keys(parts)) {
      expect(dark[name].color, `${name} text color differs between verdant and midnight`).not.to.eq(light[name].color);
      expect(dark[name].background, `${name} background differs between verdant and midnight`).not.to.eq(light[name].background);
      // 淺色主題是淺底的部分（對話框、取消鈕）在深色主題要變暗；實心的危險／強調按鈕在深色主題反而較亮（配深色字）。
      const lightBackground = luminance(parseRgb(light[name].background).rgb);
      if (lightBackground > 0.5) {
        expect(
          luminance(parseRgb(dark[name].background).rgb),
          `${name} background is dark in midnight (${dark[name].background} vs ${light[name].background})`,
        ).to.be.below(0.1);
      }
    }
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
