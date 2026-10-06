import axe from 'axe-core';
import {
  FakeServer,
  QUESTION,
  answer,
  companyReply,
  json,
  mount,
  mountReady,
  noResultReply,
  session,
  type Harness,
  required,
} from './widget.testing';

/**
 * 每一種畫面狀態都跑 axe，違規數必須是 0。
 * jsdom 沒有版面與繪製，無法計算色彩對比，所以 `color-contrast` 在這裡關掉：
 * 品牌色對比由 brand-color.spec.ts 計算，實際畫面的對比在瀏覽器裡另外跑過 axe。
 */
async function expectNoViolations(view: Harness): Promise<void> {
  const results = await axe.run(view.host, { rules: { 'color-contrast': { enabled: false } } });
  const summary = results.violations.map((violation) => `${violation.id}: ${violation.nodes.map((node) => node.html).join(' | ')}`);
  expect(summary).toEqual([]);
}

function ready(): FakeServer {
  const fake = new FakeServer();
  fake.sessions.push(session());
  return fake;
}

describe('accessibility (axe)', () => {
  beforeEach(() => sessionStorage.clear());
  afterEach(() => {
    vi.useRealTimers();
    document.body.replaceChildren();
  });

  it('connecting', async () => {
    const view = await mount(ready());
    expect(view.text()).toContain('正在連線');
    await expectNoViolations(view);
  });

  it('ready with the welcome message', async () => {
    const view = await mountReady(ready());
    await expectNoViolations(view);
  });

  it('unavailable', async () => {
    const fake = new FakeServer();
    fake.sessions.push(() => json(403, { reason: 'public-assistant' }));
    const view = await mount(fake);
    await view.until(() => view.text().includes('目前無法使用'));
    await expectNoViolations(view);
  });

  it('connection failed', async () => {
    const fake = new FakeServer();
    fake.sessions.push(() => Promise.reject(new TypeError('offline')));
    const view = await mount(fake);
    await view.until(() => view.text().includes('無法連線'));
    await expectNoViolations(view);
  });

  it('session rate limited', async () => {
    const fake = new FakeServer();
    fake.sessions.push(() => json(429, {}, { 'Retry-After': '30' }));
    const view = await mount(fake);
    await view.until(() => view.text().includes('請求太頻繁了'));
    await expectNoViolations(view);
  });

  it('an answer with sources, then the open source drawer', async () => {
    const fake = ready();
    fake.runs.push(answer(companyReply));
    const view = await mountReady(fake);
    view.type(QUESTION);
    view.send();
    await view.until(() => view.host.querySelector('.citation-toggle') !== null);
    await expectNoViolations(view);

    view.host.querySelector<HTMLButtonElement>('.citation-toggle')?.click();
    await view.until(() => view.host.querySelector('app-citation-drawer') !== null);
    await expectNoViolations(view);
  });

  it('a no-result answer', async () => {
    const fake = ready();
    fake.runs.push(answer(noResultReply));
    const view = await mountReady(fake);
    view.type(QUESTION);
    view.send();
    await view.until(() => view.host.querySelector('[data-kind="no-result"]') !== null);
    await expectNoViolations(view);
  });

  it('while an answer is streaming', async () => {
    const fake = ready();
    fake.runs.push(() => new Promise<Response>(() => undefined));
    const view = await mountReady(fake);
    view.type(QUESTION);
    view.send();
    await view.until(() => view.host.querySelector('.streaming') !== null);
    await expectNoViolations(view);
  });

  it('paused', async () => {
    const fake = ready();
    fake.runs.push(() => json(403, { reason: 'public-assistant' }));
    const view = await mountReady(fake);
    view.type(QUESTION);
    view.send();
    await view.until(() => view.text().includes('目前暫停服務'));
    await expectNoViolations(view);
  });

  it('rate limited while asking', async () => {
    const fake = ready();
    fake.runs.push(() => json(429, {}, { 'Retry-After': '30' }));
    const view = await mountReady(fake);
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] });
    view.type(QUESTION);
    view.send();
    await view.until(() => view.text().includes('問題太頻繁了'));
    await expectNoViolations(view);
  });

  it('a network error with a retry button', async () => {
    const fake = ready();
    fake.runs.push(() => Promise.reject(new TypeError('offline')));
    const view = await mountReady(fake);
    view.type(QUESTION);
    view.send();
    await view.until(() => view.text().includes('連線中斷'));
    await expectNoViolations(view);
  });

  it('keyboard: the close button is reachable and has an accessible name containing its visible text', async () => {
    const view = await mountReady(ready());
    const close = required<HTMLButtonElement>(view.host, 'button.close');
    expect(close.getAttribute('aria-label')).toContain(close.textContent?.trim());
    expect(close.tabIndex).toBeGreaterThanOrEqual(0);
    // 對話內容區可用鍵盤聚焦捲動；語言標示在 index.html（lang="zh-Hant"）。
    expect(view.host.querySelector('[role="log"]')?.getAttribute('tabindex')).toBe('0');
  });
});
