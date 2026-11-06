import type { Order, OrderRequest, OrderStatus } from '../api/types';
import { readJson, writeJson } from './cart';

/**
 * 這支手機在這家店送過的訂單（只存在瀏覽器）。token 只在送出成功的回應裡給一次，所以收到就存；
 * 訂單頁網址是 `/order/{slug}/o#<token>`，token 在 `#` 後面，不會送到伺服器。
 */
export interface RecentOrder {
  readonly token: string;
  readonly submissionKey: string;
  readonly displayNumber: string;
  readonly status: OrderStatus;
  readonly pickupAt: string;
  readonly createdAt: string;
}

const recentKey = (slug: string) => `smart-agri-shop:orders:${slug}`;
const pendingKey = (slug: string) => `smart-agri-shop:pending:${slug}`;

/** 最多記幾筆。 */
const MAX_RECENT = 10;

/** 還在進行中的狀態（訂單頁會繼續更新）。 */
export const ACTIVE_STATUSES: readonly OrderStatus[] = ['pending', 'accepted', 'ready'];

export function isFinal(status: OrderStatus): boolean {
  return !ACTIVE_STATUSES.includes(status);
}

export function recentOrders(slug: string): RecentOrder[] {
  const list = readJson<RecentOrder[]>(recentKey(slug));
  return Array.isArray(list)
    ? list.filter((entry) => entry && typeof entry.token === 'string')
    : [];
}

export function rememberOrder(
  slug: string,
  token: string,
  submissionKey: string,
  order: Order,
): void {
  const entry: RecentOrder = {
    token,
    submissionKey,
    displayNumber: order.displayNumber,
    status: order.status ?? 'pending',
    pickupAt: order.pickupAt,
    createdAt: order.createdAt,
  };
  const rest = recentOrders(slug).filter((existing) => existing.token !== token);
  writeJson(recentKey(slug), [entry, ...rest].slice(0, MAX_RECENT));
}

/** 訂單頁拿到最新狀態時更新列表（橫幅只顯示進行中的訂單）。 */
export function updateRecentStatus(slug: string, token: string, order: Order): void {
  const list = recentOrders(slug);
  if (!list.some((entry) => entry.token === token)) return;
  writeJson(
    recentKey(slug),
    list.map((entry) =>
      entry.token === token
        ? { ...entry, status: order.status ?? entry.status, pickupAt: order.pickupAt }
        : entry,
    ),
  );
}

/**
 * 首頁橫幅要提醒的訂單：最近一筆進行中、取餐時間還沒過太久（3 小時內）的訂單。
 * `now` 用伺服器時間。
 */
export function activeOrder(slug: string, now: string): RecentOrder | null {
  const cutoff = new Date(now).getTime() - 3 * 3_600_000;
  return (
    recentOrders(slug).find(
      (entry) =>
        ACTIVE_STATUSES.includes(entry.status) && new Date(entry.pickupAt).getTime() >= cutoff,
    ) ?? null
  );
}

/** 已送出的訂單之中，用這個送出鍵的那筆（`duplicate-submission` 時帶顧客去看）。 */
export function orderBySubmissionKey(slug: string, submissionKey: string): RecentOrder | null {
  return recentOrders(slug).find((entry) => entry.submissionKey === submissionKey) ?? null;
}

/** 正在送出、還沒收到結果的訂單：送出前先存，回應遺失時下一次重試沿用同一個送出鍵。 */
export interface PendingSubmission {
  readonly submissionKey: string;
  readonly request: OrderRequest;
}

export function pendingSubmission(slug: string): PendingSubmission | null {
  const pending = readJson<PendingSubmission>(pendingKey(slug));
  return pending && typeof pending.submissionKey === 'string' ? pending : null;
}

export function savePendingSubmission(slug: string, pending: PendingSubmission | null): void {
  writeJson(pendingKey(slug), pending);
}

/** 每次進入結帳產生一個新的送出鍵（UUID）。非 https 的區網測試沒有 `randomUUID`，改用 `getRandomValues` 組。 */
export function newSubmissionKey(): string {
  const cryptoApi = globalThis.crypto;
  if (typeof cryptoApi?.randomUUID === 'function') return cryptoApi.randomUUID();
  const bytes = new Uint8Array(16);
  cryptoApi.getRandomValues(bytes);
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** 從網址 `#` 後面讀狀態 token（`#` 本身與空白不算）。 */
export function tokenFromHash(hash: string): string | null {
  const raw = hash.startsWith('#') ? hash.slice(1) : hash;
  let token = raw;
  try {
    token = decodeURIComponent(raw);
  } catch {
    token = raw;
  }
  token = token.trim();
  return token.length > 0 && token.length <= 200 && /^[A-Za-z0-9_-]+$/.test(token) ? token : null;
}
