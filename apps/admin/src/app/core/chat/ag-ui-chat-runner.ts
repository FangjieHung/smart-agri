import type { AgentSubscriber, HttpAgent, Message } from '@ag-ui/client';
import { Observable } from 'rxjs';
import type { components } from '../api/api-schema';
import { toChatMessage } from '../repositories/hybrid-demo-repository';
import type { ChatRunError, ChatRunEvent, ChatRunner, ChatRunRequest } from './chat-runner';

type ApiChatMessageView = components['schemas']['ChatMessageView'];

export const REPLY_EVENT = 'smartagri.reply';
export const THREAD_EVENT = 'smartagri.thread';

export function apiChatRunsPath(assistantId: string): string {
  return `/api/v1/assistants/${encodeURIComponent(assistantId)}/chat/runs`;
}

export interface AgUiChatRunnerDeps {
  /** 目前的 access token；`HttpAgent` 用 `fetch`，不經過 Angular 的 bearer 攔截器。 */
  readonly accessToken: () => string | null;
  /** `401`：與 `unauthorizedInterceptor` 相同，結束工作階段並回登入頁。 */
  readonly onUnauthorized: () => void;
  /** 測試用：換掉 `HttpAgent` 的 fetch。 */
  readonly fetch?: (url: string, init: RequestInit) => Promise<Response>;
  /** 測試用：換掉套件載入方式。正式環境以動態 import 載入，只進入對話頁的 lazy chunk。 */
  readonly loadClient?: () => Promise<{ readonly HttpAgent: typeof HttpAgent }>;
}

/** 串流開始前的錯誤：`@ag-ui/client` 把非 2xx 轉成帶 `status`／`payload` 的 Error。 */
interface HttpRunFailure {
  readonly status?: unknown;
  readonly payload?: unknown;
  readonly name?: unknown;
}

const UNAVAILABLE_MESSAGE = '對話服務暫時無法使用，請稍後再試。';
const FAILED_MESSAGE = '連線中斷，這則問題沒有取得回答，請再試一次。';

/**
 * API 模式的 `ChatRunner`（M3 計畫 Slice 10；事件規格見 PR #97 與 `apps/api/README.md`
 * 的「Conversation runs (AG-UI)」）：
 *
 * - `POST /api/v1/assistants/{id}/chat/runs`，body 是 `HttpAgent` 產生的 `RunAgentInput`；
 * - `threadId` 一定明確設定（沒有時是 `''`）：`HttpAgent` 遇到 undefined 會自己產生 uuid，
 *   後端會回 `403 chat-thread`；
 * - bearer token 手動帶上，因為 `fetch` 不經過攔截器；
 * - `CUSTOM smartagri.reply` 是最終的 `ChatMessageView`，`smartagri.thread` 是對話串；
 * - 串流開始前的錯誤是一般 JSON（403／409／422／503），開始後只有 `RUN_ERROR`。
 */
export class AgUiChatRunner implements ChatRunner {
  constructor(private readonly deps: AgUiChatRunnerDeps) {}

