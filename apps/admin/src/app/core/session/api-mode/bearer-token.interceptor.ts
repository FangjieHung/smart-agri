import type { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { defer, from, switchMap } from 'rxjs';
import { isApiRequest, isSignInRequest } from './api-request';
import { HttpSessionBackend } from './http-session-backend';

/**
 * 替站內 API 請求加上 `Authorization: Bearer`；API 端點只接受 bearer，不接受 cookie。
 * access token 已過期或快到期時，先用 refresh token 換發再送出（#354；同時送出的請求共用同一次換發）。
 */
export const bearerTokenInterceptor: HttpInterceptorFn = (request, next) => {
  if (!isApiRequest(request.url) || isSignInRequest(request.url)) return next(request);
  const backend = inject(HttpSessionBackend);
  backend.noteActivity();
  const send = defer(() => {
    const token = backend.accessToken();
    return next(
      token === null ? request : request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }),
    );
  });
  // 換發被拒或暫時失敗都照樣送出（沒有 token 就不帶），由伺服器的 401 走統一的逾時流程。
  return backend.shouldRefreshBeforeSending() ? from(backend.refresh()).pipe(switchMap(() => send)) : send;
};
