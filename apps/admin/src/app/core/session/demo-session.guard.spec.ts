import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { DemoSessionService } from './demo-session.service';
import { demoSessionGuard } from './demo-session.guard';

describe('demoSessionGuard', () => {
  it('redirects a direct workspace visit to demo login when no persona is selected', () => {
    const loginTree = { target: '/login' };

    TestBed.configureTestingModule({
      providers: [{ provide: Router, useValue: { createUrlTree: () => loginTree } }],
    });

    const result = TestBed.runInInjectionContext(() => demoSessionGuard({} as never, {} as never));

    expect(result).toBe(loginTree);
  });

  it('allows a workspace visit after a persona is selected', () => {
    TestBed.configureTestingModule({ providers: [{ provide: Router, useValue: {} }] });
    const session = TestBed.inject(DemoSessionService);
    session.switchAccount('account-smb-admin');

    const result = TestBed.runInInjectionContext(() => demoSessionGuard({} as never, {} as never));

    expect(result).toBe(true);
  });
});
