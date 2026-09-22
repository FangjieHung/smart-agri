import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { DemoSessionService } from './demo-session.service';

export const demoSessionGuard: CanActivateFn = () => {
  const session = inject(DemoSessionService);
  return session.activeAccountId() ? true : inject(Router).createUrlTree(['/login']);
};
