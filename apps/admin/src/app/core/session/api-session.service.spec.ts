import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { vi } from 'vitest';
import type { components } from '../api/api-schema';
import type { DemoKeyValueStorage } from '../repositories/demo-repository';
import { DEMO_SEED } from '../repositories/demo-seed';
import { createMemoryStorage } from '../repositories/memory-storage';
import { MockDemoRepository } from '../repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../repositories/tokens';
import { bearerTokenInterceptor } from './api-mode/bearer-token.interceptor';
import { BrowserNavigation } from './api-mode/browser-navigation';
import {
  API_SESSION_STORAGE,
  API_SESSION_STORAGE_KEY,
  HttpSessionBackend,
} from './api-mode/http-session-backend';
import { OIDC_CLIENT, type OidcClient } from './api-mode/oidc-client';
import { unauthorizedInterceptor } from './api-mode/unauthorized.interceptor';
import { API_SESSION_BACKEND, ApiSessionService } from './api-session.service';
import { DemoSessionService } from './demo-session.service';

type MeResponse = components['schemas']['MeResponse'];

const ORIGIN = 'http://localhost:4200';

const ADMIN_ME: MeResponse = {
  id: '00000000-0000-0000-0000-000000000001',
  displayName: '安心商行管理者',
  role: 'smb-admin',
  permissions: [
    'manage-assistants',
    'manage-data-sources',
    'manage-publishing',
    'read-consented-submissions',
  ],
  organization: { id: '00000000-0000-0000-0000-0000000000aa', name: '安心商行' },
  passwordChangeRequired: false,
};

const CUSTOMER_ME: MeResponse = {
  ...ADMIN_ME,
  id: '00000000-0000-0000-0000-000000000003',
  displayName: '外部客戶',
  role: 'external-customer',
  permissions: ['submit-authorized-forms', 'read-own-tracking'],
};

function inOneHour(): number {
  return Math.floor(Date.now() / 1000) + 3600;
}

function createFakeOidc() {
  return {
    signinRedirect: vi.fn<OidcClient['signinRedirect']>().mockResolvedValue(undefined),
    signinRedirectCallback: vi.fn<OidcClient['signinRedirectCallback']>().mockResolvedValue({
      access_token: 'access-token-1',
      id_token: 'id-token-1',
      expires_at: inOneHour(),
    }),
    signoutRedirect: vi.fn<OidcClient['signoutRedirect']>().mockResolvedValue(undefined),
  };
}

function setUpApiMode({ storage = createMemoryStorage() }: { storage?: DemoKeyValueStorage } = {}) {
  const oidc = createFakeOidc();
  const navigation = {
    origin: () => ORIGIN,
    currentUrl: () => `${ORIGIN}/auth/callback?code=abc&state=xyz`,
    assign: vi.fn<(url: string) => void>(),
  };
  const navigateByUrl = vi.fn().mockResolvedValue(true);
  const demoSession = new DemoSessionService({ storage: createMemoryStorage() });

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([bearerTokenInterceptor, unauthorizedInterceptor])),
      provideHttpClientTesting(),
      HttpSessionBackend,
      { provide: API_SESSION_BACKEND, useExisting: HttpSessionBackend },
      { provide: API_SESSION_STORAGE, useValue: storage },
      { provide: OIDC_CLIENT, useValue: () => Promise.resolve(oidc) },
      { provide: BrowserNavigation, useValue: navigation },
      { provide: Router, useValue: { navigateByUrl } },
      { provide: DemoSessionService, useValue: demoSession },
      { provide: DEMO_REPOSITORY, useValue: new MockDemoRepository(DEMO_SEED) },
    ],
  });

  return {
    service: TestBed.inject(ApiSessionService),
    http: TestBed.inject(HttpTestingController),
    oidc,
    navigation,
    navigateByUrl,
    demoSession,
    storage,
  };
}

/** 讓 await 中的 Promise 往下走到下一個 HTTP 請求。 */
async function flushMicrotasks(): Promise<void> {
  for (let i = 0; i < 5; i++) await Promise.resolve();
}

