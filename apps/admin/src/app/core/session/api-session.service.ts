import { computed, inject, Injectable, InjectionToken, signal, type Signal } from '@angular/core';
import { Router } from '@angular/router';
import type { AccountId, AccountPermission } from '../domain/account.model';
import { DEMO_REPOSITORY } from '../repositories/tokens';
import { DemoSessionService } from './demo-session.service';

/** 登入頁送出的內容；只有一個組織的部署可以不填組織代碼。 */
export interface ApiLoginCredentials {
  readonly organizationCode: string | null;
  readonly loginName: string;
  readonly password: string;
}

export interface ApiLoginOptions {
  readonly organizationCodeRequired: boolean;
}

/**
 * `/me` 轉成前端要用的樣子。`demoAccountId` 是同角色的 Demo 身分：
 * 其他功能區仍是 mock，靠它沿用現有的 `activeAccountId()` 呼叫點。
 */
export interface ApiIdentity {
  readonly demoAccountId: AccountId;
  readonly displayName: string;
  readonly permissions: readonly AccountPermission[];
  readonly passwordChangeRequired: boolean;
}

/** `redirecting`：瀏覽器即將離開這個頁面去完成 authorize。 */
export type ApiSignInResult = 'redirecting' | 'invalid-credentials' | 'unavailable';

export type ApiSignInCompletion = 'signed-in' | 'password-change-required' | 'failed';

/** 回到登入頁時要說明的原因（逾時說明另由 `DemoSessionService.sessionExpired` 負責）。 */
export type ApiSessionNotice = 'password-change-required' | 'sign-in-failed';

/** 422 的逐欄位訊息；每個欄位可能同時有多則（一個規則一則）。 */
export interface ApiChangePasswordFieldErrors {
  readonly currentPassword?: readonly string[];
  readonly newPassword?: readonly string[];
}

export type ApiChangePasswordResult =
  | { readonly outcome: 'success' }
  | { readonly outcome: 'invalid'; readonly errors: ApiChangePasswordFieldErrors }
  | { readonly outcome: 'unavailable' };

/** API 模式的實作（HTTP、PKCE、token 儲存），只由 `environment.api.ts` 提供。 */
export interface ApiSessionBackend {
  loginOptions(): Promise<ApiLoginOptions>;
  signIn(credentials: ApiLoginCredentials, returnUrl: string | null): Promise<ApiSignInResult>;
  /** 在 `/auth/callback` 完成 PKCE 並讀取 `/me`；失敗時丟出例外。 */
  completeSignIn(): Promise<ApiIdentity>;
  refreshIdentity(): Promise<ApiIdentity>;
  /** 重新整理頁面後，從分頁的 sessionStorage 取回仍有效的身分。 */
  restore(): ApiIdentity | null;
  hasUsableToken(): boolean;
  /** 清除本機工作階段並導向 end-session；瀏覽器會離開這個頁面。 */
  signOut(): Promise<void>;
  clear(): void;
  /** 設定新密碼頁專用；目前密碼錯誤或新密碼不合規則都回 `invalid`，不是例外。 */
  changePassword(currentPassword: string, newPassword: string): Promise<ApiChangePasswordResult>;
}

export const API_SESSION_BACKEND = new InjectionToken<ApiSessionBackend | null>(
  'API_SESSION_BACKEND',
  { providedIn: 'root', factory: () => null },
);

const NO_PERMISSIONS: readonly AccountPermission[] = Object.freeze([]);

/**
 * 登入工作階段的單一入口。mock 模式（GitHub Pages）只是 Demo 身分切換，行為與過去相同；
 * API 模式則是真實登入，成功後把同角色的 Demo 身分交給 `DemoSessionService`。
 */
@Injectable({ providedIn: 'root' })
export class ApiSessionService {
  private readonly backend = inject(API_SESSION_BACKEND);
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly router = inject(Router);

  readonly apiMode = this.backend !== null;

  private readonly identity = signal<ApiIdentity | null>(this.backend?.restore() ?? null);
  private readonly noticeState = signal<ApiSessionNotice | null>(null);
  /** mock 的帳號清單不是 signal；每次進入工作區就重新讀一次，與過去每頁各自讀取相同。 */
  private readonly mockRevision = signal(0);

  readonly notice = this.noticeState.asReadonly();

  /** 須先改密碼的帳號不會進入後台；設定新密碼的頁面由此判斷是否要顯示。 */
  readonly passwordChangeRequired = computed(
    () => this.identity()?.passwordChangeRequired ?? false,
  );

