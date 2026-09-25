import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import type { AccountId } from '../../core/domain/account.model';
import { ApiSessionService } from '../../core/session/api-session.service';
import { DemoSessionService } from '../../core/session/demo-session.service';

interface DemoPersona { readonly accountId: AccountId; readonly username: string; readonly label: string; readonly detail: string; }

/** 上次成功登入的組織代碼；只是方便下次填寫，不是憑證。 */
export const LAST_ORGANIZATION_CODE_KEY = 'admin.last-organization-code';

/** 不區分組織代碼、帳號或密碼哪一個錯，與伺服器的單一 401 一致。 */
export const API_LOGIN_INVALID_MESSAGE = '登入資訊不正確，請確認後再試一次。';
export const API_LOGIN_UNAVAILABLE_MESSAGE = '目前無法登入，請稍後再試。';

const API_NOTICE_MESSAGES = {
  'password-change-required': '這個帳號須先設定新密碼；登入後會自動導到設定新密碼的畫面。',
  'sign-in-failed': '無法完成登入，請重新登入一次。',
} as const;

const API_TIMEOUT_NOTICE = '登入已逾時';
const API_TIMEOUT_DETAIL = '登入已超過時限或已失效，請重新登入。';

function readLastOrganizationCode(): string {
  try {
    return localStorage.getItem(LAST_ORGANIZATION_CODE_KEY) ?? '';
  } catch {
    return '';
  }
}

function rememberOrganizationCode(code: string): void {
  try {
    localStorage.setItem(LAST_ORGANIZATION_CODE_KEY, code);
  } catch {
    // 無痕模式或儲存被封鎖時就不記，下次再手動輸入。
  }
}

@Component({
  selector: 'app-demo-login-page',
  imports: [MatButtonModule],
  templateUrl: './demo-login-page.component.html',
  styleUrl: './demo-login-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DemoLoginPageComponent {
  protected readonly personas: readonly DemoPersona[] = [
    { accountId: 'account-smb-admin', username: 'admin', label: 'SMB 管理者', detail: '管理助理、知識與發布管道' },
    { accountId: 'account-internal-employee', username: 'internal', label: '內部使用者', detail: '使用團隊已分享的助理' },
    { accountId: 'account-external-customer', username: 'customer', label: '外部客戶', detail: '體驗授權表單與個人追蹤' },
  ];
  protected readonly username = signal('');
  protected readonly password = signal('');
  protected readonly loginError = signal('');

  private readonly session = inject(DemoSessionService);
  private readonly apiSession = inject(ApiSessionService);
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);

  /** API 模式：真實登入，不顯示 Demo 身分與固定密碼。 */
  protected readonly apiMode = this.apiSession.apiMode;
  /** 在 `login-options` 回來前先顯示組織代碼欄位；只有一個組織的部署才隱藏。 */
  protected readonly organizationCodeRequired = signal(true);
  protected readonly organizationCode = signal(this.apiMode ? readLastOrganizationCode() : '');
  protected readonly submitting = signal(false);
  protected readonly apiNotice = computed(() => {
    const notice = this.apiSession.notice();
    return notice === null ? null : API_NOTICE_MESSAGES[notice];
  });

  /** 逾時被結束的 Demo 工作階段，在這裡說明原因並讓使用者重新選身分。 */
  protected readonly sessionExpired = this.session.sessionExpired;
  protected readonly timeoutNotice = this.apiMode ? API_TIMEOUT_NOTICE : this.session.timeoutNotice;
  protected readonly timeoutDetail = this.apiMode ? API_TIMEOUT_DETAIL : this.session.timeoutDetail;
  protected readonly timeoutMinutes = this.session.timeoutMinutes;

  constructor() {
    if (this.apiMode) void this.loadLoginOptions();
  }

  protected setOrganizationCode(event: Event): void {
    this.organizationCode.set((event.target as HTMLInputElement).value);
    this.loginError.set('');
  }

  protected setUsername(event: Event): void {
    this.username.set((event.target as HTMLInputElement).value);
    this.loginError.set('');
  }

  protected setPassword(event: Event): void {
    this.password.set((event.target as HTMLInputElement).value);
    this.loginError.set('');
  }

  protected submit(event: Event): void {
    event.preventDefault();
    if (this.apiMode) {
      void this.submitToApi();
      return;
    }
    const persona = this.personas.find((item) => item.username === this.username().trim().toLowerCase());
    if (!persona || this.password() !== '1234') {
      this.loginError.set('帳號或密碼不正確，請使用下方的 Demo 帳號。');
      return;
    }
    this.session.switchAccount(persona.accountId);
    void this.router.navigateByUrl('/app/home');
  }

  private async loadLoginOptions(): Promise<void> {
    try {
      const options = await this.apiSession.loginOptions();
      this.organizationCodeRequired.set(options.organizationCodeRequired);
    } catch {
      // 取不到就維持顯示組織代碼；伺服器在只有一個組織時也接受填了的代碼。
    }
  }

  private async submitToApi(): Promise<void> {
    if (this.submitting()) return;
    const organizationCode = this.organizationCodeRequired() ? this.organizationCode().trim() : null;
    const loginName = this.username().trim();
    const password = this.password();
    if (!loginName || !password || organizationCode === '') {
      this.loginError.set(API_LOGIN_INVALID_MESSAGE);
      return;
    }

    this.submitting.set(true);
    const returnUrl = new URLSearchParams(this.document.location.search).get('returnUrl');
    const result = await this.apiSession.signIn({ organizationCode, loginName, password }, returnUrl);
    if (result === 'redirecting') {
      // 瀏覽器即將離開這頁，按鈕維持停用避免重複送出。
      if (organizationCode !== null) rememberOrganizationCode(organizationCode);
      return;
    }
    this.submitting.set(false);
    this.loginError.set(result === 'invalid-credentials' ? API_LOGIN_INVALID_MESSAGE : API_LOGIN_UNAVAILABLE_MESSAGE);
  }
}
