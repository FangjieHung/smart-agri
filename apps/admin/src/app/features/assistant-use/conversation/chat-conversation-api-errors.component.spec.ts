import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AgUiChatRunner } from '@smart-agri/chat';
import { CHAT_RUNNER } from '../../../core/chat/chat-runner';
import { provideAssistantUseTesting } from '../assistant-use.testing';
import { ChatConversationComponent } from './chat-conversation.component';

const ASSISTANT = 'assistant-customer-service';
const QUESTION = '收到商品後幾天內可以退貨？';

interface RecordedResponse {
  readonly status: number;
  readonly contentType: string;
  readonly body: string;
}

/**
 * 成員串流端點 `POST /api/v1/assistants/{id}/chat/runs` 的錯誤回應（issue #329），2026-10-08 由真實 API 錄下：
 * `dotnet run`（Development、Fake 模型、暫存資料庫），以 `@ag-ui/client` 的 `HttpAgent` 送出，記下狀態、
 * `content-type` 與本文原文，未經修改（與 `libs/chat` 的 `ag-ui-chat-runner.spec.ts` 同一次錄製）。403 是不存在的
 * 助理；409 是同一段對話同時送出多個問題；422 是超過 2000 字的問題。內容類型是 `application/problem+json`，
 * `@ag-ui/client` 不解析它——修正前畫面只會顯示預設文字。
 */
const RECORDED = {
  assistantUse403: {
    status: 403,
    contentType: 'application/problem+json',
    body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"assistant-use","message":"你沒有使用這個助理的權限，或它已不存在。"}',
  },
  runInProgress409: {
    status: 409,
    contentType: 'application/problem+json',
    body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Conflict","status":409,"reason":"chat-run-in-progress","message":"這段對話還在回覆上一個問題，請等回覆完成後再送出。"}',
  },
  tooLong422: {
    status: 422,
    contentType: 'application/problem+json',
    body: '{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"message":"問題請在 2000 個字以內。","errors":{"question":["問題請在 2000 個字以內。"]}}',
  },
} as const satisfies Record<string, RecordedResponse>;

/** API 模式的 runner（與 `provideApiMode` 相同的 deps），只把 fetch 換成回放錄下的回應。 */
function setup(response: RecordedResponse) {
  const testing = provideAssistantUseTesting('account-external-customer');
  const runner = new AgUiChatRunner({
    accessToken: () => 'token-1',
    onUnauthorized: () => undefined,
    fetch: async () =>
      new Response(response.body, { status: response.status, headers: { 'content-type': response.contentType } }),
  });
  TestBed.configureTestingModule({
    imports: [ChatConversationComponent],
    providers: [provideRouter([]), ...testing.providers, { provide: CHAT_RUNNER, useValue: runner }],
  });
  const fixture: ComponentFixture<ChatConversationComponent> = TestBed.createComponent(ChatConversationComponent);
  fixture.componentRef.setInput('assistantId', ASSISTANT);
  fixture.detectChanges();
  document.body.appendChild(fixture.nativeElement);
  const host = fixture.nativeElement as HTMLElement;

  const ask = (text = QUESTION) => {
    const input = host.querySelector<HTMLInputElement>('#chat-input');
    if (input === null) throw new Error('missing composer');
    input.value = text;
    input.dispatchEvent(new Event('input'));
    host.querySelector<HTMLButtonElement>('form.composer button[type="submit"]')?.click();
    fixture.detectChanges();
  };
  const until = (read: () => string | null | undefined, expected: string) =>
    vi.waitFor(() => {
      fixture.detectChanges();
      expect(read()).toContain(expected);
    });

  return { host, ask, until };
}

describe('ChatConversationComponent with the API runner and recorded problem+json errors (#329)', () => {
  afterEach(() => document.body.querySelectorAll('app-chat-conversation').forEach((node) => node.remove()));

  it.each([
    ['403 assistant-use', RECORDED.assistantUse403, '你沒有使用這個助理的權限，或它已不存在。'],
    ['409 chat-run-in-progress', RECORDED.runInProgress409, '這段對話還在回覆上一個問題，請等回覆完成後再送出。'],
  ])('%s: the alert shows the server’s message', async (_name, response, message) => {
    const { host, ask, until } = setup(response);

    ask();

    await until(() => host.querySelector('.run-error[role="alert"]')?.textContent, message);
  });

  it('422: the composer error shows the server’s message and the question goes back in the input', async () => {
    const { host, ask, until } = setup(RECORDED.tooLong422);

    ask('太長的問題');

    await until(() => host.querySelector('#chat-input-error')?.textContent, '問題請在 2000 個字以內。');
    expect(host.querySelector<HTMLInputElement>('#chat-input')?.value).toBe('太長的問題');
  });
});
