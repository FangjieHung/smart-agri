import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import {
  AgUiChatRunner,
  type ChatHistoryEntry,
  type ChatMessageView,
  type ChatRunError,
  type ChatRunEvent,
} from '@smart-agri/chat';
import type { Subscription } from 'rxjs';
import { readSession, VisitorApi } from './visitor-api';
import { WIDGET_CONTEXT, WIDGET_STORAGE } from './widget-context';
import {
  DEFAULT_TITLE,
  HISTORY_MAX_MESSAGES,
  QUESTION_MAX_LENGTH,
  type RunFailure,
  type RunState,
  type SessionResult,
  type VisitorAssistantInfo,
  type VisitorSession,
  type WidgetPhase,
} from './widget.model';

/** token 剩不到這麼久就視為過期，直接建立新的（不等到問題被 `401` 退回）。 */
const SESSION_EXPIRY_MARGIN_MS = 60_000;

const IDLE: RunState = { phase: 'idle' };

/**
 * 訪客視窗的狀態與流程（計畫第 3 節 D）：
 * - 工作階段（token）與前文都只放本分頁的 `sessionStorage`，不用 cookie、`localStorage`；
 * - 一次只處理一個問題；串流開始前的 `401`（token 過期）會重新建立一次工作階段並重送同一個問題；
 * - `429` 以 `Retry-After` 倒數，倒數結束前不能再送；
 * - 前文放在 `RunAgentInput.messages`，最多 20 則（與伺服器相同），沒有對話串。
 */
@Injectable({ providedIn: 'root' })
export class WidgetStore {
  private readonly api = inject(VisitorApi);
  private readonly context = inject(WIDGET_CONTEXT);
  private readonly storage = inject(WIDGET_STORAGE);

  readonly phase = signal<WidgetPhase>('initialising');
  readonly assistant = signal<VisitorAssistantInfo | null>(null);
  /** 已經有答案的問答（最多 20 則）；進行中與失敗的問題在 `run`。 */
  readonly messages = signal<readonly ChatMessageView[]>([]);
  readonly run = signal<RunState>(IDLE);
  readonly draft = signal('');
  /** `429` 的剩餘秒數；null 表示沒有在倒數。 */
  readonly countdown = signal<number | null>(null);

  readonly displayName = computed(() => this.assistant()?.displayName ?? DEFAULT_TITLE);
  /** 進行中或失敗的問題，畫面顯示在已有的問答後面。 */
  readonly pendingQuestion = computed<ChatMessageView | null>(
    () => {
      const run = this.run();
      return run.phase === 'idle'
        ? null
        : { id: run.clientMessageId, author: 'account', text: run.question, createdAt: '' };
    },
    { equal: (a, b) => a?.id === b?.id },
  );
  readonly canAsk = computed(
    () => this.phase() === 'ready' && this.run().phase !== 'streaming' && this.countdown() === null,
  );

