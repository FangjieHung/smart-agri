import { firstValueFrom, toArray } from 'rxjs';
// 後端錄製的串流（`tools/agui-contract/fixtures`，由 .NET 整合測試確保沒有偏移），
// 與 CI 的 `check-agui-stream.mjs` 解析的是同三份檔案。錄製檔是文字資料、不是程式模組，
// 不適用 Nx 的專案邊界規則。
/* eslint-disable @nx/enforce-module-boundaries */
import companyDataSaved from '../../../../tools/agui-contract/fixtures/company-data-saved.sse' with { loader: 'text' };
import failMidway from '../../../../tools/agui-contract/fixtures/fail-midway.sse' with { loader: 'text' };
import noResultUnsaved from '../../../../tools/agui-contract/fixtures/no-result-unsaved.sse' with { loader: 'text' };
/* eslint-enable @nx/enforce-module-boundaries */
import { AgUiChatRunner, apiChatRunsPath } from './ag-ui-chat-runner';
import type { ChatRunEvent, ChatRunRequest } from './chat-runner.types';

const FIXTURES: Record<string, string> = {
  'company-data-saved.sse': companyDataSaved,
  'fail-midway.sse': failMidway,
  'no-result-unsaved.sse': noResultUnsaved,
};

function fixture(name: string): string {
  return FIXTURES[name];
}

const REQUEST: ChatRunRequest = {
  assistantId: 'assistant-1',
  question: '收到商品後幾天內可以申請退貨？',
};

interface Captured {
  url: string;
  init: RequestInit;
  body: { threadId: string; messages: { id: string; role: string; content: string }[] };
  headers: Record<string, string>;
}

function setup(respond: () => Response, token: string | null = 'token-1') {
  const calls: Captured[] = [];
  const onUnauthorized = vi.fn();
  const fetch = vi.fn(async (url: string, init: RequestInit) => {
    calls.push({
      url,
      init,
      body: JSON.parse(String(init.body)),
      headers: init.headers as Record<string, string>,
    });
    return respond();
  });
  const runner = new AgUiChatRunner({ accessToken: () => token, onUnauthorized, fetch });
  return { runner, calls, fetch, onUnauthorized };
}

const sse = (body: string) => () =>
  new Response(body, { status: 200, headers: { 'content-type': 'text/event-stream' } });

interface RecordedResponse {
  readonly status: number;
  readonly contentType: string | null;
  readonly retryAfter?: string;
  readonly body: string;
}

/**
 * 串流開始前的錯誤回應（issue #329），2026-10-08 由真實 API 錄下：`dotnet run`（Development、Fake 模型、
 * 暫存資料庫），以 `@ag-ui/client` 的 `HttpAgent` 對 `POST …/chat/runs`（成員）與
 * `POST /api/v1/public/assistants/{id}/chat/runs`（訪客）送出，記下狀態、`content-type`、`Retry-After`
 * 與本文原文，未經修改。409 是同一段對話（訪客則是同一個工作階段）同時送出多個問題；503 是分別不設定
 * `Ai:Chat:Provider`、`Ai:Embedding:Provider` 啟動。API 的錯誤一律是 `application/problem+json`，
 * `@ag-ui/client` 不解析它（只認 `application/json`），所以錯誤的 `payload` 是字串。
 */
