import { API_LOGIN_OPTIONS_PATH, API_LOGIN_PATH } from './http-session-backend';

/** 只有站內的 `/api/` 請求才帶 token 或觸發逾時流程；絕不把 token 送到其他網站。 */
export function isApiRequest(url: string): boolean {
  return url.startsWith('/api/');
}

/** 登入本身的 401 代表帳密錯誤，不是工作階段逾時。 */
export function isSignInRequest(url: string): boolean {
  const path = url.split('?')[0];
  return path === API_LOGIN_PATH || path === API_LOGIN_OPTIONS_PATH;
}
