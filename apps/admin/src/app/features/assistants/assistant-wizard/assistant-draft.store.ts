import { computed, inject, Injectable, linkedSignal } from '@angular/core';
import type { AccountId } from '../../../core/domain/account.model';
import {
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
  type TrialAnswerView,
  type TrialQuestionId,
  type TrialQuestionView,
} from '../../../core/domain/assistant-draft.model';
import type {
  AssistantAudience,
  AssistantId,
  AssistantSourceReference,
} from '../../../core/domain/assistant.model';
import { fmtDateTime } from '../../../core/date-utils';
import { ActivatedRoute } from '@angular/router';
import type { RepositoryView } from '../../../core/repositories/demo-repository';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';

export type DraftSaveState =
  | { readonly status: 'new' }
  | { readonly status: 'resumed'; readonly savedAt: string }
  | { readonly status: 'saved'; readonly savedAt: string }
  | { readonly status: 'error'; readonly message: string };

export interface AudienceFlags {
  readonly internal: boolean;
  readonly external: boolean;
}

interface DraftState {
  readonly accountId: AccountId | null;
  readonly canManage: boolean;
  readonly draft: AssistantDraft;
  readonly save: DraftSaveState;
  readonly attemptedSteps: readonly AssistantWizardStep[];
  readonly answers: readonly TrialAnswerView[];
  readonly createError: string | null;
}

function dataOf<T>(view: RepositoryView<T>, fallback: T): T {
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
 * 建立精靈的草稿狀態：每次變更都透過 repository 自動保存，
 * 並依目前 Demo 帳號各自載入，切換帳號不會帶出前一位的草稿。
 */
@Injectable()
export class AssistantDraftStore {
  private readonly session = inject(DemoSessionService);
  private readonly repository = inject(DEMO_REPOSITORY);
  private readonly draftId = inject(ActivatedRoute, { optional: true })?.snapshot.paramMap.get('draftId') ?? null;

  private readonly state = linkedSignal<DraftState>(() =>
    this.load(this.session.activeAccountId()),
  );

  readonly draft = computed(() => this.state().draft);
  readonly canManage = computed(() => this.state().canManage);
  readonly saveState = computed(() => this.state().save);
  readonly answers = computed(() => this.state().answers);
  readonly createError = computed(() => this.state().createError);

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

  readonly sourcesView = computed<RepositoryView<readonly ConnectableSourceView[]>>(
    () => {
      const accountId = this.state().accountId;
      return accountId === null
        ? { status: 'ready', data: [] }
        : this.repository.listConnectableSources(accountId);
    },
  );

  readonly connectableSources = computed<readonly ConnectableSourceView[]>(() =>
    dataOf(this.sourcesView(), []),
  );

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

  canVisit(step: AssistantWizardStep): boolean {
    return (
      ASSISTANT_WIZARD_STEPS.indexOf(step) <=
      ASSISTANT_WIZARD_STEPS.indexOf(this.firstIncompleteStep())
    );
  }

  /** 只在使用者嘗試離開該步驟後才顯示錯誤，避免一進畫面就滿是紅字。 */
  errorsFor(step: AssistantWizardStep): readonly AssistantDraftFieldError[] {
    return this.state().attemptedSteps.includes(step)
      ? validateAssistantDraftStep(this.draft(), step)
      : [];
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

  runTrial(questionId: TrialQuestionId): TrialAnswerView | null {
    const { accountId } = this.state();
    if (accountId === null) return null;

    const draft = this.draft();
    const result = this.repository.previewTrialAnswer(accountId, {
      questionId,
      sources: draft.sources,
      rules: draft.rules,
    });
    if (result.status !== 'ready') return null;

    const answer = result.data;
    this.state.update((state) => ({
      ...state,
      answers: [
        answer,
        ...state.answers.filter((existing) => existing.questionId !== questionId),
      ],
    }));
    if (!draft.testedQuestionIds.includes(questionId)) {
      this.commit({
        ...draft,
        testedQuestionIds: [...draft.testedQuestionIds, questionId],
      });
    }

    return answer;
  }

  /** 以完整草稿建立助理；成功後清除草稿並回傳新助理 id。 */
  create(): AssistantId | null {
    const { accountId } = this.state();
    this.markAttempted('test');
    if (accountId === null || validateAssistantDraft(this.draft()).length > 0) {
      return null;
    }

    const result = this.repository.createAssistantFromDraft(accountId, this.draft(), this.draftId ?? undefined);
    if (result.status !== 'ready') {
      this.state.update((state) => ({
        ...state,
        createError:
          'message' in result ? result.message : '目前無法建立助理，請稍後再試。',
      }));
      return null;
    }

    this.state.set(this.emptyState(accountId, true));
    return result.data.id;
  }

  private markAttempted(step: AssistantWizardStep): void {
    this.state.update((state) =>
      state.attemptedSteps.includes(step)
        ? state
        : { ...state, attemptedSteps: [...state.attemptedSteps, step] },
    );
  }

  private commit(draft: AssistantDraft): void {
    const { accountId, canManage } = this.state();
    let save: DraftSaveState = this.state().save;

    if (accountId !== null && canManage) {
      const result = this.draftId
        ? this.repository.saveNamedAssistantDraft(accountId, this.draftId, draft)
        : this.repository.saveAssistantDraft(accountId, draft);
      save =
        result.status === 'ready' && result.data !== null
          ? { status: 'saved', savedAt: result.data.savedAt }
          : { status: 'error', message: '自動儲存失敗，變更暫時只保留在這個畫面。' };
    }

    this.state.update((state) => ({ ...state, draft, save, createError: null }));
  }

  private load(accountId: AccountId | null): DraftState {
    if (accountId === null) return this.emptyState(null, false);

    const result = this.draftId
      ? this.repository.getNamedAssistantDraft(accountId, this.draftId)
      : this.repository.getAssistantDraft(accountId);
    if (result.status === 'permission-denied') {
      return this.emptyState(accountId, false);
    }

    const saved = dataOf(result, null);
    if (saved === null) return this.emptyState(accountId, true);

    return {
      ...this.emptyState(accountId, true),
      draft: saved.draft,
      save: { status: 'resumed', savedAt: saved.savedAt },
    };
  }

  private emptyState(accountId: AccountId | null, canManage: boolean): DraftState {
    return {
      accountId,
      canManage,
      draft: createEmptyAssistantDraft(),
      save: { status: 'new' },
      attemptedSteps: [],
      answers: [],
      createError: null,
    };
  }
}
