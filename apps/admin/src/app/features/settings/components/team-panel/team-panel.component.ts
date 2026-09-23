import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  signal,
} from '@angular/core';
import type { AccountId, AccountPermission } from '../../../../core/domain/account.model';
import type { TeamMemberView } from '../../../../core/domain/team.model';
import { DEMO_REPOSITORY } from '../../../../core/repositories/tokens';
import { DemoSessionService } from '../../../../core/session/demo-session.service';
import { StatePanelComponent } from '../../../../shared/ui/state-panel/state-panel.component';

/**
 * 團隊與權限。**這是 Demo 身分切換器的設定面，不是真實的身分管理**：
 * 沒有邀請、沒有離職、沒有密碼，成員固定就是 `/login` 的三個 Demo 身分。
 * 可以改的只有「每個成員被允許做什麼」，而且畫面會逐條說明哪些權限**真的**
 * 被程式檢查、哪些目前只是宣告值——不在這裡假裝一個還沒接上的開關。
 */
@Component({
  selector: 'app-team-panel',
  imports: [StatePanelComponent],
  templateUrl: './team-panel.component.html',
  styleUrl: './team-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TeamPanelComponent {
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly session = inject(DemoSessionService);
  /** repository 為同步 mock，異動後遞增此值讓畫面重新讀取。 */
  private readonly revision = signal(0);

  protected readonly result = computed(() => {
    this.revision();
    const accountId = this.session.activeAccountId();
    return accountId ? this.repository.getTeam(accountId) : null;
  });

  protected readonly team = computed(() => {
    const result = this.result();
    return result?.status === 'ready' || result?.status === 'partial-failure'
      ? result.data
      : null;
  });

  /** 正在編輯哪一位成員；null 代表沒有展開任何編輯器。 */
  protected readonly editing = signal<AccountId | null>(null);
  protected readonly draft = signal<ReadonlySet<AccountPermission>>(new Set());
  protected readonly feedback = signal('');
  protected readonly error = signal('');

  protected labelOf(permission: AccountPermission): string {
    return (
      this.team()?.permissions.find((candidate) => candidate.id === permission)?.label ??
      permission
    );
  }

  protected startEditing(member: TeamMemberView): void {
    this.editing.set(member.id);
    this.draft.set(new Set(member.permissions));
    this.feedback.set('');
    this.error.set('');
  }

  protected cancelEditing(): void {
    this.editing.set(null);
    this.error.set('');
  }

  protected toggle(permission: AccountPermission, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.draft.update((current) => {
      const next = new Set(current);
      if (checked) next.add(permission);
      else next.delete(permission);
      return next;
    });
  }

  protected save(member: TeamMemberView, event: Event): void {
    event.preventDefault();
    const accountId = this.session.activeAccountId();
    if (!accountId) return;

    const result = this.repository.updateMemberPermissions(accountId, member.id, [
      ...this.draft(),
    ]);

    if (result.status === 'ready' || result.status === 'partial-failure') {
      const saved =
        result.data.members.find((candidate) => candidate.id === member.id)?.permissions ?? [];
      this.error.set('');
      this.editing.set(null);
      this.feedback.set(
        saved.length === 0
          ? `已更新 ${member.displayName} 的權限：目前沒有任何權限，他只看得到不需要權限的畫面。`
          : `已更新 ${member.displayName} 的權限：${saved
              .map((permission) => this.labelOf(permission))
              .join('、')}。切換到這個身分就會看到差異。`,
      );
      this.revision.update((value) => value + 1);
    } else if (result.status !== 'loading') {
      this.feedback.set('');
      this.error.set(result.message);
    }
  }
}
