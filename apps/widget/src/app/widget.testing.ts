/**
 * 測試用：假的訪客 API（記錄每個請求、依序回應）與 App 的組裝。
 * 不進 production build（`tsconfig.app.json` 排除 `*.testing.ts`）。
 */
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import type { ChatMessageWire } from '@smart-agri/chat';
import { App } from './app';
import { WIDGET_CONTEXT, WIDGET_FETCH, WIDGET_PARENT, WIDGET_STORAGE, type WidgetContext } from './widget-context';
import type { VisitorSession } from './widget.model';

export const ASSISTANT_ID = '11111111-1111-4111-8111-111111111111';
export const HOST = 'https://shop.example.com';
export const QUESTION = '收到商品後幾天內可以退貨？';

export interface Recorded {
  readonly url: string;
  readonly method: string;
  readonly headers: Record<string, string>;
  readonly body: { host?: string | null; threadId?: string; messages?: { id: string; role: string; content: string }[] };
}

export type Responder = (request: Recorded) => Response | Promise<Response>;

/** 回應依序用掉；用完後重複最後一個。 */
export class FakeServer {
  readonly requests: Recorded[] = [];
  readonly sessions: Responder[] = [];
  readonly runs: Responder[] = [];
  private sessionCount = 0;
  private runCount = 0;

  get sessionRequests(): Recorded[] {
    return this.requests.filter((request) => request.url.endsWith('/visitor-sessions'));
  }

  get runRequests(): Recorded[] {
    return this.requests.filter((request) => request.url.endsWith('/chat/runs'));
  }

  readonly fetch = async (url: string, init: RequestInit): Promise<Response> => {
    const request: Recorded = {
      url,
      method: init.method ?? 'GET',
      headers: { ...(init.headers as Record<string, string>) },
      body: typeof init.body === 'string' ? JSON.parse(init.body) : {},
    };
    this.requests.push(request);
    if (url.endsWith('/visitor-sessions')) return next(this.sessions, this.sessionCount++)(request);
    if (url.endsWith('/chat/runs')) return next(this.runs, this.runCount++)(request);
    throw new Error(`unexpected request ${url}`);
  };
}

function next(responders: Responder[], index: number): Responder {
  const responder = responders[Math.min(index, responders.length - 1)];
  if (responder === undefined) throw new Error('no scripted response');
  return responder;
}

export const sessionBody = (overrides: Partial<VisitorSession> = {}, assistant: Partial<VisitorSession['assistant']> = {}) => ({
  token: 'token-1',
  expiresAt: new Date(Date.now() + 12 * 3600_000).toISOString(),
  assistant: {
    displayName: '森林商店客服',
    welcomeMessage: '你好，我可以回答商品與退換貨的問題。',
    brandColor: 'forest',
    showCitations: true,
    ...assistant,
  },
  ...overrides,
});

export const json = (status: number, body: unknown, headers: Record<string, string> = {}): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json', ...headers } });

/** 一個錄下的回應：狀態、`content-type`、`Retry-After` 與本文原文。 */
export interface RecordedResponse {
  readonly status: number;
  readonly contentType: string | null;
  readonly retryAfter?: string;
  readonly body: string;
}

/**
 * 訪客 API 的錯誤回應（issue #329），2026-10-08 由真實 API 錄下：`dotnet run`（Development、Fake 模型、
 * 暫存資料庫、已發布的網站頻道），以 `fetch` 建立工作階段、以 `@ag-ui/client` 的 `HttpAgent` 送出問題，
 * 記下狀態、`content-type`、`Retry-After` 與本文原文，未經修改。`403` 是暫停網站頻道後送出；`409` 是同一個
 * 工作階段同時送出多個問題；`429` 是一分鐘內第 7 個問題（工作階段則是第 11 個）；`401` 是竄改過的 token；
 * `503` 是分別不設定 `Ai:Chat:Provider`、`Ai:Embedding:Provider` 啟動。錯誤一律是
 * `application/problem+json`（RFC 9457），`@ag-ui/client` 不解析它。
 */
