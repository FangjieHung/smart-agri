import { inject, InjectionToken } from '@angular/core';
import type { Observable } from 'rxjs';
import type { ChatViewerId } from '../domain/account.model';
import type { ChatMessageView, ChatThreadId } from '../domain/conversation.model';
import { DEMO_REPOSITORY } from '../repositories/tokens';
import { MockChatRunner } from './mock-chat-runner';

/** 不保存對話的助理：本頁已有的前文，放進 AG-UI 的 `RunAgentInput.messages`。 */
export interface ChatHistoryEntry {
  readonly role: 'user' | 'assistant';
  readonly content: string;
}

export interface ChatRunRequest {
  /** 目前的發起者；Mock 用它寫入 repository，API 模式由 bearer token 決定，不使用。 */
  readonly viewerId: ChatViewerId;
  readonly assistantId: string;
  readonly question: string;
  /** 省略時沿用最後活動的對話串，沒有就新開一段（API 送 `threadId: ''`）。 */
  readonly threadId?: ChatThreadId;
  /** 只有不保存對話的助理才帶；保存對話時前文由後端自己讀。 */
  readonly history?: readonly ChatHistoryEntry[];
  /**
   * 這則問題的 client 端訊息 id（issue #105）：送給 API 模式當 `RunAgentInput` 最後一則
   * user 訊息的 `id`。重試同一個問題時沿用原本的 id，讓後端把它視為同一則問題（串流
   * 中途失敗只保存了問題），不重複保存；沒帶時後端一律當成新問題。Mock 模式不需要它
   * （`sendChatMessage` 一次寫入問題與回覆，沒有「只保存問題」的中間狀態）。
   */
  readonly clientMessageId?: string;
}

/**
 * 這次沒有取得回答的原因：
 * - `validation-failed`：問題空白或過長（`422`），畫面把問題放回輸入框；
 * - `permission-denied`：不能使用這個助理或這段對話（`403`）；
 * - `busy`：同一段對話已有回答在產生（`409`），可重試；
 * - `unavailable`：對話或嵌入模型暫時無法使用（`503`、串流中的 `RUN_ERROR`），可重試；
 * - `unauthorized`：登入逾時（`401`），會被導回登入頁；
 * - `failed`：連線中斷或其他未預期的錯誤，可重試。
 */
export type ChatRunErrorKind =
  | 'validation-failed'
  | 'permission-denied'
  | 'busy'
  | 'unavailable'
  | 'unauthorized'
  | 'failed';

export interface ChatRunError {
  readonly kind: ChatRunErrorKind;
  readonly message: string;
  readonly retryable: boolean;
}

/**
 * 一次回答的事件，順序固定為 `text-delta`…→`reply`→（`thread`），或在任何時候以
 * `error` 結束。`reply` 是最終的訊息（id 以它為準），畫面用它**整則取代**串流文字——
 * 引用驗證失敗時它就是 `no-result`（M3 計畫第 3 節「先串流、驗證失敗時整則替換」）。
 */
export type ChatRunEvent =
  | { readonly type: 'text-delta'; readonly delta: string }
  | { readonly type: 'reply'; readonly message: ChatMessageView }
  | { readonly type: 'thread'; readonly threadId: ChatThreadId; readonly title: string }
  | { readonly type: 'error'; readonly error: ChatRunError };

/**
 * 送出一則問題並串流回答。回傳 cold Observable：訂閱才送出，**取消訂閱就是「停止」**
 * （API 模式會 abort 請求；Mock 會移除剛寫入的助理回覆，只留下問題）。
 * 錯誤一律以 `error` 事件回報後正常結束，不走 Observable 的 error 通道。
 */
export interface ChatRunner {
  run(request: ChatRunRequest): Observable<ChatRunEvent>;
}

/** 預設是 Mock（含 GitHub Pages）；API 模式由 `provideApiMode()` 換成 AG-UI 的實作。 */
export const CHAT_RUNNER = new InjectionToken<ChatRunner>('CHAT_RUNNER', {
  providedIn: 'root',
  factory: () => new MockChatRunner(inject(DEMO_REPOSITORY)),
});