describe('ApiSessionService (API mode)', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('posts the login as JSON and starts a PKCE authorize when /login was opened directly', async () => {
    const { service, http, oidc, navigation } = setUpApiMode();

    const result = service.signIn(
      { organizationCode: 'anxin', loginName: 'admin', password: 'secret' },
      null,
    );
    const request = http.expectOne('/api/v1/auth/login');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      organizationCode: 'anxin',
      loginName: 'admin',
      password: 'secret',
    });
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush(null, { status: 204, statusText: 'No Content' });

    await expect(result).resolves.toBe('redirecting');
    expect(oidc.signinRedirect).toHaveBeenCalledOnce();
    expect(navigation.assign).not.toHaveBeenCalled();
  });

  it('returns to the pending authorize request when the login page was reached from it', async () => {
    const { service, http, oidc, navigation } = setUpApiMode();
    const returnUrl = '/connect/authorize?client_id=admin-spa&code_challenge=abc';

    const result = service.signIn(
      { organizationCode: null, loginName: 'admin', password: 'secret' },
      returnUrl,
    );
    http.expectOne('/api/v1/auth/login').flush(null, { status: 204, statusText: 'No Content' });

    await expect(result).resolves.toBe('redirecting');
    expect(navigation.assign).toHaveBeenCalledWith(returnUrl);
    expect(oidc.signinRedirect).not.toHaveBeenCalled();
  });

  it('ignores a returnUrl that points anywhere but this site’s authorize endpoint', async () => {
    const { service, http, oidc, navigation } = setUpApiMode();

    const result = service.signIn(
      { organizationCode: null, loginName: 'admin', password: 'secret' },
      '//evil.example/connect/authorize',
    );
    http.expectOne('/api/v1/auth/login').flush(null, { status: 204, statusText: 'No Content' });

    await expect(result).resolves.toBe('redirecting');
    expect(navigation.assign).not.toHaveBeenCalled();
    expect(oidc.signinRedirect).toHaveBeenCalledOnce();
  });

  it('reports wrong credentials without ending any session or redirecting', async () => {
    const { service, http, oidc, navigateByUrl, demoSession } = setUpApiMode();

    const result = service.signIn(
      { organizationCode: 'anxin', loginName: 'admin', password: 'wrong' },
      null,
    );
    http.expectOne('/api/v1/auth/login').flush(null, { status: 401, statusText: 'Unauthorized' });

    await expect(result).resolves.toBe('invalid-credentials');
    expect(oidc.signinRedirect).not.toHaveBeenCalled();
    expect(navigateByUrl).not.toHaveBeenCalled();
    expect(demoSession.sessionExpired()).toBe(false);
  });

  it('reports the server being unavailable separately from wrong credentials', async () => {
    const { service, http } = setUpApiMode();

    const result = service.signIn(
      { organizationCode: 'anxin', loginName: 'admin', password: 'x' },
      null,
    );
    http.expectOne('/api/v1/auth/login').flush(null, { status: 502, statusText: 'Bad Gateway' });

    await expect(result).resolves.toBe('unavailable');
  });

  it('completes the callback, reads /me with the bearer token and enters as the same-role demo account', async () => {
    const { service, http, oidc, demoSession, storage } = setUpApiMode();

    const completion = service.completeSignIn();
    await flushMicrotasks();
    const me = http.expectOne('/api/v1/me');
    expect(me.request.headers.get('Authorization')).toBe('Bearer access-token-1');
    me.flush(ADMIN_ME);

    await expect(completion).resolves.toBe('signed-in');
    expect(oidc.signinRedirectCallback).toHaveBeenCalledWith(
      `${ORIGIN}/auth/callback?code=abc&state=xyz`,
    );
    expect(demoSession.activeAccountId()).toBe('account-smb-admin');
    expect(service.permissions()).toContain('manage-assistants');
    expect(JSON.parse(storage.getItem(API_SESSION_STORAGE_KEY) ?? 'null')).toMatchObject({
      accessToken: 'access-token-1',
      identity: { demoAccountId: 'account-smb-admin', displayName: '安心商行管理者' },
    });
  });

  it('uses the permissions from /me, not the mock account list', async () => {
    const { service, http, demoSession } = setUpApiMode();

    const completion = service.completeSignIn();
    await flushMicrotasks();
    http.expectOne('/api/v1/me').flush({ ...CUSTOMER_ME, permissions: ['read-own-tracking'] });

    await expect(completion).resolves.toBe('signed-in');
    expect(demoSession.activeAccountId()).toBe('account-external-customer');
    expect(service.permissions()).toEqual(['read-own-tracking']);
    expect(service.canEnterWorkspace()).toBe(true);
  });

  it('keeps an account that must change its password out of the workspace', async () => {
    const { service, http, demoSession } = setUpApiMode();

    const completion = service.completeSignIn();
    await flushMicrotasks();
    http.expectOne('/api/v1/me').flush({ ...ADMIN_ME, passwordChangeRequired: true });

    await expect(completion).resolves.toBe('password-change-required');
    expect(service.passwordChangeRequired()).toBe(true);
    expect(service.notice()).toBe('password-change-required');
    expect(demoSession.activeAccountId()).toBeNull();
    expect(service.canEnterWorkspace()).toBe(false);
  });

  it('reports a failed callback and keeps no token', async () => {
    const { service, oidc, storage } = setUpApiMode();
    oidc.signinRedirectCallback.mockRejectedValue(new Error('No matching state found in storage'));

    await expect(service.completeSignIn()).resolves.toBe('failed');
    expect(service.notice()).toBe('sign-in-failed');
    expect(storage.getItem(API_SESSION_STORAGE_KEY)).toBeNull();
  });

  it('restores the identity from this tab after a reload', () => {
    const storage = createMemoryStorage();
    storage.setItem(
      API_SESSION_STORAGE_KEY,
      JSON.stringify({
        accessToken: 'access-token-1',
        idToken: 'id-token-1',
        expiresAt: Date.now() + 60_000,
        identity: {
          demoAccountId: 'account-external-customer',
          displayName: '外部客戶',
          permissions: ['read-own-tracking'],
          passwordChangeRequired: false,
        },
      }),
    );
    const { service } = setUpApiMode({ storage });

    expect(service.permissions()).toEqual(['read-own-tracking']);
  });

  it('sends a visitor with an expired token back to login with the timeout message', () => {
    const storage = createMemoryStorage();
    storage.setItem(
      API_SESSION_STORAGE_KEY,
      JSON.stringify({
        accessToken: 'old',
        idToken: null,
        expiresAt: Date.now() - 1,
        identity: null,
      }),
    );
    const { service, demoSession } = setUpApiMode({ storage });
    demoSession.switchAccount('account-smb-admin');

    expect(service.canEnterWorkspace()).toBe(false);
    expect(demoSession.sessionExpired()).toBe(true);
    expect(storage.getItem(API_SESSION_STORAGE_KEY)).toBeNull();
  });

  it('logs out through the end-session endpoint with the id token as hint', async () => {
    const { service, http, oidc, demoSession, storage } = setUpApiMode();
    const completion = service.completeSignIn();
    await flushMicrotasks();
    http.expectOne('/api/v1/me').flush(ADMIN_ME);
    await completion;

    await service.logout();

    expect(oidc.signoutRedirect).toHaveBeenCalledWith({ id_token_hint: 'id-token-1' });
    expect(demoSession.activeAccountId()).toBeNull();
    expect(storage.getItem(API_SESSION_STORAGE_KEY)).toBeNull();
    expect(service.permissions()).toEqual([]);
    expect(service.canEnterWorkspace()).toBe(false);
  });

  it('still clears the local session and returns to login when end-session cannot start', async () => {
    const { service, oidc, navigateByUrl } = setUpApiMode();
    oidc.signoutRedirect.mockRejectedValue(new Error('discovery failed'));

    await service.logout();

    expect(navigateByUrl).toHaveBeenCalledWith('/login');
  });
});