  private session: VisitorSession | null = null;
  private subscription: Subscription | null = null;
  private timer: ReturnType<typeof setInterval> | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      this.stopCountdown();
    });
  }

  /** 開啟視窗：還原本分頁的前文與工作階段；沒有（或過期）就建立新的。 */
  async init(): Promise<void> {
    if (this.context.assistantId === null) {
      this.phase.set('unavailable');
      return;
    }
    this.messages.set(this.readHistory());
    const stored = this.readStoredSession();
    if (stored !== null) {
      this.useSession(stored);
      this.phase.set('ready');
      return;
    }
    await this.openSession();
  }

  /** 「無法連線」或「請求太頻繁」畫面的重試。 */
  async retrySession(): Promise<void> {
    if (this.countdown() !== null) return;
    await this.openSession();
  }

  ask(question: string, retryMessageId?: string): void {
    const text = question.trim();
    if (text === '' || text.length > QUESTION_MAX_LENGTH || !this.canAsk()) return;
    const clientMessageId = retryMessageId ?? newId();
    if (retryMessageId === undefined) this.draft.set('');
    if (this.session === null) {
      // 前一次 401 之後沒有可用的 token：先建立工作階段再送。
      this.run.set({ phase: 'streaming', question: text, text: '', clientMessageId });
      void this.recreateAndRetry(text, clientMessageId);
      return;
    }
    this.start(text, clientMessageId, true);
  }

  retry(): void {
    const run = this.run();
    if (run.phase === 'failed') this.ask(run.question, run.clientMessageId);
  }

  /**
   * 「目前暫停服務」之後再試：回到可以輸入的狀態（沒送出的問題仍在輸入框），下一個問題由伺服器重新判斷。
   * 擁有者恢復頻道後，已經開著視窗的訪客不必重新整理整個頁面（#205 實機驗收發現）。
   */
  resumeAfterPause(): void {
    if (this.phase() === 'paused') this.phase.set('ready');
  }

  private async openSession(): Promise<void> {
    this.phase.set('initialising');
    const result = await this.createSession();
    switch (result.status) {
      case 'created':
        this.phase.set('ready');
        break;
      case 'unavailable':
        this.phase.set('unavailable');
        break;
      case 'rate-limited':
        this.phase.set('session-rate-limited');
        this.startCountdown(result.retryAfterSeconds);
        break;
      case 'failed':
        this.phase.set('session-failed');
        break;
    }
  }

  private async createSession(): Promise<SessionResult> {
    const result = await this.api.createSession();
    if (result.status === 'created') {
      this.useSession(result.session);
      this.write(this.sessionKey(), JSON.stringify(result.session));
    }
    return result;
  }

  private useSession(session: VisitorSession): void {
    this.session = session;
    this.assistant.set(session.assistant);
  }

  private start(question: string, clientMessageId: string, mayRecreate: boolean): void {
    const assistantId = this.context.assistantId;
    if (assistantId === null) return;
    this.run.set({ phase: 'streaming', question, text: '', clientMessageId });
    const runner = new AgUiChatRunner({
      authorizationHeader: () => (this.session === null ? null : `Visitor ${this.session.token}`),
      // 沒有登入工作階段可以結束：`401` 由 `fail` 處理（重建一次工作階段）。
      onUnauthorized: () => undefined,
      runsPath: this.api.runsPath,
      fetch: this.api.runFetch,
    });
    this.subscription?.unsubscribe();
    this.subscription = runner
      .run({ assistantId, question, history: this.historyEntries(), clientMessageId })
      .subscribe((event) => this.onEvent(event, question, clientMessageId, mayRecreate));
  }

  private onEvent(event: ChatRunEvent, question: string, clientMessageId: string, mayRecreate: boolean): void {
    switch (event.type) {
      case 'text-delta': {
        const run = this.run();
        if (run.phase === 'streaming' && run.clientMessageId === clientMessageId) {
          this.run.set({ ...run, text: run.text + event.delta });
        }
        break;
      }
      case 'reply':
        this.complete(question, clientMessageId, event.message);
        break;
      case 'error':
        void this.fail(event.error, question, clientMessageId, mayRecreate);
        break;
      default:
        // 訪客 API 不送 form-check／thread；收到也忽略。
        break;
    }
  }

  private complete(question: string, clientMessageId: string, reply: ChatMessageView): void {
    const asked: ChatMessageView = {
      id: clientMessageId,
      author: 'account',
      text: question,
      createdAt: new Date().toISOString(),
    };
    this.messages.update((current) => capHistory([...current, asked, this.visibleReply(reply)]));
    this.write(this.historyKey(), JSON.stringify(this.messages()));
    this.run.set(IDLE);
  }

  /** 助理關閉「顯示引用出處」時，即使伺服器多送了引用也不顯示。 */
  private visibleReply(message: ChatMessageView): ChatMessageView {
    if (this.assistant()?.showCitations !== false || message.author !== 'assistant') return message;
    if (message.reply.kind !== 'company-data') return message;
    return { ...message, reply: { ...message.reply, citations: [], citationNotice: null } };
  }

  private async fail(error: ChatRunError, question: string, clientMessageId: string, mayRecreate: boolean): Promise<void> {
    const response = this.api.lastRunResponse();
    if (response.status === 429) {
      this.fail429(question, clientMessageId, response.retryAfterSeconds);
      return;
    }
    switch (error.kind) {
      case 'unauthorized':
        // token 過期（或被竄改）：重新建立一次工作階段，再重送同一個問題。第二次還是 401 就放棄。
        this.clearStoredSession();
        if (!mayRecreate) {
          this.setFailed(question, clientMessageId, { kind: 'session-expired' });
          return;
        }
        await this.recreateAndRetry(question, clientMessageId);
        return;
      case 'permission-denied':
        this.pause(question);
        return;
      case 'busy':
        this.setFailed(question, clientMessageId, { kind: 'busy', message: error.message });
        return;
      case 'validation-failed':
        // 問題放回輸入框讓使用者修改；伺服器的說明（例如「問題請在 2000 個字以內」）直接顯示。
        if (this.draft() === '') this.draft.set(question);
        this.setFailed(question, clientMessageId, { kind: 'invalid', message: error.message });
        return;
      case 'unavailable':
        this.setFailed(question, clientMessageId, { kind: 'unavailable' });
        return;
      default:
        this.setFailed(question, clientMessageId, { kind: 'network' });
    }
  }

  private async recreateAndRetry(question: string, clientMessageId: string): Promise<void> {
    const result = await this.createSession();
    switch (result.status) {
      case 'created':
        this.start(question, clientMessageId, false);
        break;
      case 'unavailable':
        this.pause(question);
        break;
      case 'rate-limited':
        this.fail429(question, clientMessageId, result.retryAfterSeconds);
        break;
      case 'failed':
        this.setFailed(question, clientMessageId, { kind: 'network' });
        break;
    }
  }

  private fail429(question: string, clientMessageId: string, retryAfterSeconds: number): void {
    this.startCountdown(retryAfterSeconds);
    this.setFailed(question, clientMessageId, { kind: 'rate-limited', retryAfterSeconds });
  }

  /** `403 public-assistant`：目前暫停服務。已有的對話留著，沒送出的問題放回輸入框。 */
  private pause(question: string): void {
    if (this.draft() === '') this.draft.set(question);
    this.run.set(IDLE);
    this.phase.set('paused');
  }

  private setFailed(question: string, clientMessageId: string, failure: RunFailure): void {
    this.run.set({ phase: 'failed', question, clientMessageId, failure });
  }

  private startCountdown(seconds: number): void {
    this.stopCountdown();
    const until = Date.now() + seconds * 1000;
    this.countdown.set(seconds);
    this.timer = setInterval(() => {
      const left = Math.ceil((until - Date.now()) / 1000);
      if (left <= 0) {
        this.stopCountdown();
        this.countdown.set(null);
      } else {
        this.countdown.set(left);
      }
    }, 1000);
  }

  private stopCountdown(): void {
    if (this.timer !== null) clearInterval(this.timer);
    this.timer = null;
  }

  private historyEntries(): ChatHistoryEntry[] {
    return this.messages()
      .map((message): ChatHistoryEntry => (
        message.author === 'account'
          ? { role: 'user', content: message.text }
          : { role: 'assistant', content: message.reply.text }
      ))
      .filter((entry) => entry.content.trim() !== '')
      .slice(-HISTORY_MAX_MESSAGES);
  }

  /* sessionStorage：所有存取都容忍被瀏覽器擋掉或內容損毀。 */

  private sessionKey(): string {
    return `smartagri-widget:${this.context.assistantId}:session`;
  }

  private historyKey(): string {
    return `smartagri-widget:${this.context.assistantId}:history`;
  }

  private read(key: string): unknown {
    try {
      const raw = this.storage?.getItem(key);
      return raw == null ? null : JSON.parse(raw);
    } catch {
      return null;
    }
  }

  private write(key: string, value: string): void {
    try {
      this.storage?.setItem(key, value);
    } catch {
      // 容量滿了或被擋：對話照常，只是重新整理後不保留。
    }
  }

  private clearStoredSession(): void {
    this.session = null;
    try {
      this.storage?.removeItem(this.sessionKey());
    } catch {
      // 同上。
    }
  }

  private readStoredSession(): VisitorSession | null {
    const session = readSession(this.read(this.sessionKey()));
    if (session === null) return null;
    const expiresAt = Date.parse(session.expiresAt);
    return Number.isNaN(expiresAt) || expiresAt - Date.now() < SESSION_EXPIRY_MARGIN_MS ? null : session;
  }

  private readHistory(): ChatMessageView[] {
    const stored = this.read(this.historyKey());
    return Array.isArray(stored) ? capHistory(stored.filter(isStoredMessage)) : [];
  }
}

