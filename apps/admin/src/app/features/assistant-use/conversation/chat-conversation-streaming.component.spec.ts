import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, type Subscriber } from 'rxjs';
import { CHAT_RUNNER, type ChatRunEvent, type ChatRunner, type ChatRunRequest } from '../../../core/chat/chat-runner';
import type { ChatMessageView } from '../../../core/domain/conversation.model';
import { provideAssistantUseTesting } from '../assistant-use.testing';
import { ChatConversationComponent } from './chat-conversation.component';

const ASSISTANT = 'assistant-customer-service';
const QUESTION = '收到商品後幾天內可以退貨？';

/** 由測試逐一推送事件的 runner：記錄每次請求，以及是否被停止（取消訂閱）。 */
class ScriptedRunner implements ChatRunner {
  readonly requests: ChatRunRequest[] = [];
  stopped = 0;
  private current: Subscriber<ChatRunEvent> | null = null;

  run(request: ChatRunRequest): Observable<ChatRunEvent> {
    this.requests.push(request);
    return new Observable<ChatRunEvent>((subscriber) => {
      this.current = subscriber;
      this.finished = false;
      return () => {
        if (!this.finished) this.stopped += 1;
      };
    });
  }

  private finished = false;

  emit(event: ChatRunEvent): void {
    this.current?.next(event);
  }

  complete(): void {
    this.finished = true;
    this.current?.complete();
  }
}

const companyReply: ChatMessageView = {
  id: 'server-reply-1',
  author: 'assistant',
  createdAt: '2026-09-22T02:00:00.000Z',
  reply: {
    kind: 'company-data',
    text: '收到商品後七天內可以申請退貨 [1]。',
    citations: [
      {
        id: 'citation-1',
        knowledgeBaseName: '退貨政策',
        documentName: '退貨政策.md',
        excerpt: '收到商品後七天內可申請退貨。',
        updatedLabel: '2026-09-01',
      },
    ],
    citationNotice: null,
  },
};

const noResultReply: ChatMessageView = {
  id: 'server-reply-2',
  author: 'assistant',
  createdAt: '2026-09-22T02:00:00.000Z',
  reply: { kind: 'no-result', text: '目前的資料中找不到這個問題的答案。', nextSteps: ['換個說法再問一次。'] },
};

function setup() {
  const testing = provideAssistantUseTesting('account-external-customer');
  const runner = new ScriptedRunner();
  TestBed.configureTestingModule({
    imports: [ChatConversationComponent],
    providers: [provideRouter([]), ...testing.providers, { provide: CHAT_RUNNER, useValue: runner }],
  });
  const fixture: ComponentFixture<ChatConversationComponent> = TestBed.createComponent(ChatConversationComponent);
  fixture.componentRef.setInput('assistantId', ASSISTANT);
  fixture.detectChanges();
  document.body.appendChild(fixture.nativeElement);
  const host = fixture.nativeElement as HTMLElement;
  const changed: (string | null)[] = [];
  fixture.componentInstance.changed.subscribe((threadId) => changed.push(threadId));

  const ask = (text = QUESTION) => {
    const input = host.querySelector<HTMLInputElement>('#chat-input');
    if (input === null) throw new Error('missing composer');
    input.value = text;
    input.dispatchEvent(new Event('input'));
    host.querySelector<HTMLButtonElement>('form.composer button[type="submit"]')?.click();
    fixture.detectChanges();
  };
  const push = (...events: ChatRunEvent[]) => {
    events.forEach((event) => runner.emit(event));
    fixture.detectChanges();
  };
  const finish = () => {
    runner.complete();
    fixture.detectChanges();
  };

  return { fixture, host, runner, ask, push, finish, changed };
}

const log = (host: HTMLElement) => host.querySelector<HTMLElement>('[role="log"]');
const submit = (host: HTMLElement) => host.querySelector<HTMLButtonElement>('form.composer button[type="submit"]');
const stopButton = (host: HTMLElement) => host.querySelector<HTMLButtonElement>('button.stop');
const accountBubbles = (host: HTMLElement) =>
  Array.from(host.querySelectorAll('[data-author="account"]')).map((node) => node.textContent?.trim());

