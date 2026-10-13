import { ChangeDetectionStrategy, Component, computed, DestroyRef, inject, linkedSignal, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { normalizeNotificationEmail, NOTIFICATION_EMAIL_INVALID_MESSAGE } from '../../../../core/domain/handoff.model';
import { HandoffsRepository } from '../../../../core/repositories/handoffs.repository';
import { repositoryResource } from '../../../../core/repositories/repository-resource';
import { DEMO_ACCOUNT_BY_ROLE } from '../../../../core/session/api-mode/demo-identity-bridge';
import { DemoSessionService } from '../../../../core/session/demo-session.service';
import { StatePanelComponent } from '../../../../shared/ui/state-panel/state-panel.component';

/**
 * 系統設定的「我的通知」（issue #355）：每個帳號自己的通知 Email，以及要不要收「轉真人」的 Email。只有組織內部
 * 帳號（外部客戶不會是助理的擁有者）。部署沒有設定寄信伺服器時照樣可以填，畫面說明目前不會寄出。
 */
@Component({
  selector: 'app-notification-settings-panel',
  imports: [StatePanelComponent],
  templateUrl: './notification-settings-panel.component.html',
  styleUrl: './notification-settings-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NotificationSettingsPanelComponent {
  private readonly handoffs = inject(HandoffsRepository);
  private readonly session = inject(DemoSessionService);
  private readonly destroyRef = inject(DestroyRef);

  /** 外部客戶不顯示這個區塊（也不發請求）。 */
  protected readonly visible = computed(() => {
    const account = this.session.activeAccountId();
    return account !== null && account !== DEMO_ACCOUNT_BY_ROLE['external-customer'];
  });

  protected readonly resource = repositoryResource({
    params: () => (this.visible() ? this.session.activeAccountId() ?? undefined : undefined),
    stream: () => this.handoffs.notificationSettings(),
  });
  protected readonly result = this.resource.view;
  protected readonly data = computed(() => {
    const result = this.result();
    return result.status === 'ready' || result.status === 'partial-failure' ? result.data : null;
  });

  /** 表單的值：讀到（或存好）設定時重設，之後跟著使用者輸入。 */
  protected readonly email = linkedSignal(() => this.data()?.email ?? '');
  protected readonly handoffEmails = linkedSignal(() => this.data()?.handoffEmails ?? true);

  protected readonly saving = signal(false);
  protected readonly fieldError = signal('');
  protected readonly feedback = signal('');
  protected readonly error = signal('');

  protected onEmail(event: Event): void {
    this.email.set((event.target as HTMLInputElement).value);
    this.fieldError.set('');
  }

  protected onHandoffEmails(event: Event): void {
    this.handoffEmails.set((event.target as HTMLInputElement).checked);
  }

  protected save(event: Event): void {
    event.preventDefault();
    if (this.saving()) return;
    this.feedback.set('');
    this.error.set('');
    if (!normalizeNotificationEmail(this.email()).valid) {
      this.fieldError.set(NOTIFICATION_EMAIL_INVALID_MESSAGE);
      return;
    }
    this.fieldError.set('');
    this.saving.set(true);
    this.handoffs
      .updateNotificationSettings({ email: this.email().trim() || null, handoffEmails: this.handoffEmails() })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.saving.set(false);
          if (result.status === 'ready') {
            this.feedback.set(result.data.email === null
              ? '已儲存。沒有通知 Email，不會寄信給你。'
              : result.data.handoffEmails ? `已儲存。訪客要找真人時會寄到 ${result.data.email}。` : '已儲存。不會寄轉真人的 Email 給你。');
            this.resource.reload();
          } else if (result.status === 'validation-failed') {
            this.fieldError.set(result.message);
          } else if (result.status === 'permission-denied') {
            this.error.set(result.message);
          }
        },
        error: () => {
          this.saving.set(false);
          this.error.set('目前無法儲存通知設定，請稍後再試。');
        },
      });
  }
}
