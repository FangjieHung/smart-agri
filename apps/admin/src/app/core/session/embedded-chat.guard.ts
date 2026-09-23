import { inject } from '@angular/core';
import { CanActivateFn } from '@angular/router';
import { AnonymousVisitorService } from './anonymous-visitor.service';
import { DemoSessionService } from './demo-session.service';

/**
 * `/use/:assistantId` 的守衛。這條路由會被嵌入客戶官網、也會從 LINE 開啟，
 * 所以**永遠不轉址**：沒有 Demo 身分的人是「未登入的官網訪客」，不是未登入的使用者。
 *
 * 有 Demo 身分時照舊延長工作階段（逾時仍會在這裡結束）；沒有（或剛逾時）時就發給
 * 這個瀏覽器分頁一個匿名訪客 id，由 repository 決定這個助理是否對外開放。
 * 權限判斷一律留在 repository 層，這裡不做，也不因此洩漏助理是否存在。
 */
export const embeddedChatGuard: CanActivateFn = () => {
  if (!inject(DemoSessionService).refreshActivity()) {
    inject(AnonymousVisitorService).ensureVisitor();
  }
  return true;
};
