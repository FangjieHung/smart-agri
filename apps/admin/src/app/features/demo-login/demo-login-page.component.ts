import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import type { AccountId } from '../../core/domain/account.model';
import { DemoSessionService } from '../../core/session/demo-session.service';

interface DemoPersona { readonly accountId: AccountId; readonly label: string; readonly detail: string; }

@Component({
  selector: 'app-demo-login-page',
  imports: [MatButtonModule],
  templateUrl: './demo-login-page.component.html',
  styleUrl: './demo-login-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DemoLoginPageComponent {
  protected readonly personas: readonly DemoPersona[] = [
    { accountId: 'account-smb-admin', label: 'SMB 管理者', detail: '管理助理、知識與發布管道' },
    { accountId: 'account-internal-employee', label: '內部使用者', detail: '使用團隊已分享的助理' },
    { accountId: 'account-external-customer', label: '外部客戶', detail: '體驗授權表單與個人追蹤' },
  ];

  private readonly session = inject(DemoSessionService);
  private readonly router = inject(Router);

  /** 逾時被結束的 Demo 工作階段，在這裡說明原因並讓使用者重新選身分。 */
  protected readonly sessionExpired = this.session.sessionExpired;
  protected readonly timeoutNotice = this.session.timeoutNotice;
  protected readonly timeoutDetail = this.session.timeoutDetail;
  protected readonly timeoutMinutes = this.session.timeoutMinutes;

  protected selectPersona(accountId: AccountId): void {
    this.session.switchAccount(accountId);
    void this.router.navigateByUrl('/app/home');
  }
}
