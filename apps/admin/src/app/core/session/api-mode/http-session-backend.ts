import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, inject, Injectable, InjectionToken } from '@angular/core';
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
import { authorizeReturnUrl, workspaceReturnPath } from './authorize-return-url';
import { BrowserNavigation } from './browser-navigation';
import { DEMO_ACCOUNT_BY_ROLE } from './demo-identity-bridge';
import { ADMIN_SPA_CLIENT_ID, OIDC_CLIENT } from './oidc-client';

type LoginRequest = components['schemas']['LoginRequest'];
type LoginOptionsResponse = components['schemas']['LoginOptionsResponse'];
type MeResponse = components['schemas']['MeResponse'];
type ChangePasswordRequest = components['schemas']['ChangePasswordRequest'];

export const API_LOGIN_PATH = '/api/v1/auth/login';
export const API_LOGIN_OPTIONS_PATH = '/api/v1/auth/login-options';
export const API_ME_PATH = '/api/v1/me';
export const API_CHANGE_PASSWORD_PATH = '/api/v1/auth/change-password';
export const TOKEN_PATH = '/connect/token';
export const REVOCATION_PATH = '/connect/revoke';

/** access token 到期前多久就先換發（#354）：主動換發與「送出前換發」都用這個提前量。 */
export const ACCESS_TOKEN_REFRESH_LEAD_MS = 60_000;

/**
 * `refreshed`：已換到新 token；`rejected`：伺服器不再接受這個 refresh token（閒置逾時、登入滿 7 天、
 * 已撤銷、改過密碼），工作階段結束；`unavailable`：伺服器暫時無法回應，工作階段保留、之後再試。
 */
export type TokenRefreshOutcome = 'refreshed' | 'rejected' | 'unavailable';

/** `POST /connect/token` 的回應（OAuth 2.0，不在 openapi 合約裡）。 */
interface TokenEndpointResponse {
  readonly access_token: string;
  readonly id_token?: string;
  readonly refresh_token?: string;
  /** 秒。 */
  readonly expires_in?: number;
}

const FORM_HEADERS = { 'Content-Type': 'application/x-www-form-urlencoded' };

/** 422 的 ProblemDetails 主體（`ApiErrors.ValidationFailed`）；openapi 沒有型別化內容。 */
interface ValidationProblemBody {
  readonly errors?: ApiChangePasswordFieldErrors;
}

/** 分頁的 sessionStorage key：token 與 `/me` 只活在這個分頁，沿用 Demo「一個分頁一個身分」。 */
export const API_SESSION_STORAGE_KEY = 'api-session';

/** 登入完成後要回去的工作區網址（逾時前所在的頁面，#354）；跨過 authorize 的整頁導向，所以也放分頁的 storage。 */
export const API_RETURN_PATH_STORAGE_KEY = 'api-session.return-path';

export const API_SESSION_STORAGE = new InjectionToken<DemoKeyValueStorage>('API_SESSION_STORAGE', {
  providedIn: 'root',
  factory: () => browserSessionStorage() ?? createMemoryStorage(),
});

interface StoredApiSession {
  readonly accessToken: string;
  readonly idToken: string | null;
  /** 毫秒 epoch；到期後不再送出這個 access token，改用 refresh token 換新的。 */
  readonly expiresAt: number;
  /**
   * 換發 access token 用（#354）；每次換發都會換成新的一個（舊的立即失效）。與 access token 一樣只放在
   * 這個分頁的 sessionStorage：XSS 讀得到，取捨見驗證 ADR。伺服器拒絕後就清成 null。
   * 舊版存的工作階段沒有這個欄位，視為 null。
   */
  readonly refreshToken?: string | null;
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
    // 真實 GUID：見 `ApiIdentity.organizationId` 的說明。
    organizationId: me.organization.id,
    statisticsTimeZone: me.statisticsTimeZone,
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

