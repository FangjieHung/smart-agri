import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { ApiSessionService } from '../api-session.service';
import { HttpSessionBackend, type TokenRefreshOutcome } from './http-session-backend';
import { unauthorizedInterceptor } from './unauthorized.interceptor';

/** 換發的結果由各測試決定；預設伺服器拒絕（refresh token 已失效）。 */
function setUp(outcome: TokenRefreshOutcome = 'rejected') {
  const endExpiredSession = vi.fn();
  let current: string | null = null;
  const renewAfterUnauthorized = vi.fn(async (): Promise<TokenRefreshOutcome> => {
    if (outcome === 'refreshed') current = 'renewed-token';
    return outcome;
  });
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([unauthorizedInterceptor])),
      provideHttpClientTesting(),
      { provide: ApiSessionService, useValue: { endExpiredSession } },
      {
        provide: HttpSessionBackend,
        useValue: { renewAfterUnauthorized, accessToken: () => current },
      },
    ],
  });
  return {
    http: TestBed.inject(HttpClient),
    controller: TestBed.inject(HttpTestingController),
    endExpiredSession,
    renewAfterUnauthorized,
  };
}

const UNAUTHORIZED = { status: 401, statusText: 'Unauthorized' };

describe('unauthorizedInterceptor', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('ends the session when a 401 cannot be renewed, and still reports the error', async () => {
    const { http, controller, endExpiredSession, renewAfterUnauthorized } = setUp('rejected');
    const error = vi.fn();
    http.get('/api/v1/team').subscribe({ error });

    controller.expectOne('/api/v1/team').flush(null, UNAUTHORIZED);

    await vi.waitFor(() => expect(error).toHaveBeenCalledWith(expect.objectContaining({ status: 401 })));
    expect(renewAfterUnauthorized).toHaveBeenCalledOnce();
    expect(endExpiredSession).toHaveBeenCalledOnce();
  });

  it('renews once and retries the request once with the new token (#354)', async () => {
    const { http, controller, endExpiredSession } = setUp('refreshed');
    const next = vi.fn();
    http.get('/api/v1/team', { headers: { Authorization: 'Bearer old-token' } }).subscribe(next);

    controller.expectOne('/api/v1/team').flush(null, UNAUTHORIZED);
    const retry = await vi.waitFor(() => controller.expectOne('/api/v1/team'));
    expect(retry.request.headers.get('Authorization')).toBe('Bearer renewed-token');
    retry.flush({ members: [] });

    expect(next).toHaveBeenCalledWith({ members: [] });
    expect(endExpiredSession).not.toHaveBeenCalled();
  });

  it('gives up after one retry: a second 401 ends the session without renewing again', async () => {
    const { http, controller, endExpiredSession, renewAfterUnauthorized } = setUp('refreshed');
    const error = vi.fn();
    http.get('/api/v1/team').subscribe({ error });

    controller.expectOne('/api/v1/team').flush(null, UNAUTHORIZED);
    const retry = await vi.waitFor(() => controller.expectOne('/api/v1/team'));
    retry.flush(null, UNAUTHORIZED);

    expect(error).toHaveBeenCalledWith(expect.objectContaining({ status: 401 }));
    expect(renewAfterUnauthorized).toHaveBeenCalledOnce();
    expect(endExpiredSession).toHaveBeenCalledOnce();
  });

  it('keeps the session when the server cannot renew right now (e.g. restarting)', async () => {
    const { http, controller, endExpiredSession } = setUp('unavailable');
    const error = vi.fn();
    http.get('/api/v1/team').subscribe({ error });

    controller.expectOne('/api/v1/team').flush(null, UNAUTHORIZED);

    await vi.waitFor(() => expect(error).toHaveBeenCalledWith(expect.objectContaining({ status: 401 })));
    expect(endExpiredSession).not.toHaveBeenCalled();
  });

  it('treats a 401 from the login itself as wrong credentials, not an expired session', () => {
    const { http, controller, endExpiredSession, renewAfterUnauthorized } = setUp();
    http.post('/api/v1/auth/login', {}).subscribe({ error: () => undefined });

    controller.expectOne('/api/v1/auth/login').flush(null, UNAUTHORIZED);

    expect(renewAfterUnauthorized).not.toHaveBeenCalled();
    expect(endExpiredSession).not.toHaveBeenCalled();
  });

  it('leaves 403 and other errors to the screen that made the request', () => {
    const { http, controller, endExpiredSession, renewAfterUnauthorized } = setUp();
    http.get('/api/v1/team').subscribe({ error: () => undefined });
    http.get('/api/v1/me').subscribe({ error: () => undefined });

    controller
      .expectOne('/api/v1/team')
      .flush(
        { reason: 'password-change-required', message: '請先設定新密碼' },
        { status: 403, statusText: 'Forbidden' },
      );
    controller.expectOne('/api/v1/me').flush(null, { status: 500, statusText: 'Server Error' });

    expect(renewAfterUnauthorized).not.toHaveBeenCalled();
    expect(endExpiredSession).not.toHaveBeenCalled();
  });

  it('ignores 401s from other origins', () => {
    const { http, controller, endExpiredSession, renewAfterUnauthorized } = setUp();
    http.get('https://other.example/api/v1/me').subscribe({ error: () => undefined });

    controller.expectOne('https://other.example/api/v1/me').flush(null, UNAUTHORIZED);

    expect(renewAfterUnauthorized).not.toHaveBeenCalled();
    expect(endExpiredSession).not.toHaveBeenCalled();
  });
});