describe('ApiSessionService (mock mode)', () => {
  function setUpMockMode() {
    const navigateByUrl = vi.fn().mockResolvedValue(true);
    const demoSession = new DemoSessionService({ storage: createMemoryStorage() });
    TestBed.configureTestingModule({
      providers: [
        { provide: Router, useValue: { navigateByUrl } },
        { provide: DemoSessionService, useValue: demoSession },
        { provide: DEMO_REPOSITORY, useValue: new MockDemoRepository(DEMO_SEED) },
      ],
    });
    return { service: TestBed.inject(ApiSessionService), demoSession, navigateByUrl };
  }

  it('is not in API mode and derives permissions from the demo account list', () => {
    const { service, demoSession } = setUpMockMode();
    expect(service.apiMode).toBe(false);
    expect(service.permissions()).toEqual([]);

    demoSession.switchAccount('account-internal-employee');
    expect(service.permissions()).toEqual(['use-shared-assistants', 'read-consented-submissions']);

    demoSession.switchAccount('account-smb-admin');
    expect(service.permissions()).toContain('manage-assistants');
  });

  it('logs out by clearing the demo persona and returning to login', async () => {
    const { service, demoSession, navigateByUrl } = setUpMockMode();
    demoSession.switchAccount('account-smb-admin');

    await service.logout();

    expect(demoSession.activeAccountId()).toBeNull();
    expect(navigateByUrl).toHaveBeenCalledWith('/login');
  });
});
