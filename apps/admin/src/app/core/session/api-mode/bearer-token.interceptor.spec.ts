import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { bearerTokenInterceptor } from './bearer-token.interceptor';
import { HttpSessionBackend, type TokenRefreshOutcome } from './http-session-backend';

function setUp(token: string | null, { expiring = false } = {}) {
  let current = token;
  const backend = {
    accessToken: () => current,
    noteActivity: vi.fn(),
    shouldRefreshBeforeSending: () => expiring,
    refresh: vi.fn(async (): Promise<TokenRefreshOutcome> => {
      current = 'renewed-token';
      expiring = false;
      return 'refreshed';
    }),
  };
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([bearerTokenInterceptor])),
      provideHttpClientTesting(),
      { provide: HttpSessionBackend, useValue: backend },
    ],
  });
  return { http: TestBed.inject(HttpClient), controller: TestBed.inject(HttpTestingController), backend };
}

describe('bearerTokenInterceptor', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('adds the access token to this site’s API requests and counts them as activity', () => {
    const { http, controller, backend } = setUp('token-1');
    http.get('/api/v1/me').subscribe();
    expect(controller.expectOne('/api/v1/me').request.headers.get('Authorization')).toBe(
      'Bearer token-1',
    );
    expect(backend.noteActivity).toHaveBeenCalledOnce();
    expect(backend.refresh).not.toHaveBeenCalled();
  });

  it('never sends the token to another origin', () => {
    const { http, controller } = setUp('token-1');
    http.get('https://other.example/api/v1/me').subscribe();
    expect(
      controller.expectOne('https://other.example/api/v1/me').request.headers.has('Authorization'),
    ).toBe(false);
  });

  it('does not attach the token to the sign-in requests', () => {
    const { http, controller } = setUp('token-1');
    http.post('/api/v1/auth/login', {}).subscribe();
    http.get('/api/v1/auth/login-options').subscribe();
    expect(controller.expectOne('/api/v1/auth/login').request.headers.has('Authorization')).toBe(
      false,
    );
    expect(
      controller.expectOne('/api/v1/auth/login-options').request.headers.has('Authorization'),
    ).toBe(false);
  });

  it('sends the request without a header when there is no usable token', () => {
    const { http, controller } = setUp(null);
    http.get('/api/v1/me').subscribe();
    expect(controller.expectOne('/api/v1/me').request.headers.has('Authorization')).toBe(false);
  });

  it('renews an expiring access token before sending, then sends the new one (#354)', async () => {
    const { http, controller, backend } = setUp('old-token', { expiring: true });
    http.get('/api/v1/me').subscribe();

    // Nothing goes out until the renewal has finished.
    controller.expectNone('/api/v1/me');
    await vi.waitFor(() => controller.expectOne('/api/v1/me'));

    expect(backend.refresh).toHaveBeenCalledOnce();
  });

  it('attaches the renewed token to the request that waited for it', async () => {
    const { http, controller } = setUp('old-token', { expiring: true });
    http.get('/api/v1/team').subscribe();

    const request = await vi.waitFor(() => controller.expectOne('/api/v1/team'));
    expect(request.request.headers.get('Authorization')).toBe('Bearer renewed-token');
  });
});
