import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { ApiSessionService } from './api-session.service';

/**
 * 每次進入工作區都當作一次活動：逾時的 Demo 工作階段會在這裡結束，
 * 使用者回到身分選擇畫面並看到「Demo 登入已逾時」說明。
 * API 模式另外要求 token 仍有效，且帳號不是「須先改密碼」。
 */
export const demoSessionGuard: CanActivateFn = () =>
  inject(ApiSessionService).canEnterWorkspace() ? true : inject(Router).createUrlTree(['/login']);