  run(request: ChatRunRequest): Observable<ChatRunEvent> {
    return new Observable<ChatRunEvent>((subscriber) => {
      let closed = false;
      let agent: HttpAgent | null = null;
      let replied = false;

      const finishWith = (error: ChatRunError | null): void => {
        if (closed) return;
        closed = true;
        if (error !== null) subscriber.next({ type: 'error', error });
        subscriber.complete();
      };

      const subscriberCallbacks: AgentSubscriber = {
        onTextMessageContentEvent: ({ event }) => {
          if (!closed && !replied) subscriber.next({ type: 'text-delta', delta: event.delta });
        },
        onCustomEvent: ({ event }) => {
          if (closed) return;
          if (event.name === REPLY_EVENT) {
            const message = readReply(event.value);
            if (message === null) {
              agent?.abortRun();
              finishWith({ kind: 'failed', message: FAILED_MESSAGE, retryable: true });
              return;
            }
            replied = true;
            subscriber.next({ type: 'reply', message });
          } else if (event.name === THREAD_EVENT) {
            const thread = readThread(event.value);
            if (thread !== null) subscriber.next({ type: 'thread', ...thread });
          }
        },
        onRunErrorEvent: ({ event }) => {
          finishWith({
            kind: 'unavailable',
            message: event.message || UNAVAILABLE_MESSAGE,
            retryable: true,
          });
        },
      };

      const load = this.deps.loadClient ?? (() => import('@ag-ui/client'));
      load()
        .then(({ HttpAgent: Agent }) => {
          if (closed) return undefined;
          const token = this.deps.accessToken();
          agent = new Agent({
            url: apiChatRunsPath(request.assistantId),
            // 必須明確設定：省略時 HttpAgent 會自產 uuid，後端回 403 chat-thread。
            threadId: request.threadId ?? '',
            headers: token === null ? {} : { Authorization: `Bearer ${token}` },
            initialMessages: runMessages(request),
            ...(this.deps.fetch === undefined ? {} : { fetch: this.deps.fetch }),
          });
          return agent.runAgent({}, subscriberCallbacks);
        })
        .then(
          () => finishWith(replied ? null : { kind: 'failed', message: FAILED_MESSAGE, retryable: true }),
          (error: unknown) => {
            if (closed) return;
            finishWith(this.toRunError(error));
          },
        );

      // 取消訂閱就是「停止」：abort 進行中的請求。
      return () => {
        closed = true;
        agent?.abortRun();
      };
    });
  }

  private toRunError(error: unknown): ChatRunError {
    const failure = (error ?? {}) as HttpRunFailure;
    const status = typeof failure.status === 'number' ? failure.status : null;
    const message = payloadMessage(failure.payload);
    switch (status) {
      case 401:
        this.deps.onUnauthorized();
        return { kind: 'unauthorized', message: '登入已逾時，請重新登入。', retryable: false };
      case 403:
        return {
          kind: 'permission-denied',
          message: message ?? '你沒有使用這個助理的權限，或這段對話已不存在。',
          retryable: false,
        };
      case 409:
        return {
          kind: 'busy',
          message: message ?? '上一則回答還在產生中，請稍候再試。',
          retryable: true,
        };
      case 422:
        return { kind: 'validation-failed', message: message ?? '請確認問題內容後再送出。', retryable: false };
      case 503:
        return { kind: 'unavailable', message: message ?? UNAVAILABLE_MESSAGE, retryable: true };
      default:
        return { kind: 'failed', message: FAILED_MESSAGE, retryable: true };
    }
  }
}

/**
 * 保存對話時只送這一則問題（前文由後端讀取）；不保存對話時，把本頁的前文放在前面，
 * 後端只把它當成前文、引用永遠來自本次檢索。
 */
function runMessages(request: ChatRunRequest): Message[] {
  const history: Message[] = (request.history ?? []).map((entry, index) => ({
    id: `history-${index + 1}`,
    role: entry.role,
    content: entry.content,
  }));
  return [...history, { id: `question-${history.length + 1}`, role: 'user', content: request.question }];
}

function readReply(value: unknown) {
  const candidate = value as Partial<ApiChatMessageView> | null;
  if (
    candidate === null ||
    typeof candidate !== 'object' ||
    typeof candidate.id !== 'string' ||
    typeof candidate.createdAt !== 'string' ||
    typeof candidate.reply !== 'object' ||
    candidate.reply === null
  ) {
    return null;
  }
  return toChatMessage(candidate as ApiChatMessageView);
}

function readThread(value: unknown): { threadId: string; title: string } | null {
  const candidate = value as { threadId?: unknown; title?: unknown } | null;
  if (candidate === null || typeof candidate !== 'object' || typeof candidate.threadId !== 'string') return null;
  return { threadId: candidate.threadId, title: typeof candidate.title === 'string' ? candidate.title : '' };
}

/** 後端的錯誤本文：`{ reason, message }`，422 也可能是 `{ errors: { 欄位: [訊息] } }`。 */
function payloadMessage(payload: unknown): string | null {
  if (payload === null || typeof payload !== 'object') return null;
  const body = payload as { message?: unknown; errors?: Record<string, unknown> };
  if (typeof body.message === 'string' && body.message.length > 0) return body.message;
  const first = Object.values(body.errors ?? {})[0];
  return Array.isArray(first) && typeof first[0] === 'string' ? first[0] : null;
}
