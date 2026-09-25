import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { ApiSessionService } from './api-session.service';

/**
 * 每次進入工作區都當作一次活動：逾時的 Demo 工作階段會在這裡結束，
 * 使用者回到身分選擇畫面並看到「Demo 登入已逾時」說明。
 * API 模式另外要求 token 仍有效；帳號須先改密碼時導到設定新密碼頁，而不是清掉工作階段。
 */
export const demoSessionGuard: CanActivateFn = () => {
  const apiSession = inject(ApiSessionService);
  if (apiSession.canEnterWorkspace()) return true;
  const target = apiSession.passwordChangeRequired() ? '/change-password' : '/login';
  return inject(Router).createUrlTree([target]);
};
