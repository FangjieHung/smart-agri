import { computed, inject, Injectable, linkedSignal, signal } from '@angular/core';
import { firstValueFrom, of, type Observable } from 'rxjs';
import type { AccountId } from '../../../core/domain/account.model';
import {
  ASSISTANT_DRAFT_FIELD_STEPS,
  ASSISTANT_WIZARD_STEPS,
  createEmptyAssistantDraft,
  validateAssistantDraft,
  validateAssistantDraftStep,
  type AssistantAnswerRules,
  type AssistantDraft,
  type AssistantDraftField,
  type AssistantDraftFieldError,
  type AssistantTemplateId,
  type AssistantTemplateView,
  type AssistantWizardStep,
  type ConnectableSourceView,
  type NamedAssistantDraftView,
  type TrialAnswerResultView,
  type TrialQuestionView,
} from '../../../core/domain/assistant-draft.model';
import type {
  AssistantAudience,
  AssistantId,
  AssistantSourceReference,
} from '../../../core/domain/assistant.model';
import { fmtDateTime } from '../../../core/date-utils';
import { ActivatedRoute } from '@angular/router';
import type { PreviewTrialAnswerResult, RepositoryView } from '../../../core/repositories/demo-repository';
import { repositoryResource, type LoadedView } from '../../../core/repositories/repository-resource';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { ApiSessionService } from '../../../core/session/api-session.service';
import { DemoSessionService } from '../../../core/session/demo-session.service';

export type DraftSaveState =
  | { readonly status: 'new' }
  | { readonly status: 'resumed'; readonly savedAt: string }
  | { readonly status: 'saved'; readonly savedAt: string }
  | { readonly status: 'error'; readonly message: string };

/** 讀取草稿的畫面狀態：載入中、讀取失敗、沒有權限（含不存在與別人的草稿）或可以編輯。 */
export type DraftLoadStatus = 'loading' | 'error' | 'permission-denied' | 'ready';

export interface AudienceFlags {
  readonly internal: boolean;
  readonly external: boolean;
}

interface DraftState {
  readonly accountId: AccountId | null;
  readonly canManage: boolean;
  readonly draft: AssistantDraft;
  /** 下一次保存要帶的樂觀鎖版本（`NamedAssistantDraftView.revision`）。 */
  readonly revision: number;
  /** 另一個分頁先存過（`conflict`）：之後的保存一定也會衝突，所以停止自動保存。 */
  readonly conflicted: boolean;
  readonly save: DraftSaveState;
  readonly attemptedSteps: readonly AssistantWizardStep[];
  readonly answers: readonly TrialAnswerResultView[];
  /** 試問本身被拒絕（422）或暫時無法完成（503）時的訊息；下一次試問或編輯都會清掉。 */
  readonly trialError: string | null;
  readonly createError: string | null;
  /** 「建立助理」被伺服器以逐欄錯誤拒絕（`422`）時的錯誤；任何編輯都會清掉。 */
  readonly serverErrors: readonly AssistantDraftFieldError[];
}

const SAVE_FAILED_MESSAGE = '自動儲存失敗，變更暫時只保留在這個畫面。';
const CREATE_FAILED_MESSAGE = '目前無法建立助理，請稍後再試。';
const DRAFT_NOT_FOUND_MESSAGE = '找不到這份草稿，或它不屬於你的帳號。';
const TRIAL_FAILED_MESSAGE = '目前無法產生回答，請稍後再試。';

function dataOf<T>(view: RepositoryView<T> | LoadedView<T>, fallback: T): T {
  return view.status === 'ready' || view.status === 'partial-failure'
    ? view.data
    : fallback;
}

function sameSource(
  a: AssistantSourceReference,
  b: AssistantSourceReference,
): boolean {
  return a.id === b.id && a.type === b.type;
}

/**
 * 建立精靈的草稿狀態：每次變更都透過 repository 自動保存，並依目前帳號各自載入，
 * 切換帳號不會帶出前一位的草稿。
 *
 * 非同步契約（issue #81）：草稿以 `repositoryResource` 讀取；保存一次只送一個請求，
 * 送出期間的變更合併成下一次保存（讀取當下最新的草稿），每次都帶上一次回來的
 * `revision`，所以自己連續的保存不會互相衝突，只有另一個分頁先存過才會 `conflict`。
 */