export const RECORDED = {
  sessionPublicAssistant403: { status: 403, contentType: 'application/problem+json', body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"public-assistant","message":"這個對話視窗目前無法使用。"}' },
  sessionRateLimited429: { status: 429, contentType: 'application/problem+json', retryAfter: '60', body: '{"type":"https://tools.ietf.org/html/rfc6585#section-4","title":"Too Many Requests","status":429,"reason":"rate-limited","message":"問題太頻繁了，請稍後再試。"}' },
  runUnauthorized401: { status: 401, contentType: null, body: '' },
  runPublicAssistant403: { status: 403, contentType: 'application/problem+json', body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"public-assistant","message":"這個對話視窗目前無法使用。"}' },
  runInProgress409: { status: 409, contentType: 'application/problem+json', body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Conflict","status":409,"reason":"chat-run-in-progress","message":"上一個問題還在回覆中，請等回覆完成後再送出。"}' },
  runTooLong422: { status: 422, contentType: 'application/problem+json', body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"message":"問題請在 2000 個字以內。","errors":{"question":["問題請在 2000 個字以內。"]}}' },
  runRateLimited429: { status: 429, contentType: 'application/problem+json', retryAfter: '60', body: '{"type":"https://tools.ietf.org/html/rfc6585#section-4","title":"Too Many Requests","status":429,"reason":"rate-limited","message":"問題太頻繁了，請稍後再試。"}' },
  runChatNotConfigured503: { status: 503, contentType: 'application/problem+json', body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.6.4","title":"Service Unavailable","status":503,"reason":"chat-not-configured","message":"沒有設定對話模型，請聯絡系統管理員設定 Ai:Chat:Provider 後再試。"}' },
  runEmbeddingNotConfigured503: { status: 503, contentType: 'application/problem+json', body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.6.4","title":"Service Unavailable","status":503,"reason":"embedding-not-configured","message":"系統尚未設定嵌入模型，請聯絡系統管理員設定後重試"}' },
} as const satisfies Record<string, RecordedResponse>;

/**
 * 回放錄下的回應。`retryAfter` 只換掉 `Retry-After` 標頭（倒數的測試需要較短的秒數），本文不變。
 */
export const problem =
  (response: RecordedResponse, overrides: { readonly retryAfter?: string } = {}): Responder =>
  () => {
    const retryAfter = overrides.retryAfter ?? response.retryAfter;
    return new Response(response.body, {
      status: response.status,
      headers: {
        ...(response.contentType === null ? {} : { 'content-type': response.contentType }),
        ...(retryAfter === undefined ? {} : { 'Retry-After': retryAfter }),
      },
    });
  };

export const session = (overrides: Partial<VisitorSession> = {}, assistant: Partial<VisitorSession['assistant']> = {}): Responder =>
  () => json(201, sessionBody(overrides, assistant));

export const companyReply: ChatMessageWire = {
  id: 'reply-1',
  text: null,
  createdAt: '2026-10-06T02:00:00Z',
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
    notice: null,
    nextSteps: [],
  },
};

export const noResultReply: ChatMessageWire = {
  id: 'reply-2',
  text: null,
  createdAt: '2026-10-06T02:00:00Z',
  reply: {
    kind: 'no-result',
    text: '很抱歉，我找不到這個問題的答案，請來電 02-1234-5678。',
    citations: [],
    notice: null,
    nextSteps: ['換個說法再問一次。'],
  },
};

/** 訪客端點的 SSE：只有 `smartagri.reply` 一個自訂事件。 */
export function replyStream(reply: ChatMessageWire): Response {
  const events = [
    { type: 'RUN_STARTED', threadId: '', runId: 'run-1' },
    { type: 'TEXT_MESSAGE_START', messageId: 'm-1', role: 'assistant' },
    { type: 'TEXT_MESSAGE_CONTENT', messageId: 'm-1', delta: reply.reply?.text ?? '' },
    { type: 'TEXT_MESSAGE_END', messageId: 'm-1' },
    { type: 'CUSTOM', name: 'smartagri.reply', value: reply },
    { type: 'RUN_FINISHED', threadId: '', runId: 'run-1' },
  ];
  return sse(events);
}

export function sse(events: readonly unknown[]): Response {
  return new Response(events.map((event) => `data: ${JSON.stringify(event)}\n\n`).join(''), {
    status: 200,
    headers: { 'content-type': 'text/event-stream' },
  });
}

export const answer = (reply: ChatMessageWire): Responder => () => replyStream(reply);

export interface SetupOptions {
  readonly context?: Partial<WidgetContext>;
  readonly parent?: { postMessage: (message: unknown, targetOrigin: string) => void } | null;
  readonly storage?: Storage | null;
}

export interface Harness {
  readonly fixture: ComponentFixture<App>;
  readonly host: HTMLElement;
  /** 反覆檢查（並觸發變更偵測）直到條件成立；用真實計時器輪詢，所以假計時器開著也能用。 */
  readonly until: (condition: () => boolean, message?: string) => Promise<void>;
  readonly text: () => string;
  readonly type: (value: string) => void;
  readonly send: () => void;
}

const realSetTimeout = globalThis.setTimeout.bind(globalThis);

export async function mount(server: FakeServer, options: SetupOptions = {}): Promise<Harness> {
  // 同一個測試裡可以再開一個新的視窗（模擬重新整理）：先銷毀舊的。
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: WIDGET_FETCH, useValue: server.fetch },
      {
        provide: WIDGET_CONTEXT,
        useValue: { assistantId: ASSISTANT_ID, basePath: '', hostOrigin: HOST, ...options.context } satisfies WidgetContext,
      },
      { provide: WIDGET_STORAGE, useValue: options.storage === undefined ? sessionStorage : options.storage },
      { provide: WIDGET_PARENT, useValue: options.parent ?? null },
    ],
  });
  const fixture = TestBed.createComponent(App);
  const host = fixture.nativeElement as HTMLElement;
  document.body.appendChild(host);
  fixture.detectChanges();

  const until = async (condition: () => boolean, message = 'condition not met'): Promise<void> => {
    for (let attempt = 0; attempt < 400; attempt += 1) {
      fixture.detectChanges();
      if (condition()) return;
      await new Promise<void>((resolve) => realSetTimeout(resolve, 5));
    }
    throw new Error(`${message}\n${host.textContent}`);
  };
  const text = () => (host.textContent ?? '').replace(/\s+/g, ' ');
  const type = (value: string) => {
    const field = host.querySelector<HTMLTextAreaElement>('textarea');
    if (field === null) throw new Error('no composer');
    field.value = value;
    field.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };
  const send = () => {
    host.querySelector<HTMLButtonElement>('button.send')?.click();
    fixture.detectChanges();
  };
  return { fixture, host, until, text, type, send };
}

/** 開啟並等到對話畫面（輸入框）出現。 */
export async function mountReady(server: FakeServer, options: SetupOptions = {}): Promise<Harness> {
  const harness = await mount(server, options);
  await harness.until(() => harness.host.querySelector('textarea') !== null, 'composer never appeared');
  return harness;
}

/** 取得一定要存在的元素；沒有就讓測試失敗並說明是哪個選擇器。 */
export function required<T extends Element>(root: ParentNode, selector: string): T {
  const element = root.querySelector<T>(selector);
  if (element === null) throw new Error(`missing element: ${selector}`);
  return element;
}
