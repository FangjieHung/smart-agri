import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  signal,
} from '@angular/core';
import { rxResource, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import type { AccountId, AccountPermission } from '../../../../core/domain/account.model';
import type { TeamMemberView, TeamView } from '../../../../core/domain/team.model';
import type {
  RepositoryView,
  UpdateMemberPermissionsResult,
} from '../../../../core/repositories/demo-repository';
import { DEMO_REPOSITORY } from '../../../../core/repositories/tokens';
import {
  API_SESSION_BACKEND,
  ApiSessionService,
} from '../../../../core/session/api-session.service';
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
  private readonly apiSession = inject(ApiSessionService);
  private readonly destroyRef = inject(DestroyRef);
  /** API 模式的成員是真實帳號，說明文字不再提 Demo 身分與這台瀏覽器。 */
  protected readonly apiMode = inject(API_SESSION_BACKEND) !== null;

  /**
   * 非同步契約：還沒有 Demo 身分時不讀取（停在 loading），切換身分就重新讀取。
   * `reload()` 期間保留上一份資料，儲存後不會整塊閃回載入中。
   */
  private readonly teamResource = rxResource<RepositoryView<TeamView>, AccountId | undefined>({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: () => this.repository.getTeam(),
    defaultValue: { status: 'loading' },
  });

  /** 讀取失敗（5xx、連線中斷）時為 null；權限不足是正常的 permission-denied 結果。 */
  protected readonly result = computed(() =>
    this.teamResource.hasValue() ? this.teamResource.value() : null,
  );

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
  /** 送出中不能再按一次儲存。 */
  protected readonly saving = signal(false);

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
    if (this.saving()) return;

    this.saving.set(true);
    this.repository
      .updateMemberPermissions(member.id, [...this.draft()])
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => this.saved(member, result),
        error: () => {
          this.saving.set(false);
          this.feedback.set('');
          this.error.set('目前無法儲存權限，請稍後再試。');
        },
      });
  }

  private saved(member: TeamMemberView, result: UpdateMemberPermissionsResult): void {
    this.saving.set(false);
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
      this.teamResource.reload();
      // API 模式的權限來自 `/me`（例如「建立助理」入口）；改到自己時立刻重讀，不等下次啟動。
      if (member.isViewer) void this.apiSession.refreshIdentity();
    } else if (result.status !== 'loading') {
      this.feedback.set('');
      this.error.set(result.message);
    }
  }
}
