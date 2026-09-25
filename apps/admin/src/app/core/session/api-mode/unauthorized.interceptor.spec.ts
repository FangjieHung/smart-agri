import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { ApiSessionService } from '../api-session.service';
import { unauthorizedInterceptor } from './unauthorized.interceptor';

function setUp() {
  const endExpiredSession = vi.fn();
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([unauthorizedInterceptor])),
      provideHttpClientTesting(),
      { provide: ApiSessionService, useValue: { endExpiredSession } },
    ],
  });
  return {
    http: TestBed.inject(HttpClient),
    controller: TestBed.inject(HttpTestingController),
    endExpiredSession,
  };
}

describe('unauthorizedInterceptor', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('ends the session on a 401 from any API endpoint and still reports the error', () => {
    const { http, controller, endExpiredSession } = setUp();
    const error = vi.fn();
    http.get('/api/v1/team').subscribe({ error });

    controller.expectOne('/api/v1/team').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(endExpiredSession).toHaveBeenCalledOnce();
    expect(error).toHaveBeenCalledWith(expect.objectContaining({ status: 401 }));
  });

  it('treats a 401 from the login itself as wrong credentials, not an expired session', () => {
    const { http, controller, endExpiredSession } = setUp();
    http.post('/api/v1/auth/login', {}).subscribe({ error: () => undefined });

    controller
      .expectOne('/api/v1/auth/login')
      .flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(endExpiredSession).not.toHaveBeenCalled();
  });

  it('leaves 403 and other errors to the screen that made the request', () => {
    const { http, controller, endExpiredSession } = setUp();
    http.get('/api/v1/team').subscribe({ error: () => undefined });
    http.get('/api/v1/me').subscribe({ error: () => undefined });

    controller
      .expectOne('/api/v1/team')
      .flush(
        { reason: 'password-change-required', message: '請先設定新密碼' },
        { status: 403, statusText: 'Forbidden' },
      );
    controller.expectOne('/api/v1/me').flush(null, { status: 500, statusText: 'Server Error' });

    expect(endExpiredSession).not.toHaveBeenCalled();
  });

  it('ignores 401s from other origins', () => {
    const { http, controller, endExpiredSession } = setUp();
    http.get('https://other.example/api/v1/me').subscribe({ error: () => undefined });

    controller
      .expectOne('https://other.example/api/v1/me')
      .flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(endExpiredSession).not.toHaveBeenCalled();
  });
});
