import { ChangeDetectionStrategy, Component, inject, input, linkedSignal, output, signal } from '@angular/core';
import type { AccountId } from '../../../core/domain/account.model';
import type { PlatformSharingView } from '../../../core/domain/publishing.model';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';

/** 平台內分享：指定可使用助理的帳號。使用者只能操作助理，不能查看設定。 */
@Component({
  selector: 'app-platform-sharing',
  templateUrl: './platform-sharing.component.html',
  styleUrl: './platform-sharing.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PlatformSharingComponent {
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly session = inject(DemoSessionService);

  readonly assistantId = input.required<string>();
  readonly view = input.required<PlatformSharingView>();
  readonly changed = output<void>();

  protected readonly selected = linkedSignal(() => new Set<AccountId>(this.view().allowedAccountIds));
  protected readonly feedback = signal('');
  protected readonly error = signal('');

  protected toggle(accountId: AccountId, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.selected.update((current) => {
      const next = new Set(current);
      if (checked) next.add(accountId);
      else next.delete(accountId);
      return next;
    });
  }

  protected save(event: Event): void {
    event.preventDefault();
    const accountId = this.session.activeAccountId();
    if (!accountId) return;
    const before = this.view().allowedAccountIds;
    const result = this.repository.updatePlatformSharing(accountId, this.assistantId(), [...this.selected()]);
    if (result.status === 'ready' || result.status === 'partial-failure') {
      this.error.set('');
      const removed = before.filter((id) => !result.data.allowedAccountIds.includes(id)).length;
      // 被移除的帳號只是失去權限：明講對話不會被刪掉，重新勾選就回來。
      const retention =
        removed > 0
          ? `取消勾選的 ${removed} 個帳號已失去使用權限，他們既有的對話不會刪除，重新勾選就會回來。`
          : '';
      const summary =
        result.data.allowedAccountIds.length === 0
          ? '已更新可使用的帳號：目前沒有其他帳號可以使用，管道狀態改為尚未設定（你是擁有者，仍然開得了）。'
          : `已更新可使用的帳號：共 ${result.data.allowedAccountIds.length} 個帳號可以使用。`;
      this.feedback.set(`${summary}${retention === '' ? '' : ` ${retention}`}`);
      this.changed.emit();
    } else if (result.status !== 'loading') {
      this.feedback.set('');
      this.error.set(result.message);
    }
  }
}
