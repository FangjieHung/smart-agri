import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { createMemoryStorage } from '../repositories/memory-storage';
import { DemoSessionService } from './demo-session.service';
import { demoSessionGuard } from './demo-session.guard';

/** 每個測試都用自己的記憶體儲存，避免共用瀏覽器 sessionStorage 互相污染。 */
function provideIsolatedSession(timeoutMs?: number, now?: () => number) {
  return {
    provide: DemoSessionService,
    useFactory: () =>
      new DemoSessionService({ storage: createMemoryStorage(), timeoutMs, now }),
  };
}

describe('demoSessionGuard', () => {
  it('redirects a direct workspace visit to demo login when no persona is selected', () => {
    const loginTree = { target: '/login' };

    TestBed.configureTestingModule({
      providers: [
        { provide: Router, useValue: { createUrlTree: () => loginTree } },
        provideIsolatedSession(),
      ],
    });

    const result = TestBed.runInInjectionContext(() => demoSessionGuard({} as never, {} as never));

    expect(result).toBe(loginTree);
  });

  it('allows a workspace visit after a persona is selected', () => {
    TestBed.configureTestingModule({
      providers: [{ provide: Router, useValue: {} }, provideIsolatedSession()],
    });
    const session = TestBed.inject(DemoSessionService);
    session.switchAccount('account-smb-admin');

    const result = TestBed.runInInjectionContext(() => demoSessionGuard({} as never, {} as never));

    expect(result).toBe(true);
  });

  it('ends an idle demo session and sends the visitor back to demo login', () => {
    const loginTree = { target: '/login' };
    let clock = 1_000_000;

    TestBed.configureTestingModule({
      providers: [
        { provide: Router, useValue: { createUrlTree: () => loginTree } },
        provideIsolatedSession(1000, () => clock),
      ],
    });
    const session = TestBed.inject(DemoSessionService);
    session.switchAccount('account-smb-admin');
    clock += 1001;

    const result = TestBed.runInInjectionContext(() => demoSessionGuard({} as never, {} as never));

    expect(result).toBe(loginTree);
    expect(session.sessionExpired()).toBe(true);
  });
});
