/**
 * 內用掃碼（#402）：桌號 QR code 的網址是 `/order/{slug}/table#<token>`，token 在 `#` 後面，不會送到伺服器。
 * 網頁讀到後存在 sessionStorage（每個分頁、每家店一份：重新整理、去訂單頁再回來都還在；在新分頁打開
 * `/order/{slug}` 就是一般外帶），再把網址換成菜單頁，token 不留在網址列。查詢與送出時只放在 `X-Ordering-Table` 標頭。
 */

/** 桌號 token 的長度（32 bytes 的 base64url，`OrderingTable.TokenLength`）。 */
export const TABLE_TOKEN_LENGTH = 43;

const tableKey = (slug: string) => `smart-agri-shop:table:${slug}`;

const isTableToken = (value: unknown): value is string =>
  typeof value === 'string' && /^[A-Za-z0-9_-]{43}$/.test(value);

/** 從網址 `#` 後面讀桌號 token；格式不對是 `null`。 */
export function tableTokenFromHash(hash: string): string | null {
  let raw = hash.startsWith('#') ? hash.slice(1) : hash;
  try {
    raw = decodeURIComponent(raw);
  } catch {
    // 不是合法的百分比編碼：照原樣檢查。
  }
  raw = raw.trim();
  return isTableToken(raw) ? raw : null;
}

/** 這個分頁記住的這家店的桌號 token（sessionStorage 被封鎖時當成沒有）。 */
export function storedTableToken(slug: string): string | null {
  try {
    const value = globalThis.sessionStorage?.getItem(tableKey(slug));
    return isTableToken(value) ? value : null;
  } catch {
    return null;
  }
}

/** 記住或忘記（`null`）這家店的桌號 token。 */
export function saveTableToken(slug: string, token: string | null): void {
  try {
    if (token === null) {
      globalThis.sessionStorage?.removeItem(tableKey(slug));
    } else {
      globalThis.sessionStorage?.setItem(tableKey(slug), token);
    }
  } catch {
    // 存不了就只放在記憶體裡（重新整理後變回一般外帶）。
  }
}
