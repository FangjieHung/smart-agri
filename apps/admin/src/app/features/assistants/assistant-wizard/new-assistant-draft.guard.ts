import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';

/** 每次從「建立新助理」進來都產生獨立草稿。 */
export const newAssistantDraftGuard: CanActivateFn = () => {
  const accountId = inject(DemoSessionService).activeAccountId();
  const router = inject(Router);
  if (!accountId) return router.createUrlTree(['/login']);
  const result = inject(DEMO_REPOSITORY).createNamedAssistantDraft(accountId);
  return result.status === 'ready'
    ? router.createUrlTree(['/app/assistants/drafts', result.data.id, 'purpose'])
    : router.createUrlTree(['/app/assistants']);
};

/** Prevent direct URLs from opening another account's or a missing draft. */
export const existingAssistantDraftGuard: CanActivateFn = (route) => {
  const accountId = inject(DemoSessionService).activeAccountId();
  const router = inject(Router);
  if (!accountId) return router.createUrlTree(['/login']);
  const draftId = route.paramMap.get('draftId') ?? '';
  const result = inject(DEMO_REPOSITORY).getNamedAssistantDraft(accountId, draftId);
  return result.status === 'ready' && result.data !== null
    ? true
    : router.createUrlTree(['/app/assistants']);
};
