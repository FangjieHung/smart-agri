import { InjectionToken } from '@angular/core';

/** 從網址讀到的設定：iframe 網址是 `{前綴}/use/{助理 id}?host={嵌入頁面的 origin}`（見 embed-loader README）。 */
export interface WidgetContext {
  /** 助理 id；網址不是 `…/use/{id}` 時為 null。 */
  readonly assistantId: string | null;
  /** API 的路徑前綴（通常是空字串）：載入器允許 API 掛在路徑前綴下，所以相對網址要帶上它。 */
  readonly basePath: string;
  /** `host` 查詢參數驗證過的 origin；缺少或不是 http(s) origin 時為 null（不送 postMessage、不帶 host）。 */
  readonly hostOrigin: string | null;
}

const USE_PATH = /^(.*)\/use\/([^/]+)\/?$/;

export function readWidgetContext(location: Pick<Location, 'pathname' | 'search'>): WidgetContext {
  const match = USE_PATH.exec(location.pathname);
  return {
    assistantId: match === null ? null : safeDecode(match[2]),
    basePath: match === null ? '' : match[1],
    hostOrigin: parseHostOrigin(new URLSearchParams(location.search).get('host')),
  };
}

export function parseHostOrigin(value: string | null): string | null {
  if (value === null) return null;
  try {
    const url = new URL(value);
    // 只接受「純 origin」：不接受路徑、query，也不接受 `null`／file／javascript 等來源。
    if ((url.protocol === 'https:' || url.protocol === 'http:') && url.origin === value) return url.origin;
  } catch {
    // 不是網址：當作沒有。
  }
  return null;
}

function safeDecode(value: string): string | null {
  try {
    return decodeURIComponent(value);
  } catch {
    return null;
  }
}

export const WIDGET_CONTEXT = new InjectionToken<WidgetContext>('WIDGET_CONTEXT', {
  providedIn: 'root',
  factory: () => readWidgetContext(globalThis.location),
});

export type WidgetFetch = (url: string, init: RequestInit) => Promise<Response>;

/** 所有 HTTP 都走同一個 fetch（工作階段與串流），測試換成假的。 */
export const WIDGET_FETCH = new InjectionToken<WidgetFetch>('WIDGET_FETCH', {
  providedIn: 'root',
  factory: () => (url, init) => globalThis.fetch(url, init),
});

/** 本分頁的 `sessionStorage`；瀏覽器擋掉時為 null（對話照常運作，只是不保留）。 */
export const WIDGET_STORAGE = new InjectionToken<Storage | null>('WIDGET_STORAGE', {
  providedIn: 'root',
  factory: () => {
    try {
      return globalThis.sessionStorage;
    } catch {
      return null;
    }
  },
});

/** 嵌入頁面（載入器）的視窗；不在 iframe 裡時為 null。 */
export const WIDGET_PARENT = new InjectionToken<Pick<Window, 'postMessage'> | null>('WIDGET_PARENT', {
  providedIn: 'root',
  factory: () => {
    const self = globalThis as unknown as Window;
    return self.parent !== self ? self.parent : null;
  },
});