describe('ChatConversationComponent streaming', () => {
  afterEach(() => document.body.querySelectorAll('app-chat-conversation').forEach((node) => node.remove()));

  it('streaming: shows the question at once, locks sending, offers stop and keeps citations inert', () => {
    const { host, runner, ask, push } = setup();

    ask();

    expect(runner.requests).toEqual([
      expect.objectContaining({ assistantId: ASSISTANT, question: QUESTION, viewerId: 'account-external-customer' }),
    ]);
    expect(accountBubbles(host).at(-1)).toContain(QUESTION);
    expect(submit(host)?.disabled).toBe(true);
    expect(stopButton(host)?.textContent).toContain('停止回答');
    expect(host.querySelectorAll<HTMLButtonElement>('button.suggested-prompt')[0]?.disabled).toBe(true);
    // 串流中整個紀錄 aria-busy，螢幕報讀器不逐字朗讀。
    expect(log(host)?.getAttribute('aria-busy')).toBe('true');
    expect(log(host)?.getAttribute('aria-live')).toBe('polite');
    expect(host.querySelector('[role="status"]')?.textContent).toContain('助理正在回答');

    push({ type: 'text-delta', delta: '收到商品後七天內' }, { type: 'text-delta', delta: '可以申請退貨 [1]。' });

    const streaming = host.querySelector('app-streaming-reply');
    expect(streaming?.textContent).toContain('收到商品後七天內可以申請退貨');
    const pending = streaming?.querySelector('.citation-pending');
    expect(pending?.textContent).toBe('[1]');
    expect(pending?.tagName).toBe('SPAN');
    expect(streaming?.querySelector('button, a')).toBeNull();
    expect(host.querySelector('button.citation-toggle')).toBeNull();
  });

  it('completed: replaces the streamed text with the final reply, unlocks sending and reports the thread', () => {
    const { host, ask, push, finish, changed } = setup();
    ask();
    push({ type: 'text-delta', delta: '收到商品後七天內可以申請退貨 [1]。' });

    push({ type: 'reply', message: companyReply }, { type: 'thread', threadId: 'thread-7', title: '退貨' });
    finish();

    expect(host.querySelector('app-streaming-reply')).toBeNull();
    const final = host.querySelector('[data-kind="company-data"]');
    expect(final?.textContent).toContain('收到商品後七天內可以申請退貨 [1]。');
    expect(final?.querySelector('button.citation-toggle')?.textContent).toContain('查看引用來源（1）');
    expect(accountBubbles(host).at(-1)).toContain(QUESTION);
    expect(submit(host)?.disabled).toBe(false);
    expect(stopButton(host)).toBeNull();
    expect(log(host)?.hasAttribute('aria-busy')).toBe(false);
    expect(changed).toEqual(['thread-7']);
  });

  it('replaced: a no-result reply takes the place of a draft that failed citation checks', () => {
    const { host, ask, push, finish } = setup();
    ask();
    push({ type: 'text-delta', delta: '根據目前的資料 [3]。' });
    expect(host.querySelector('app-streaming-reply')?.textContent).toContain('根據目前的資料');

    push({ type: 'reply', message: noResultReply });
    finish();

    expect(host.textContent).not.toContain('根據目前的資料');
    const final = host.querySelector('[data-kind="no-result"]');
    expect(final?.textContent).toContain('目前的資料中找不到這個問題的答案。');
    expect(final?.textContent).toContain('換個說法再問一次。');
  });

  it('error: keeps the question, shows a retryable alert and retries the same question', () => {
    const { fixture, host, runner, ask, push, finish } = setup();
    ask();
    push({ type: 'text-delta', delta: '收到' });
    push({
      type: 'error',
      error: { kind: 'unavailable', message: '對話模型暫時無法使用，請稍後重試。', retryable: true },
    });
    finish();

    expect(host.querySelector('app-streaming-reply')).toBeNull();
    const alert = host.querySelector('.run-error[role="alert"]');
    expect(alert?.textContent).toContain('對話模型暫時無法使用');
    expect(accountBubbles(host).at(-1)).toContain(QUESTION);
    expect(submit(host)?.disabled).toBe(false);

    alert?.querySelector<HTMLButtonElement>('button.retry')?.click();
    fixture.detectChanges();

    expect(runner.requests).toHaveLength(2);
    expect(runner.requests[1].question).toBe(QUESTION);
    expect(accountBubbles(host).filter((text) => text?.includes(QUESTION))).toHaveLength(1);
    // #105: the retry carries the failed attempt's own client message id, unchanged, so the
    // backend can recognize it as the same question instead of saving a second one.
    expect(runner.requests[0].clientMessageId).toEqual(expect.any(String));
    expect(runner.requests[1].clientMessageId).toBe(runner.requests[0].clientMessageId);
  });

  it('a new question (not a retry) gets a different client message id each time', () => {
    const { ask, push, finish, runner } = setup();
    ask();
    push({ type: 'reply', message: companyReply }, { type: 'thread', threadId: 'thread-7', title: '退貨' });
    finish();

    ask('另一個問題');
    push({ type: 'reply', message: noResultReply });
    finish();

    expect(runner.requests).toHaveLength(2);
    expect(runner.requests[0].clientMessageId).toEqual(expect.any(String));
    expect(runner.requests[1].clientMessageId).toEqual(expect.any(String));
    expect(runner.requests[1].clientMessageId).not.toBe(runner.requests[0].clientMessageId);
  });

  it('error: puts an invalid question back in the composer', () => {
    const { host, ask, push } = setup();
    ask('太長的問題');
    push({ type: 'error', error: { kind: 'validation-failed', message: '問題請在 2000 個字以內。', retryable: false } });

    expect(accountBubbles(host)).not.toContain('太長的問題');
    expect(host.querySelector('#chat-input-error')?.textContent).toContain('2000');
    expect(host.querySelector<HTMLInputElement>('#chat-input')?.value).toBe('太長的問題');
  });

  it('stopped: cancels the run from the keyboard, keeps the question and drops the partial answer', () => {
    const { fixture, host, runner, ask, push, changed } = setup();
    ask();
    push({ type: 'text-delta', delta: '收到商品後七天內' });

    const stop = stopButton(host);
    stop?.focus();
    stop?.click();
    fixture.detectChanges();

    expect(runner.stopped).toBe(1);
    expect(host.querySelector('app-streaming-reply')).toBeNull();
    expect(host.textContent).not.toContain('收到商品後七天內');
    expect(accountBubbles(host).at(-1)).toContain(QUESTION);
    expect(host.querySelector('.run-note')?.textContent).toContain('已停止回答');
    expect(host.querySelector('[role="status"]')?.textContent).toContain('已停止回答');
    expect(submit(host)?.disabled).toBe(false);
    expect(document.activeElement).toBe(host.querySelector('#chat-input'));
    expect(changed).toEqual([null]);
  });

  it('does not send a blank question', () => {
    const { host, runner, ask } = setup();
    ask('   ');

    expect(runner.requests).toEqual([]);
    expect(host.querySelector('#chat-input-error')?.textContent).toContain('請先輸入問題');
  });
});
