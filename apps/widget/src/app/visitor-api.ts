import { Injectable, inject } from '@angular/core';
import { WIDGET_CONTEXT, WIDGET_FETCH, type WidgetFetch } from './widget-context';
import type { SessionResult, VisitorAssistantInfo, VisitorSession } from './widget.model';

const DEFAULT_RETRY_AFTER_SECONDS = 30;
const MAX_RETRY_AFTER_SECONDS = 3600;

/** `Retry-After`：秒數或 HTTP 日期；沒有或讀不懂時退回 30 秒，並限制在 1 秒到 1 小時之間。 */
export function parseRetryAfter(value: string | null, now: number = Date.now()): number {
  let seconds = DEFAULT_RETRY_AFTER_SECONDS;
  if (value !== null && value.trim() !== '') {
    const numeric = Number(value);
    if (Number.isFinite(numeric)) {
      seconds = numeric;
    } else {
      const date = Date.parse(value);
      if (!Number.isNaN(date)) seconds = (date - now) / 1000;
    }
  }
  return Math.min(MAX_RETRY_AFTER_SECONDS, Math.max(1, Math.ceil(seconds)));
}

/** 串流請求最近一次的回應狀態：`@ag-ui/client` 的錯誤不帶標頭，`429` 的 `Retry-After` 靠這裡取得。 */
export interface LastRunResponse {
  readonly status: number | null;
  readonly retryAfterSeconds: number;
}

/**
 * 訪客 API 的 HTTP（同源、相對網址、不帶 cookie）：建立工作階段，以及給 `AgUiChatRunner` 用的 fetch。
 * 契約見計畫第 3 節 D。
 */
@Injectable({ providedIn: 'root' })
export class VisitorApi {
  private readonly fetchImpl: WidgetFetch = inject(WIDGET_FETCH);
  private readonly context = inject(WIDGET_CONTEXT);
  private last: LastRunResponse = { status: null, retryAfterSeconds: DEFAULT_RETRY_AFTER_SECONDS };

  readonly runsPath = (assistantId: string): string => `${this.assistantPath(assistantId)}/chat/runs`;

  async createSession(): Promise<SessionResult> {
    const assistantId = this.context.assistantId;
    if (assistantId === null) return { status: 'unavailable' };
    let response: Response;
    try {
      response = await this.fetchImpl(`${this.assistantPath(assistantId)}/visitor-sessions`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
        credentials: 'omit',
        body: JSON.stringify({ host: this.context.hostOrigin }),
      });
    } catch {
      return { status: 'failed' };
    }
    if (response.status === 403) return { status: 'unavailable' };
    if (response.status === 429) {
      return { status: 'rate-limited', retryAfterSeconds: parseRetryAfter(response.headers.get('Retry-After')) };
    }
    if (response.status !== 201 && response.status !== 200) return { status: 'failed' };
    const session = readSession(await response.json().catch(() => null));
    return session === null ? { status: 'failed' } : { status: 'created', session };
  }

  /** 給 `AgUiChatRunner`：多記下回應狀態，且一律不帶 cookie。 */
  readonly runFetch: WidgetFetch = async (url, init) => {
    this.last = { status: null, retryAfterSeconds: DEFAULT_RETRY_AFTER_SECONDS };
    const response = await this.fetchImpl(url, { ...init, credentials: 'omit' });
    this.last = { status: response.status, retryAfterSeconds: parseRetryAfter(response.headers.get('Retry-After')) };
    return response;
  };

  lastRunResponse(): LastRunResponse {
    return this.last;
  }

  private assistantPath(assistantId: string): string {
    return `${this.context.basePath}/api/v1/public/assistants/${encodeURIComponent(assistantId)}`;
  }
}

export function readSession(value: unknown): VisitorSession | null {
  if (value === null || typeof value !== 'object') return null;
  const body = value as { token?: unknown; expiresAt?: unknown; assistant?: unknown };
  const assistant = readAssistant(body.assistant);
  if (typeof body.token !== 'string' || body.token === '' || typeof body.expiresAt !== 'string' || assistant === null) {
    return null;
  }
  return { token: body.token, expiresAt: body.expiresAt, assistant };
}

export function readAssistant(value: unknown): VisitorAssistantInfo | null {
  if (value === null || typeof value !== 'object') return null;
  const body = value as Record<string, unknown>;
  if (typeof body['displayName'] !== 'string') return null;
  return {
    displayName: body['displayName'],
    welcomeMessage: typeof body['welcomeMessage'] === 'string' ? body['welcomeMessage'] : '',
    brandColor: typeof body['brandColor'] === 'string' ? body['brandColor'] : '',
    showCitations: body['showCitations'] !== false,
  };
}
