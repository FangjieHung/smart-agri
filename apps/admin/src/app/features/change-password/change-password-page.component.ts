import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { ApiSessionService } from '../../core/session/api-session.service';

/** 密碼規則的說明文字；實際檢查一律由伺服器把關，這裡只是先讓使用者知道要符合什麼條件。 */
export const CHANGE_PASSWORD_RULES: readonly string[] = [
  '至少 12 個字元',
  '至少一個大寫英文字母',
  '至少一個小寫英文字母',
  '至少一個數字',
  '至少一個符號',
];

export const CHANGE_PASSWORD_INCOMPLETE_MESSAGE = '請完整填寫所有欄位。';
export const CHANGE_PASSWORD_MISMATCH_MESSAGE = '新密碼與確認新密碼不一致，請重新輸入。';
export const CHANGE_PASSWORD_UNAVAILABLE_MESSAGE = '目前無法變更密碼，請稍後再試。';

/**
 * 首次登入（或被要求）改密碼的帳號會被導到這裡；只在 API 模式存在（路由 `canMatch`），
 * 且只有 `passwordChangeRequired` 為 true 時才進得來（`changePasswordGuard`）。
 * 目前密碼錯誤與新密碼不合規則都是 422，逐欄位顯示；token 過期則由全域的 401 攔截器處理。
 */
@Component({
  selector: 'app-change-password-page',
  imports: [MatButtonModule],
  templateUrl: './change-password-page.component.html',
  styleUrl: './change-password-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ChangePasswordPageComponent {
  protected readonly rules = CHANGE_PASSWORD_RULES;

  protected readonly currentPassword = signal('');
  protected readonly newPassword = signal('');
  protected readonly confirmPassword = signal('');
  protected readonly submitting = signal(false);

  protected readonly formError = signal('');
  protected readonly currentPasswordErrors = signal<readonly string[]>([]);
  protected readonly newPasswordErrors = signal<readonly string[]>([]);
  protected readonly confirmError = signal('');

  private readonly apiSession = inject(ApiSessionService);
  private readonly router = inject(Router);

  protected setCurrentPassword(event: Event): void {
    this.currentPassword.set((event.target as HTMLInputElement).value);
    this.formError.set('');
    this.currentPasswordErrors.set([]);
  }

  protected setNewPassword(event: Event): void {
    this.newPassword.set((event.target as HTMLInputElement).value);
    this.formError.set('');
    this.newPasswordErrors.set([]);
    this.confirmError.set('');
  }

  protected setConfirmPassword(event: Event): void {
    this.confirmPassword.set((event.target as HTMLInputElement).value);
    this.formError.set('');
    this.confirmError.set('');
  }

  protected async submit(event: Event): Promise<void> {
    event.preventDefault();
    if (this.submitting()) return;
    this.formError.set('');
    this.currentPasswordErrors.set([]);
    this.newPasswordErrors.set([]);
    this.confirmError.set('');

    const currentPassword = this.currentPassword();
    const newPassword = this.newPassword();
    const confirmPassword = this.confirmPassword();
    if (!currentPassword || !newPassword || !confirmPassword) {
      this.formError.set(CHANGE_PASSWORD_INCOMPLETE_MESSAGE);
      return;
    }
    if (newPassword !== confirmPassword) {
      this.confirmError.set(CHANGE_PASSWORD_MISMATCH_MESSAGE);
      return;
    }

    this.submitting.set(true);
    const result = await this.apiSession.changePassword(currentPassword, newPassword);

    if (result.outcome === 'invalid') {
      this.submitting.set(false);
      this.currentPasswordErrors.set(result.errors.currentPassword ?? []);
      this.newPasswordErrors.set(result.errors.newPassword ?? []);
      return;
    }
    if (result.outcome === 'unavailable') {
      this.submitting.set(false);
      this.formError.set(CHANGE_PASSWORD_UNAVAILABLE_MESSAGE);
      return;
    }

    try {
      // 204：重新讀取 `/me`（此時 passwordChangeRequired 應已為 false）並切換到同角色的 Demo 身分。
      await this.apiSession.completePasswordChange();
      void this.router.navigateByUrl('/app/home', { replaceUrl: true });
    } catch {
      // 401 已由攔截器結束工作階段並導回登入頁，這裡不用再處理，按鈕維持停用即可。
    }
  }
}