const RECORDED = {
  memberAssistantUse403: { status: 403, contentType: 'application/problem+json', body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"assistant-use","message":"你沒有使用這個助理的權限，或它已不存在。"}' },
  memberChatThread403: { status: 403, contentType: 'application/problem+json', body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"chat-thread","message":"你沒有這段對話的存取權限，或它已不存在。"}' },
  memberRunInProgress409: { status: 409, contentType: 'application/problem+json', body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Conflict","status":409,"reason":"chat-run-in-progress","message":"這段對話還在回覆上一個問題，請等回覆完成後再送出。"}' },
  memberTooLong422: { status: 422, contentType: 'application/problem+json', body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"message":"問題請在 2000 個字以內。","errors":{"question":["問題請在 2000 個字以內。"]}}' },
  memberBlank422: { status: 422, contentType: 'application/problem+json', body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"message":"請先輸入問題。","errors":{"question":["請先輸入問題。"]}}' },
  memberChatNotConfigured503: { status: 503, contentType: 'application/problem+json', body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.6.4","title":"Service Unavailable","status":503,"reason":"chat-not-configured","message":"沒有設定對話模型，請聯絡系統管理員設定 Ai:Chat:Provider 後再試。"}' },
  memberEmbeddingNotConfigured503: { status: 503, contentType: 'application/problem+json', body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.6.4","title":"Service Unavailable","status":503,"reason":"embedding-not-configured","message":"系統尚未設定嵌入模型，請聯絡系統管理員設定後重試"}' },
  visitorTampered401: { status: 401, contentType: null, body: '' },
  visitorPublicAssistant403: { status: 403, contentType: 'application/problem+json', body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"public-assistant","message":"這個對話視窗目前無法使用。"}' },
  visitorRunInProgress409: { status: 409, contentType: 'application/problem+json', body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Conflict","status":409,"reason":"chat-run-in-progress","message":"上一個問題還在回覆中，請等回覆完成後再送出。"}' },
  visitorTooLong422: { status: 422, contentType: 'application/problem+json', body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"message":"問題請在 2000 個字以內。","errors":{"question":["問題請在 2000 個字以內。"]}}' },
  visitorRateLimited429: { status: 429, contentType: 'application/problem+json', retryAfter: '60', body: '{"type":"https://tools.ietf.org/html/rfc6585#section-4","title":"Too Many Requests","status":429,"reason":"rate-limited","message":"問題太頻繁了，請稍後再試。"}' },
  visitorChatNotConfigured503: { status: 503, contentType: 'application/problem+json', body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.6.4","title":"Service Unavailable","status":503,"reason":"chat-not-configured","message":"沒有設定對話模型，請聯絡系統管理員設定 Ai:Chat:Provider 後再試。"}' },
  visitorEmbeddingNotConfigured503: { status: 503, contentType: 'application/problem+json', body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.6.4","title":"Service Unavailable","status":503,"reason":"embedding-not-configured","message":"系統尚未設定嵌入模型，請聯絡系統管理員設定後重試"}' },
} as const satisfies Record<string, RecordedResponse>;

const recorded = (response: RecordedResponse) => () =>
  new Response(response.body, {
    status: response.status,
    headers: {
      ...(response.contentType === null ? {} : { 'content-type': response.contentType }),
      ...(response.retryAfter === undefined ? {} : { 'retry-after': response.retryAfter }),
    },
  });

const collect = (runner: AgUiChatRunner, request: ChatRunRequest = REQUEST) =>
  firstValueFrom(runner.run(request).pipe(toArray()));

describe('AgUiChatRunner', () => {
  it('posts RunAgentInput with an explicit empty threadId and the bearer token', async () => {
    const { runner, calls } = setup(sse(fixture('company-data-saved.sse')));

    await collect(runner);

    expect(calls).toHaveLength(1);
    expect(calls[0].url).toBe(apiChatRunsPath('assistant-1'));
    expect(calls[0].url).toBe('/api/v1/assistants/assistant-1/chat/runs');
    expect(calls[0].init.method).toBe('POST');
    // 沒有設定時 HttpAgent 會自產 uuid，後端回 403 chat-thread。
    expect(calls[0].body.threadId).toBe('');
    expect(calls[0].headers['Authorization']).toBe('Bearer token-1');
    expect(calls[0].body.messages).toEqual([
      expect.objectContaining({ role: 'user', content: REQUEST.question }),
    ]);
  });

  it('sends a custom authorization header value verbatim, taking precedence over accessToken', async () => {
    const calls: Captured[] = [];
    const fetch = vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, init, body: JSON.parse(String(init.body)), headers: init.headers as Record<string, string> });
      return sse(fixture('company-data-saved.sse'))();
    });
    const runner = new AgUiChatRunner({
      accessToken: () => 'ignored',
      authorizationHeader: () => 'Visitor visitor-token',
      onUnauthorized: vi.fn(),
      fetch,
      runsPath: (id) => `/api/v1/public/assistants/${id}/chat/runs`,
    });

    await collect(runner);

    expect(calls[0].url).toBe('/api/v1/public/assistants/assistant-1/chat/runs');
    expect(calls[0].headers['Authorization']).toBe('Visitor visitor-token');
  });

  it('sends no Authorization header when authorizationHeader returns null and no accessToken is given', async () => {
    const calls: Captured[] = [];
    const fetch = vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, init, body: JSON.parse(String(init.body)), headers: init.headers as Record<string, string> });
      return sse(fixture('company-data-saved.sse'))();
    });
    const runner = new AgUiChatRunner({ authorizationHeader: () => null, onUnauthorized: vi.fn(), fetch });

    await collect(runner);

    expect(calls[0].headers['Authorization']).toBeUndefined();
  });

  it('sends the thread id and, for unsaved assistants, the page history before the question', async () => {
    const { runner, calls } = setup(sse(fixture('no-result-unsaved.sse')), null);

    await collect(runner, {
      ...REQUEST,
      threadId: 'thread-9',
      history: [
        { role: 'user', content: '上一題' },
        { role: 'assistant', content: '上一題的回答' },
      ],
    });

    expect(calls[0].body.threadId).toBe('thread-9');
    expect(calls[0].headers['Authorization']).toBeUndefined();
    expect(calls[0].body.messages.map((message) => [message.role, message.content])).toEqual([
      ['user', '上一題'],
      ['assistant', '上一題的回答'],
      ['user', REQUEST.question],
    ]);
  });

  it('uses clientMessageId as the question message id, for a retry to reuse (issue #105)', async () => {
    const { runner, calls } = setup(sse(fixture('fail-midway.sse')));

    await collect(runner, { ...REQUEST, clientMessageId: 'retry-msg-1' });

    expect(calls[0].body.messages).toEqual([
      expect.objectContaining({ id: 'retry-msg-1', role: 'user', content: REQUEST.question }),
    ]);
  });

  it('falls back to a positional id when no clientMessageId is given', async () => {
    const { runner, calls } = setup(sse(fixture('fail-midway.sse')));

    await collect(runner);

    expect(calls[0].body.messages).toEqual([
      expect.objectContaining({ id: 'question-1', role: 'user', content: REQUEST.question }),
    ]);
  });

  it('passes the server’s form-check event on before the answer text (#171)', async () => {
    // 模型判斷模式的串流：TEXT_MESSAGE_START 之後、第一段文字之前多一個 CUSTOM smartagri.form-check。
    const records = fixture('company-data-saved.sse').split('\n\n');
    const start = records.findIndex((record) => record.includes('"TEXT_MESSAGE_START"'));
    records.splice(start + 1, 0, 'data: {"type":"CUSTOM","name":"smartagri.form-check","value":{}}');
    const { runner } = setup(sse(records.join('\n\n')));

    const events = await collect(runner);

    expect(events[0]).toEqual({ type: 'form-check' });
    expect(events[1]?.type).toBe('text-delta');
    expect(events.filter((event) => event.type === 'form-check')).toHaveLength(1);
    expect(events.some((event) => event.type === 'reply')).toBe(true);
  });

  it('sends no form-check for a stream without one (keyword mode)', async () => {
    const { runner } = setup(sse(fixture('company-data-saved.sse')));
    const events: ChatRunEvent[] = await collect(runner);
    expect(events.some((event) => event.type === 'form-check')).toBe(false);
  });

  it('turns a saved company-data stream into deltas, the final reply and the thread', async () => {
    const { runner } = setup(sse(fixture('company-data-saved.sse')));

    const events = await collect(runner);

    expect(events.map((event) => event.type)).toEqual(['text-delta', 'text-delta', 'reply', 'thread']);
    const streamed = events
      .filter((event): event is Extract<ChatRunEvent, { type: 'text-delta' }> => event.type === 'text-delta')
      .map((event) => event.delta)
      .join('');
    expect(streamed).toBe('根據資料回答，本回答引用段落 [1]。');

    const reply = events.find((event) => event.type === 'reply');
    expect(reply).toEqual({
      type: 'reply',
      message: {
        id: '00000000-0000-0000-0000-000000000003',
        author: 'assistant',
        createdAt: '2026-01-01T00:00:00Z',
        reply: {
          kind: 'company-data',
          text: '根據資料回答，本回答引用段落 [1]。',
          citations: [
            {
              id: 'citation-00000000-0000-0000-0000-000000000003-1',
              knowledgeBaseName: '對話知識庫',
              documentName: '退貨政策.md',
              excerpt: '收到商品後七天內可申請退貨，退貨運費由買家負擔。',
              updatedLabel: '2026-01-01',
            },
          ],
          citationNotice: null,
        },
      },
    });
    expect(events.at(-1)).toEqual({
      type: 'thread',
      threadId: '00000000-0000-0000-0000-000000000001',
      title: '收到商品後幾天內可以申請退貨？退貨運費由誰負擔？',
    });
  });

  it('replaces the streamed text with the no-result reply and sends no thread when not saved', async () => {
    const { runner } = setup(sse(fixture('no-result-unsaved.sse')));

    const events = await collect(runner);

    expect(events.map((event) => event.type)).toEqual(['text-delta', 'reply']);
    const reply = events[1];
    expect(reply.type === 'reply' && reply.message.author === 'assistant' && reply.message.reply.kind).toBe(
      'no-result',
    );
  });

  it('reports RUN_ERROR mid-stream as a retryable error and nothing after it', async () => {
    const { runner } = setup(sse(fixture('fail-midway.sse')));

    const events = await collect(runner);

    expect(events.map((event) => event.type)).toEqual(['text-delta', 'error']);
    expect(events[1]).toEqual({
      type: 'error',
      error: { kind: 'unavailable', message: '對話模型暫時無法使用，請稍後重試。', retryable: true },
    });
  });

  it.each([
    ['member 403 assistant-use', RECORDED.memberAssistantUse403, 'permission-denied', false, '你沒有使用這個助理的權限，或它已不存在。'],
    ['member 403 chat-thread', RECORDED.memberChatThread403, 'permission-denied', false, '你沒有這段對話的存取權限，或它已不存在。'],
    ['member 409 chat-run-in-progress', RECORDED.memberRunInProgress409, 'busy', true, '這段對話還在回覆上一個問題，請等回覆完成後再送出。'],
    ['member 422 too long', RECORDED.memberTooLong422, 'validation-failed', false, '問題請在 2000 個字以內。'],
    ['member 422 blank', RECORDED.memberBlank422, 'validation-failed', false, '請先輸入問題。'],
    ['member 503 chat-not-configured', RECORDED.memberChatNotConfigured503, 'unavailable', true, '沒有設定對話模型，請聯絡系統管理員設定 Ai:Chat:Provider 後再試。'],
    ['member 503 embedding-not-configured', RECORDED.memberEmbeddingNotConfigured503, 'unavailable', true, '系統尚未設定嵌入模型，請聯絡系統管理員設定後重試'],
    ['visitor 403 public-assistant', RECORDED.visitorPublicAssistant403, 'permission-denied', false, '這個對話視窗目前無法使用。'],
    ['visitor 409 chat-run-in-progress', RECORDED.visitorRunInProgress409, 'busy', true, '上一個問題還在回覆中，請等回覆完成後再送出。'],
    ['visitor 422 too long', RECORDED.visitorTooLong422, 'validation-failed', false, '問題請在 2000 個字以內。'],
    ['visitor 503 chat-not-configured', RECORDED.visitorChatNotConfigured503, 'unavailable', true, '沒有設定對話模型，請聯絡系統管理員設定 Ai:Chat:Provider 後再試。'],
  ] as const)(
    'turns the recorded %s (application/problem+json) into an error with the server’s message (#329)',
    async (_name, response, kind, retryable, message) => {
      const { runner, onUnauthorized } = setup(recorded(response));

      const events = await collect(runner);

      expect(events).toEqual([{ type: 'error', error: { kind, message, retryable } }]);
      expect(onUnauthorized).not.toHaveBeenCalled();
    },
  );

  it('reports the recorded visitor 429 as a retryable failure (the widget reads the status and Retry-After itself)', async () => {
    const { runner } = setup(recorded(RECORDED.visitorRateLimited429));

    const events = await collect(runner);

    expect(events).toEqual([
      { type: 'error', error: { kind: 'failed', message: expect.stringContaining('連線中斷'), retryable: true } },
    ]);
  });

  it('still reads a body the HTTP layer already parsed (content-type application/json)', async () => {
    // `@ag-ui/client` 只解析 `application/json`；本文已經是物件時（套件日後若也解析 `+json`）照樣取 message。
    const { runner } = setup(
      () =>
        new Response(JSON.stringify({ reason: 'chat-run-in-progress', message: '這段對話還在回覆上一個問題。' }), {
          status: 409,
          headers: { 'content-type': 'application/json' },
        }),
    );

    const events = await collect(runner);

    expect(events).toEqual([
      { type: 'error', error: { kind: 'busy', message: '這段對話還在回覆上一個問題。', retryable: true } },
    ]);
  });

  it('falls back to the default text when the error body is not JSON (e.g. a proxy’s HTML page)', async () => {
    const { runner } = setup(
      () => new Response('<html><body>503 Service Temporarily Unavailable</body></html>', {
        status: 503,
        headers: { 'content-type': 'text/html' },
      }),
    );

    const events = await collect(runner);

    expect(events).toEqual([
      { type: 'error', error: { kind: 'unavailable', message: '對話服務暫時無法使用，請稍後再試。', retryable: true } },
    ]);
  });

  it('ends the session on the recorded 401 (no body, no content-type)', async () => {
    const { runner, onUnauthorized } = setup(recorded(RECORDED.visitorTampered401));

    const events = await collect(runner);

    expect(events).toEqual([
      { type: 'error', error: { kind: 'unauthorized', message: '登入已逾時，請重新登入。', retryable: false } },
    ]);
    expect(onUnauthorized).toHaveBeenCalledTimes(1);
  });

  it('reports a network failure as retryable', async () => {
    const { runner } = setup(() => {
      throw new TypeError('Failed to fetch');
    });

    const events = await collect(runner);

    expect(events).toEqual([
      { type: 'error', error: { kind: 'failed', message: expect.stringContaining('連線中斷'), retryable: true } },
    ]);
  });

  it('aborts the request when the subscriber stops the run', async () => {
    const encoder = new TextEncoder();
    const firstEvents = `${fixture('company-data-saved.sse').split('\n\n').slice(0, 3).join('\n\n')}\n\n`;
    // 只送出前三個事件，之後保持連線，像模型還在生成。
    const { runner, calls } = setup(
      () =>
        new Response(
          new ReadableStream<Uint8Array>({ start: (controller) => controller.enqueue(encoder.encode(firstEvents)) }),
          { status: 200, headers: { 'content-type': 'text/event-stream' } },
        ),
    );

    const events: ChatRunEvent[] = [];
    let completed = false;
    const subscription = runner.run(REQUEST).subscribe({
      next: (event) => events.push(event),
      complete: () => (completed = true),
    });
    await vi.waitFor(() => expect(events.map((event) => event.type)).toEqual(['text-delta']));

    subscription.unsubscribe();

    expect(calls[0].init.signal?.aborted).toBe(true);
    expect(completed).toBe(false);
  });

  it('does not log an AGUIError after RUN_ERROR when the underlying stream is still open (issue #107)', async () => {
    // 根因：`finishWith` 讓 Observable 完成後，RxJS 會觸發下面的 teardown。若 teardown
    // 不分青紅皂白呼叫 `agent.abortRun()`，會中止一個其實已經自然結束（收到 RUN_ERROR）的
    // fetch stream；`@ag-ui/client` 的 reader 被 abort 後補送一個 AbortError，撞上它自己
    // 「run 已經 errored」的事件驗證器，印出
    // `Cannot send event type 'RUN_ERROR': The run has already errored…` 到 console（上游行為，
    // 見 `AgUiChatRunner.run` 內的註解；未見上游 issue，暫不回報）。用一個「送完事件後才慢慢關閉」
    // 的串流＋真的會在 abort 時噴 AbortError 的 reader，重現真實瀏覽器的時序。
    const consoleErrors: unknown[] = [];
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation((...args) => {
      consoleErrors.push(args);
    });
    const unhandledRejections: unknown[] = [];
    const onUnhandledRejection = ((event: PromiseRejectionEvent) =>
      unhandledRejections.push(event.reason)) as unknown as EventListener;
    window.addEventListener('unhandledrejection', onUnhandledRejection);

    const encoder = new TextEncoder();
    const frames = fixture('fail-midway.sse')
      .split('\n\n')
      .filter((frame) => frame.length > 0);
    const fetch = vi.fn(async (_url: string, init: RequestInit) => {
      let aborted = false;
      return new Response(
        new ReadableStream<Uint8Array>({
          start(controller) {
            init.signal?.addEventListener('abort', () => {
              aborted = true;
              controller.error(new DOMException('The operation was aborted.', 'AbortError'));
            });
            (async () => {
              for (const frame of frames) {
                if (aborted) return;
                controller.enqueue(encoder.encode(`${frame}\n\n`));
                await new Promise((resolve) => setTimeout(resolve, 5));
              }
              // 故意讓連線晚一點才關閉，模擬真實情況下瀏覽器還沒注意到伺服器已經關閉連線。
              await new Promise((resolve) => setTimeout(resolve, 50));
              if (!aborted) controller.close();
            })();
          },
        }),
        { status: 200, headers: { 'content-type': 'text/event-stream' } },
      );
    });
    const runner = new AgUiChatRunner({ accessToken: () => 'token-1', onUnauthorized: vi.fn(), fetch });

    const events: ChatRunEvent[] = [];
    runner.run(REQUEST).subscribe({ next: (event) => events.push(event) });
    await new Promise((resolve) => setTimeout(resolve, 200));

    window.removeEventListener('unhandledrejection', onUnhandledRejection);
    consoleSpy.mockRestore();

    expect(events.map((event) => event.type)).toEqual(['text-delta', 'error']);
    expect(consoleErrors).toEqual([]);
    expect(unhandledRejections).toEqual([]);
  });
});
