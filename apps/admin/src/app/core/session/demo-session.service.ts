import { Injectable, signal } from '@angular/core';
import type { AccountId } from '../domain/account.model';
import type { AssistantId } from '../domain/assistant.model';
import {
  DEMO_SECURITY_NOTICE,
  type DemoKeyValueStorage,
} from '../repositories/demo-repository';
import { createMemoryStorage } from '../repositories/memory-storage';

export type DemoAssistantTab =
  'overview' | 'data-sources' | 'test' | 'publishing';

export interface DemoViewState {
  readonly assistantId: AssistantId | null;
  readonly assistantTab: DemoAssistantTab | null;
  readonly returnPath: string | null;
}

const EMPTY_VIEW_STATE: DemoViewState = Object.freeze({
  assistantId: null,
  assistantTab: null,
  returnPath: null,
});

/** 只存在於單一瀏覽器分頁的 key；兩個分頁可以同時示範不同帳號。 */
export const DEMO_SESSION_STORAGE_KEY = 'demo-session';

/** Demo 工作階段的閒置上限，逾時後回到身分選擇畫面。 */
export const DEMO_SESSION_TIMEOUT_MS = 30 * 60 * 1000;

export const DEMO_SESSION_TIMEOUT_NOTICE = 'Demo 登入已逾時';

export const DEMO_SESSION_TIMEOUT_DETAIL =
  '這個 Demo 工作階段閒置太久，已自動結束。重新選擇一個 Demo 身分就可以繼續；這不是真實登入，也沒有真實帳號被登出。';

export interface DemoSessionOptions {
  /** 預設為記憶體儲存；瀏覽器中由 root provider 換成 sessionStorage。 */
  readonly storage?: DemoKeyValueStorage;
  readonly timeoutMs?: number;
  readonly now?: () => number;
}

interface StoredDemoSession {
  readonly accountId: AccountId;
  readonly lastActiveAt: number;
}

function isStoredDemoSession(value: unknown): value is StoredDemoSession {
  if (value === null || typeof value !== 'object') return false;
  const record = value as Record<string, unknown>;
  return typeof record['accountId'] === 'string' && typeof record['lastActiveAt'] === 'number';
}

/** sessionStorage 在無痕模式或被封鎖時可能丟出例外，取不到就退回記憶體。 */
function browserSessionStorage(): DemoKeyValueStorage | undefined {
  if (typeof sessionStorage === 'undefined') return undefined;
  try {
    sessionStorage.getItem(DEMO_SESSION_STORAGE_KEY);
    return sessionStorage;
  } catch {
    return undefined;
  }
}

/**
 * Demo 身分切換，不是真實驗證：目前身分只記在這個瀏覽器分頁的 sessionStorage，
 * 重新整理會保留、關閉分頁或閒置逾時就結束，且不包含任何憑證。
 */
@Injectable({
  providedIn: 'root',
  useFactory: () => new DemoSessionService({ storage: browserSessionStorage() }),
})
export class DemoSessionService {
  readonly securityNotice = DEMO_SECURITY_NOTICE;
  readonly timeoutNotice = DEMO_SESSION_TIMEOUT_NOTICE;
  readonly timeoutDetail = DEMO_SESSION_TIMEOUT_DETAIL;
  readonly timeoutMinutes: number;

  private readonly storage: DemoKeyValueStorage;
  private readonly timeoutMs: number;
  private readonly now: () => number;

  private readonly activeAccountIdState = signal<AccountId | null>(null);
  private readonly viewStateValue = signal<DemoViewState>(EMPTY_VIEW_STATE);
  private readonly expiredState = signal(false);

  readonly activeAccountId = this.activeAccountIdState.asReadonly();
  readonly viewState = this.viewStateValue.asReadonly();
  /** 逾時結束時為 true，重新選擇 Demo 身分後清除。 */
  readonly sessionExpired = this.expiredState.asReadonly();

  constructor(options: DemoSessionOptions = {}) {
    this.storage = options.storage ?? createMemoryStorage();
    this.timeoutMs = options.timeoutMs ?? DEMO_SESSION_TIMEOUT_MS;
    this.now = options.now ?? (() => Date.now());
    this.timeoutMinutes = Math.max(1, Math.round(this.timeoutMs / 60_000));
    this.restore();
  }

  switchAccount(accountId: AccountId): void {
    this.expiredState.set(false);
    this.activeAccountIdState.set(accountId);
    this.viewStateValue.set(EMPTY_VIEW_STATE);
    this.persist(accountId);
  }

  updateViewState(viewState: Partial<DemoViewState>): void {
    this.viewStateValue.update((current) =>
      Object.freeze({ ...current, ...viewState }),
    );
    this.touch();
  }

  /**
   * 每次進入工作區時呼叫：閒置逾時就結束工作階段並回報 false，否則延長有效時間。
   * 回傳 true 代表目前仍有已選擇的 Demo 身分。
   */
  refreshActivity(): boolean {
    const accountId = this.activeAccountIdState();
    if (accountId === null) return false;

    const stored = this.readStored();
    if (stored === null || this.hasTimedOut(stored.lastActiveAt)) {
      this.expire();
      return false;
    }

    this.persist(accountId);
    return true;
  }

  clearSession(): void {
    this.expiredState.set(false);
    this.reset();
  }

  /** 伺服器判定工作階段已失效（API 模式的 401）：與閒置逾時一樣結束並顯示逾時說明。 */
  expireSession(): void {
    this.expire();
  }

  private restore(): void {
    const stored = this.readStored();
    if (stored === null) return;
    if (this.hasTimedOut(stored.lastActiveAt)) {
      this.expire();
      return;
    }
    this.activeAccountIdState.set(stored.accountId);
  }

  private hasTimedOut(lastActiveAt: number): boolean {
    return this.now() - lastActiveAt >= this.timeoutMs;
  }

  private expire(): void {
    this.reset();
    this.expiredState.set(true);
  }

  private reset(): void {
    this.activeAccountIdState.set(null);
    this.viewStateValue.set(EMPTY_VIEW_STATE);
    this.storage.removeItem(DEMO_SESSION_STORAGE_KEY);
  }

  private touch(): void {
    const accountId = this.activeAccountIdState();
    if (accountId !== null) this.persist(accountId);
  }

  private persist(accountId: AccountId): void {
    const record: StoredDemoSession = { accountId, lastActiveAt: this.now() };
    this.storage.setItem(DEMO_SESSION_STORAGE_KEY, JSON.stringify(record));
  }

  private readStored(): StoredDemoSession | null {
    const raw = this.storage.getItem(DEMO_SESSION_STORAGE_KEY);
    if (raw === null) return null;
    try {
      const parsed: unknown = JSON.parse(raw);
      if (!isStoredDemoSession(parsed)) {
        this.storage.removeItem(DEMO_SESSION_STORAGE_KEY);
        return null;
      }
      return parsed;
    } catch {
      this.storage.removeItem(DEMO_SESSION_STORAGE_KEY);
      return null;
    }
  }
}