/** 最多 20 則，且不以孤立的助理回覆開頭。 */
function capHistory(messages: readonly ChatMessageView[]): ChatMessageView[] {
  const capped = messages.slice(-HISTORY_MAX_MESSAGES);
  return capped[0]?.author === 'assistant' ? capped.slice(1) : capped;
}

function isStoredMessage(value: unknown): value is ChatMessageView {
  if (value === null || typeof value !== 'object') return false;
  const message = value as { id?: unknown; author?: unknown; text?: unknown; reply?: unknown };
  if (typeof message.id !== 'string') return false;
  if (message.author === 'account') return typeof message.text === 'string';
  if (message.author !== 'assistant' || message.reply === null || typeof message.reply !== 'object') return false;
  // 訪客 API 只會回這三種；其他種類（或形狀不對）的內容一律丟掉，不交給模板。
  const reply = message.reply as { kind?: unknown; text?: unknown; citations?: unknown; nextSteps?: unknown; notice?: unknown };
  if (typeof reply.text !== 'string') return false;
  switch (reply.kind) {
    case 'company-data':
      return Array.isArray(reply.citations);
    case 'general-knowledge':
      return typeof reply.notice === 'string';
    case 'no-result':
      return Array.isArray(reply.nextSteps);
    default:
      return false;
  }
}

function newId(): string {
  return globalThis.crypto?.randomUUID?.() ?? `q-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}
