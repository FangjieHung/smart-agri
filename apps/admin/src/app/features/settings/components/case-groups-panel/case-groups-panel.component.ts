import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  signal,
  untracked,
  type WritableSignal,
} from '@angular/core';
import { rxResource, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import type { AccountId } from '../../../../core/domain/account.model';
import type {
  CaseGroupListView,
  CaseGroupMemberChangeView,
  CaseGroupView,
} from '../../../../core/domain/case-settings.model';
import {
  CASE_GROUP_NAME_MAX_LENGTH,
  CaseSettingsRepository,
  type CaseGroupMembersResult,
  type CaseGroupNameResult,
} from '../../../../core/repositories/case-settings.repository';
import type { RepositoryView } from '../../../../core/repositories/demo-repository';
import { DemoSessionService } from '../../../../core/session/demo-session.service';
import { OrganizationSettingsChanges } from '../../organization-settings-changes.service';

type Editing =
  | { readonly groupId: string; readonly mode: 'rename' }
  | { readonly groupId: string; readonly mode: 'members' }
  | { readonly groupId: string; readonly mode: 'history' };

const ROLE_LABELS: Readonly<Record<string, string>> = {
  'smb-admin': '管理者',
  'internal-employee': '內部同仁',
};

/**
 * 系統設定的「承辦組」（issue #246，M7 計畫第 3 節 A、J）。只有管理者看得到這個區塊：
 * 其他內部帳號雖然讀得到清單（建立案件時要選），但不在設定頁顯示；外部客戶是 `403 case`。
 *
 * - 建立、改名：名稱 1–40 字、同組織不重複，錯誤顯示在欄位下方。
 * - 封存／取消封存可以來回操作；封存的組不會出現在建立案件、轉組時的選單。是啟用中案件類型的
 *   預設承辦組時不能封存（訊息列出那些類型，issue #247）。
 * - 編輯成員：整份名單一次儲存，候選人只有內部帳號（管理者、內部同仁）。
 * - 異動歷史：每個加入、移除各一筆，最新的在前。
 */
@Component({
  selector: 'app-case-groups-panel',
  imports: [DatePipe],
  templateUrl: './case-groups-panel.component.html',
  styleUrl: './case-groups-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CaseGroupsPanelComponent {
  private readonly repository = inject(CaseSettingsRepository);
  private readonly session = inject(DemoSessionService);
  private readonly destroyRef = inject(DestroyRef);
  /** 承辦組有變更時通知「案件類型」區塊重新讀取（預設承辦組的名稱與可選項目，issue #247）。 */
  private readonly settingsChanges = inject(OrganizationSettingsChanges, { optional: true });

  protected readonly nameMaxLength = CASE_GROUP_NAME_MAX_LENGTH;
  protected readonly roleLabels = ROLE_LABELS;

  private readonly groupsResource = rxResource<RepositoryView<CaseGroupListView>, AccountId | undefined>({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: () => this.repository.listCaseGroups({ includeArchived: true }),
    defaultValue: { status: 'loading' },
  });

  /** 只有管理者才顯示；讀取失敗、外部客戶、非管理者一律不顯示這個區塊。 */
  protected readonly view = computed(() => {
    if (!this.groupsResource.hasValue()) return null;
    const result = this.groupsResource.value();
    return result.status === 'ready' && result.data.canManage ? result.data : null;
  });

  protected readonly newName = signal('');
  protected readonly createError = signal('');
  protected readonly editing = signal<Editing | null>(null);
  protected readonly renameDraft = signal('');
  protected readonly renameError = signal('');
  protected readonly memberDraft = signal<ReadonlySet<string>>(new Set());
  protected readonly history = signal<RepositoryView<readonly CaseGroupMemberChangeView[]> | null>(null);
  protected readonly saving = signal(false);
  protected readonly feedback = signal('');
  protected readonly error = signal('');

  constructor() {
    effect(() => {
      const change = this.settingsChanges?.saved();
      if (change && change.by !== this) untracked(() => this.groupsResource.reload());
    });
  }

  protected isEditing(group: CaseGroupView, mode: Editing['mode']): boolean {
    const editing = this.editing();
    return editing !== null && editing.groupId === group.id && editing.mode === mode;
  }

  protected memberNames(group: CaseGroupView): string {
    return group.members.map((member) => member.displayName).join('、');
  }

  protected onNewName(event: Event): void {
    this.newName.set((event.target as HTMLInputElement).value);
    this.createError.set('');
  }

  protected create(event: Event): void {
    event.preventDefault();
    if (this.saving()) return;
    this.begin();
    this.repository
      .createCaseGroup(this.newName())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.saving.set(false);
          if (this.nameSaved(result, this.createError) && result.status === 'ready') {
            this.feedback.set(`已建立承辦組「${result.data.name}」。`);
            this.newName.set('');
          }
        },
        error: () => this.failed('目前無法建立承辦組，請稍後再試。這次沒有變更。'),
      });
  }

  protected startRename(group: CaseGroupView): void {
    this.editing.set({ groupId: group.id, mode: 'rename' });
    this.renameDraft.set(group.name);
    this.renameError.set('');
  }

  protected onRenameDraft(event: Event): void {
    this.renameDraft.set((event.target as HTMLInputElement).value);
    this.renameError.set('');
  }

  protected rename(group: CaseGroupView, event: Event): void {
    event.preventDefault();
    if (this.saving()) return;
    this.begin();
    this.repository
      .renameCaseGroup(group.id, this.renameDraft())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.saving.set(false);
          if (this.nameSaved(result, this.renameError) && result.status === 'ready') {
            this.editing.set(null);
            this.feedback.set(`已將承辦組改名為「${result.data.name}」。`);
          }
        },
        error: () => this.failed('目前無法改名，請稍後再試。這次沒有變更。'),
      });
  }

  protected setArchived(group: CaseGroupView, archive: boolean): void {
    if (this.saving()) return;
    this.begin();
    this.repository
      .setCaseGroupArchived(group.id, archive)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.saving.set(false);
          if (result.status !== 'ready') {
            this.error.set(result.status === 'permission-denied' || result.status === 'validation-failed'
              ? result.message
              : '目前無法變更，請稍後再試。');
            return;
          }
          this.groupsResource.reload();
          this.settingsChanges?.announce(this);
          this.feedback.set(archive
            ? `已封存「${result.data.name}」。建立案件與轉組時不會再出現這個承辦組。`
            : `已取消封存「${result.data.name}」，可以再被選用。`);
        },
        error: () => this.failed('目前無法變更封存狀態，請稍後再試。這次沒有變更。'),
      });
  }

  protected startMembers(group: CaseGroupView): void {
    this.editing.set({ groupId: group.id, mode: 'members' });
    this.memberDraft.set(new Set(group.members.map((member) => member.id)));
  }

  protected toggleMember(accountId: string, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    const next = new Set(this.memberDraft());
    if (checked) next.add(accountId);
    else next.delete(accountId);
    this.memberDraft.set(next);
  }

  protected saveMembers(group: CaseGroupView, event: Event): void {
    event.preventDefault();
    if (this.saving()) return;
    this.begin();
    this.repository
      .updateCaseGroupMembers(group.id, [...this.memberDraft()])
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => this.membersSaved(result),
        error: () => this.failed('目前無法儲存成員，請稍後再試。這次沒有變更。'),
      });
  }

  protected toggleHistory(group: CaseGroupView): void {
    if (this.isEditing(group, 'history')) {
      this.editing.set(null);
      return;
    }
    this.editing.set({ groupId: group.id, mode: 'history' });
    this.history.set({ status: 'loading' });
    this.repository
      .listCaseGroupMemberChanges(group.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => this.history.set(result),
        error: () => this.history.set(null),
      });
  }

  protected cancelEditing(): void {
    this.editing.set(null);
  }

  private begin(): void {
    this.saving.set(true);
    this.feedback.set('');
    this.error.set('');
  }

  private failed(message: string): void {
    this.saving.set(false);
    this.error.set(message);
  }

  /** 名稱錯誤顯示在欄位下方；成功時重新讀取清單。 */
  private nameSaved(result: CaseGroupNameResult, fieldError: WritableSignal<string>): boolean {
    if (result.status === 'ready') {
      this.groupsResource.reload();
      this.settingsChanges?.announce(this);
      return true;
    }
    if (result.status === 'validation-failed') fieldError.set(result.message);
    else if (result.status === 'permission-denied') this.error.set(result.message);
    else this.error.set('目前無法儲存，請稍後再試。');
    return false;
  }

  private membersSaved(result: CaseGroupMembersResult): void {
    this.saving.set(false);
    if (result.status === 'ready') {
      this.editing.set(null);
      this.groupsResource.reload();
      this.feedback.set(`已儲存「${result.data.name}」的成員（${result.data.members.length} 人）。`);
      return;
    }
    if (result.status === 'conflict') {
      this.error.set(`${result.message} 這次沒有變更。`);
      this.editing.set(null);
      this.groupsResource.reload();
    } else if (result.status === 'validation-failed' || result.status === 'permission-denied') {
      this.error.set(result.message);
    } else {
      this.error.set('目前無法儲存成員，請稍後再試。');
    }
  }
}
