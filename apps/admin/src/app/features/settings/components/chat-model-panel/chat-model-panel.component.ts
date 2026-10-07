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
} from '@angular/core';
import { rxResource, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { SettingRowComponent } from '@smart-agri/ui';
import type { AccountId } from '../../../../core/domain/account.model';
import type { OrganizationChatModelView } from '../../../../core/domain/organization-settings.model';
import type {
  RepositoryView,
  UpdateOrganizationChatModelResult,
} from '../../../../core/repositories/demo-repository';
import { DEMO_REPOSITORY } from '../../../../core/repositories/tokens';
import { DemoSessionService } from '../../../../core/session/demo-session.service';
import { StatePanelComponent } from '../../../../shared/ui/state-panel/state-panel.component';
import { OrganizationSettingsChanges } from '../../organization-settings-changes.service';

/** 選單裡「原本選的模型已不再提供」那一項的值；不是任何模型的 id，也不能被選。 */
export const REMOVED_CHAT_MODEL_OPTION = '__removed__';

/**
 * 系統設定的「對話模型」（issue #240，M6 計畫第 3 節 I）。
 *
 * - 清單只有一個模型、或目前帳號不是管理者：只顯示「目前使用：X」，沒有選單。
 * - 管理者且有多個模型：選單，換了就立即儲存（`PUT`），結果用 `aria-live` 的狀態訊息告知。
 * - 選單的第一項是部署預設；選它送出 `modelId: null`（跟著部署預設走，營運者換預設時一起換）。
 * - `source: removed`：顯示「原本選的模型已不再提供，目前使用部署預設 X」。
 */
@Component({
  selector: 'app-chat-model-panel',
  imports: [DatePipe, SettingRowComponent, StatePanelComponent],
  templateUrl: './chat-model-panel.component.html',
  styleUrl: './chat-model-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ChatModelPanelComponent {
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly session = inject(DemoSessionService);
  private readonly destroyRef = inject(DestroyRef);
  /** 同一頁的其他區塊存好設定時重新讀取（共用 revision，issue #243）。 */
  private readonly settingsChanges = inject(OrganizationSettingsChanges, { optional: true });

  protected readonly removedOption = REMOVED_CHAT_MODEL_OPTION;

  private readonly chatModelResource = rxResource<
    RepositoryView<OrganizationChatModelView>,
    AccountId | undefined
  >({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: () => this.repository.getOrganizationChatModel(),
    defaultValue: { status: 'loading' },
  });

  /** 讀取失敗（5xx、連線中斷）時為 null；權限不足是正常的 permission-denied 結果。 */
  protected readonly result = computed(() =>
    this.chatModelResource.hasValue() ? this.chatModelResource.value() : null,
  );

  protected readonly view = computed(() => {
    const result = this.result();
    return result?.status === 'ready' || result?.status === 'partial-failure' ? result.data : null;
  });

  /** 有多個模型、而且是管理者，才有選單。 */
  protected readonly canChoose = computed(() => {
    const view = this.view();
    return view !== null && view.canChange && view.effective !== null && view.options.length > 1;
  });

  /** 選單目前的值：已移除的選擇停在「已不再提供」那一項，沒選是部署預設（第一項）。 */
  protected readonly selectedValue = computed(() => {
    const view = this.view();
    if (view === null) return '';
    if (view.source === 'removed') return REMOVED_CHAT_MODEL_OPTION;
    if (view.source === 'selected' && view.selectedId !== null) return view.selectedId;
    return view.options[0]?.id ?? '';
  });

  protected readonly feedback = signal('');
  protected readonly error = signal('');
  /** 送出中再換一次選單會被還原，不會同時送出兩個請求。 */
  protected readonly saving = signal(false);

  constructor() {
    effect(() => {
      const change = this.settingsChanges?.saved();
      if (change && change.by !== this) untracked(() => this.chatModelResource.reload());
    });
  }

  protected choose(event: Event): void {
    const select = event.target as HTMLSelectElement;
    const view = this.view();
    if (view === null || this.saving()) {
      select.value = this.selectedValue();
      return;
    }
    const chosenId = select.value;
    const chosen = view.options.find((option) => option.id === chosenId);
    if (chosen === undefined || chosenId === this.selectedValue()) return;

    const modelId = chosen.id === view.options[0]?.id ? null : chosen.id;
    this.saving.set(true);
    this.feedback.set(`正在改用「${chosen.displayName}」…`);
    this.error.set('');
    this.repository
      .updateOrganizationChatModel(modelId, view.revision)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => this.saved(result, select),
        error: () => {
          this.saving.set(false);
          this.feedback.set('');
          this.error.set('目前無法儲存對話模型，請稍後再試。這次沒有變更。');
          select.value = this.selectedValue();
        },
      });
  }

  private saved(result: UpdateOrganizationChatModelResult, select: HTMLSelectElement): void {
    this.saving.set(false);
    if (result.status === 'ready' || result.status === 'partial-failure') {
      this.chatModelResource.set(result);
      this.settingsChanges?.announce(this);
      const effective = result.data.effective;
      this.feedback.set(
        effective === null
          ? '已儲存對話模型。'
          : `已改用「${effective.displayName}」。新的對話、試問與題組重跑會使用這個模型。`,
      );
      return;
    }
    this.feedback.set('');
    if (result.status === 'conflict') {
      this.error.set(`${result.message} 已重新載入最新的設定，這次沒有變更。`);
      this.chatModelResource.reload();
    } else if (result.status === 'validation-failed') {
      this.error.set(`${result.message} 已重新載入清單，這次沒有變更。`);
      this.chatModelResource.reload();
    } else if (result.status === 'permission-denied') {
      this.error.set(result.message);
    }
    select.value = this.selectedValue();
  }
}
