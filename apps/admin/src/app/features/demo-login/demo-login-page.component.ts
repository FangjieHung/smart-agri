import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import type { AccountId } from '../../core/domain/account.model';
import { DemoSessionService } from '../../core/session/demo-session.service';

interface DemoPersona { readonly accountId: AccountId; readonly username: string; readonly label: string; readonly detail: string; }

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
  private readonly router = inject(Router);

  /** 逾時被結束的 Demo 工作階段，在這裡說明原因並讓使用者重新選身分。 */
  protected readonly sessionExpired = this.session.sessionExpired;
  protected readonly timeoutNotice = this.session.timeoutNotice;
  protected readonly timeoutDetail = this.session.timeoutDetail;
  protected readonly timeoutMinutes = this.session.timeoutMinutes;

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
    const persona = this.personas.find((item) => item.username === this.username().trim().toLowerCase());
    if (!persona || this.password() !== '1234') {
      this.loginError.set('帳號或密碼不正確，請使用下方的 Demo 帳號。');
      return;
    }
    this.session.switchAccount(persona.accountId);
    void this.router.navigateByUrl('/app/home');
  }
}
