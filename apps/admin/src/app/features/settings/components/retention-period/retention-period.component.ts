import { DatePipe, formatDate } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  LOCALE_ID,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { rxResource, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { SettingRowComponent } from '@smart-agri/ui';
import type { AccountId } from '../../../../core/domain/account.model';
import {
  HANDOFF_COPIES_KEPT,
  isShorterRetention,
  RETENTION_BUFFER_DAYS,
  retentionLabel,
  type OrganizationRetentionView,
} from '../../../../core/domain/organization-settings.model';
import type {
  RepositoryView,
  UpdateOrganizationRetentionResult,
} from '../../../../core/repositories/demo-repository';
import { DEMO_REPOSITORY } from '../../../../core/repositories/tokens';
import { DemoSessionService } from '../../../../core/session/demo-session.service';
import { ConfirmDialogComponent } from '../../../../shared/ui/confirm-dialog/confirm-dialog.component';
import { StatePanelComponent } from '../../../../shared/ui/state-panel/state-panel.component';
import { OrganizationSettingsChanges } from '../../organization-settings-changes.service';

/** 選單裡「永久」的值（`days: null`）。 */
export const FOREVER_RETENTION_OPTION = 'forever';

/** 固定說明：處理事項裡的問答副本不受保存期限影響（M6 計畫第 3 節 G）。 */
export const RETENTION_ISSUES_NOTE = `${HANDOFF_COPIES_KEPT}，不受保存期限影響。`;

type SaveKind = 'shorten' | 'extend' | 'revert';

/** 縮短前的確認框：`threadCount` 是預覽的數量；預覽失敗時是 `null`（仍可確認）。 */
interface ShortenConfirmation {
  readonly days: number;
  readonly threadCount: number | null;
}

function optionValue(days: number | null): string {
  return days === null ? FOREVER_RETENTION_OPTION : String(days);
}

/**
 * 系統設定「對話保存」區塊的保存期限（issue #243，M6 計畫第 3 節 F、I）。
 *
 * - 管理者：選單（30、90、180、365 天或永久）。
 *   - 縮短：先讀預覽，在確認框寫出「大約會刪除 N 串對話」與 7 天緩衝期；確認後成為待生效的期限。
 *   - 延長：不確認、立即生效。
 *   - 有待生效的期限時：顯示「將於 M/d 起改為 N 天」與「改回」（送出目前生效的值）。
 * - 其他帳號：唯讀的目前期限與待生效的期限；不呼叫預覽（管理者限定）。
 * - 一律顯示處理事項的固定說明與「上次變更」。結果以 `aria-live` 的狀態訊息告知。
 */
@Component({
  selector: 'app-retention-period',
  imports: [DatePipe, SettingRowComponent, StatePanelComponent, ConfirmDialogComponent],
  templateUrl: './retention-period.component.html',
  styleUrl: './retention-period.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RetentionPeriodComponent {
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly session = inject(DemoSessionService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly locale = inject(LOCALE_ID);
  /** 同一頁的其他區塊存好設定時重新讀取（共用 revision）。 */
  private readonly settingsChanges = inject(OrganizationSettingsChanges, { optional: true });
  private readonly selectElement = viewChild<ElementRef<HTMLSelectElement>>('retentionSelect');

  protected readonly label = retentionLabel;
  protected readonly optionValue = optionValue;
  protected readonly foreverOption = FOREVER_RETENTION_OPTION;
  protected readonly issuesNote = RETENTION_ISSUES_NOTE;
  protected readonly bufferDays = RETENTION_BUFFER_DAYS;

  private readonly retentionResource = rxResource<
    RepositoryView<OrganizationRetentionView>,
    AccountId | undefined
  >({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: () => this.repository.getOrganizationRetention(),
    defaultValue: { status: 'loading' },
  });

  /** 讀取失敗（5xx、連線中斷）時為 null；權限不足是正常的 permission-denied 結果。 */
  protected readonly result = computed(() =>
    this.retentionResource.hasValue() ? this.retentionResource.value() : null,
  );

  protected readonly view = computed(() => {
    const result = this.result();
    return result?.status === 'ready' || result?.status === 'partial-failure' ? result.data : null;
  });

  /** 選單的值：有待生效的期限時是它（管理者剛選的），否則是目前生效的期限。 */
  protected readonly selectedValue = computed(() => {
    const view = this.view();
    if (view === null) return '';
    return optionValue(view.pending !== null ? view.pending.days : view.days);
  });

  protected readonly confirmation = signal<ShortenConfirmation | null>(null);
  protected readonly feedback = signal('');
  protected readonly error = signal('');
  /** 讀預覽或送出中：再換一次選單會被還原，不會同時送出兩個請求。 */
  protected readonly previewing = signal(false);
  protected readonly saving = signal(false);

  constructor() {
    effect(() => {
      const change = this.settingsChanges?.saved();
      if (change && change.by !== this) untracked(() => this.retentionResource.reload());
    });
  }

  protected choose(event: Event): void {
    const select = event.target as HTMLSelectElement;
    const view = this.view();
    if (view === null || !view.canChange || this.previewing() || this.saving() || this.confirmation() !== null) {
      this.resetSelect();
      return;
    }
    if (select.value === this.selectedValue()) return;
    const days = this.parseOption(select.value, view);
    if (days === undefined) {
      this.resetSelect();
      return;
    }
    this.error.set('');
    if (days === view.days) {
      // 有待生效的期限時選回目前的期限，等同「改回」。
      this.save(days, 'revert');
    } else if (days !== null && isShorterRetention(days, view.days)) {
      this.askToShorten(days);
    } else {
      this.save(days, 'extend');
    }
  }

  /** 「改回」：送出目前生效的期限，清掉待生效的期限。 */
  protected revert(): void {
    const view = this.view();
    if (view === null || view.pending === null || this.saving() || this.previewing()) return;
    this.error.set('');
    this.save(view.days, 'revert');
  }

  protected cancelShorten(): void {
    if (this.saving()) return;
    this.confirmation.set(null);
    this.resetSelect();
    this.selectElement()?.nativeElement.focus();
  }

  protected confirmShorten(): void {
    const pending = this.confirmation();
    if (pending === null || this.saving()) return;
    this.save(pending.days, 'shorten');
  }

  private askToShorten(days: number): void {
    this.previewing.set(true);
    this.feedback.set(`正在計算改為 ${retentionLabel(days)} 時大約會刪除的對話數…`);
    this.repository
      .previewOrganizationRetention(days)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.previewing.set(false);
          this.feedback.set('');
          if (result.status === 'ready' || result.status === 'partial-failure') {
            this.confirmation.set({ days, threadCount: result.data.threadCount });
            return;
          }
          if (result.status === 'validation-failed') {
            this.error.set(`${result.message} 已重新載入選項，這次沒有變更。`);
            this.retentionResource.reload();
          } else if (result.status === 'permission-denied') {
            this.error.set(result.message);
          }
          this.resetSelect();
        },
        // 算不出數量（例如連線中斷）時仍讓管理者決定；確認框寫明這次無法計算。
        error: () => {
          this.previewing.set(false);
          this.feedback.set('');
          this.confirmation.set({ days, threadCount: null });
        },
      });
  }

  private save(days: number | null, kind: SaveKind): void {
    const view = this.view();
    if (view === null) return;
    this.saving.set(true);
    this.feedback.set(kind === 'revert' ? `正在改回 ${retentionLabel(days)}…` : `正在改為 ${retentionLabel(days)}…`);
    this.repository
      .updateOrganizationRetention(days, view.revision)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => this.saved(result, kind),
        error: () => {
          this.saving.set(false);
          this.feedback.set('');
          this.error.set('目前無法儲存保存期限，請稍後再試。這次沒有變更。');
          this.finish();
        },
      });
  }

  private saved(result: UpdateOrganizationRetentionResult, kind: SaveKind): void {
    this.saving.set(false);
    if (result.status === 'ready' || result.status === 'partial-failure') {
      this.retentionResource.set(result);
      this.settingsChanges?.announce(this);
      this.feedback.set(this.savedMessage(result.data, kind));
      this.finish();
      return;
    }
    this.feedback.set('');
    if (result.status === 'conflict') {
      this.error.set(`${result.message} 已重新載入最新的設定，這次沒有變更。`);
      this.retentionResource.reload();
    } else if (result.status === 'validation-failed') {
      this.error.set(`${result.message} 已重新載入選項，這次沒有變更。`);
      this.retentionResource.reload();
    } else if (result.status === 'permission-denied') {
      this.error.set(result.message);
    }
    this.finish();
  }

  private savedMessage(view: OrganizationRetentionView, kind: SaveKind): string {
    if (kind === 'shorten' && view.pending !== null) {
      const date = formatDate(view.pending.effectiveAt, 'M/d', this.locale);
      return `已改為 ${retentionLabel(view.pending.days)}，將於 ${date} 起生效。在那之前不會刪除任何對話，也可以改回。`;
    }
    if (kind === 'revert') return `已改回 ${retentionLabel(view.days)}，不會套用待生效的期限。`;
    return `已改為 ${retentionLabel(view.days)}，立即生效。`;
  }

  /** 關掉確認框（若開著就把焦點還給選單），並讓選單回到目前的值。 */
  private finish(): void {
    const wasConfirming = this.confirmation() !== null;
    this.confirmation.set(null);
    this.resetSelect();
    if (wasConfirming) this.selectElement()?.nativeElement.focus();
  }

  private resetSelect(): void {
    const select = this.selectElement()?.nativeElement;
    if (select) select.value = this.selectedValue();
  }

  private parseOption(value: string, view: OrganizationRetentionView): number | null | undefined {
    if (value === FOREVER_RETENTION_OPTION) return null;
    const days = Number(value);
    return view.options.includes(days) ? days : undefined;
  }
}
