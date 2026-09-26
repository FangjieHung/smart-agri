import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable, InjectionToken } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type { components } from '../../api/api-schema';
import type { DemoKeyValueStorage } from '../../repositories/demo-repository';
import { createMemoryStorage } from '../../repositories/memory-storage';
import type {
  ApiChangePasswordFieldErrors,
  ApiChangePasswordResult,
  ApiIdentity,
  ApiLoginCredentials,
  ApiLoginOptions,
  ApiSessionBackend,
  ApiSignInResult,
} from '../api-session.service';
import { DEMO_SESSION_TIMEOUT_MS } from '../demo-session.service';
import { authorizeReturnUrl } from './authorize-return-url';
import { BrowserNavigation } from './browser-navigation';
import { DEMO_ACCOUNT_BY_ROLE } from './demo-identity-bridge';
import { OIDC_CLIENT } from './oidc-client';

type LoginRequest = components['schemas']['LoginRequest'];
type LoginOptionsResponse = components['schemas']['LoginOptionsResponse'];
type MeResponse = components['schemas']['MeResponse'];
type ChangePasswordRequest = components['schemas']['ChangePasswordRequest'];

export const API_LOGIN_PATH = '/api/v1/auth/login';
export const API_LOGIN_OPTIONS_PATH = '/api/v1/auth/login-options';
export const API_ME_PATH = '/api/v1/me';
export const API_CHANGE_PASSWORD_PATH = '/api/v1/auth/change-password';

/** 422 的 ProblemDetails 主體（`ApiErrors.ValidationFailed`）；openapi 沒有型別化內容。 */
interface ValidationProblemBody {
  readonly errors?: ApiChangePasswordFieldErrors;
}

/** 分頁的 sessionStorage key：token 與 `/me` 只活在這個分頁，沿用 Demo「一個分頁一個身分」。 */
export const API_SESSION_STORAGE_KEY = 'api-session';

export const API_SESSION_STORAGE = new InjectionToken<DemoKeyValueStorage>('API_SESSION_STORAGE', {
  providedIn: 'root',
  factory: () => browserSessionStorage() ?? createMemoryStorage(),
});

interface StoredApiSession {
  readonly accessToken: string;
  readonly idToken: string | null;
  /** 毫秒 epoch；到期後不再送出 token，下一次進入工作區就回登入頁。 */
  readonly expiresAt: number;
  readonly identity: ApiIdentity | null;
}

function isStoredApiSession(value: unknown): value is StoredApiSession {
  if (value === null || typeof value !== 'object') return false;
  const record = value as Record<string, unknown>;
  return typeof record['accessToken'] === 'string' && typeof record['expiresAt'] === 'number';
}

/** sessionStorage 在無痕模式或被封鎖時可能丟出例外，取不到就退回記憶體。 */
function browserSessionStorage(): DemoKeyValueStorage | undefined {
  if (typeof sessionStorage === 'undefined') return undefined;
  try {
    sessionStorage.getItem(API_SESSION_STORAGE_KEY);
    return sessionStorage;
  } catch {
    return undefined;
  }
}

function toIdentity(me: MeResponse): ApiIdentity {
  return {
    accountId: me.id,
    demoAccountId: DEMO_ACCOUNT_BY_ROLE[me.role],
    displayName: me.displayName,
    permissions: me.permissions,
    passwordChangeRequired: me.passwordChangeRequired,
  };
}

/**
 * API 模式的登入流程：`POST /auth/login` 取得 Identity cookie → authorize（PKCE）→
 * `/auth/callback` 換 token → `GET /me`。API 與前端同源（開發時經 Angular proxy），
 * cookie 為 SameSite=Strict，所以登入請求不需要 `withCredentials`。
 */
@Injectable()
export class HttpSessionBackend implements ApiSessionBackend {
  private readonly http = inject(HttpClient);
  private readonly navigation = inject(BrowserNavigation);
  private readonly storage = inject(API_SESSION_STORAGE);
  private readonly oidc = inject(OIDC_CLIENT);

