import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { ApiSessionService } from './api-session.service';

/**
 * 設定新密碼頁只在「須先改密碼」時才進得去；已經不需要改密碼就沒有理由留在這頁，
 * 一律導回工作區——`demoSessionGuard` 會依當時的登入狀態決定是放行還是回登入頁。
 */
export const changePasswordGuard: CanActivateFn = () =>
  inject(ApiSessionService).passwordChangeRequired()
    ? true
    : inject(Router).createUrlTree(['/app/home']);
