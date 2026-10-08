/**
 * 訪客 API 的線上形狀（後端是 M5a Slice 4／#196；契約見計畫第 3 節 D）。
 * widget 不依賴 admin 的生成型別，這裡自己定義。
 */
export interface VisitorAssistantInfo {
  readonly displayName: string;
  readonly welcomeMessage: string;
  /** 發布設定的品牌色代號（`forest`／`ocean`／`amber`／`plum`）；不認得的值退回預設色。 */
  readonly brandColor: string;
  readonly showCitations: boolean;
}

/** `POST …/visitor-sessions` 的 `201` 本文。 */
export interface VisitorSession {
  readonly token: string;
  readonly expiresAt: string;
  readonly assistant: VisitorAssistantInfo;
}

export type SessionResult =
  | { readonly status: 'created'; readonly session: VisitorSession }
  /** `403 public-assistant`：任何不在服務中的狀態都是同一個回應，不揭露原因。 */
  | { readonly status: 'unavailable' }
  | { readonly status: 'rate-limited'; readonly retryAfterSeconds: number }
  | { readonly status: 'failed' };

/** 問題送出後沒有取得回答的原因（畫面各有不同的文字與動作）。 */
export type RunFailure =
  | { readonly kind: 'rate-limited'; readonly retryAfterSeconds: number }
  /** `409 chat-run-in-progress`：文字直接用伺服器的說明（#329）。 */
  | { readonly kind: 'busy'; readonly message: string }
  /** `422`：問題過長等，文字直接用伺服器的說明。 */
  | { readonly kind: 'invalid'; readonly message: string }
  | { readonly kind: 'unavailable' }
  | { readonly kind: 'session-expired' }
  | { readonly kind: 'network' };

/** 一則問題的處理狀態。 */
export type RunState =
  | { readonly phase: 'idle' }
  | { readonly phase: 'streaming'; readonly question: string; readonly text: string; readonly clientMessageId: string }
  | {
      readonly phase: 'failed';
      readonly question: string;
      readonly clientMessageId: string;
      readonly failure: RunFailure;
    };

/** 整個視窗的狀態：沒有可用的工作階段時，取代對話畫面。 */
export type WidgetPhase =
  | 'initialising'
  | 'ready'
  /** `403` 建立工作階段：「這個對話視窗目前無法使用」。 */
  | 'unavailable'
  /** 對話中收到 `403`：「目前暫停服務」，保留已有的對話內容。 */
  | 'paused'
  | 'session-rate-limited'
  | 'session-failed';

export const QUESTION_MAX_LENGTH = 2000;
/** 與伺服器相同：`RunAgentInput.messages` 前文最多 20 則。 */
export const HISTORY_MAX_MESSAGES = 20;
export const SERVICE_UNAVAILABLE_TEXT = '這個對話視窗目前無法使用';
export const SERVICE_PAUSED_TEXT = '目前暫停服務';
export const DEFAULT_TITLE = '客服對話';