  loginOptions(): Promise<ApiLoginOptions> {
    return firstValueFrom(this.http.get<LoginOptionsResponse>(API_LOGIN_OPTIONS_PATH));
  }

  async signIn(
    credentials: ApiLoginCredentials,
    returnUrl: string | null,
  ): Promise<ApiSignInResult> {
    const body: LoginRequest = {
      organizationCode: credentials.organizationCode,
      loginName: credentials.loginName,
      password: credentials.password,
    };
    try {
      await firstValueFrom(this.http.post<void>(API_LOGIN_PATH, body));
    } catch (error) {
      // 伺服器對所有帳密錯誤都回同一個 401，畫面也不區分是哪一欄錯。
      return error instanceof HttpErrorResponse && error.status === 401
        ? 'invalid-credentials'
        : 'unavailable';
    }

    try {
      const authorizeUrl = authorizeReturnUrl(returnUrl, this.navigation.origin());
      // 從 authorize 被導來登入頁：回到原本那個請求；直接開 /login：由這裡發起新的 authorize。
      if (authorizeUrl !== null) this.navigation.assign(authorizeUrl);
      else await (await this.oidc()).signinRedirect();
      return 'redirecting';
    } catch {
      return 'unavailable';
    }
  }

  async completeSignIn(): Promise<ApiIdentity> {
    this.clear();
    const user = await (await this.oidc()).signinRedirectCallback(this.navigation.currentUrl());
    this.write({
      accessToken: user.access_token,
      idToken: user.id_token ?? null,
      expiresAt: user.expires_at ? user.expires_at * 1000 : Date.now() + DEMO_SESSION_TIMEOUT_MS,
      identity: null,
    });
    return this.refreshIdentity();
  }

  async refreshIdentity(): Promise<ApiIdentity> {
    const identity = toIdentity(await firstValueFrom(this.http.get<MeResponse>(API_ME_PATH)));
    const stored = this.read();
    if (stored !== null) this.write({ ...stored, identity });
    return identity;
  }

  restore(): ApiIdentity | null {
    return this.hasUsableToken() ? (this.read()?.identity ?? null) : null;
  }

  /** 攔截器用：過期就不再送出，讓伺服器回 401 走統一的逾時流程。 */
  accessToken(): string | null {
    const stored = this.read();
    return stored !== null && Date.now() < stored.expiresAt ? stored.accessToken : null;
  }

  hasUsableToken(): boolean {
    return this.accessToken() !== null;
  }

  async signOut(): Promise<void> {
    const idToken = this.read()?.idToken ?? undefined;
    this.clear();
    await (await this.oidc()).signoutRedirect({ id_token_hint: idToken });
  }

  clear(): void {
    this.storage.removeItem(API_SESSION_STORAGE_KEY);
  }

  async changePassword(currentPassword: string, newPassword: string): Promise<ApiChangePasswordResult> {
    const body: ChangePasswordRequest = { currentPassword, newPassword };
    try {
      await firstValueFrom(this.http.post<void>(API_CHANGE_PASSWORD_PATH, body));
      return { outcome: 'success' };
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 422) {
        const problem = error.error as ValidationProblemBody | null;
        return { outcome: 'invalid', errors: problem?.errors ?? {} };
      }
      // 401（例如 token 過期）已由攔截器結束工作階段；這裡一律回報無法變更。
      return { outcome: 'unavailable' };
    }
  }

  private write(session: StoredApiSession): void {
    this.storage.setItem(API_SESSION_STORAGE_KEY, JSON.stringify(session));
  }

  private read(): StoredApiSession | null {
    const raw = this.storage.getItem(API_SESSION_STORAGE_KEY);
    if (raw === null) return null;
    try {
      const parsed: unknown = JSON.parse(raw);
      if (isStoredApiSession(parsed)) return parsed;
    } catch {
      // 內容損毀就當作沒有登入。
    }
    this.clear();
    return null;
  }
}
