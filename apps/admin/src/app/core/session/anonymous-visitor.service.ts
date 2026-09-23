import { Injectable, signal } from '@angular/core';
import type { VisitorId } from '../domain/account.model';
import type { DemoKeyValueStorage } from '../repositories/demo-repository';
import { createMemoryStorage } from '../repositories/memory-storage';

/**
 * 只存在於單一瀏覽器分頁的 key，與 `demo-session` 同一個命名習慣：
 * 關閉分頁就消失，兩個分頁是兩位不同的訪客。
 */
export const ANONYMOUS_VISITOR_STORAGE_KEY = 'demo-visitor';

/** 訪客 id 只允許英數與連字號，避免被拿來當作儲存 key 的注入點。 */
const VISITOR_ID_PATTERN = /^visitor-[A-Za-z0-9-]{4,64}$/;

export interface AnonymousVisitorOptions {
  /** 預設為記憶體儲存；瀏覽器中由 root provider 換成 sessionStorage。 */
  readonly storage?: DemoKeyValueStorage;
  /** 可注入的 id 產生器，讓測試取得可預期的值。 */
  readonly newId?: () => string;
}

/** sessionStorage 在無痕模式或被封鎖時可能丟出例外，取不到就退回記憶體。 */
function browserSessionStorage(): DemoKeyValueStorage | undefined {
  if (typeof sessionStorage === 'undefined') return undefined;
  try {
    sessionStorage.getItem(ANONYMOUS_VISITOR_STORAGE_KEY);
    return sessionStorage;
  } catch {
    return undefined;
  }
}

function randomSuffix(): string {
  const cryptoApi = typeof crypto === 'undefined' ? undefined : crypto;
  if (cryptoApi?.randomUUID !== undefined) return cryptoApi.randomUUID().replace(/-/g, '');
  return `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 12)}`;
}

/**
 * 未登入官網訪客的身分，對應設計文件「未登入的官網訪客以獨立瀏覽工作階段保存對話」。
 *
 * 這**不是登入**：沒有憑證、沒有權限、也不對應任何 Demo 帳號。id 只用來把這位訪客的
 * 對話與其他人隔開（`sme-demo:chat:<visitorId>:<assistantId>`），且與帳號一樣
 * 只存在於這個瀏覽器分頁的 sessionStorage，關閉分頁就結束。
 */
@Injectable({
  providedIn: 'root',
  useFactory: () => new AnonymousVisitorService({ storage: browserSessionStorage() }),
})
export class AnonymousVisitorService {
  private readonly storage: DemoKeyValueStorage;
  private readonly newId: () => string;
  private readonly visitorIdState = signal<VisitorId | null>(null);

  readonly visitorId = this.visitorIdState.asReadonly();

  constructor(options: AnonymousVisitorOptions = {}) {
    this.storage = options.storage ?? createMemoryStorage();
    this.newId = options.newId ?? (() => `visitor-${randomSuffix()}`);
    this.restore();
  }

  /** 進入嵌入的對話頁時呼叫：沿用這個分頁既有的訪客，沒有就發一個新的。 */
  ensureVisitor(): VisitorId {
    const current = this.visitorIdState();
    if (current !== null) return current;

    const minted = this.mint();
    this.visitorIdState.set(minted);
    this.storage.setItem(ANONYMOUS_VISITOR_STORAGE_KEY, minted);
    return minted;
  }

  /** 結束這位訪客；對話本身也存在同一個分頁的儲存中，會一起消失。 */
  endVisit(): void {
    this.visitorIdState.set(null);
    this.storage.removeItem(ANONYMOUS_VISITOR_STORAGE_KEY);
  }

  private restore(): void {
    const raw = this.storage.getItem(ANONYMOUS_VISITOR_STORAGE_KEY);
    if (raw === null) return;
    if (!VISITOR_ID_PATTERN.test(raw)) {
      this.storage.removeItem(ANONYMOUS_VISITOR_STORAGE_KEY);
      return;
    }
    this.visitorIdState.set(raw as VisitorId);
  }

  private mint(): VisitorId {
    const candidate = this.newId();
    return VISITOR_ID_PATTERN.test(candidate)
      ? (candidate as VisitorId)
      : (`visitor-${randomSuffix()}` as VisitorId);
  }
}
