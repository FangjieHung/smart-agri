import { HttpErrorResponse, type HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { ApiSessionService } from '../api-session.service';
import { isApiRequest, isSignInRequest } from './api-request';

/** 任何 API 回 401（未登入、token 過期、帳號被刪除）：結束工作階段並回登入頁顯示逾時說明。 */
export const unauthorizedInterceptor: HttpInterceptorFn = (request, next) => {
  const apiSession = inject(ApiSessionService);
  return next(request).pipe(
    catchError((error: unknown) => {
      if (
        error instanceof HttpErrorResponse &&
        error.status === 401 &&
        isApiRequest(request.url) &&
        !isSignInRequest(request.url)
      ) {
        apiSession.endExpiredSession();
      }
      return throwError(() => error);
    }),
  );
};
