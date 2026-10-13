import { ChangeDetectionStrategy, Component, inject, type OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { ApiSessionService } from '../../core/session/api-session.service';

/**
 * authorize 導回來的地方：完成 PKCE、讀取 `/me` 後進入後台（逾時前所在的頁面，沒有就是首頁），
 * 失敗就回登入頁說明原因。
 */
@Component({
  selector: 'app-auth-callback-page',
  template: `
    <main class="auth-callback">
      <p role="status">正在完成登入…</p>
    </main>
  `,
  styles: `
    .auth-callback {
      display: grid;
      min-height: 100dvh;
      place-items: center;
      color: var(--color-text-secondary);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AuthCallbackPageComponent implements OnInit {
  private readonly apiSession = inject(ApiSessionService);
  private readonly router = inject(Router);

  async ngOnInit(): Promise<void> {
    const result = await this.apiSession.completeSignIn();
    // 取一次就清掉：須先改密碼或登入失敗時，也不留到下一次登入。
    const returnPath = this.apiSession.takeReturnPath();
    const target =
      result === 'signed-in'
        ? returnPath
        : result === 'password-change-required'
          ? '/change-password'
          : '/login';
    void this.router.navigateByUrl(target, { replaceUrl: true });
  }
}
