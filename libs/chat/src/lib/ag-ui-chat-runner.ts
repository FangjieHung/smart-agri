import type { AgentSubscriber, HttpAgent, Message } from '@ag-ui/client';
import { Observable } from 'rxjs';
import {
  toChatMessage,
  type ChatMessageWire,
  type ChatReplyExtensionMapper,
  type ChatReplyWire,
} from './chat-message-mapper';
import type { ChatRunError, ChatRunEvent, ChatRunner, ChatRunRequest } from './chat-runner.types';
import type { ChatMessageView } from './chat-view.model';

export const REPLY_EVENT = 'smartagri.reply';
export const THREAD_EVENT = 'smartagri.thread';
/** 伺服器開始判斷要不要跳出表單（issue #171，`ChatRunEndpoints.FormCheckEventName`）。 */
export const FORM_CHECK_EVENT = 'smartagri.form-check';

export function apiChatRunsPath(assistantId: string): string {
  return `/api/v1/assistants/${encodeURIComponent(assistantId)}/chat/runs`;
}

export interface AgUiChatRunnerDeps {
  /** 目前的 access token；`HttpAgent` 用 `fetch`，不經過 Angular 的 bearer 攔截器。 */
  readonly accessToken?: () => string | null;
  /**
   * 完整的 `Authorization` 標頭值（例如訪客視窗的 `Visitor <token>`）；有給時優先於 `accessToken`
   * （`accessToken` 只會被包成 `Bearer <token>`）。回傳 `null` 代表不帶標頭。
   */
  readonly authorizationHeader?: () => string | null;
  /** `401`：與 `unauthorizedInterceptor` 相同，結束工作階段並回登入頁。 */
  readonly onUnauthorized: () => void;
  /** 測試用：換掉 `HttpAgent` 的 fetch。 */
  readonly fetch?: (url: string, init: RequestInit) => Promise<Response>;
  /** 測試用：換掉套件載入方式。正式環境以動態 import 載入，只進入對話頁的 lazy chunk。 */
  readonly loadClient?: () => Promise<{ readonly HttpAgent: typeof HttpAgent }>;
  /** 串流端點；省略時是 admin 的 `POST /api/v1/assistants/{id}/chat/runs`（`apiChatRunsPath`）。 */
  readonly runsPath?: (assistantId: string) => string;
  /** 只有使用端才認得的回覆種類（見 `ChatReplyExtensionMapper`）；省略時只轉換通用的三種。 */
  readonly replyExtension?: ChatReplyExtensionMapper;
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
 * - `CUSTOM smartagri.reply` 是最終的 `ChatMessageView`，`smartagri.thread` 是對話串，
 *   `smartagri.form-check`（#171）是「正在判斷表單」；
 * - 串流開始前的錯誤是一般 JSON（403／409／422／503），開始後只有 `RUN_ERROR`。
 */
export class AgUiChatRunner implements ChatRunner {
  constructor(private readonly deps: AgUiChatRunnerDeps) {}

  run(request: ChatRunRequest): Observable<ChatRunEvent> {
    return new Observable<ChatRunEvent>((subscriber) => {
      let closed = false;
      // 真的透過 `finishWith` 走到終點（收到 `RUN_ERROR`、拿到回覆、或串流開始前出錯），
      // 而不是被下面的取消訂閱（使用者按「停止回答」）打斷。
      let finishedNaturally = false;
      let agent: HttpAgent | null = null;
      let replied = false;

      const finishWith = (error: ChatRunError | null): void => {
        if (closed) return;
        closed = true;
        finishedNaturally = true;
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
            const message = readReply(event.value, this.deps.replyExtension);
            if (message === null) {
              agent?.abortRun();
              finishWith({ kind: 'failed', message: FAILED_MESSAGE, retryable: true });
              return;
            }
            replied = true;
            subscriber.next({ type: 'reply', message });
          } else if (event.name === FORM_CHECK_EVENT) {
            if (!replied) subscriber.next({ type: 'form-check' });
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
          const authorization = this.authorization();
          agent = new Agent({
            url: (this.deps.runsPath ?? apiChatRunsPath)(request.assistantId),
            // 必須明確設定：省略時 HttpAgent 會自產 uuid，後端回 403 chat-thread。
            threadId: request.threadId ?? '',
            headers: authorization === null ? {} : { Authorization: authorization },
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
      //
      // 但 `finishWith` 已經讓這個 run 走到終點（`RUN_ERROR` 或完成）時，`subscriber.complete()`
      // 也會觸發這個 teardown——這時若還呼叫 `abortRun()`，`@ag-ui/client` 的 SSE reader 會被中止並
      // 補送一個 `AbortError`，其內部的事件序列驗證器發現「run 已經 errored/finished」，就會印出
      // `Cannot send event type 'RUN_ERROR': The run has already errored…` 到 console（見 issue #107；
      // 上游行為，未回報）。只有在真正被使用者取消（run 還沒 finishWith）時才需要中止底層請求。
      return () => {
        closed = true;
        if (!finishedNaturally) agent?.abortRun();
      };
    });
  }

  private authorization(): string | null {
    if (this.deps.authorizationHeader !== undefined) return this.deps.authorizationHeader();
    const token = this.deps.accessToken?.() ?? null;
    return token === null ? null : `Bearer ${token}`;
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
 *
 * 最後一則 user 訊息（問題）的 id 用 `request.clientMessageId`（issue #105）：重試時沿用
 * 失敗那次的 id，後端才能把它識別成同一則問題、不重複保存。沒帶時退回舊的位置編號
 * （理論上 `ChatConversationComponent` 一定會帶；只是不讓這裡在缺漏時整個壞掉）。
 */
function runMessages(request: ChatRunRequest): Message[] {
  const history: Message[] = (request.history ?? []).map((entry, index) => ({
    id: `history-${index + 1}`,
    role: entry.role,
    content: entry.content,
  }));
  const questionId = request.clientMessageId ?? `question-${history.length + 1}`;
  return [...history, { id: questionId, role: 'user', content: request.question }];
}

function readReply(value: unknown, extension: ChatReplyExtensionMapper | undefined): ChatMessageView | null {
  const candidate = value as Partial<ChatMessageWire> | null;
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
  return toChatMessage(candidate as ChatMessageWire<ChatReplyWire>, extension);
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