  /** 進行中的換發；同時要換發的請求都等這一個（single-flight），不會各自送出、互相作廢。 */
  private refreshing: Promise<TokenRefreshOutcome> | null = null;
  private refreshTimer: ReturnType<typeof setTimeout> | null = null;
  /** 這組 token 發出後有沒有送過 API 請求；沒有就不主動換發，閒置時間才由伺服器的 8 小時決定。 */
  private activeSinceIssue = false;

  constructor() {
    this.scheduleRefresh(this.read());
    inject(DestroyRef).onDestroy(() => this.cancelRefreshTimer());
  }

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
      const origin = this.navigation.origin();
      const authorizeUrl = authorizeReturnUrl(returnUrl, origin);
      // 從 authorize 被導來登入頁：回到原本那個請求；直接開 /login：由這裡發起新的 authorize，
      // 登入前若在工作區某一頁（逾時被送來登入），登入後回到那一頁。
      if (authorizeUrl !== null) {
        this.navigation.assign(authorizeUrl);
      } else {
        const returnPath = workspaceReturnPath(returnUrl, origin);
        if (returnPath === null) this.storage.removeItem(API_RETURN_PATH_STORAGE_KEY);
        else this.storage.setItem(API_RETURN_PATH_STORAGE_KEY, returnPath);
        await (await this.oidc()).signinRedirect();
      }
      return 'redirecting';
    } catch {
      return 'unavailable';
    }
  }

  async completeSignIn(): Promise<ApiIdentity> {
    this.clear();
    const user = await (await this.oidc()).signinRedirectCallback(this.navigation.currentUrl());
    this.writeTokens({
      accessToken: user.access_token,
      idToken: user.id_token ?? null,
      expiresAt: user.expires_at ? user.expires_at * 1000 : Date.now() + DEMO_SESSION_TIMEOUT_MS,
      refreshToken: user.refresh_token ?? null,
      identity: null,
    });
    return this.refreshIdentity();
  }

  /** 登入完成後要去的工作區網址（只取一次）；沒有就回 null，由呼叫端進首頁。 */
  takeReturnPath(): string | null {
    const path = workspaceReturnPath(
      this.storage.getItem(API_RETURN_PATH_STORAGE_KEY),
      this.navigation.origin(),
    );
    this.storage.removeItem(API_RETURN_PATH_STORAGE_KEY);
    return path;
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

  /** 攔截器用：過期就不再送出（改由 refresh token 換新，或讓伺服器回 401 走統一的逾時流程）。 */
  accessToken(): string | null {
    const stored = this.read();
    return stored !== null && Date.now() < stored.expiresAt ? stored.accessToken : null;
  }

  /** 還有未過期的 access token，或還能換發的 refresh token；真的能不能換發由伺服器決定。 */
  hasUsableToken(): boolean {
    return this.accessToken() !== null || (this.read()?.refreshToken ?? null) !== null;
  }

  /** bearer 攔截器每送出一個 API 請求就呼叫；有活動才會在到期前主動換發。 */
  noteActivity(): void {
    this.activeSinceIssue = true;
  }

  /** access token 已過期或快到期，而且手上有 refresh token：送出請求前先換發。 */
  shouldRefreshBeforeSending(): boolean {
    const stored = this.read();
    return (
      stored !== null &&
      (stored.refreshToken ?? null) !== null &&
      Date.now() >= stored.expiresAt - ACCESS_TOKEN_REFRESH_LEAD_MS
    );
  }

  /** 送出前取得可用的 access token（AG-UI 串流用 `fetch`，不經過攔截器）：快到期就先換發。 */
  async freshAccessToken(): Promise<string | null> {
    this.noteActivity();
    if (this.shouldRefreshBeforeSending()) await this.refresh();
    return this.accessToken();
  }

  /**
   * 收到 401 時呼叫，`sentToken` 是那個請求帶的 access token：若別的請求已經換到新的，直接用新的重送，
   * 不再換發一次；否則換發（與同時收到 401 的其他請求共用同一次）。
   */
  renewAfterUnauthorized(sentToken: string | null): Promise<TokenRefreshOutcome> {
    const current = this.accessToken();
    if (current !== null && current !== sentToken) return Promise.resolve('refreshed');
    return this.refresh();
  }

  /** 用 refresh token 換新的 access token 與 refresh token（single-flight）。 */
  refresh(): Promise<TokenRefreshOutcome> {
    this.refreshing ??= this.exchangeRefreshToken().finally(() => {
      this.refreshing = null;
    });
    return this.refreshing;
  }

  async signOut(): Promise<void> {
    const stored = this.read();
    this.clear();
    // 先在伺服器撤銷 refresh token（end-session 只清登入 cookie），之後就算 token 外洩也換不到新的。
    const refreshToken = stored?.refreshToken ?? null;
    if (refreshToken !== null) await this.revoke(refreshToken);
    await (await this.oidc()).signoutRedirect({ id_token_hint: stored?.idToken ?? undefined });
  }

  clear(): void {
    this.cancelRefreshTimer();
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

  private async exchangeRefreshToken(): Promise<TokenRefreshOutcome> {
    const refreshToken = this.read()?.refreshToken ?? null;
    if (refreshToken === null) return 'rejected';
    const body = new URLSearchParams({
      grant_type: 'refresh_token',
      refresh_token: refreshToken,
      client_id: ADMIN_SPA_CLIENT_ID,
    });
    let response: TokenEndpointResponse;
    try {
      response = await firstValueFrom(
        this.http.post<TokenEndpointResponse>(TOKEN_PATH, body.toString(), { headers: FORM_HEADERS }),
      );
    } catch (error) {
      const rejected = error instanceof HttpErrorResponse && (error.status === 400 || error.status === 401);
      if (!rejected) return 'unavailable';
      // 伺服器不再接受：忘掉它，之後的請求不再嘗試，直接走逾時流程。
      const stored = this.read();
      if (stored !== null) this.write({ ...stored, refreshToken: null });
      return 'rejected';
    }

    // 換發途中已登出或逾時：不要把 token 寫回去。
    const stored = this.read();
    if (stored === null) return 'rejected';
    this.writeTokens({
      ...stored,
      accessToken: response.access_token,
      idToken: response.id_token ?? stored.idToken,
      expiresAt: Date.now() + (response.expires_in ?? DEMO_SESSION_TIMEOUT_MS / 1000) * 1000,
      refreshToken: response.refresh_token ?? null,
    });
    return 'refreshed';
  }

  private async revoke(refreshToken: string): Promise<void> {
    const body = new URLSearchParams({
      token: refreshToken,
      token_type_hint: 'refresh_token',
      client_id: ADMIN_SPA_CLIENT_ID,
    });
    try {
      await firstValueFrom(this.http.post(REVOCATION_PATH, body.toString(), { headers: FORM_HEADERS }));
    } catch {
      // 撤銷失敗也照常登出：本機已清掉，token 最晚 8 小時沒用就失效。
    }
  }

  /** 寫入新發的 token：重新計算主動換發的時間，並從「沒有活動」開始算。 */
  private writeTokens(session: StoredApiSession): void {
    this.write(session);
    this.activeSinceIssue = false;
    this.scheduleRefresh(session);
  }

  /**
   * 到期前 `ACCESS_TOKEN_REFRESH_LEAD_MS` 主動換發，但只在這組 token 發出後有過 API 請求時：分頁開著
   * 卻沒人使用，就讓 token 自然到期，閒置 8 小時後由伺服器結束登入，而不是被計時器一直延長。
   * 之後再有請求時，bearer 攔截器會先換發再送出。
   */
  private scheduleRefresh(session: StoredApiSession | null): void {
    this.cancelRefreshTimer();
    if (session === null || (session.refreshToken ?? null) === null) return;
    const delay = Math.max(0, session.expiresAt - ACCESS_TOKEN_REFRESH_LEAD_MS - Date.now());
    this.refreshTimer = setTimeout(() => {
      this.refreshTimer = null;
      if (this.activeSinceIssue) void this.refresh();
    }, delay);
  }

  private cancelRefreshTimer(): void {
    if (this.refreshTimer === null) return;
    clearTimeout(this.refreshTimer);
    this.refreshTimer = null;
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
