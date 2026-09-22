import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { DemoSessionService } from './demo-session.service';

/**
 * 每次進入工作區都當作一次活動：逾時的 Demo 工作階段會在這裡結束，
 * 使用者回到身分選擇畫面並看到「Demo 登入已逾時」說明。
 */
export const demoSessionGuard: CanActivateFn = () => {
  const session = inject(DemoSessionService);
  return session.refreshActivity() ? true : inject(Router).createUrlTree(['/login']);
};
