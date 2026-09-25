import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { bearerTokenInterceptor } from './bearer-token.interceptor';
import { HttpSessionBackend } from './http-session-backend';

function setUp(token: string | null) {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([bearerTokenInterceptor])),
      provideHttpClientTesting(),
      { provide: HttpSessionBackend, useValue: { accessToken: () => token } },
    ],
  });
  return { http: TestBed.inject(HttpClient), controller: TestBed.inject(HttpTestingController) };
}

describe('bearerTokenInterceptor', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('adds the access token to this site’s API requests', () => {
    const { http, controller } = setUp('token-1');
    http.get('/api/v1/me').subscribe();
    expect(controller.expectOne('/api/v1/me').request.headers.get('Authorization')).toBe(
      'Bearer token-1',
    );
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
});
