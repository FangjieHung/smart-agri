import { computed, DestroyRef, inject, Injectable, linkedSignal, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import type { Observable } from 'rxjs';
import { fmtDateTime } from '../../../core/date-utils';
import type {
  AssistantAnswerRules,
  ConnectableSourceView,
} from '../../../core/domain/assistant-draft.model';
import type { AssistantSourceReference } from '../../../core/domain/assistant.model';
import type {
  AssistantSettingsField,
  AssistantSettingsFieldError,
  AssistantSettingsPatch,
  AssistantSettingsView,
} from '../../../core/domain/assistant-settings.model';
import type { UpdateAssistantSettingsResult } from '../../../core/repositories/demo-repository';
import { repositoryResource, type LoadedView } from '../../../core/repositories/repository-resource';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import type { AssistantProfileChange } from '../components/assistant-profile-form/assistant-profile-form.component';

type SaveState =
  | { readonly status: 'idle' }
  | { readonly status: 'saved'; readonly savedAt: string }
  | { readonly status: 'error'; readonly message: string };

/** 排隊中的一次寫入：欄位變更可以合併，來源連接一次一個。 */
type SettingsWrite =
  | { readonly kind: 'patch'; readonly patch: AssistantSettingsPatch }
  | { readonly kind: 'source'; readonly source: AssistantSourceReference; readonly connect: boolean };

const SAVE_FAILED_MESSAGE = '目前無法儲存這項變更，請稍後再試。';

/** 把一次變更套到畫面上的設定（樂觀更新）；`rules` 只覆寫有帶的規則。 */
function applyPatch(settings: AssistantSettingsView, patch: AssistantSettingsPatch): AssistantSettingsView {
  return {
    ...settings,
    configuration: {
      ...settings.configuration,
      name: patch.name ?? settings.configuration.name,
      purpose: patch.purpose ?? settings.configuration.purpose,
      audience: patch.audience ?? settings.configuration.audience,
    },
    tone: patch.tone ?? settings.tone,
    roleInstructions: patch.roleInstructions ?? settings.roleInstructions,
    rules: { ...settings.rules, ...patch.rules },
  };
}

function mergePatches(a: AssistantSettingsPatch, b: AssistantSettingsPatch): AssistantSettingsPatch {
  return {
    ...a,
    ...b,
    ...(a.rules !== undefined || b.rules !== undefined ? { rules: { ...a.rules, ...b.rules } } : {}),
  };
}

/**
 * 建立後的助理設定狀態。和建立精靈的草稿 store 差在三點：
 * 沒有步驟、沒有「下一步」，而且每一次變更都直接套用到一個已經有人在用的助理身上——
 * 所以驗證失敗時保留畫面上的輸入並就地顯示錯誤，不會把助理改成不完整的狀態。
 *
 * 非同步契約（issue #81）：設定以 `repositoryResource` 讀取。寫入一次只送一個請求，
 * 送出期間的欄位變更合併成下一次 `PATCH`；畫面先顯示使用者剛輸入的值（樂觀更新），
 * 全部送完才換成伺服器回傳的設定，避免較早的回應把輸入框改回舊值。
 */
@Injectable()
export class AssistantSettingsStore {
  private readonly route = inject(ActivatedRoute);
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly destroyRef = inject(DestroyRef);
  private readonly params = toSignal(this.route.paramMap, {
    initialValue: this.route.snapshot.paramMap,
  });
  private readonly save = signal<SaveState>({ status: 'idle' });
  private readonly errors = signal<readonly AssistantSettingsFieldError[]>([]);
  private readonly notice = signal('');

  readonly assistantId = computed(() => this.params().get('id') ?? '');

  private readonly resource = repositoryResource({
    params: () => {
      const accountId = this.session.activeAccountId();
      return accountId === null ? undefined : { accountId, assistantId: this.assistantId() };
    },
    stream: ({ assistantId }) => this.repository.getAssistantSettings(assistantId),
  });

  /** 讀取結果：載入中、錯誤、沒有權限（含不存在）或設定本身。 */
  readonly view: () => LoadedView<AssistantSettingsView> = this.resource.view;

  refresh(): void {
    this.resource.reload();
  }

  /** 畫面上的設定：讀到的設定，加上尚未送完的變更（樂觀更新）。換助理或重新讀取時重設。 */
  private readonly local = linkedSignal<LoadedView<AssistantSettingsView>, AssistantSettingsView | null>({
    source: this.resource.view,
    computation: (view) => (view.status === 'ready' || view.status === 'partial-failure' ? view.data : null),
  });

  /** 表單用的設定：包含尚未送完、甚至被伺服器拒絕的輸入，輸入框才不會被改回舊值。 */
  readonly settings = computed<AssistantSettingsView | null>(() => this.local());

  /**
   * 伺服器最後一次確認的設定：頁面標題與摘要用這一份，驗證失敗（全有或全無，沒有寫入）時
   * 仍是上一個有效的值。
   */
  private readonly confirmed = linkedSignal<LoadedView<AssistantSettingsView>, AssistantSettingsView | null>({
    source: this.resource.view,
    computation: (view) => (view.status === 'ready' || view.status === 'partial-failure' ? view.data : null),
  });

  readonly savedSettings = computed<AssistantSettingsView | null>(() => this.confirmed());

  readonly canEdit = computed(() => this.settings() !== null);

  readonly deniedMessage = computed(() =>
    this.view().status === 'permission-denied'
      ? '你沒有這個助理的設定權限，或它已不存在。'
      : '',
  );

  readonly loading = computed(() => this.view().status === 'loading');

  /** 有寫入正在送出或排隊中。 */
  readonly busy = signal(false);

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

  /** 還沒有 Demo 身分時停在載入中；換身分就重新讀取（見 `repositoryResource`）。 */
  private readonly sourcesResource = repositoryResource({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: () => this.repository.listConnectableSources(),
  });

  /** 已連接與可連接的來源都只包含目前帳號看得到的資源。 */
  readonly connectableSources = computed<readonly ConnectableSourceView[]>(() => {
    const view = this.sourcesResource.view();
    return view.status === 'ready' || view.status === 'partial-failure' ? view.data : [];
  });

  /** 只有已連接到這個助理的資料庫可以當寫入對象。 */
  readonly writableDatabases = computed(() =>
    this.connectableSources().flatMap((source) =>
      source.type === 'database' && this.isConnected(source)
        ? [{ id: source.id, name: source.name }]
        : [],
    ),
  );

  private readonly queue: SettingsWrite[] = [];

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

    this.enqueuePatch(patch);
  }

  /**
   * 寫入的資料庫與收集目的一起送出（issue #148）：伺服器要求「有寫入對象就要有目的」，只送其中
   * 一項時，另一項要用畫面上目前的值，否則先選資料庫、再填目的的使用者永遠存不進去。
   */
  updateRules(patch: Partial<AssistantAnswerRules>): void {
    const touchesWrite = patch.dataWriteDatabaseId !== undefined || patch.dataWritePurpose !== undefined;
    const current = this.settings()?.rules;
    const full: Partial<AssistantAnswerRules> = touchesWrite && current !== undefined
      ? {
          ...patch,
          dataWriteDatabaseId: patch.dataWriteDatabaseId !== undefined ? patch.dataWriteDatabaseId : current.dataWriteDatabaseId,
          dataWritePurpose: patch.dataWritePurpose ?? current.dataWritePurpose,
        }
      : patch;
    this.enqueuePatch({ rules: full });
  }

  /** 加入或解除連接；只送出 id 與類型，不會複製來源內容。等伺服器回應才更新畫面。 */
  toggleSource(source: ConnectableSourceView): void {
    const connect = !this.isConnected(source);
    const reference: AssistantSourceReference =
      source.type === 'knowledge-base'
        ? { id: source.id, type: 'knowledge-base' }
        : { id: source.id, type: 'database' };

    this.enqueue({ kind: 'source', source: reference, connect });
  }

  /** 由畫面擋下、根本沒有送出的變更；只留一則說明，不改動任何設定。 */
  note(message: string): void {
    this.notice.set(message);
  }

  private enqueuePatch(patch: AssistantSettingsPatch): void {
    this.local.update((current) => (current === null ? current : applyPatch(current, patch)));
    this.enqueue({ kind: 'patch', patch });
  }

  private enqueue(write: SettingsWrite): void {
    this.notice.set('');
    const last = this.queue.at(-1);
    if (write.kind === 'patch' && last?.kind === 'patch') {
      this.queue[this.queue.length - 1] = { kind: 'patch', patch: mergePatches(last.patch, write.patch) };
    } else {
      this.queue.push(write);
    }
    if (!this.busy()) this.sendNext();
  }

  private sendNext(): void {
    const write = this.queue.shift();
    if (write === undefined) {
      this.busy.set(false);
      return;
    }

    this.busy.set(true);
    const assistantId = this.assistantId();
    const request: Observable<UpdateAssistantSettingsResult> =
      write.kind === 'patch'
        ? this.repository.updateAssistantSettings(assistantId, write.patch)
        : this.repository.setAssistantSourceConnection(assistantId, write.source, write.connect);

    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (result) => this.settle(result),
      error: () => {
        this.save.set({ status: 'error', message: SAVE_FAILED_MESSAGE });
        this.sendNext();
      },
      complete: () => {
        if (this.busy()) this.sendNext();
      },
    });
  }

  private settle(result: UpdateAssistantSettingsResult): void {
    if (result.status === 'validation-failed') {
      // 全有或全無：伺服器沒有寫入，但畫面保留使用者的輸入，讓他就地修正。
      this.errors.set(result.errors);
      this.save.set({ status: 'error', message: result.message });
      return;
    }

    if (result.status === 'permission-denied') {
      this.queue.length = 0;
      this.errors.set([]);
      this.save.set({ status: 'error', message: result.message });
      this.resource.reload();
      return;
    }

    if (result.status === 'ready' || result.status === 'partial-failure') {
      this.errors.set([]);
      this.save.set({ status: 'saved', savedAt: result.data.savedAt ?? '' });
      // 還有排隊中的欄位變更時，先疊回去，輸入框才不會被較早的回應改回舊值。
      const pending = this.queue.reduce<AssistantSettingsPatch>(
        (merged, write) => (write.kind === 'patch' ? mergePatches(merged, write.patch) : merged),
        {},
      );
      this.local.set(applyPatch(result.data, pending));
      this.confirmed.set(result.data);
    }
  }
}
