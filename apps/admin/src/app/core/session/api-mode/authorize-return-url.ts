const AUTHORIZE_PATH = '/connect/authorize';

/**
 * 登入頁的 `returnUrl` 只接受本站的 authorize 請求，其他一律忽略，
 * 避免登入頁被拿來轉址到任意網站。回傳可直接導向的站內路徑，不合格時回傳 null。
 */
export function authorizeReturnUrl(returnUrl: string | null, origin: string): string | null {
  if (!returnUrl?.startsWith('/')) return null;
  let url: URL;
  try {
    url = new URL(returnUrl, origin);
  } catch {
    return null;
  }
  if (url.origin !== origin || url.pathname !== AUTHORIZE_PATH) return null;
  return `${url.pathname}${url.search}`;
}

const WORKSPACE_PATH = /^\/app(?:[/?#]|$)/;

/**
 * 登入逾時（換發 token 失敗）時帶去登入頁的工作區網址（#354）：只接受本站 `/app` 底下的路徑，
 * 其他一律忽略，避免登入頁被拿來轉址到任意網站。回傳可直接導覽的站內路徑，不合格時回傳 null。
 */
export function workspaceReturnPath(returnUrl: string | null, origin: string): string | null {
  if (!returnUrl?.startsWith('/') || returnUrl.startsWith('//')) return null;
  let url: URL;
  try {
    url = new URL(returnUrl, origin);
  } catch {
    return null;
  }
  const path = `${url.pathname}${url.search}${url.hash}`;
  if (url.origin !== origin || !WORKSPACE_PATH.test(path)) return null;
  return path;
}
