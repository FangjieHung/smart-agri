import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AnonymousVisitorService } from './anonymous-visitor.service';
import { ApiSessionService } from './api-session.service';
import { DemoSessionService } from './demo-session.service';

/**
 * `/chat/:assistantId` 的守衛（舊網址 `/use/:assistantId` 轉址到這裡，issue #305）。
 *
 * **Mock 模式**：這條路由會被嵌入客戶官網、也會從 LINE 開啟，所以永遠不轉址——
 * 沒有 Demo 身分的人是「未登入的官網訪客」，不是未登入的使用者。有 Demo 身分時照舊
 * 延長工作階段（逾時仍會在這裡結束）；沒有（或剛逾時）時就發給這個瀏覽器分頁一個匿名
 * 訪客 id，由 repository 決定這個助理是否對外開放。權限判斷一律留在 repository 層，
 * 這裡不做，也不因此洩漏助理是否存在。
 *
 * **API 模式**（issue #79，M3 計畫第 3 節「前端沿用 M2 的替換模式」）：對外發布留待
 * M5，`/chat/:assistantId` 一律轉址——已登入轉到工作區的 `/app/chat/:assistantId`；
 * 未登入（或須先改密碼）轉到登入頁／設定新密碼頁，與 `demoSessionGuard` 的目標一致。
 */
export const embeddedChatGuard: CanActivateFn = (route) => {
  const apiSession = inject(ApiSessionService);
  if (apiSession.apiMode) {
    const router = inject(Router);
    const assistantId = route.paramMap.get('assistantId') ?? '';
    if (apiSession.canEnterWorkspace()) return router.createUrlTree(['/app/chat', assistantId]);
    return router.createUrlTree([apiSession.passwordChangeRequired() ? '/change-password' : '/login']);
  }

  if (!inject(DemoSessionService).refreshActivity()) {
    inject(AnonymousVisitorService).ensureVisitor();
  }
  return true;
};
