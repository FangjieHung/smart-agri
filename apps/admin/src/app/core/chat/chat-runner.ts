import { inject, InjectionToken } from '@angular/core';
import type {
  ChatHistoryEntry,
  ChatRunError,
  ChatRunErrorKind,
  ChatRunEvent,
  ChatRunRequest as AgUiChatRunRequest,
} from '@smart-agri/chat';
import type { Observable } from 'rxjs';
import type { ChatViewerId } from '../domain/account.model';
import { DEMO_REPOSITORY } from '../repositories/tokens';
import { API_CHAT_RUNNER } from './api-chat-runner';
import { MockChatRunner } from './mock-chat-runner';

// 事件與錯誤的型別住在 `@smart-agri/chat`（官網訪客的對話視窗也用同一組事件）；
// admin 沿用原本的名稱與匯入路徑。
export type { ChatHistoryEntry, ChatRunError, ChatRunErrorKind, ChatRunEvent };

/**
 * admin 的請求比 lib 的多一個 `viewerId`：Mock 用它寫入 repository，
 * API 模式由 bearer token 決定發起者，不使用。
 */
export interface ChatRunRequest extends AgUiChatRunRequest {
  /** 目前的發起者；Mock 用它寫入 repository，API 模式由 bearer token 決定，不使用。 */
  readonly viewerId: ChatViewerId;
}

/**
 * 送出一則問題並串流回答。回傳 cold Observable：訂閱才送出，**取消訂閱就是「停止」**
 * （API 模式會 abort 請求；Mock 會移除剛寫入的助理回覆，只留下問題）。
 * 錯誤一律以 `error` 事件回報後正常結束，不走 Observable 的 error 通道。
 * 事件順序與各欄位的意義見 `@smart-agri/chat` 的 `ChatRunEvent`。
 */
export interface ChatRunner {
  run(request: ChatRunRequest): Observable<ChatRunEvent>;
}

/**
 * 預設是 Mock（含 GitHub Pages）；API 模式由 `provideApiMode()` 經 `API_CHAT_RUNNER` 換成 AG-UI 的實作
 * （不直接覆寫這個 token，見 `api-chat-runner.ts`）。
 */
export const CHAT_RUNNER = new InjectionToken<ChatRunner>('CHAT_RUNNER', {
  providedIn: 'root',
  factory: () => inject(API_CHAT_RUNNER) ?? new MockChatRunner(inject(DEMO_REPOSITORY)),
});
