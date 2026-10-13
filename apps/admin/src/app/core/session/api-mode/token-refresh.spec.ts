import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting, type TestRequest } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { vi } from 'vitest';
import type { DemoKeyValueStorage } from '../../repositories/demo-repository';
import { DEMO_SEED } from '../../repositories/demo-seed';
import { createMemoryStorage } from '../../repositories/memory-storage';
import { MockDemoRepository } from '../../repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../repositories/tokens';
import { API_SESSION_BACKEND, ApiSessionService } from '../api-session.service';
import { DemoSessionService } from '../demo-session.service';
import { bearerTokenInterceptor } from './bearer-token.interceptor';
import { BrowserNavigation } from './browser-navigation';
import {
  ACCESS_TOKEN_REFRESH_LEAD_MS,
  API_RETURN_PATH_STORAGE_KEY,
  API_SESSION_STORAGE,
  API_SESSION_STORAGE_KEY,
  HttpSessionBackend,
} from './http-session-backend';
import { OIDC_CLIENT, type OidcClient } from './oidc-client';
import { unauthorizedInterceptor } from './unauthorized.interceptor';

/**
 * Refresh tokens in the admin SPA (#354), with the real `HttpSessionBackend`, both interceptors and
 * `ApiSessionService`; only the network (`HttpTestingController`), the OIDC library and the router are fakes.
 */
const ORIGIN = 'http://localhost:4200';
const IDENTITY = {
  accountId: '00000000-0000-0000-0000-000000000001',
  demoAccountId: 'account-smb-admin',
  displayName: '安心商行管理者',
  permissions: [],
  passwordChangeRequired: false,
};
const UNAUTHORIZED = { status: 401, statusText: 'Unauthorized' };

function storedSession(overrides: Record<string, unknown> = {}): string {
  return JSON.stringify({
    accessToken: 'access-1',
    idToken: 'id-1',
    expiresAt: Date.now() + 30 * 60_000,
    refreshToken: 'refresh-1',
    identity: IDENTITY,
    ...overrides,
  });
}

function setUp({ session = storedSession() as string | null, routerUrl = '/app/assistants/a-1' } = {}) {
  const storage: DemoKeyValueStorage = createMemoryStorage();
  if (session !== null) storage.setItem(API_SESSION_STORAGE_KEY, session);
  const oidc = {
    signinRedirect: vi.fn<OidcClient['signinRedirect']>().mockResolvedValue(undefined),
    signinRedirectCallback: vi.fn<OidcClient['signinRedirectCallback']>().mockResolvedValue({
      access_token: 'access-from-code',
      id_token: 'id-from-code',
      refresh_token: 'refresh-from-code',
      expires_at: Math.floor(Date.now() / 1000) + 1800,
    }),
    signoutRedirect: vi.fn<OidcClient['signoutRedirect']>().mockResolvedValue(undefined),
  };
  const navigateByUrl = vi.fn().mockResolvedValue(true);
  const demoSession = new DemoSessionService({ storage: createMemoryStorage() });
  demoSession.switchAccount('account-smb-admin');

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([bearerTokenInterceptor, unauthorizedInterceptor])),
      provideHttpClientTesting(),
      HttpSessionBackend,
      { provide: API_SESSION_BACKEND, useExisting: HttpSessionBackend },
      { provide: API_SESSION_STORAGE, useValue: storage },
      { provide: OIDC_CLIENT, useValue: () => Promise.resolve(oidc) },
      {
        provide: BrowserNavigation,
        useValue: { origin: () => ORIGIN, currentUrl: () => `${ORIGIN}/auth/callback?code=c`, assign: vi.fn() },
      },
      { provide: Router, useValue: { navigateByUrl, url: routerUrl } },
      { provide: DemoSessionService, useValue: demoSession },
      { provide: DEMO_REPOSITORY, useValue: new MockDemoRepository(DEMO_SEED) },
    ],
  });

  return {
    http: TestBed.inject(HttpClient),
    controller: TestBed.inject(HttpTestingController),
    backend: TestBed.inject(HttpSessionBackend),
    apiSession: TestBed.inject(ApiSessionService),
    storage,
    oidc,
    navigateByUrl,
    demoSession,
  };
}

function stored(storage: DemoKeyValueStorage): Record<string, unknown> | null {
  const raw = storage.getItem(API_SESSION_STORAGE_KEY);
  return raw === null ? null : (JSON.parse(raw) as Record<string, unknown>);
}

