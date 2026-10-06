import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { rxResource, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { formatDueHours } from '../../../core/domain/case-due-time';
import type { DatabaseAutoCaseOptionView, DatabaseAutoCaseView } from '../../../core/domain/case-settings.model';
import { CaseSettingsRepository, type DatabaseAutoCaseResult } from '../../../core/repositories/case-settings.repository';
import type { RepositoryView } from '../../../core/repositories/demo-repository';
import { DemoSessionService } from '../../../core/session/demo-session.service';

/** 與後端 `DatabaseAutoCaseRules` 相同：自動開的案件標題與說明，畫面上讓管理者預先知道。 */
export const DATABASE_AUTO_CASE_DESCRIPTION = '由數據庫送出自動建立，內容請開啟紀錄查看。';

/** 承辦組中讀不到紀錄的提示（M7 計畫第 3 節 I）。 */
export function autoCaseReadHint(option: DatabaseAutoCaseOptionView): string {
  if (option.memberCount === 0) return `承辦組「${option.group.name}」目前沒有成員，案件開出後沒有人能受理。`;
  if (option.unreadableMemberCount === 0) return `承辦組「${option.group.name}」的成員都能讀取這個數據庫的紀錄。`;
  return `承辦組中有 ${option.unreadableMemberCount} 人無法讀取這個數據庫的紀錄。`
    + '他們看得到案件，但要開啟紀錄，仍需由擁有者指定為資料管理者，且帳號具備「查看同意提交的紀錄」權限。';
}

/**
 * 數據庫設定的「送出後自動開案」（issue #255，M7 計畫第 3 節 I、決定 M）：只有管理者看得到；
 * 其他人讀取時是 `403 organization-settings`，這個區塊就不顯示。
 *
 * - 選一個啟用中的案件類型，之後每一筆新紀錄送出時，就自動建立一件該類型的案件，交給類型的預設承辦組，
 *   時限依類型。案件沒有建立者、不複製紀錄內容，送出者（可能是外部客戶）也看不到它。
 * - 依所選類型提示承辦組中有幾人讀不到這個數據庫的紀錄：看得到案件不代表讀得到紀錄。
 */
@Component({
  selector: 'app-database-auto-case',
  templateUrl: './database-auto-case.component.html',
  styleUrl: './database-auto-case.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DatabaseAutoCaseComponent {
  private readonly repository = inject(CaseSettingsRepository);
  private readonly session = inject(DemoSessionService);
  private readonly destroyRef = inject(DestroyRef);

  readonly databaseId = input.required<string>();
  /** 目前能讀紀錄的人（以逗號連接的 id；資料管理者變更後它會變，提示的人數就重新讀取）。 */
  readonly readerKey = input('');

  protected readonly formatDue = formatDueHours;
  protected readonly readHint = autoCaseReadHint;
  protected readonly descriptionText = DATABASE_AUTO_CASE_DESCRIPTION;

  private readonly resource = rxResource<RepositoryView<DatabaseAutoCaseView>, { databaseId: string; readers: string } | undefined>({
    params: () => {
      const accountId = this.session.activeAccountId();
      return accountId ? { databaseId: this.databaseId(), readers: `${accountId}|${this.readerKey()}` } : undefined;
    },
    stream: ({ params }) => this.repository.getDatabaseAutoCase(params.databaseId),
    defaultValue: { status: 'loading' },
  });

  /** 只有讀取成功（管理者）才顯示；載入中、非管理者、讀取失敗都不顯示這個區塊。 */
  protected readonly view = computed(() => {
    if (!this.resource.hasValue()) return null;
    const result = this.resource.value();
    return result.status === 'ready' ? result.data : null;
  });

  /** 選單目前的值：類型 id，`''` 是不自動開案。 */
  protected readonly selected = signal('');
  protected readonly saving = signal(false);
  protected readonly feedback = signal('');
  protected readonly error = signal('');

  protected readonly selectedOption = computed(() => this.view()?.options.find((option) => option.id === this.selected()) ?? null);
  protected readonly unchanged = computed(() => (this.view()?.caseTypeId ?? '') === this.selected());

  constructor() {
    effect(() => {
      const view = this.view();
      untracked(() => this.selected.set(view?.caseTypeId ?? ''));
    });
  }

  protected optionLabel(option: DatabaseAutoCaseOptionView): string {
    return `${option.name}（${option.group.name}・${formatDueHours(option.defaultDueHours)}）`;
  }

  protected select(event: Event): void {
    this.selected.set((event.target as HTMLSelectElement).value);
    this.feedback.set('');
    this.error.set('');
  }

  protected save(event: Event): void {
    event.preventDefault();
    if (this.saving() || this.unchanged()) return;
    const caseTypeId = this.selected() || null;
    const name = this.selectedOption()?.name ?? '';
    this.saving.set(true);
    this.feedback.set('');
    this.error.set('');
    this.repository.setDatabaseAutoCase(this.databaseId(), caseTypeId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result: DatabaseAutoCaseResult) => {
          this.saving.set(false);
          if (result.status === 'ready') {
            this.resource.set(result);
            this.feedback.set(caseTypeId ? `已設定：每筆新紀錄送出後，自動建立一件「${name}」案件。` : '已關閉送出後自動開案。');
          } else if (result.status === 'validation-failed' || result.status === 'permission-denied') {
            this.error.set(result.message);
          } else {
            this.error.set('目前無法儲存，請稍後再試。這次沒有變更。');
          }
        },
        error: () => {
          this.saving.set(false);
          this.error.set('目前無法儲存，請稍後再試。這次沒有變更。');
        },
      });
  }
}
