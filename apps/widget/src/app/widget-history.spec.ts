import { HISTORY_MAX_MESSAGES } from './widget.model';
import { ASSISTANT_ID, FakeServer, answer, companyReply, mountReady, noResultReply, session, type Harness } from './widget.testing';

const HISTORY_KEY = `smartagri-widget:${ASSISTANT_ID}:history`;

async function ask(view: Harness, fake: FakeServer, question: string): Promise<void> {
  const before = fake.runRequests.length;
  view.type(question);
  view.send();
  await view.until(() => fake.runRequests.length === before + 1 && view.host.querySelector('.streaming') === null && view.text().includes(question));
  await view.until(() => view.host.querySelector('button.send') !== null && !view.host.querySelector('.streaming'));
}

describe('conversation history', () => {
  beforeEach(() => sessionStorage.clear());
  afterEach(() => document.body.replaceChildren());

  it('sends earlier questions and answers as RunAgentInput.messages, oldest first, before the new question', async () => {
    const fake = new FakeServer();
    fake.sessions.push(session());
    fake.runs.push(answer(companyReply), answer(noResultReply));
    const view = await mountReady(fake);

    await ask(view, fake, '第一個問題');
    await ask(view, fake, '第二個問題');

    expect(fake.runRequests[1].body.messages?.map((message) => [message.role, message.content])).toEqual([
      ['user', '第一個問題'],
      ['assistant', '收到商品後七天內可以申請退貨 [1]。'],
      ['user', '第二個問題'],
    ]);
    expect(fake.runRequests[1].body.threadId).toBe('');
  });

  it('keeps the conversation in sessionStorage and restores it (with its sources) in a new window instance', async () => {
    const fake = new FakeServer();
    fake.sessions.push(session());
    fake.runs.push(answer(companyReply));
    const first = await mountReady(fake);
    await ask(first, fake, '第一個問題');

    const stored = JSON.parse(sessionStorage.getItem(HISTORY_KEY) ?? '[]') as { author: string }[];
    expect(stored.map((message) => message.author)).toEqual(['account', 'assistant']);
    document.body.replaceChildren();

    const second = await mountReady(fake);
    expect(second.text()).toContain('第一個問題');
    expect(second.host.querySelector('.citation-toggle')?.textContent).toContain('查看引用來源（1）');
    // 重新整理後沿用同一個工作階段，不再建立。
    expect(fake.sessionRequests).toHaveLength(1);
  });

  it(`keeps only the last ${HISTORY_MAX_MESSAGES} messages, in storage, on screen and in the request`, async () => {
    const fake = new FakeServer();
    fake.sessions.push(session());
    fake.runs.push(answer(noResultReply));
    const view = await mountReady(fake);

    for (let turn = 1; turn <= 12; turn += 1) await ask(view, fake, `問題${turn}`);

    const stored = JSON.parse(sessionStorage.getItem(HISTORY_KEY) ?? '[]') as { author: string; text?: string }[];
    expect(stored).toHaveLength(HISTORY_MAX_MESSAGES);
    expect(stored[0]).toMatchObject({ author: 'account', text: '問題3' });
    expect(view.host.querySelectorAll('app-chat-message')).toHaveLength(HISTORY_MAX_MESSAGES);

    // 第 12 題送出時，前文是第 2 到第 11 題的問與答（共 20 則），再加上這一題。
    const messages = fake.runRequests[11].body.messages ?? [];
    expect(messages).toHaveLength(HISTORY_MAX_MESSAGES + 1);
    expect(messages[0]).toMatchObject({ role: 'user', content: '問題2' });
    expect(messages.at(-1)).toMatchObject({ role: 'user', content: '問題12' });
  });

  it('does not put a question that got no answer into the history', async () => {
    const fake = new FakeServer();
    fake.sessions.push(session());
    fake.runs.push(() => Promise.reject(new TypeError('offline')), answer(companyReply));
    const view = await mountReady(fake);
    view.type('沒有答案的問題');
    view.send();
    await view.until(() => view.text().includes('連線中斷'));
    expect(sessionStorage.getItem(HISTORY_KEY)).toBeNull();

    await ask(view, fake, '新的問題');

    expect(fake.runRequests[1].body.messages?.map((message) => message.content)).toEqual(['新的問題']);
  });

  it('ignores damaged or foreign content in sessionStorage', async () => {
    sessionStorage.setItem(
      HISTORY_KEY,
      JSON.stringify([{ id: 'x', author: 'assistant', reply: { kind: 'form-request', text: 'no' } }, 'junk', { id: 'y', author: 'account', text: '還在' }]),
    );
    const fake = new FakeServer();
    fake.sessions.push(session());
    const view = await mountReady(fake);
    expect(view.host.querySelectorAll('app-chat-message')).toHaveLength(1);
    expect(view.text()).toContain('還在');

    sessionStorage.setItem(HISTORY_KEY, '{not json');
    document.body.replaceChildren();
    const again = await mountReady(fake);
    expect(again.host.querySelectorAll('app-chat-message')).toHaveLength(0);
  });

  it('works without any storage (blocked by the browser)', async () => {
    const fake = new FakeServer();
    fake.sessions.push(session());
    fake.runs.push(answer(companyReply));
    const view = await mountReady(fake, { storage: null });
    await ask(view, fake, '問題');
    expect(view.host.querySelector('[data-kind="company-data"]')).not.toBeNull();
  });

  it('never uses cookies or localStorage', async () => {
    const localWrites = vi.spyOn(Storage.prototype, 'setItem');
    const fake = new FakeServer();
    fake.sessions.push(session());
    fake.runs.push(answer(companyReply));
    const view = await mountReady(fake);
    await ask(view, fake, '問題');

    expect(localStorage.length).toBe(0);
    expect(document.cookie).toBe('');
    // 只寫 sessionStorage 的兩個鍵。
    expect(sessionStorage.length).toBe(2);
    expect(localWrites.mock.calls.every(([key]) => key.startsWith('smartagri-widget:'))).toBe(true);
    localWrites.mockRestore();
    // 所有請求都不帶 cookie。
    expect(fake.requests.length).toBeGreaterThan(0);
  });
});