function tokenRequests(controller: HttpTestingController): TestRequest[] {
  return controller.match('/connect/token');
}

function grantOf(request: TestRequest): URLSearchParams {
  return new URLSearchParams(request.request.body as string);
}

const RENEWED = { access_token: 'access-2', refresh_token: 'refresh-2', expires_in: 1800, token_type: 'Bearer' };

describe('refresh tokens in API mode (#354)', () => {
  afterEach(() => {
    vi.useRealTimers();
    TestBed.inject(HttpTestingController).verify();
  });

  it('renews once for several requests that get 401 at the same time, then retries each once', async () => {
    const { http, controller, storage, navigateByUrl } = setUp();
    const results: unknown[] = [];
    for (const path of ['/api/v1/team', '/api/v1/me', '/api/v1/assistants']) {
      http.get(path).subscribe((body) => results.push(body));
    }
    for (const path of ['/api/v1/team', '/api/v1/me', '/api/v1/assistants']) {
      const first = controller.expectOne(path);
      expect(first.request.headers.get('Authorization')).toBe('Bearer access-1');
      first.flush(null, UNAUTHORIZED);
    }

    const [refresh, ...more] = await vi.waitFor(() => {
      const requests = tokenRequests(controller);
      expect(requests.length).toBeGreaterThan(0);
      return requests;
    });
    expect(more).toHaveLength(0);
    expect(refresh.request.method).toBe('POST');
    expect(refresh.request.headers.get('Content-Type')).toBe('application/x-www-form-urlencoded');
    expect(refresh.request.headers.has('Authorization')).toBe(false);
    expect(Object.fromEntries(grantOf(refresh))).toEqual({
      grant_type: 'refresh_token',
      refresh_token: 'refresh-1',
      client_id: 'admin-spa',
    });
    refresh.flush(RENEWED);

    const retries = await vi.waitFor(() => {
      const requests = ['/api/v1/team', '/api/v1/me', '/api/v1/assistants'].map((path) => controller.expectOne(path));
      return requests;
    });
    for (const retry of retries) {
      expect(retry.request.headers.get('Authorization')).toBe('Bearer access-2');
      retry.flush({ ok: true });
    }

    expect(results).toEqual([{ ok: true }, { ok: true }, { ok: true }]);
    expect(tokenRequests(controller)).toHaveLength(0);
    expect(stored(storage)).toMatchObject({ accessToken: 'access-2', refreshToken: 'refresh-2', identity: IDENTITY });
    expect(navigateByUrl).not.toHaveBeenCalled();
  });

  it('a 401 arriving after another request already renewed retries with the new token, without renewing again', async () => {
    const { http, controller } = setUp();
    http.get('/api/v1/team').subscribe();
    http.get('/api/v1/me').subscribe();
    const team = controller.expectOne('/api/v1/team');
    const me = controller.expectOne('/api/v1/me');

    team.flush(null, UNAUTHORIZED);
    (await vi.waitFor(() => controller.expectOne('/connect/token'))).flush(RENEWED);
    (await vi.waitFor(() => controller.expectOne('/api/v1/team'))).flush({});

    // The old token's second 401 comes back late.
    me.flush(null, UNAUTHORIZED);
    const retry = await vi.waitFor(() => controller.expectOne('/api/v1/me'));
    expect(retry.request.headers.get('Authorization')).toBe('Bearer access-2');
    retry.flush({});
    expect(tokenRequests(controller)).toHaveLength(0);
  });

  it('when the renewal is refused, ends the session and goes to login with the page it was on', async () => {
    const { http, controller, storage, navigateByUrl, demoSession, apiSession } = setUp({
      routerUrl: '/app/assistants/a-1/data-sources?tab=files',
    });
    const error = vi.fn();
    http.get('/api/v1/team').subscribe({ error });
    controller.expectOne('/api/v1/team').flush(null, UNAUTHORIZED);

    (await vi.waitFor(() => controller.expectOne('/connect/token'))).flush(
      { error: 'invalid_grant', error_description: 'The refresh token is no longer valid.' },
      { status: 400, statusText: 'Bad Request' },
    );

    await vi.waitFor(() => expect(error).toHaveBeenCalledWith(expect.objectContaining({ status: 401 })));
    expect(navigateByUrl).toHaveBeenCalledWith(
      '/login?returnUrl=%2Fapp%2Fassistants%2Fa-1%2Fdata-sources%3Ftab%3Dfiles',
    );
    expect(stored(storage)).toBeNull();
    expect(demoSession.sessionExpired()).toBe(true);
    expect(apiSession.canEnterWorkspace()).toBe(false);
  });

  it('keeps the session (and the refresh token) when the token endpoint is temporarily unavailable', async () => {
    const { http, controller, storage, navigateByUrl } = setUp();
    http.get('/api/v1/team').subscribe({ error: () => undefined });
    controller.expectOne('/api/v1/team').flush(null, UNAUTHORIZED);

    (await vi.waitFor(() => controller.expectOne('/connect/token'))).flush(null, {
      status: 503,
      statusText: 'Service Unavailable',
    });

    await vi.waitFor(() => expect(tokenRequests(controller)).toHaveLength(0));
    await Promise.resolve();
    expect(navigateByUrl).not.toHaveBeenCalled();
    expect(stored(storage)).toMatchObject({ refreshToken: 'refresh-1' });
  });

  it('renews an expired access token before sending, so the request never gets a 401', async () => {
    const { http, controller } = setUp({ session: storedSession({ expiresAt: Date.now() - 1 }) });
    const next = vi.fn();
    http.get('/api/v1/team').subscribe(next);

    controller.expectNone('/api/v1/team');
    (await vi.waitFor(() => controller.expectOne('/connect/token'))).flush(RENEWED);
    const request = await vi.waitFor(() => controller.expectOne('/api/v1/team'));
    expect(request.request.headers.get('Authorization')).toBe('Bearer access-2');
    request.flush({ members: [] });

    expect(next).toHaveBeenCalledWith({ members: [] });
  });

  it('still counts a session whose access token expired as usable while it has a refresh token', () => {
    const { apiSession, controller } = setUp({ session: storedSession({ expiresAt: Date.now() - 1 }) });

    expect(apiSession.canEnterWorkspace()).toBe(true);
    controller.verify();
  });

  it('refreshes shortly before the access token expires when the tab was used since it was issued', async () => {
    vi.useFakeTimers();
    const { http, controller, backend, storage } = setUp({
      session: storedSession({ expiresAt: Date.now() + 10 * 60_000 }),
    });
    // Activity: one API request with the current token.
    http.get('/api/v1/team').subscribe();
    controller.expectOne('/api/v1/team').flush({});
    expect(backend.accessToken()).toBe('access-1');

    vi.advanceTimersByTime(10 * 60_000 - ACCESS_TOKEN_REFRESH_LEAD_MS - 1);
    expect(tokenRequests(controller)).toHaveLength(0);
    vi.advanceTimersByTime(1);
    const refresh = controller.expectOne('/connect/token');
    expect(grantOf(refresh).get('refresh_token')).toBe('refresh-1');
    refresh.flush(RENEWED);
    await vi.waitFor(() => expect(stored(storage)).toMatchObject({ accessToken: 'access-2' }));

    // The new token gets its own timer; without activity since, it is left to expire.
    vi.advanceTimersByTime(30 * 60_000);
    expect(tokenRequests(controller)).toHaveLength(0);
  });

  it('does not keep an unused tab signed in: no activity, no proactive refresh', () => {
    vi.useFakeTimers();
    const { controller } = setUp({ session: storedSession({ expiresAt: Date.now() + 10 * 60_000 }) });

    vi.advanceTimersByTime(60 * 60_000);

    expect(tokenRequests(controller)).toHaveLength(0);
  });

  it('keeps the refresh token from the code exchange and returns to the page the sign-in started from', async () => {
    const { apiSession, backend, controller, storage, oidc } = setUp({ session: null });

    const signIn = apiSession.signIn(
      { organizationCode: null, loginName: 'admin', password: 'secret' },
      '/app/assistants/a-1?tab=x',
    );
    controller.expectOne('/api/v1/auth/login').flush(null, { status: 204, statusText: 'No Content' });
    await expect(signIn).resolves.toBe('redirecting');
    expect(oidc.signinRedirect).toHaveBeenCalledOnce();
    expect(storage.getItem(API_RETURN_PATH_STORAGE_KEY)).toBe('/app/assistants/a-1?tab=x');

    const completion = apiSession.completeSignIn();
    (await vi.waitFor(() => controller.expectOne('/api/v1/me'))).flush({
      id: IDENTITY.accountId,
      displayName: IDENTITY.displayName,
      role: 'smb-admin',
      permissions: [],
      organization: { id: '00000000-0000-0000-0000-0000000000aa', name: '安心商行' },
      passwordChangeRequired: false,
      statisticsTimeZone: 'Asia/Taipei',
    });
    await expect(completion).resolves.toBe('signed-in');

    expect(stored(storage)).toMatchObject({ accessToken: 'access-from-code', refreshToken: 'refresh-from-code' });
    expect(apiSession.takeReturnPath()).toBe('/app/assistants/a-1?tab=x');
    expect(apiSession.takeReturnPath()).toBe('/app/home');
    expect(backend.hasUsableToken()).toBe(true);
  });

  it('ignores a return URL outside the workspace', async () => {
    const { apiSession, controller, storage } = setUp({ session: null });
    storage.setItem(API_RETURN_PATH_STORAGE_KEY, '/app/stale');

    const signIn = apiSession.signIn({ organizationCode: null, loginName: 'admin', password: 's' }, 'https://evil.example/app');
    controller.expectOne('/api/v1/auth/login').flush(null, { status: 204, statusText: 'No Content' });
    await signIn;

    expect(storage.getItem(API_RETURN_PATH_STORAGE_KEY)).toBeNull();
    expect(apiSession.takeReturnPath()).toBe('/app/home');
  });

  it('stays signed in after a password change: the same tokens go on and the refresh token still renews (#360)', async () => {
    const { http, controller, apiSession, storage, navigateByUrl } = setUp({
      session: storedSession({ identity: { ...IDENTITY, passwordChangeRequired: true } }),
    });

    const changed = apiSession.changePassword('One-Time-Pass-7x', 'New-Own-Secret-42z');
    const change = controller.expectOne('/api/v1/auth/change-password');
    expect(change.request.headers.get('Authorization')).toBe('Bearer access-1');
    change.flush(null, { status: 204, statusText: 'No Content' });
    await expect(changed).resolves.toEqual({ outcome: 'success' });
    expect(stored(storage)).toMatchObject({ accessToken: 'access-1', refreshToken: 'refresh-1' });

    // The server keeps this sign-in, so its refresh token from before the change renews as usual.
    http.get('/api/v1/team').subscribe();
    controller.expectOne('/api/v1/team').flush(null, UNAUTHORIZED);
    const refresh = await vi.waitFor(() => controller.expectOne('/connect/token'));
    expect(grantOf(refresh).get('refresh_token')).toBe('refresh-1');
    refresh.flush(RENEWED);
    const retry = await vi.waitFor(() => controller.expectOne('/api/v1/team'));
    expect(retry.request.headers.get('Authorization')).toBe('Bearer access-2');
    retry.flush({});

    expect(stored(storage)).toMatchObject({ accessToken: 'access-2', refreshToken: 'refresh-2' });
    expect(navigateByUrl).not.toHaveBeenCalled();
  });

  it('revokes the refresh token on the server when logging out, then ends the sign-in session', async () => {
    const { apiSession, controller, oidc, storage } = setUp();

    const logout = apiSession.logout();
    const revoke = await vi.waitFor(() => controller.expectOne('/connect/revoke'));
    expect(revoke.request.method).toBe('POST');
    expect(Object.fromEntries(grantOf(revoke))).toEqual({
      token: 'refresh-1',
      token_type_hint: 'refresh_token',
      client_id: 'admin-spa',
    });
    expect(stored(storage)).toBeNull();
    expect(oidc.signoutRedirect).not.toHaveBeenCalled();
    revoke.flush(null);
    await logout;

    expect(oidc.signoutRedirect).toHaveBeenCalledWith({ id_token_hint: 'id-1' });
  });

  it('still logs out when the revocation fails', async () => {
    const { apiSession, controller, oidc } = setUp();

    const logout = apiSession.logout();
    (await vi.waitFor(() => controller.expectOne('/connect/revoke'))).flush(null, {
      status: 500,
      statusText: 'Server Error',
    });
    await logout;

    expect(oidc.signoutRedirect).toHaveBeenCalledOnce();
  });

  it('gives the chat stream a renewed token when the current one is about to expire', async () => {
    const { backend, controller } = setUp({ session: storedSession({ expiresAt: Date.now() + 30_000 }) });

    const token = backend.freshAccessToken();
    (await vi.waitFor(() => controller.expectOne('/connect/token'))).flush(RENEWED);

    await expect(token).resolves.toBe('access-2');
    expect(backend.accessToken()).toBe('access-2');
  });
});
