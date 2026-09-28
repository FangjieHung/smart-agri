import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';

/** 每次從「建立新助理」進來都產生獨立草稿；建立失敗（沒有權限或連線錯誤）就回到我的助理。 */
export const newAssistantDraftGuard: CanActivateFn = () => {
  const accountId = inject(DemoSessionService).activeAccountId();
  const router = inject(Router);
  if (!accountId) return router.createUrlTree(['/login']);
  return inject(DEMO_REPOSITORY)
    .createNamedAssistantDraft()
    .pipe(
      map((result) =>
        result.status === 'ready'
          ? router.createUrlTree(['/app/assistants/drafts', result.data.id, 'purpose'])
          : router.createUrlTree(['/app/assistants']),
      ),
      catchError(() => of(router.createUrlTree(['/app/assistants']))),
    );
};

/**
 * 直接輸入網址時，不讓別人的或不存在的草稿打開精靈。讀取失敗（連線錯誤）時仍放行，
 * 由精靈頁面自己顯示「目前無法載入這份草稿」，不把錯誤當成「沒有權限」。
 */
export const existingAssistantDraftGuard: CanActivateFn = (route) => {
  const accountId = inject(DemoSessionService).activeAccountId();
  const router = inject(Router);
  if (!accountId) return router.createUrlTree(['/login']);
  const draftId = route.paramMap.get('draftId') ?? '';
  return inject(DEMO_REPOSITORY)
    .getNamedAssistantDraft(draftId)
    .pipe(
      map((result) => (result.status === 'permission-denied' ? router.createUrlTree(['/app/assistants']) : true)),
      catchError(() => of(true)),
    );
};
