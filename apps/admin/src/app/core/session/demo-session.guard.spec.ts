import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { vi } from 'vitest';
import { createMemoryStorage } from '../repositories/memory-storage';
import type { ApiIdentity, ApiSessionBackend } from './api-session.service';
import { API_SESSION_BACKEND } from './api-session.service';
import { DemoSessionService } from './demo-session.service';
import { demoSessionGuard } from './demo-session.guard';

/** 只填 guard 會用到的部分；其餘方法丟例外，用到就代表測試寫錯了。 */
function fakeBackend(overrides: Partial<ApiSessionBackend> = {}): ApiSessionBackend {
  return {
    loginOptions: () => Promise.reject(new Error('not used')),
    signIn: () => Promise.reject(new Error('not used')),
    completeSignIn: () => Promise.reject(new Error('not used')),
    refreshIdentity: () => Promise.reject(new Error('not used')),
    restore: () => null,
    hasUsableToken: () => true,
    signOut: () => Promise.reject(new Error('not used')),
    clear: () => undefined,
    changePassword: () => Promise.reject(new Error('not used')),
    ...overrides,
  };
}

const PASSWORD_CHANGE_IDENTITY: ApiIdentity = {
  demoAccountId: 'account-smb-admin',
  displayName: '安心商行管理者',
  permissions: [],
  passwordChangeRequired: true,
};

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

  it('sends a password-change-required account to the change-password page instead of discarding its token', () => {
    const backend = fakeBackend({ restore: () => PASSWORD_CHANGE_IDENTITY });
    const clear = vi.spyOn(backend, 'clear');

    TestBed.configureTestingModule({
      providers: [
        { provide: API_SESSION_BACKEND, useValue: backend },
        { provide: Router, useValue: { createUrlTree: (commands: string[]) => ({ target: commands[0] }) } },
        provideIsolatedSession(),
      ],
    });

    const result = TestBed.runInInjectionContext(() => demoSessionGuard({} as never, {} as never));

    expect(result).toEqual({ target: '/change-password' });
    expect(clear).not.toHaveBeenCalled();
  });
});