  /** 目前身分的權限：API 模式來自 `/me`，mock 模式來自 `listAccounts()`。 */
  readonly permissions: Signal<readonly AccountPermission[]> = computed(() =>
    this.apiMode ? (this.identity()?.permissions ?? NO_PERMISSIONS) : this.mockPermissions(),
  );

  private readonly mockPermissions = computed(() => {
    this.mockRevision();
    const accountId = this.session.activeAccountId();
    if (!accountId) return NO_PERMISSIONS;
    const accounts = this.repository.listAccounts();
    if (accounts.status !== 'ready') return NO_PERMISSIONS;
    return accounts.data.find((account) => account.id === accountId)?.permissions ?? NO_PERMISSIONS;
  });

  loginOptions(): Promise<ApiLoginOptions> {
    return this.backend?.loginOptions() ?? Promise.resolve({ organizationCodeRequired: false });
  }

  signIn(credentials: ApiLoginCredentials, returnUrl: string | null): Promise<ApiSignInResult> {
    this.noticeState.set(null);
    return this.backend?.signIn(credentials, returnUrl) ?? Promise.resolve('unavailable');
  }

  async completeSignIn(): Promise<ApiSignInCompletion> {
    if (this.backend === null) return 'failed';
    try {
      const identity = await this.backend.completeSignIn();
      this.identity.set(identity);
      if (identity.passwordChangeRequired) {
        // token 保留給設定新密碼使用，但不交出 Demo 身分，工作區守衛因此會擋下。
        this.noticeState.set('password-change-required');
        return 'password-change-required';
      }
      this.noticeState.set(null);
      this.session.switchAccount(identity.demoAccountId);
      return 'signed-in';
    } catch {
      this.discardApiSession();
      // `/me` 回 401 時攔截器已顯示逾時說明，不再疊一則失敗訊息。
      if (!this.session.sessionExpired()) this.noticeState.set('sign-in-failed');
      return 'failed';
    }
  }

  /** 啟動時在背景重讀 `/me`，讓權限跟上伺服器（權限不在 token 裡）。 */
  async refreshIdentity(): Promise<void> {
    if (this.backend === null || this.identity() === null || !this.backend.hasUsableToken()) return;
    try {
      this.identity.set(await this.backend.refreshIdentity());
    } catch {
      // 401 由攔截器處理；其他錯誤先沿用上次的 `/me`。
    }
  }

  /** 設定新密碼頁呼叫；422 的逐欄位訊息交由呼叫端顯示。 */
  changePassword(currentPassword: string, newPassword: string): Promise<ApiChangePasswordResult> {
    return this.backend?.changePassword(currentPassword, newPassword) ?? Promise.resolve({ outcome: 'unavailable' });
  }

  /**
   * 改密碼成功後呼叫：重新讀取 `/me`（`passwordChangeRequired` 應已為 false），
   * 再把同角色的 Demo 身分交給 `DemoSessionService`，讓工作區守衛放行。
   */
  async completePasswordChange(): Promise<void> {
    if (this.backend === null) return;
    const identity = await this.backend.refreshIdentity();
    this.identity.set(identity);
    this.noticeState.set(null);
    this.session.switchAccount(identity.demoAccountId);
  }

  /** 工作區守衛：mock 只看 Demo 身分與閒置時間，API 模式還要有未過期的 token。 */
  canEnterWorkspace(): boolean {
    const demoActive = this.session.refreshActivity();
    if (this.backend === null) {
      if (demoActive) this.mockRevision.update((value) => value + 1);
      return demoActive;
    }
    // 須先改密碼：token 保留給設定新密碼頁使用，守衛會導去那裡而不是在這裡清掉工作階段。
    if (this.passwordChangeRequired()) return false;
    if (!demoActive) {
      this.discardApiSession();
      return false;
    }
    if (this.identity() === null || !this.backend.hasUsableToken()) {
      this.discardApiSession();
      this.session.expireSession();
      return false;
    }
    return true;
  }

  /** 任何 API 回 401：清除工作階段，回登入頁並顯示既有的逾時說明。 */
  endExpiredSession(): void {
    this.discardApiSession();
    this.session.expireSession();
    void this.router.navigateByUrl('/login');
  }

  async logout(): Promise<void> {
    this.session.clearSession();
    this.noticeState.set(null);
    if (this.backend === null) {
      void this.router.navigateByUrl('/login');
      return;
    }
    this.identity.set(null);
    try {
      await this.backend.signOut();
    } catch {
      // end-session 無法開始（例如 API 沒有回應）時，至少把本機工作階段清乾淨。
      this.backend.clear();
      void this.router.navigateByUrl('/login');
    }
  }

  private discardApiSession(): void {
    this.backend?.clear();
    this.identity.set(null);
  }
}
