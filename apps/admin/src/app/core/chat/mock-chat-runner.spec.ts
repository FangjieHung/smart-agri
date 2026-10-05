import { firstValueFrom, of } from 'rxjs';
import { DEMO_SEED } from '../repositories/demo-seed';
import { MockDemoRepository } from '../repositories/mock-demo-repository';
import type { ChatRunEvent, ChatRunRequest } from './chat-runner';
import { MOCK_FORM_CHECK_MS, MOCK_REJECTED_DRAFT, MOCK_SLICE_INTERVAL_MS, MockChatRunner, sliceText } from './mock-chat-runner';

const VIEWER = 'account-external-customer';
const REQUEST: ChatRunRequest = {
  viewerId: VIEWER,
  assistantId: 'assistant-customer-service',
  question: '收到商品後幾天內可以退貨？',
};

/**
 * 客服助理有可用表單，所以每題先送 `form-check`（issue #171）；預設不等待，專門的測試才用
 * `MOCK_FORM_CHECK_MS`。
 */
function setup(formCheckMs = 0) {
  const repository = new MockDemoRepository(DEMO_SEED, {
    now: () => new Date('2026-09-22T02:00:00.000Z'),
    viewer: () => VIEWER,
  });
  return { repository, runner: new MockChatRunner(repository, MOCK_SLICE_INTERVAL_MS, formCheckMs) };
}

/** 去掉開頭的 `form-check`，只看串流與回覆。 */
const withoutCheck = (events: readonly ChatRunEvent[]) => events.filter((event) => event.type !== 'form-check');

function record(runner: MockChatRunner, request: ChatRunRequest = REQUEST) {
  const events: ChatRunEvent[] = [];
  let completed = false;
  const subscription = runner.run(request).subscribe({
    next: (event) => events.push(event),
    complete: () => (completed = true),
  });
  return { events, subscription, isCompleted: () => completed };
}

const streamed = (events: readonly ChatRunEvent[]) =>
  events.map((event) => (event.type === 'text-delta' ? event.delta : '')).join('');

async function storedMessages(repository: MockDemoRepository) {
  const view = await firstValueFrom(repository.getAssistantChat(REQUEST.assistantId));
  if (view.status !== 'ready') throw new Error(`expected ready, got ${view.status}`);
  return view.data.messages;
}

describe('MockChatRunner', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('splits text into at most twelve slices that join back to the original', () => {
    const text = '收到商品後七天內可以申請退貨，請保留完整包裝與發票。退貨運費由買家負擔。';
    const slices = sliceText(text);
    expect(slices.length).toBeLessThanOrEqual(12);
    expect(slices.join('')).toBe(text);
    expect(sliceText('')).toEqual([]);
  });

  it('streams the fixture reply slice by slice, then sends the final reply and the thread', async () => {
    const { runner } = setup();
    const { events, isCompleted } = record(runner);

    expect(events).toEqual([{ type: 'form-check' }]);
    vi.advanceTimersByTime(MOCK_SLICE_INTERVAL_MS);
    expect(withoutCheck(events)).toHaveLength(1);
    expect(withoutCheck(events)[0].type).toBe('text-delta');

    await vi.runAllTimersAsync();

    expect(isCompleted()).toBe(true);
    const reply = events.find((event) => event.type === 'reply');
    if (reply?.type !== 'reply' || reply.message.author !== 'assistant') throw new Error('expected reply');
    expect(reply.message.reply.kind).toBe('company-data');
    expect(streamed(events)).toBe(reply.message.reply.text);
    expect(events.at(-1)).toEqual({ type: 'thread', threadId: expect.any(String), title: expect.any(String) });
  });

  it('keeps only the question when the run is stopped before the reply', async () => {
    const { runner, repository } = setup();
    const { events, subscription } = record(runner);
    vi.advanceTimersByTime(MOCK_SLICE_INTERVAL_MS * 2);
    expect(withoutCheck(events).length).toBe(2);

    subscription.unsubscribe();
    await vi.runAllTimersAsync();

    expect(withoutCheck(events).every((event) => event.type === 'text-delta')).toBe(true);
    const messages = await storedMessages(repository);
    expect(messages.map((message) => message.author)).toEqual(['account']);
  });

  it('keeps the whole exchange once the reply has been delivered', async () => {
    const { runner, repository } = setup();
    record(runner);
    await vi.runAllTimersAsync();

    const messages = await storedMessages(repository);
    expect(messages.map((message) => message.author)).toEqual(['account', 'assistant']);
  });

  it('streams a draft and replaces it with no-result in the answer-rejected scenario', async () => {
    const { runner, repository } = setup();
    repository.setScenario('answer-rejected');
    const { events } = record(runner);
    await vi.runAllTimersAsync();

    expect(streamed(events)).toBe(MOCK_REJECTED_DRAFT);
    const reply = events.find((event) => event.type === 'reply');
    expect(reply?.type === 'reply' && reply.message.author === 'assistant' && reply.message.reply.kind).toBe(
      'no-result',
    );
  });

  it('reports an invalid question as validation-failed without streaming', () => {
    const { runner } = setup();
    const { events, isCompleted } = record(runner, { ...REQUEST, question: '   ' });

    expect(isCompleted()).toBe(true);
    expect(events).toEqual([
      { type: 'error', error: { kind: 'validation-failed', message: '請先輸入問題。', retryable: false } },
    ]);
  });

  it('reports an unusable assistant as permission-denied', () => {
    const { runner } = setup();
    const { events } = record(runner, { ...REQUEST, assistantId: 'assistant-missing' });

    expect(events).toEqual([
      { type: 'error', error: { kind: 'permission-denied', message: expect.any(String), retryable: false } },
    ]);
  });

  it('checks for a form first and waits before answering when the assistant has a usable form (#171)', async () => {
    const { runner } = setup(MOCK_FORM_CHECK_MS);
    const { events } = record(runner);

    expect(events).toEqual([{ type: 'form-check' }]);
    vi.advanceTimersByTime(MOCK_FORM_CHECK_MS - 1);
    expect(events).toHaveLength(1);
    vi.advanceTimersByTime(1 + MOCK_SLICE_INTERVAL_MS);
    expect(events[1]?.type).toBe('text-delta');
    await vi.runAllTimersAsync();
    expect(events.filter((event) => event.type === 'form-check')).toHaveLength(1);
  });

  it('never checks for a form when the assistant has none for this viewer (#171)', async () => {
    const { runner, repository } = setup(MOCK_FORM_CHECK_MS);
    vi.spyOn(repository, 'listChatForms').mockReturnValue(of({ status: 'ready', data: [] }));
    const { events } = record(runner);
    await vi.runAllTimersAsync();

    expect(events.some((event) => event.type === 'form-check')).toBe(false);
    expect(events.some((event) => event.type === 'reply')).toBe(true);
  });
});
