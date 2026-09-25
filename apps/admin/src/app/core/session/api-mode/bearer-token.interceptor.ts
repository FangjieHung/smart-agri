import type { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { isApiRequest, isSignInRequest } from './api-request';
import { HttpSessionBackend } from './http-session-backend';

/** 替站內 API 請求加上 `Authorization: Bearer`；API 端點只接受 bearer，不接受 cookie。 */
export const bearerTokenInterceptor: HttpInterceptorFn = (request, next) => {
  if (!isApiRequest(request.url) || isSignInRequest(request.url)) return next(request);
  const token = inject(HttpSessionBackend).accessToken();
  return next(
    token === null ? request : request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }),
  );
};
