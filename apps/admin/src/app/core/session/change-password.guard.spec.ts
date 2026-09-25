import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { createMemoryStorage } from '../repositories/memory-storage';
import type { ApiIdentity, ApiSessionBackend } from './api-session.service';
import { API_SESSION_BACKEND } from './api-session.service';
import { changePasswordGuard } from './change-password.guard';
import { DemoSessionService } from './demo-session.service';

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

function identity(passwordChangeRequired: boolean): ApiIdentity {
  return {
    demoAccountId: 'account-smb-admin',
    displayName: '安心商行管理者',
    permissions: [],
    passwordChangeRequired,
  };
}

function provideIsolatedSession() {
  return { provide: DemoSessionService, useFactory: () => new DemoSessionService({ storage: createMemoryStorage() }) };
}

describe('changePasswordGuard', () => {
  it('lets a password-change-required account reach the page', () => {
    TestBed.configureTestingModule({
      providers: [
        { provide: API_SESSION_BACKEND, useValue: fakeBackend({ restore: () => identity(true) }) },
        { provide: Router, useValue: {} },
        provideIsolatedSession(),
      ],
    });

    const result = TestBed.runInInjectionContext(() => changePasswordGuard({} as never, {} as never));

    expect(result).toBe(true);
  });

  it('sends an account that already changed its password back to the workspace', () => {
    const homeTree = { target: '/app/home' };
    TestBed.configureTestingModule({
      providers: [
        { provide: API_SESSION_BACKEND, useValue: fakeBackend({ restore: () => identity(false) }) },
        { provide: Router, useValue: { createUrlTree: () => homeTree } },
        provideIsolatedSession(),
      ],
    });

    const result = TestBed.runInInjectionContext(() => changePasswordGuard({} as never, {} as never));

    expect(result).toBe(homeTree);
  });

  it('sends a visitor with no session at all back to the workspace, which then bounces to login', () => {
    const homeTree = { target: '/app/home' };
    TestBed.configureTestingModule({
      providers: [
        { provide: API_SESSION_BACKEND, useValue: fakeBackend() },
        { provide: Router, useValue: { createUrlTree: () => homeTree } },
        provideIsolatedSession(),
      ],
    });

    const result = TestBed.runInInjectionContext(() => changePasswordGuard({} as never, {} as never));

    expect(result).toBe(homeTree);
  });
});
