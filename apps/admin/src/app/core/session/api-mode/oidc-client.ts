import { inject, InjectionToken } from '@angular/core';
import { BrowserNavigation } from './browser-navigation';

/** 從 `oidc-client-ts` 用到的部分；測試以假物件替換。 */
export interface OidcClient {
  signinRedirect(): Promise<void>;
  signinRedirectCallback(url: string): Promise<OidcSignInResult>;
  signoutRedirect(args: { id_token_hint?: string }): Promise<void>;
}

export interface OidcSignInResult {
  readonly access_token: string;
  readonly id_token?: string;
  /** 要求了 `offline_access` 才有（#354）。 */
  readonly refresh_token?: string;
  /** 秒為單位的 epoch。 */
  readonly expires_at?: number;
}

/** 由 API 註冊的 public client（`apps/api/README.md` 的 `admin-spa`）。 */
export const ADMIN_SPA_CLIENT_ID = 'admin-spa';

/**
 * 延遲建立 OIDC 用戶端：`oidc-client-ts` 以動態 import 載入，只有真的要登入或登出時才下載。
 * PKCE 的暫存狀態放在分頁的 sessionStorage（回呼一定回到同一個分頁）；
 * token 由 `HttpSessionBackend` 自己保存與換發（refresh token，#354），所以使用者資料只放記憶體，
 * 也不用這個套件的 silent renew。
 */
export const OIDC_CLIENT = new InjectionToken<() => Promise<OidcClient>>('OIDC_CLIENT', {
  providedIn: 'root',
  factory: () => {
    const origin = inject(BrowserNavigation).origin();
    let client: Promise<OidcClient> | undefined;
    return () => (client ??= createOidcClient(origin));
  },
});

async function createOidcClient(origin: string): Promise<OidcClient> {
  const { InMemoryWebStorage, UserManager, WebStorageStateStore } = await import('oidc-client-ts');
  return new UserManager({
    authority: origin,
    client_id: ADMIN_SPA_CLIENT_ID,
    redirect_uri: `${origin}/auth/callback`,
    post_logout_redirect_uri: `${origin}/login`,
    response_type: 'code',
    // offline_access：換到 refresh token，access token 到期前在背景換發（#354）。
    scope: 'openid offline_access',
    loadUserInfo: false,
    automaticSilentRenew: false,
    monitorSession: false,
    stateStore: new WebStorageStateStore({ store: sessionStorage }),
    userStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),
  });
}
