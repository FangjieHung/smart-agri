import { HttpErrorResponse, type HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, from, switchMap, throwError } from 'rxjs';
import { ApiSessionService } from '../api-session.service';
import { isApiRequest, isSignInRequest } from './api-request';
import { HttpSessionBackend } from './http-session-backend';

function isUnauthorized(error: unknown): error is HttpErrorResponse {
  return error instanceof HttpErrorResponse && error.status === 401;
}

function sentAccessToken(authorization: string | null): string | null {
  return authorization?.startsWith('Bearer ') ? authorization.slice('Bearer '.length) : null;
}

/**
 * 任何 API 回 401（token 過期、被撤銷、帳號被刪除）：先用 refresh token 換發一次（同時收到 401 的請求
 * 共用同一次），成功就以新 token 重送這個請求一次（#354）。伺服器拒絕換發、或重送仍是 401：結束工作階段，
 * 帶著目前的網址回登入頁顯示逾時說明。伺服器暫時無法換發時保留工作階段，只把原本的錯誤交給畫面。
 */
export const unauthorizedInterceptor: HttpInterceptorFn = (request, next) => {
  if (!isApiRequest(request.url) || isSignInRequest(request.url)) return next(request);
  const apiSession = inject(ApiSessionService);
  const backend = inject(HttpSessionBackend);
  return next(request).pipe(
    catchError((error: unknown) => {
      if (!isUnauthorized(error)) return throwError(() => error);
      return from(
        backend.renewAfterUnauthorized(sentAccessToken(request.headers.get('Authorization'))),
      ).pipe(
        switchMap((outcome) => {
          const token = outcome === 'refreshed' ? backend.accessToken() : null;
          if (token === null) {
            if (outcome !== 'unavailable') apiSession.endExpiredSession();
            return throwError(() => error);
          }
          return next(request.clone({ setHeaders: { Authorization: `Bearer ${token}` } })).pipe(
            catchError((retryError: unknown) => {
              if (isUnauthorized(retryError)) apiSession.endExpiredSession();
              return throwError(() => retryError);
            }),
          );
        }),
      );
    }),
  );
};