@Injectable()
export class AssistantDraftStore {
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);
  readonly draftId = inject(ActivatedRoute, { optional: true })?.snapshot.paramMap.get('draftId') ?? null;

  private readonly loaded = repositoryResource({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: (): Observable<RepositoryView<NamedAssistantDraftView>> =>
      this.draftId === null
        ? of({ status: 'permission-denied', reason: 'assistant-draft', message: DRAFT_NOT_FOUND_MESSAGE })
        : this.repository.getNamedAssistantDraft(this.draftId),
  });

  /** 讀取結果一變（第一次讀到、換身分）就重設畫面狀態；之後的編輯只改這份本地狀態。 */
  private readonly state = linkedSignal<LoadedView<NamedAssistantDraftView>, DraftState>({
    source: this.loaded.view,
    computation: (view) => this.fromLoaded(view),
  });

  readonly loadStatus = computed<DraftLoadStatus>(() => {
    const view = this.loaded.view();
    if (view.status === 'ready' || view.status === 'partial-failure') return 'ready';
    return view.status;
  });

  readonly draft = computed(() => this.state().draft);
  readonly canManage = computed(() => this.state().canManage);
  readonly saveState = computed(() => this.state().save);
  readonly answers = computed(() => this.state().answers);
  readonly trialError = computed(() => this.state().trialError);
  readonly createError = computed(() => this.state().createError);
  /** 建立中：按鈕要停用，避免重複送出。 */
  readonly creating = signal(false);
  /** 試問中：按鈕要停用，避免重複送出。 */
  readonly trialing = signal(false);

  readonly saveStatusLabel = computed(() => {
    const save = this.state().save;
    switch (save.status) {
      case 'new':
        return '尚未開始編輯';
      case 'resumed':
        return `已載入先前的草稿（${fmtDateTime(save.savedAt)} 儲存）`;
      case 'saved':
        return `已自動儲存 · ${fmtDateTime(save.savedAt)}`;
      case 'error':
        return save.message;
    }
  });

  readonly templates = computed<readonly AssistantTemplateView[]>(() =>
    dataOf(this.repository.listAssistantTemplates(), []),
  );

  /** 還沒有 Demo 身分時停在載入中；換身分就重新讀取（見 `repositoryResource`）。 */
  private readonly sourcesResource = repositoryResource({
    params: () => this.session.activeAccountId() ?? undefined,
    stream: () => this.repository.listConnectableSources(),
  });

  readonly sourcesView = this.sourcesResource.view;

  /**
   * API 模式的建立精靈還不能連接資料庫（#148 只開放建立後的「資料來源」頁籤；由草稿建立時
   * 後端仍拒絕資料庫來源），所以只列出知識庫。
   */
  private readonly apiMode = inject(ApiSessionService).apiMode;

  readonly connectableSources = computed<readonly ConnectableSourceView[]>(() => {
    const sources = dataOf(this.sourcesView(), []);
    return this.apiMode ? sources.filter((source) => source.type === 'knowledge-base') : sources;
  });

  readonly trialQuestions = computed<readonly TrialQuestionView[]>(() =>
    dataOf(this.repository.listTrialQuestions(), []),
  );

  readonly audienceFlags = computed<AudienceFlags>(() => {
    const audience = this.draft().audience;
    return {
      internal:
        audience === 'account-members' ||
        audience === 'members-and-external-customers',
      external:
        audience === 'authorized-external-customers' ||
        audience === 'members-and-external-customers',
    };
  });

  readonly connectedSources = computed(() =>
    this.connectableSources().filter((source) =>
      this.draft().sources.some((selected) => sameSource(selected, source)),
    ),
  );

  /** 第一個仍缺必要欄位的步驟；全部完成時停在試問確認。 */
  readonly firstIncompleteStep = computed<AssistantWizardStep>(() => {
    const draft = this.draft();
    return (
      ASSISTANT_WIZARD_STEPS.find(
        (step) => validateAssistantDraftStep(draft, step).length > 0,
      ) ?? 'test'
    );
  });

  private saveScheduled = false;
  private saveChain: Promise<boolean> = Promise.resolve(true);

  canVisit(step: AssistantWizardStep): boolean {
    return (
      ASSISTANT_WIZARD_STEPS.indexOf(step) <=
      ASSISTANT_WIZARD_STEPS.indexOf(this.firstIncompleteStep())
    );
  }

  /**
   * 只在使用者嘗試離開該步驟後才顯示畫面自己的檢查結果，避免一進畫面就滿是紅字；
   * 伺服器拒絕建立時的逐欄錯誤（`422`）一律顯示在欄位所屬的步驟。
   */
  errorsFor(step: AssistantWizardStep): readonly AssistantDraftFieldError[] {
    const local = this.state().attemptedSteps.includes(step)
      ? validateAssistantDraftStep(this.draft(), step)
      : [];
    const server = this.state().serverErrors.filter(
      (error) =>
        ASSISTANT_DRAFT_FIELD_STEPS[error.field] === step &&
        !local.some((existing) => existing.field === error.field),
    );
    return [...local, ...server];
  }

  fieldError(
    step: AssistantWizardStep,
    field: AssistantDraftField,
  ): string | null {
    return this.errorsFor(step).find((error) => error.field === field)?.message ?? null;
  }

  applyTemplate(templateId: AssistantTemplateId): void {
    const template = this.templates().find((candidate) => candidate.id === templateId);
    if (template === undefined) return;

    this.commit({ ...this.draft(), templateId, ...template.defaults });
  }

  update(
    patch: Partial<
      Pick<AssistantDraft, 'name' | 'purpose' | 'tone' | 'roleInstructions'>
    >,
  ): void {
    this.commit({ ...this.draft(), ...patch });
  }

  setAudience(flags: AudienceFlags): void {
    let audience: AssistantAudience | null = null;
    if (flags.internal && flags.external) audience = 'members-and-external-customers';
    else if (flags.internal) audience = 'account-members';
    else if (flags.external) audience = 'authorized-external-customers';

    this.setAudienceValue(audience);
  }

  /** 草稿允許「還沒選使用對象」，所以這裡接受 null。 */
  setAudienceValue(audience: AssistantAudience | null): void {
    this.commit({ ...this.draft(), audience });
  }

  isConnected(source: AssistantSourceReference): boolean {
    return this.draft().sources.some((selected) => sameSource(selected, source));
  }

  /** 加入或解除連接；只保存來源 id 與類型，不複製來源內容。 */
  toggleSource(source: AssistantSourceReference): void {
    const draft = this.draft();
    const connected = this.isConnected(source);
    const reference: AssistantSourceReference =
      source.type === 'knowledge-base'
        ? { id: source.id, type: 'knowledge-base' }
        : { id: source.id, type: 'database' };
    const sources = connected
      ? draft.sources.filter((selected) => !sameSource(selected, source))
      : [...draft.sources, reference];
    const dropsWriteTarget =
      connected &&
      source.type === 'database' &&
      draft.rules.dataWriteDatabaseId === source.id;

    this.commit({
      ...draft,
      sources,
      rules: dropsWriteTarget
        ? { ...draft.rules, dataWriteDatabaseId: null }
        : draft.rules,
    });
  }

  updateRules(patch: Partial<AssistantAnswerRules>): void {
    const draft = this.draft();
    this.commit({ ...draft, rules: { ...draft.rules, ...patch } });
  }

  setCurrentStep(step: AssistantWizardStep): void {
    if (this.draft().currentStep === step) return;
    this.commit({ ...this.draft(), currentStep: step });
  }

  /** 驗證目前步驟；通過時前進到下一步並保存所在位置。 */
  tryAdvance(step: AssistantWizardStep): boolean {
    this.markAttempted(step);
    if (validateAssistantDraftStep(this.draft(), step).length > 0) return false;

    const next = ASSISTANT_WIZARD_STEPS[ASSISTANT_WIZARD_STEPS.indexOf(step) + 1];
    if (next !== undefined) this.setCurrentStep(next);
    return true;
  }

  /**
   * 試問可以自由輸入問題（issue #82）；`draftId` 尚未就緒或已有一個試問在進行中時忽略。
   * 成功時把回答放到 `answers` 的最前面，並記下「已至少試問一次」；422／503 只設定
   * `trialError`，不影響已經顯示的回答清單。
   */
  async runTrial(question: string): Promise<TrialAnswerResultView | null> {
    const draftId = this.draftId;
    const trimmed = question.trim();
    if (draftId === null || trimmed === '' || this.trialing()) return null;

    this.trialing.set(true);
    try {
      const draft = this.draft();
      const result: PreviewTrialAnswerResult = await firstValueFrom(
        this.repository.previewTrialAnswer(draftId, {
          question: trimmed,
          sources: draft.sources,
          rules: draft.rules,
        }),
      );

      if (result.status === 'ready' || result.status === 'partial-failure') {
        const answer = result.data;
        this.state.update((state) => ({
          ...state,
          answers: [answer, ...state.answers],
          trialError: null,
        }));
        if (!draft.hasTrialAnswer) {
          this.commit({ ...draft, hasTrialAnswer: true });
        }
        return answer;
      }

      const message =
        result.status === 'validation-failed' || result.status === 'unavailable'
          ? result.message
          : TRIAL_FAILED_MESSAGE;
      this.state.update((state) => ({ ...state, trialError: message }));
      return null;
    } catch {
      this.state.update((state) => ({ ...state, trialError: TRIAL_FAILED_MESSAGE }));
      return null;
    } finally {
      this.trialing.set(false);
    }
  }

  /**
   * 以完整草稿建立助理，成功時回傳新助理 id。先等進行中的自動保存結束、再保存一次目前的
   * 草稿（API 以伺服器上的草稿為準），才送出建立。建立中重複呼叫直接忽略。
   */
  async create(): Promise<AssistantId | null> {
    if (this.creating()) return null;
    this.markAttempted('test');
    const draftId = this.draftId;
    if (!this.state().canManage || draftId === null || validateAssistantDraft(this.draft()).length > 0) {
      return null;
    }

    this.creating.set(true);
    try {
      if (!(await this.scheduleSave())) {
        const save = this.state().save;
        this.setCreateError(save.status === 'error' ? save.message : SAVE_FAILED_MESSAGE);
        return null;
      }

      const result = await firstValueFrom(this.repository.createAssistantFromDraft(draftId, this.draft()));
      if (result.status === 'ready' || result.status === 'partial-failure') {
        return result.data.id;
      }
      if (result.status === 'validation-failed') {
        const steps = result.errors.map((error) => ASSISTANT_DRAFT_FIELD_STEPS[error.field]);
        this.state.update((state) => ({
          ...state,
          createError: result.message,
          serverErrors: result.errors,
          attemptedSteps: [...new Set([...state.attemptedSteps, ...steps])],
        }));
        return null;
      }
      this.setCreateError(result.status === 'loading' ? CREATE_FAILED_MESSAGE : result.message);
      return null;
    } catch {
      this.setCreateError(CREATE_FAILED_MESSAGE);
      return null;
    } finally {
      this.creating.set(false);
    }
  }

  private setCreateError(message: string): void {
    this.state.update((state) => ({ ...state, createError: message }));
  }

  private markAttempted(step: AssistantWizardStep): void {
    this.state.update((state) =>
      state.attemptedSteps.includes(step)
        ? state
        : { ...state, attemptedSteps: [...state.attemptedSteps, step] },
    );
  }

  private commit(draft: AssistantDraft): void {
    this.state.update((state) => ({ ...state, draft, createError: null, serverErrors: [] }));
    if (this.state().canManage) void this.scheduleSave();
  }

  /**
   * 排一次保存並回傳「到這次保存為止」的結果。已經有一次排隊中（還沒開始送出）時共用它，
   * 它開始時會讀取當下最新的草稿，所以連續的變更只會多送一次。
   */
  private scheduleSave(): Promise<boolean> {
    if (!this.saveScheduled) {
      this.saveScheduled = true;
      this.saveChain = this.saveChain.then(() => {
        this.saveScheduled = false;
        return this.persist();
      });
    }
    return this.saveChain;
  }

  private async persist(): Promise<boolean> {
    const { canManage, conflicted, draft, revision } = this.state();
    if (!canManage || this.draftId === null) return false;
    if (conflicted) return false;

    try {
      const result = await firstValueFrom(
        this.repository.saveNamedAssistantDraft(this.draftId, draft, revision),
      );
      if (result.status === 'ready' || result.status === 'partial-failure') {
        const saved = result.data;
        this.state.update((state) => ({
          ...state,
          revision: saved.revision,
          save: { status: 'saved', savedAt: saved.savedAt },
        }));
        return true;
      }
      if (result.status === 'conflict') {
        this.state.update((state) => ({
          ...state,
          conflicted: true,
          save: { status: 'error', message: result.message },
        }));
        return false;
      }
    } catch {
      // 連線中斷或 5xx：下一次變更會再試。
    }
    this.state.update((state) => ({ ...state, save: { status: 'error', message: SAVE_FAILED_MESSAGE } }));
    return false;
  }

  private fromLoaded(view: LoadedView<NamedAssistantDraftView>): DraftState {
    const accountId = this.session.activeAccountId();
    if (view.status !== 'ready' && view.status !== 'partial-failure') {
      return this.emptyState(accountId, false);
    }

    return {
      ...this.emptyState(accountId, true),
      draft: view.data.draft,
      revision: view.data.revision,
      save: { status: 'resumed', savedAt: view.data.savedAt },
    };
  }

  private emptyState(accountId: AccountId | null, canManage: boolean): DraftState {
    return {
      accountId,
      canManage,
      draft: createEmptyAssistantDraft(),
      revision: 1,
      conflicted: false,
      save: { status: 'new' },
      attemptedSteps: [],
      answers: [],
      trialError: null,
      createError: null,
      serverErrors: [],
    };
  }
}
