import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { fmtDateTime } from '../../../core/date-utils';
import type { DatabaseRecordSource, OwnDatabaseSubmissionView } from '../../../core/domain/database.model';
import { repositoryResource } from '../../../core/repositories/repository-resource';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { StatePanelComponent } from '../../../shared/ui/state-panel/state-panel.component';
import { RECORD_SOURCE_LABELS } from '../../databases/database-labels';

/** 撤回失敗（5xx、連線中斷）：伺服器在同一個交易裡撤回，失敗就什麼都沒變。 */
const WITHDRAW_FAILED_MESSAGE = '目前無法撤回，這筆資料沒有任何變更。請稍後再按一次「確認撤回」。';

/**
 * 「我送出的資料」（issue #146）：目前帳號自己送出過的資料與回執，新到舊，**含已撤回的軌跡**；
 * 每一筆有效的資料都可以由本人撤回（先確認，再送出）。只依提交者本人判斷，別人的不會出現。
 */
@Component({
  selector: 'app-own-submissions',
  imports: [RouterLink, StatePanelComponent],
  templateUrl: './own-submissions.component.html',
  styleUrl: './own-submissions.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OwnSubmissionsComponent {
  private readonly destroyRef = inject(DestroyRef);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly session = inject(DemoSessionService);

  protected readonly resource = repositoryResource({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: () => this.repository.listOwnDatabaseSubmissions(),
  });
  protected readonly view = this.resource.view;
  protected readonly items = computed(() => {
    const result = this.view();
    return result.status === 'ready' || result.status === 'partial-failure' ? result.data : [];
  });

  /** 正在確認撤回的那一筆（同時只有一筆）。 */
  protected readonly confirmingId = signal<string | null>(null);
  /** 撤回送出中的那一筆：按鈕停用，避免重複送出。 */
  protected readonly withdrawingId = signal<string | null>(null);
  protected readonly failure = signal<{ readonly id: string; readonly message: string } | null>(null);
  protected readonly feedback = signal('');

  protected sourceLabel(source: DatabaseRecordSource): string {
    return RECORD_SOURCE_LABELS[source];
  }

  protected time(iso: string): string {
    return fmtDateTime(iso);
  }

  protected askWithdraw(item: OwnDatabaseSubmissionView): void {
    this.failure.set(null);
    this.feedback.set('');
    this.confirmingId.set(item.id);
  }

  protected cancelWithdraw(): void {
    if (this.withdrawingId() !== null) return;
    this.confirmingId.set(null);
    this.failure.set(null);
  }

  protected withdraw(item: OwnDatabaseSubmissionView): void {
    if (this.withdrawingId() !== null) return;
    this.withdrawingId.set(item.id);
    this.failure.set(null);
    this.feedback.set('');

    this.repository
      .withdrawDatabaseSubmission(item.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.withdrawingId.set(null);
          if (result.status === 'ready' || result.status === 'partial-failure') {
            this.confirmingId.set(null);
            this.feedback.set(`已撤回回執 ${result.data.receiptNumber} 的資料，內容已刪除。`);
          } else if (result.status === 'permission-denied') {
            this.failure.set({ id: item.id, message: result.message });
          }
          this.resource.reload();
        },
        error: () => {
          this.withdrawingId.set(null);
          this.failure.set({ id: item.id, message: WITHDRAW_FAILED_MESSAGE });
        },
      });
  }
}
