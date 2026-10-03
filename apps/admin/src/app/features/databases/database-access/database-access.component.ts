import {
  ChangeDetectionStrategy,
  Component,
  inject,
  input,
  linkedSignal,
  output,
  signal,
} from '@angular/core';
import type { AccountId } from '../../../core/domain/account.model';
import { fmtDateTime } from '../../../core/date-utils';
import type { DatabaseAccessView, DatabaseId } from '../../../core/domain/database.model';
import type { UpdateDatabaseAccessResult } from '../../../core/repositories/demo-repository';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';

/**
 * 資料庫的「權限」頁籤：擁有者指定誰是資料管理者，也就是**誰可以查看收集紀錄與趨勢比較**。
 *
 * 兩層缺一不可，畫面要直說：這裡指定的是資料庫層級的授權，被指定的帳號還必須在
 * 團隊設定有「查看同意提交的紀錄」才真的看得到（候選清單會標出誰還沒有）。
 * 移除只收回查看權限，**不會刪除任何紀錄**，重新指定就原封不動回來。
 */
@Component({
  selector: 'app-database-access',
  templateUrl: './database-access.component.html',
  styleUrl: './database-access.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DatabaseAccessComponent {
  private readonly repository = inject(DEMO_REPOSITORY);

  readonly databaseId = input.required<DatabaseId>();
  readonly access = input.required<DatabaseAccessView>();
  readonly changed = output<void>();

  protected readonly selected = linkedSignal(
    () => new Set<AccountId>(this.access().dataManagers.map((manager) => manager.id)),
  );
  protected readonly feedback = signal('');
  protected readonly error = signal('');
  /** 儲存中不能重複送出。 */
  protected readonly saving = signal(false);
  protected readonly formatTime = fmtDateTime;

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
    if (this.saving()) return;

    const before = this.access().dataManagers.map((manager) => manager.id);
    this.saving.set(true);
    this.repository.updateDatabaseAccess(this.databaseId(), [...this.selected()]).subscribe({
      next: (result) => {
        this.saving.set(false);
        this.applyResult(result, before);
      },
      // 連線中斷或 5xx：保留勾選，讓使用者可以再按一次。
      error: () => {
        this.saving.set(false);
        this.feedback.set('');
        this.error.set('目前無法儲存資料管理者，請稍後再試一次。你的勾選仍保留。');
      },
    });
  }

  private applyResult(result: UpdateDatabaseAccessResult, before: readonly AccountId[]): void {
    if (result.status === 'ready' || result.status === 'partial-failure') {
      const managers = result.data.dataManagers;
      const removed = before.filter(
        (id) => !managers.some((manager) => manager.id === id),
      ).length;
      // 收回查看權限跟刪資料是兩件事：明講紀錄還在。
      const retention =
        removed > 0
          ? `已收回 ${removed} 個帳號的查看權限，收集到的紀錄沒有被刪除，重新指定就會原封不動回來。`
          : '';
      const blocked = managers.filter((manager) => {
        const candidate = result.data.candidates.find((entry) => entry.id === manager.id);
        return candidate !== undefined && !candidate.hasReadPermission;
      });
      const warning =
        blocked.length > 0
          ? `注意：${blocked
              .map((manager) => manager.displayName)
              .join('、')}還沒有「查看同意提交的紀錄」權限，指定了也還是看不到，請到系統設定的團隊與權限開啟。`
          : '';
      const summary =
        managers.length === 0
          ? '已更新資料管理者：目前沒有任何帳號可以查看收集紀錄，包含你自己。'
          : `已更新資料管理者：${managers.map((manager) => manager.displayName).join('、')}可以查看收集紀錄與趨勢比較。`;

      this.error.set('');
      this.feedback.set([summary, retention, warning].filter((part) => part !== '').join(' '));
      this.changed.emit();
    } else if (result.status !== 'loading') {
      this.feedback.set('');
      this.error.set(result.message);
    }
  }
}
