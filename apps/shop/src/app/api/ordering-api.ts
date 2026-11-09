import { Injectable } from '@angular/core';
import type {
  Menu,
  Order,
  OrderRequest,
  Problem,
  ShopInfo,
  Slots,
  Submitted,
  TableView,
} from './types';

/** 公開點餐 API 的路徑前綴（`OrderingPublicEndpoints.Prefix`），永遠從網站根目錄算起，不受 `<base href="/order/">` 影響。 */
export const API_PREFIX = '/api/v1/public/ordering/';

/** 查詢與取消訂單時帶狀態 token 的請求標頭（`OrderingPublicEndpoints.TokenHeader`）。 */
export const TOKEN_HEADER = 'X-Ordering-Token';

/** 內用（#402）：桌號 QR code 的 token 放在這個請求標頭（`OrderingPublicEndpoints.TableHeader`），同樣絕不放進網址。 */
export const TABLE_HEADER = 'X-Ordering-Table';

/**
 * 一次 API 呼叫的結果。`network` 是連不上伺服器（離線、逾時、伺服器沒有回應）；`problem` 是伺服器的拒絕
 * （`application/problem+json`），框架預設的 400 之類沒有 `reason` 的回應以 `reason: 'unexpected'` 表示。
 */
export type ApiResult<T> =
  | { readonly ok: true; readonly status: number; readonly data: T }
  | {
      readonly ok: false;
      readonly status: number;
      readonly problem: Problem;
      readonly retryAfter: number | null;
    }
  | { readonly ok: false; readonly status: 0; readonly network: true };

/** 測試可替換的 fetch。 */
export type FetchLike = (input: string, init: RequestInit) => Promise<Response>;

/**
 * 公開點餐 API（設計文件 §4.2）的極小用戶端：只用瀏覽器的 fetch，不帶 Angular HttpClient，讓初始 bundle 保持小。
 *
 * 狀態 token 只放在 `X-Ordering-Token` 標頭，絕不放進網址或查詢字串：伺服器把請求路徑寫進每一筆紀錄的
 * logging scope 與追蹤，標頭則都不記。
 */
@Injectable({ providedIn: 'root' })
export class OrderingApi {
  /** 測試替換用；正式環境是 `globalThis.fetch`。 */
  fetcher: FetchLike = (input, init) => globalThis.fetch(input, init);

  shop(slug: string): Promise<ApiResult<ShopInfo>> {
    return this.send<ShopInfo>(slug, '', { method: 'GET' });
  }

  menu(slug: string): Promise<ApiResult<Menu>> {
    return this.send<Menu>(slug, '/menu', { method: 'GET' });
  }

  slots(slug: string): Promise<ApiResult<Slots>> {
    return this.send<Slots>(slug, '/slots', { method: 'GET' });
  }

  /** 送出訂單；內用（#402）帶這桌的 token（只放在 `X-Ordering-Table` 標頭）。 */
  submit(
    slug: string,
    request: OrderRequest,
    tableToken: string | null = null,
  ): Promise<ApiResult<Submitted>> {
    return this.send<Submitted>(slug, '/orders', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        ...(tableToken ? { [TABLE_HEADER]: tableToken } : {}),
      },
      body: JSON.stringify(request),
    });
  }

  /** 內用（#402）：桌號與這桌目前的桌次（已點的訂單）。 */
  table(slug: string, tableToken: string): Promise<ApiResult<TableView>> {
    return this.send<TableView>(slug, '/table', {
      method: 'GET',
      headers: { [TABLE_HEADER]: tableToken },
    });
  }

  order(slug: string, token: string): Promise<ApiResult<Order>> {
    return this.send<Order>(slug, '/orders/current', {
      method: 'GET',
      headers: { [TOKEN_HEADER]: token },
    });
  }

  cancel(slug: string, token: string): Promise<ApiResult<Order>> {
    return this.send<Order>(slug, '/orders/current/cancel', {
      method: 'POST',
      headers: { [TOKEN_HEADER]: token },
    });
  }

  private async send<T>(slug: string, path: string, init: RequestInit): Promise<ApiResult<T>> {
    const url = `${API_PREFIX}${encodeURIComponent(slug)}${path}`;
    let response: Response;
    try {
      response = await this.fetcher(url, {
        ...init,
        headers: {
          Accept: 'application/json',
          ...(init.headers as Record<string, string> | undefined),
        },
        cache: 'no-store',
        credentials: 'same-origin',
        referrerPolicy: 'no-referrer',
      });
    } catch {
      return { ok: false, status: 0, network: true };
    }

    let body: unknown = null;
    try {
      body = await response.json();
    } catch {
      body = null;
    }

    if (response.ok) {
      return body === null
        ? { ok: false, status: 0, network: true }
        : { ok: true, status: response.status, data: body as T };
    }

    return {
      ok: false,
      status: response.status,
      problem: toProblem(response.status, body),
      retryAfter: retryAfterSeconds(response.headers.get('Retry-After')),
    };
  }
}

function toProblem(status: number, body: unknown): Problem {
  if (body && typeof body === 'object' && typeof (body as Problem).reason === 'string') {
    const problem = body as Problem;
    return { ...problem, status: problem.status ?? status };
  }
  return { status, reason: 'unexpected', message: '' };
}

function retryAfterSeconds(header: string | null): number | null {
  if (header === null) return null;
  const seconds = Number(header);
  return Number.isFinite(seconds) && seconds >= 0 ? Math.ceil(seconds) : null;
}

/** 這個結果是不是連不上伺服器。 */
export function isNetworkFailure<T>(
  result: ApiResult<T>,
): result is { ok: false; status: 0; network: true } {
  return !result.ok && 'network' in result;
}
