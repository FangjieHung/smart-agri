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
  sse,
  required,
} from './widget.testing';

function server(): FakeServer {
  const fake = new FakeServer();
  fake.sessions.push(session());
  return fake;
}

describe('widget states', () => {
  beforeEach(() => sessionStorage.clear());
  afterEach(() => {
    vi.useRealTimers();
    document.body.replaceChildren();
  });

  it('shows a connecting state, then the display name, welcome message and composer', async () => {
    const fake = server();
    const view = await mount(fake);
    expect(view.text()).toContain('正在連線');

    await view.until(() => view.host.querySelector('textarea') !== null);

    expect(view.host.querySelector('h1')?.textContent).toBe('森林商店客服');
    expect(view.text()).toContain('你好，我可以回答商品與退換貨的問題。');
    expect(document.title).toBe('森林商店客服');
    expect(fake.sessionRequests).toHaveLength(1);
    expect(fake.sessionRequests[0].url).toBe(`/api/v1/public/assistants/11111111-1111-4111-8111-111111111111/visitor-sessions`);
    expect(fake.sessionRequests[0].body).toEqual({ host: 'https://shop.example.com' });
  });

  it('sends host null when the host parameter is missing', async () => {
    const fake = server();
    await mountReady(fake, { context: { hostOrigin: null } });
    expect(fake.sessionRequests[0].body).toEqual({ host: null });
  });

  describe('a normal answer with citations', () => {
    it('streams the question to the visitor endpoint with the Visitor token and shows the answer and its sources', async () => {
      const fake = server();
      fake.runs.push(answer(companyReply));
      const view = await mountReady(fake);

      view.type(QUESTION);
      view.send();
      await view.until(() => view.host.querySelector('[data-kind="company-data"]') !== null, 'no answer');

      const run = fake.runRequests[0];
      expect(run.url).toBe('/api/v1/public/assistants/11111111-1111-4111-8111-111111111111/chat/runs');
      expect(run.method).toBe('POST');
      expect(run.headers['Authorization']).toBe('Visitor token-1');
      expect(run.body.messages).toEqual([expect.objectContaining({ role: 'user', content: QUESTION })]);
      expect(view.text()).toContain('根據你的資料');
      expect(view.text()).toContain('收到商品後七天內可以申請退貨 [1]。');
      expect(view.host.querySelector('textarea')?.value).toBe('');

      const sources = view.host.querySelector<HTMLButtonElement>('.citation-toggle');
      expect(sources?.textContent).toContain('查看引用來源（1）');
      sources?.click();
      await view.until(() => view.host.querySelector('app-citation-drawer') !== null);
      expect(view.text()).toContain('退貨政策.md');
      expect(view.text()).toContain('收到商品後七天內可申請退貨。');
    });

    it('shows the question while the answer is streaming and marks the log busy', async () => {
      const fake = server();
      let finish: (response: Response) => void = () => undefined;
      fake.runs.push(() => new Promise<Response>((resolve) => (finish = resolve)));
      const view = await mountReady(fake);

      view.type(QUESTION);
      view.send();
      await view.until(() => view.host.querySelector('.streaming') !== null && fake.runRequests.length === 1);

      expect(view.text()).toContain(QUESTION);
      expect(view.host.querySelector('[role="log"]')?.getAttribute('aria-busy')).toBe('true');
      expect(view.host.querySelector('button.send')?.hasAttribute('disabled')).toBe(true);

      finish(answer(companyReply)({ url: '', method: '', headers: {}, body: {} }) as Response);
      await view.until(() => view.host.querySelector('[data-kind="company-data"]') !== null);
      expect(view.host.querySelector('[role="log"]')?.getAttribute('aria-busy')).toBeNull();
    });

    it('hides citations when the assistant turns off showCitations', async () => {
      const fake = new FakeServer();
      fake.sessions.push(session({}, { showCitations: false }));
      fake.runs.push(answer(companyReply));
      const view = await mountReady(fake);

      view.type(QUESTION);
      view.send();
      await view.until(() => view.host.querySelector('[data-kind="company-data"]') !== null);

      expect(view.host.querySelector('.citation-toggle')).toBeNull();
      expect(view.text()).toContain('收到商品後七天內可以申請退貨');
    });
  });

  it('shows the assistant’s refusal text for a no-result reply', async () => {
    const fake = server();
    fake.runs.push(answer(noResultReply));
    const view = await mountReady(fake);

    view.type('你們有賣火星土地嗎？');
    view.send();
    await view.until(() => view.host.querySelector('[data-kind="no-result"]') !== null);

    expect(view.text()).toContain('查無資料');
    expect(view.text()).toContain('很抱歉，我找不到這個問題的答案，請來電 02-1234-5678。');
    expect(view.host.querySelector('.citation-toggle')).toBeNull();
  });

  describe('unavailable (403 when creating the session)', () => {
    it('shows one neutral message and no composer', async () => {
      const fake = new FakeServer();
      fake.sessions.push(() => json(403, { reason: 'public-assistant' }));
      const view = await mount(fake);

      await view.until(() => view.text().includes('這個對話視窗目前無法使用'));

      expect(view.host.querySelector('textarea')).toBeNull();
      expect(view.host.querySelector('button.close')).not.toBeNull();
      expect(fake.runRequests).toHaveLength(0);
    });

    it('is also what a page without an assistant id gets', async () => {
      const view = await mount(server(), { context: { assistantId: null } });
      await view.until(() => view.text().includes('這個對話視窗目前無法使用'));
    });
  });

  describe('paused (403 when asking)', () => {
    it('shows 目前暫停服務, keeps the earlier conversation and puts the question back in the draft', async () => {
      const fake = server();
      fake.runs.push(answer(companyReply), () => json(403, { reason: 'public-assistant' }));
      const view = await mountReady(fake);
      view.type(QUESTION);
      view.send();
      await view.until(() => view.host.querySelector('[data-kind="company-data"]') !== null);

      view.type('還有別的問題');
      view.send();
      await view.until(() => view.text().includes('目前暫停服務'));

      expect(view.host.querySelector('textarea')).toBeNull();
      expect(view.text()).toContain('收到商品後七天內可以申請退貨');
      expect(fake.runRequests).toHaveLength(2);
    });

    it('is also what a 403 on the replacement session after a 401 becomes', async () => {
      const fake = new FakeServer();
      fake.sessions.push(session(), () => json(403, { reason: 'public-assistant' }));
      fake.runs.push(() => json(401, { reason: 'unauthorized' }));
      const view = await mountReady(fake);

      view.type(QUESTION);
      view.send();
      await view.until(() => view.text().includes('目前暫停服務'));
    });
  });

  describe('rate limiting (429)', () => {
    it('shows 問題太頻繁了 with a countdown from Retry-After, then allows a retry', async () => {
      const fake = server();
      fake.runs.push(() => json(429, { reason: 'rate-limited' }, { 'Retry-After': '3' }), answer(companyReply));
      const view = await mountReady(fake);
      vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] });

      view.type(QUESTION);
      view.send();
      await view.until(() => view.text().includes('問題太頻繁了，請稍後再試'));

      expect(view.text()).toContain('請等候 3 秒');
      const retry = () => view.host.querySelector<HTMLButtonElement>('button.retry');
      expect(retry()?.disabled).toBe(true);
      expect(view.host.querySelector<HTMLButtonElement>('button.send')?.disabled).toBe(true);

      vi.advanceTimersByTime(1000);
      await view.until(() => view.text().includes('請等候 2 秒'));
      vi.advanceTimersByTime(2000);
      await view.until(() => !view.text().includes('請等候'));
      expect(retry()?.disabled).toBe(false);

      retry()?.click();
      await view.until(() => view.host.querySelector('[data-kind="company-data"]') !== null);
      // 重試沿用同一則問題，沒有多出一則。
      expect(fake.runRequests.map((request) => request.body.messages?.at(-1)?.id)).toEqual([
        fake.runRequests[0].body.messages?.at(-1)?.id,
        fake.runRequests[0].body.messages?.at(-1)?.id,
      ]);
    });

    it('formats a long wait in minutes', async () => {
      const fake = server();
      fake.runs.push(() => json(429, {}, { 'Retry-After': '125' }));
      const view = await mountReady(fake);

      view.type(QUESTION);
      view.send();
      await view.until(() => view.text().includes('請等候 2 分 5 秒'));
    });

    it('also counts down when creating the session is rate limited', async () => {
      const fake = new FakeServer();
      fake.sessions.push(() => json(429, { reason: 'rate-limited' }, { 'Retry-After': '2' }), session());
      const view = await mount(fake);
      vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] });

      await view.until(() => view.text().includes('請求太頻繁了'));
      expect(view.host.querySelector<HTMLButtonElement>('button.action')?.disabled).toBe(true);

      vi.advanceTimersByTime(2000);
      await view.until(() => view.host.querySelector<HTMLButtonElement>('button.action')?.disabled === false);
      view.host.querySelector<HTMLButtonElement>('button.action')?.click();
      await view.until(() => view.host.querySelector('textarea') !== null);
    });
  });

  describe('token expiry (401)', () => {
    it('creates a new session once and resends the same question with the new token', async () => {
      const fake = new FakeServer();
      fake.sessions.push(session({ token: 'token-1' }), session({ token: 'token-2' }));
      fake.runs.push(() => json(401, { reason: 'unauthorized' }), answer(companyReply));
      const view = await mountReady(fake);

      view.type(QUESTION);
      view.send();
      await view.until(() => view.host.querySelector('[data-kind="company-data"]') !== null, 'no answer after recreate');

      expect(fake.sessionRequests).toHaveLength(2);
      expect(fake.runRequests.map((request) => request.headers['Authorization'])).toEqual(['Visitor token-1', 'Visitor token-2']);
      expect(fake.runRequests[1].body.messages).toEqual(fake.runRequests[0].body.messages);
      expect(view.host.querySelector('[role="alert"]')).toBeNull();
      expect(JSON.parse(sessionStorage.getItem(`smartagri-widget:11111111-1111-4111-8111-111111111111:session`) ?? '{}').token).toBe('token-2');
    });

    it('gives up after a second 401 and offers a retry (which gets a fresh session again)', async () => {
      const fake = new FakeServer();
      fake.sessions.push(session({ token: 'token-1' }), session({ token: 'token-2' }), session({ token: 'token-3' }));
      fake.runs.push(() => json(401, {}), () => json(401, {}), answer(companyReply));
      const view = await mountReady(fake);

      view.type(QUESTION);
      view.send();
      await view.until(() => view.text().includes('連線已逾時，請再試一次。'));

      expect(fake.sessionRequests).toHaveLength(2);
      expect(fake.runRequests).toHaveLength(2);

      view.host.querySelector<HTMLButtonElement>('button.retry')?.click();
      await view.until(() => view.host.querySelector('[data-kind="company-data"]') !== null);
      expect(fake.runRequests.at(-1)?.headers['Authorization']).toBe('Visitor token-3');
    });

    it('treats an expired stored session as missing and creates a new one up front', async () => {
      const fake = new FakeServer();
      fake.sessions.push(session({ token: 'fresh' }));
      sessionStorage.setItem(
        'smartagri-widget:11111111-1111-4111-8111-111111111111:session',
        JSON.stringify({ token: 'stale', expiresAt: new Date(Date.now() + 10_000).toISOString(), assistant: { displayName: 'x', welcomeMessage: '', brandColor: 'forest', showCitations: true } }),
      );
      await mountReady(fake);
      expect(fake.sessionRequests).toHaveLength(1);
    });

    it('reuses a stored, unexpired session without calling the API', async () => {
      const fake = new FakeServer();
      sessionStorage.setItem(
        'smartagri-widget:11111111-1111-4111-8111-111111111111:session',
        JSON.stringify({
          token: 'stored',
          expiresAt: new Date(Date.now() + 3600_000).toISOString(),
          assistant: { displayName: '舊視窗', welcomeMessage: '', brandColor: 'ocean', showCitations: true },
        }),
      );
      fake.runs.push(answer(companyReply));
      const view = await mountReady(fake);

      expect(fake.sessionRequests).toHaveLength(0);
      expect(view.host.querySelector('h1')?.textContent).toBe('舊視窗');
      view.type(QUESTION);
      view.send();
      await view.until(() => view.host.querySelector('[data-kind="company-data"]') !== null);
      expect(fake.runRequests[0].headers['Authorization']).toBe('Visitor stored');
    });
  });

  describe('errors', () => {
    it('offers a retry after a network error and resends the same question', async () => {
      const fake = server();
      fake.runs.push(() => Promise.reject(new TypeError('Failed to fetch')), answer(companyReply));
      const view = await mountReady(fake);

      view.type(QUESTION);
      view.send();
      await view.until(() => view.text().includes('連線中斷，這則問題沒有取得回答，請再試一次。'));
      expect(view.text()).toContain(QUESTION);

      view.host.querySelector<HTMLButtonElement>('button.retry')?.click();
      await view.until(() => view.host.querySelector('[data-kind="company-data"]') !== null);
      expect(fake.runRequests[1].body.messages?.at(-1)?.id).toBe(fake.runRequests[0].body.messages?.at(-1)?.id);
      expect(view.host.querySelector('[role="alert"]')).toBeNull();
    });

    it('shows a retryable error when the session cannot be created (network)', async () => {
      const fake = new FakeServer();
      fake.sessions.push(() => Promise.reject(new TypeError('Failed to fetch')), session());
      const view = await mount(fake);

      await view.until(() => view.text().includes('無法連線'));
      view.host.querySelector<HTMLButtonElement>('button.action')?.click();
      await view.until(() => view.host.querySelector('textarea') !== null);
    });

    it('treats a malformed session response as a connection failure', async () => {
      const fake = new FakeServer();
      fake.sessions.push(() => json(201, { nope: true }));
      const view = await mount(fake);
      await view.until(() => view.text().includes('無法連線'));
    });

    it('409: asks the visitor to wait for the previous answer', async () => {
      const fake = server();
      fake.runs.push(() => json(409, { reason: 'chat-run-in-progress' }));
      const view = await mountReady(fake);
      view.type(QUESTION);
      view.send();
      await view.until(() => view.text().includes('上一個問題還在回答中'));
      expect(view.host.querySelector('button.retry')).not.toBeNull();
    });

    it('422: shows the server message and puts the question back in the composer', async () => {
      const fake = server();
      fake.runs.push(() => json(422, { message: '問題請在 2000 個字以內。' }));
      const view = await mountReady(fake);
      view.type(QUESTION);
      view.send();
      await view.until(() => view.text().includes('問題請在 2000 個字以內。'));

      expect(view.host.querySelector('textarea')?.value).toBe(QUESTION);
      expect(view.host.querySelector('button.retry')).toBeNull();
    });

    it.each(['chat-not-configured', 'chat-unavailable'])('503 %s: says the service is temporarily unavailable', async (reason) => {
      const fake = server();
      fake.runs.push(() => json(503, { reason }));
      const view = await mountReady(fake);
      view.type(QUESTION);
      view.send();
      await view.until(() => view.text().includes('對話服務暫時無法使用，請稍後再試。'));
    });

    it('RUN_ERROR in the middle of the stream is a retryable error and keeps no half answer', async () => {
      const fake = server();
      fake.runs.push(
        () =>
          sse([
            { type: 'RUN_STARTED', threadId: '', runId: 'r' },
            { type: 'TEXT_MESSAGE_START', messageId: 'm', role: 'assistant' },
            { type: 'TEXT_MESSAGE_CONTENT', messageId: 'm', delta: '根據資料回答，本回答引用段落 [' },
            { type: 'RUN_ERROR', message: '對話模型暫時無法使用，請稍後重試。', code: 'chat-unavailable' },
          ]),
        answer(companyReply),
      );
      const view = await mountReady(fake);
      view.type(QUESTION);
      view.send();
      await view.until(() => view.text().includes('對話服務暫時無法使用，請稍後再試。'));

      expect(view.host.querySelector('app-streaming-reply')).toBeNull();
      view.host.querySelector<HTMLButtonElement>('button.retry')?.click();
      await view.until(() => view.host.querySelector('[data-kind="company-data"]') !== null);
    });
  });

  it('ignores blank questions and does not send while an answer is being produced', async () => {
    const fake = server();
    fake.runs.push(() => new Promise<Response>(() => undefined));
    const view = await mountReady(fake);

    view.type('   ');
    view.send();
    expect(fake.runRequests).toHaveLength(0);

    view.type(QUESTION);
    view.send();
    await view.until(() => fake.runRequests.length === 1);
    view.type('第二個問題');
    view.send();
    expect(fake.runRequests).toHaveLength(1);
  });

  it('limits the question length and warns when close to the limit', async () => {
    const view = await mountReady(server());
    expect(view.host.querySelector('textarea')?.getAttribute('maxlength')).toBe('2000');

    view.type('字'.repeat(1850));
    expect(view.text()).toContain('還可輸入 150 字');
  });

  it('sends on Enter but not on Shift+Enter or while composing', async () => {
    const fake = server();
    fake.runs.push(answer(companyReply));
    const view = await mountReady(fake);
    const field = required<HTMLTextAreaElement>(view.host, 'textarea');
    view.type(QUESTION);

    field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', shiftKey: true, bubbles: true, cancelable: true }));
    field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', isComposing: true, bubbles: true, cancelable: true }));
    expect(fake.runRequests).toHaveLength(0);

    field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
    await view.until(() => fake.runRequests.length === 1);
  });
});
