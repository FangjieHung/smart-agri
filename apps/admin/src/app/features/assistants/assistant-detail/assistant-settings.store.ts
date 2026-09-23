import { computed, inject, Injectable, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { fmtDateTime } from '../../../core/date-utils';
import type { AccountId } from '../../../core/domain/account.model';
import type {
  AssistantAnswerRules,
  ConnectableSourceView,
} from '../../../core/domain/assistant-draft.model';
import type {
  AssistantSettingsField,
  AssistantSettingsFieldError,
  AssistantSettingsPatch,
  AssistantSettingsView,
} from '../../../core/domain/assistant-settings.model';
import type { UpdateAssistantSettingsResult } from '../../../core/repositories/demo-repository';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import type { AssistantProfileChange } from '../components/assistant-profile-form/assistant-profile-form.component';

type SaveState =
  | { readonly status: 'idle' }
  | { readonly status: 'saved'; readonly savedAt: string }
  | { readonly status: 'error'; readonly message: string };

/**
 * 建立後的助理設定狀態。和建立精靈的草稿 store 差在三點：
 * 沒有步驟、沒有「下一步」，而且每一次變更都直接套用到一個已經有人在用的助理身上——
 * 所以驗證失敗時保留畫面上的舊值並就地顯示錯誤，不會把助理改成不完整的狀態。
 */
@Injectable()
export class AssistantSettingsStore {
  private readonly route = inject(ActivatedRoute);
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly params = toSignal(this.route.paramMap, {
    initialValue: this.route.snapshot.paramMap,
  });
  /** repository 是同步 mock，寫入後遞增此值讓畫面重新讀取。 */
  private readonly revision = signal(0);
  private readonly save = signal<SaveState>({ status: 'idle' });
  private readonly errors = signal<readonly AssistantSettingsFieldError[]>([]);
  private readonly notice = signal('');

  readonly assistantId = computed(() => this.params().get('id') ?? '');

  private readonly result = computed(() => {
    this.revision();
    const accountId = this.session.activeAccountId();
    return accountId
      ? this.repository.getAssistantSettings(accountId, this.assistantId())
      : null;
  });

  readonly settings = computed<AssistantSettingsView | null>(() => {
    const result = this.result();
    return result?.status === 'ready' || result?.status === 'partial-failure'
      ? result.data
      : null;
  });

  readonly canEdit = computed(() => this.settings() !== null);

  readonly deniedMessage = computed(() =>
    this.result()?.status === 'permission-denied'
      ? '你沒有這個助理的設定權限，或它已不存在。'
      : '',
  );

  readonly loading = computed(() => this.result()?.status === 'loading');

  readonly noticeMessage = computed(() => this.notice());

  readonly saveStatusLabel = computed(() => {
    const save = this.save();
    if (save.status === 'error') return save.message;
    if (save.status === 'saved') {
      return save.savedAt === ''
        ? '已自動儲存'
        : `已自動儲存 · ${fmtDateTime(save.savedAt)}`;
    }

    const savedAt = this.settings()?.savedAt ?? null;
    return savedAt === null
      ? '尚未編輯過，設定維持原樣'
      : `上次儲存 · ${fmtDateTime(savedAt)}`;
  });

  /** 已連接與可連接的來源都只包含目前帳號看得到的資源。 */
  readonly connectableSources = computed<readonly ConnectableSourceView[]>(() => {
    this.revision();
    const accountId = this.session.activeAccountId();
    if (!accountId) return [];

    const result = this.repository.listConnectableSources(accountId);
    return result.status === 'ready' || result.status === 'partial-failure'
      ? result.data
      : [];
  });

  /** 只有已連接到這個助理的資料庫可以當寫入對象。 */
  readonly writableDatabases = computed(() =>
    this.connectableSources().flatMap((source) =>
      source.type === 'database' && this.isConnected(source)
        ? [{ id: source.id, name: source.name }]
        : [],
    ),
  );

  fieldError(field: AssistantSettingsField): string | null {
    return this.errors().find((error) => error.field === field)?.message ?? null;
  }

  isConnected(source: ConnectableSourceView): boolean {
    return (this.settings()?.sources ?? []).some(
      (candidate) => candidate.id === source.id && candidate.type === source.type,
    );
  }

  updateProfile(change: AssistantProfileChange): void {
    const patch: AssistantSettingsPatch = {
      ...('name' in change ? { name: change.name } : {}),
      ...('purpose' in change ? { purpose: change.purpose } : {}),
      ...('tone' in change ? { tone: change.tone } : {}),
      ...('roleInstructions' in change
        ? { roleInstructions: change.roleInstructions }
        : {}),
      // 使用對象不可為空，元件已擋下清空的情況；這裡再確認一次。
      ...(change.audience != null ? { audience: change.audience } : {}),
    };

    this.apply((accountId, assistantId) =>
      this.repository.updateAssistantSettings(accountId, assistantId, patch),
    );
  }

  updateRules(patch: Partial<AssistantAnswerRules>): void {
    this.apply((accountId, assistantId) =>
      this.repository.updateAssistantSettings(accountId, assistantId, { rules: patch }),
    );
  }

  /** 加入或解除連接；只送出 id 與類型，不會複製來源內容。 */
  toggleSource(source: ConnectableSourceView): void {
    const connect = !this.isConnected(source);
    const reference =
      source.type === 'knowledge-base'
        ? ({ id: source.id, type: 'knowledge-base' } as const)
        : ({ id: source.id, type: 'database' } as const);

    this.apply((accountId, assistantId) =>
      this.repository.setAssistantSourceConnection(
        accountId,
        assistantId,
        reference,
        connect,
      ),
    );
  }

  /** 由畫面擋下、根本沒有送出的變更；只留一則說明，不改動任何設定。 */
  note(message: string): void {
    this.notice.set(message);
  }

  private apply(
    write: (accountId: AccountId, assistantId: string) => UpdateAssistantSettingsResult,
  ): void {
    const accountId = this.session.activeAccountId();
    if (!accountId) return;

    this.notice.set('');
    const result = write(accountId, this.assistantId());

    if (result.status === 'validation-failed') {
      this.errors.set(result.errors);
      this.save.set({ status: 'error', message: result.message });
      return;
    }

    if (result.status === 'permission-denied') {
      this.errors.set([]);
      this.save.set({ status: 'error', message: result.message });
      this.revision.update((value) => value + 1);
      return;
    }

    if (result.status === 'ready' || result.status === 'partial-failure') {
      this.errors.set([]);
      this.save.set({ status: 'saved', savedAt: result.data.savedAt ?? '' });
      this.revision.update((value) => value + 1);
    }
  }
}
