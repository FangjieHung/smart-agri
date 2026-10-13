import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  signal,
  TemplateRef,
  viewChild,
} from '@angular/core';
import { rxResource, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import type { AccountId, AccountPermission, AccountRole } from '../../../../core/domain/account.model';
import {
  ACCOUNT_ROLE_LABELS,
  caseHandOverNote,
  permissionDescriptor,
  SUCCESSOR_REQUIRED_MESSAGE,
  type MemberOffboardingView,
  type MemberTransferCountsView,
  type SuccessorCandidateView,
  type TeamMemberView,
  type TeamView,
} from '../../../../core/domain/team.model';
import { AssistantIssuesRepository } from '../../../../core/repositories/assistant-issues.repository';
import { CasesRepository } from '../../../../core/repositories/cases.repository';
import type {
  CreateMemberResult,
  DeactivateMemberResult,
  ReactivateMemberResult,
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

/** 新增成員對話框的角色選項，依 `ACCOUNT_ROLE_LABELS` 的順序呈現。 */
const CREATABLE_ROLES: readonly AccountRole[] = Object.keys(ACCOUNT_ROLE_LABELS) as AccountRole[];

const LOGIN_NAME_MAX_LENGTH = 64;
const DISPLAY_NAME_MAX_LENGTH = 200;

/**
 * 團隊與權限。**這是 Demo 身分切換器的設定面，不是真實的身分管理**：
 * 沒有邀請、沒有密碼，成員固定就是 `/login` 的三個 Demo 身分。
 * 可以改的只有「每個成員被允許做什麼」，而且畫面會逐條說明哪些權限**真的**
 * 被程式檢查、哪些目前只是宣告值——不在這裡假裝一個還沒接上的開關。
 *
 * API 模式另外提供「新增成員」（issue #52，M2 Slice 18）：只有看得到這個面板的帳號
 * （已經具備 `manage-assistants`）才看得到入口，新成員的一次性密碼只在建立當下顯示一次。
 */
@Component({
  selector: 'app-team-panel',
  imports: [StatePanelComponent, MatDialogModule],
  templateUrl: './team-panel.component.html',
  styleUrl: './team-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TeamPanelComponent {
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly session = inject(DemoSessionService);
  private readonly apiSession = inject(ApiSessionService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly dialog = inject(MatDialog);
  /** 停用成員時，處理事項與案件的 mock 清點與移交（API 模式是後端做的，這兩個方法什麼都不做）。 */
  private readonly issues = inject(AssistantIssuesRepository);
  private readonly cases = inject(CasesRepository);
  /** API 模式的成員是真實帳號，說明文字不再提 Demo 身分與這台瀏覽器。 */
  protected readonly apiMode = inject(API_SESSION_BACKEND) !== null;

  protected readonly creatableRoles = CREATABLE_ROLES;
  protected readonly roleLabels = ACCOUNT_ROLE_LABELS;
  protected readonly loginNameMaxLength = LOGIN_NAME_MAX_LENGTH;
  protected readonly displayNameMaxLength = DISPLAY_NAME_MAX_LENGTH;

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

  // ---------- 新增成員（issue #52） ----------
  private readonly addMemberDialog = viewChild<TemplateRef<unknown>>('addMemberDialog');
  private readonly passwordDialog = viewChild<TemplateRef<unknown>>('passwordDialog');

  protected readonly newLoginName = signal('');
  protected readonly newDisplayName = signal('');
  protected readonly newRole = signal<AccountRole>('internal-employee');
  protected readonly newPermissions = signal<ReadonlySet<AccountPermission>>(new Set());
  protected readonly createError = signal('');
  /** 送出中不能再按一次新增。 */
  protected readonly creatingMember = signal(false);
  /** 剛新增的成員與一次性密碼；只在對話框開著的這段時間存在，關閉後就丟棄。 */
  protected readonly createdMember = signal<{ displayName: string; oneTimePassword: string } | null>(
    null,
  );
  protected readonly passwordCopyFeedback = signal('');

  // ---------- 停用成員並指定接手人（issue #356） ----------
  private readonly deactivateDialog = viewChild<TemplateRef<unknown>>('deactivateDialog');

  /** 正在停用的成員；對話框開著時才有值。 */
  protected readonly offboardingMember = signal<TeamMemberView | null>(null);
  /** 預覽：null 是讀取中，`failed` 是讀不到。 */
  protected readonly offboarding = signal<MemberOffboardingView | 'failed' | null>(null);
  protected readonly successorId = signal<AccountId | null>(null);
  protected readonly deactivateError = signal('');
  protected readonly deactivating = signal(false);
  /** 送出中不能再按一次重新啟用。 */
  protected readonly reactivating = signal<AccountId | null>(null);

  protected readonly eligibleCandidates = computed(() => {
    const preview = this.offboarding();
    return preview === null || preview === 'failed' ? [] : preview.candidates.filter((candidate) => candidate.eligible);
  });

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

  // ---------- 新增成員 ----------

  protected openAddMemberDialog(): void {
    const content = this.addMemberDialog();
    if (!content) return;
    this.newLoginName.set('');
    this.newDisplayName.set('');
    this.newRole.set('internal-employee');
    this.newPermissions.set(new Set());
    this.createError.set('');
    this.dialog.open(content, {
      width: 'min(36rem, calc(100vw - 2rem))',
      autoFocus: '#new-member-login-name',
      restoreFocus: true,
      ariaLabelledBy: 'add-member-title',
    });
  }

  protected closeAddMemberDialog(): void {
    this.dialog.closeAll();
  }

  protected setNewLoginName(event: Event): void {
    this.newLoginName.set((event.target as HTMLInputElement).value);
    this.createError.set('');
  }

  protected setNewDisplayName(event: Event): void {
    this.newDisplayName.set((event.target as HTMLInputElement).value);
    this.createError.set('');
  }

  protected setNewRole(event: Event): void {
    this.newRole.set((event.target as HTMLSelectElement).value as AccountRole);
  }

  protected toggleNewPermission(permission: AccountPermission, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.newPermissions.update((current) => {
      const next = new Set(current);
      if (checked) next.add(permission);
      else next.delete(permission);
      return next;
    });
  }

  protected submitAddMember(event: Event): void {
    event.preventDefault();
    if (this.creatingMember()) return;

    // 與後端相同的必填檢查先在前端擋下，省一次來回；其餘規則（例如重複的登入名稱）
    // 以 repository 的結果為準。
    if (this.newLoginName().trim().length === 0) {
      this.createError.set('請輸入登入名稱。');
      return;
    }
    if (this.newDisplayName().trim().length === 0) {
      this.createError.set('請輸入顯示名稱。');
      return;
    }

    this.creatingMember.set(true);
    this.repository
      .createMember({
        loginName: this.newLoginName().trim(),
        displayName: this.newDisplayName().trim(),
        role: this.newRole(),
        permissions: [...this.newPermissions()],
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => this.memberCreated(result),
        error: () => {
          this.creatingMember.set(false);
          this.createError.set('目前無法新增成員，請稍後再試。');
        },
      });
  }

  private memberCreated(result: CreateMemberResult): void {
    this.creatingMember.set(false);
    if (result.status === 'ready' || result.status === 'partial-failure') {
      this.dialog.closeAll();
      this.teamResource.reload();
      this.feedback.set(`已新增成員「${result.data.member.displayName}」。`);
      this.createdMember.set({
        displayName: result.data.member.displayName,
        oneTimePassword: result.data.oneTimePassword,
      });
      this.passwordCopyFeedback.set('');
      const passwordContent = this.passwordDialog();
      if (passwordContent) {
        this.dialog.open(passwordContent, {
          width: 'min(32rem, calc(100vw - 2rem))',
          restoreFocus: true,
          disableClose: true,
          ariaLabelledBy: 'new-member-password-title',
        });
      }
    } else if (result.status !== 'loading') {
      this.createError.set(result.message);
    }
  }

  protected closePasswordDialog(): void {
    this.dialog.closeAll();
    // 對話框關閉後就不再保留：這是一次性密碼唯一顯示的機會，畫面上不留備份。
    this.createdMember.set(null);
  }

  protected async copyOneTimePassword(password: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(password);
      this.passwordCopyFeedback.set('已複製到剪貼簿。');
    } catch {
      this.passwordCopyFeedback.set('無法自動複製，請手動選取密碼文字。');
    }
  }

  // ---------- 停用成員並指定接手人 ----------

  protected openDeactivateDialog(member: TeamMemberView): void {
    const content = this.deactivateDialog();
    if (!content) return;
    this.offboardingMember.set(member);
    this.offboarding.set(null);
    this.successorId.set(null);
    this.deactivateError.set('');
    this.feedback.set('');
    this.dialog.open(content, {
      width: 'min(40rem, calc(100vw - 2rem))',
      restoreFocus: true,
      ariaModal: true,
      ariaLabelledBy: 'deactivate-member-title',
    });
    this.repository
      .getMemberOffboarding(member.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          if (result.status === 'ready' || result.status === 'partial-failure') {
            // 處理事項與案件在各自的 repository：mock 在這裡補上數量（API 模式加的是 0）。
            const data = result.data;
            this.offboarding.set({
              ...data,
              holdings: {
                ...data.holdings,
                openIssues: data.holdings.openIssues + this.issues.mockOpenAssignedCount(member.id),
                openCases: data.holdings.openCases + this.cases.mockOpenOwnedCount(member.id),
              },
            });
          } else if (result.status !== 'loading') {
            this.offboarding.set('failed');
          }
        },
        error: () => this.offboarding.set('failed'),
      });
  }

  protected closeDeactivateDialog(): void {
    this.dialog.closeAll();
    this.offboardingMember.set(null);
  }

  protected chooseSuccessor(candidate: SuccessorCandidateView): void {
    this.successorId.set(candidate.id);
    this.deactivateError.set('');
  }

  /** 候選人旁邊的說明：為什麼不能選，或選了之後處理事項會怎麼樣。 */
  protected candidateNote(candidate: SuccessorCandidateView, preview: MemberOffboardingView): string {
    if (candidate.ineligibleReason === 'external-customer') return '外部客戶不能接手同仁的工作。';
    if (candidate.ineligibleReason === 'missing-permissions') {
      return `缺少「${candidate.missingPermissions.map((permission) => this.labelOf(permission)).join('」「')}」權限。`;
    }
    if (candidate.ineligibleReason === 'deactivated') return '這個帳號已停用。';
    if (preview.holdings.openIssues > 0 && !candidate.canHandleIssues) {
      return `沒有「處理助理的處理事項」權限：${preview.holdings.openIssues} 件處理事項會改成未指派，由助理擁有者重新指派。`;
    }
    return '';
  }

  /** 接手人需要的權限名稱，例如「管理助理與團隊」「管理資料來源」。 */
  protected requiredLabels(preview: MemberOffboardingView): string {
    return preview.requiredPermissions.map((permission) => `「${permissionDescriptor(permission)?.label ?? permission}」`).join('');
  }

  protected submitDeactivate(event: Event): void {
    event.preventDefault();
    const member = this.offboardingMember();
    const preview = this.offboarding();
    if (member === null || preview === null || preview === 'failed' || this.deactivating()) return;
    const successor = preview.candidates.find((candidate) => candidate.id === this.successorId() && candidate.eligible);
    if (successor === undefined) {
      this.deactivateError.set(SUCCESSOR_REQUIRED_MESSAGE);
      return;
    }

    this.deactivating.set(true);
    this.repository
      .deactivateMember(member.id, successor.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => this.deactivated(member, successor, result),
        error: () => {
          this.deactivating.set(false);
          this.deactivateError.set('目前無法停用成員，請稍後再試。');
        },
      });
  }

  private deactivated(member: TeamMemberView, successor: SuccessorCandidateView, result: DeactivateMemberResult): void {
    this.deactivating.set(false);
    if (result.status === 'ready' || result.status === 'partial-failure') {
      // Demo 模式的處理事項與案件在這裡移交（API 模式後端已經做完，這兩個呼叫什麼都不做）。
      const issues = this.issues.mockHandOverMember(member.id, successor.id, successor.canHandleIssues);
      const cases = this.cases.mockHandOverMember(member.id, successor.id, caseHandOverNote(member.displayName, successor.displayName));
      const transferred = result.data.transferred;
      const counts: MemberTransferCountsView = {
        ...transferred,
        issuesReassigned: transferred.issuesReassigned + issues.reassigned,
        issuesUnassigned: transferred.issuesUnassigned + issues.unassigned,
        cases: transferred.cases + cases,
      };
      this.dialog.closeAll();
      this.offboardingMember.set(null);
      this.error.set('');
      this.feedback.set(this.deactivatedMessage(member.displayName, successor.displayName, counts));
      this.teamResource.reload();
    } else if (result.status === 'validation-failed' || result.status === 'conflict' || result.status === 'permission-denied') {
      this.deactivateError.set(result.message);
    }
  }

  private deactivatedMessage(memberName: string, successorName: string, counts: MemberTransferCountsView): string {
    const parts = [
      counts.assistants > 0 ? `助理 ${counts.assistants} 個` : '',
      counts.drafts > 0 ? `助理草稿 ${counts.drafts} 份` : '',
      counts.knowledgeBases > 0 ? `知識庫 ${counts.knowledgeBases} 個` : '',
      counts.databases > 0 ? `數據庫 ${counts.databases} 個` : '',
      counts.issuesReassigned > 0 ? `處理事項 ${counts.issuesReassigned} 件` : '',
      counts.cases > 0 ? `案件 ${counts.cases} 件` : '',
    ].filter((part) => part !== '');
    const moved = parts.length === 0
      ? `沒有需要轉給「${successorName}」的資產。`
      : `${parts.join('、')}已轉給「${successorName}」。`;
    const unassigned = counts.issuesUnassigned > 0 ? `${counts.issuesUnassigned} 件處理事項改成未指派。` : '';
    return `已停用「${memberName}」：${moved}${unassigned}`;
  }

  protected reactivate(member: TeamMemberView): void {
    if (this.reactivating() !== null) return;
    this.reactivating.set(member.id);
    this.repository
      .reactivateMember(member.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => this.reactivated(member, result),
        error: () => {
          this.reactivating.set(null);
          this.feedback.set('');
          this.error.set('目前無法重新啟用成員，請稍後再試。');
        },
      });
  }

  private reactivated(member: TeamMemberView, result: ReactivateMemberResult): void {
    this.reactivating.set(null);
    if (result.status === 'ready' || result.status === 'partial-failure') {
      this.error.set('');
      this.feedback.set(`已重新啟用「${member.displayName}」，他可以再次登入；停用時轉出的資產不會轉回。`);
      this.teamResource.reload();
    } else if (result.status !== 'loading') {
      this.feedback.set('');
      this.error.set(result.message);
    }
  }
}
