import type {
  AssistantAudience,
  AssistantSourceReference,
} from './assistant.model';
import type { DatabaseId } from './database.model';
import type { KnowledgeBaseId } from './knowledge-base.model';

export type AssistantWizardStep = 'purpose' | 'sources' | 'rules' | 'test';

export const ASSISTANT_WIZARD_STEPS: readonly AssistantWizardStep[] = [
  'purpose',
  'sources',
  'rules',
  'test',
];

export type AssistantTemplateId =
  | 'answer-customer-questions'
  | 'search-company-data'
  | 'onboard-new-employees'
  | 'collect-periodic-reports'
  | 'compare-changes'
  | 'blank';

export type AssistantTone = 'friendly' | 'professional' | 'concise';

/** 嚴格：只依據已連接的資料回答；一般：允許補充一般知識並分區標示。 */
export type AssistantKnowledgeScope =
  | 'company-data-only'
  | 'allow-general-knowledge';

export type PeriodicReportSchedule = 'off' | 'weekly' | 'monthly';

/** 定期回報週期的顯示文字；設定表單與資料庫的回報面板共用同一份。 */
export const PERIODIC_REPORT_LABELS: Readonly<Record<PeriodicReportSchedule, string>> = {
  off: '不需要',
  weekly: '每週一次',
  monthly: '每月一次',
};

export interface AssistantAnswerRules {
  readonly knowledgeScope: AssistantKnowledgeScope;
  readonly refusalMessage: string;
  readonly showCitations: boolean;
  readonly keepOwnConversations: boolean;
  readonly dataWriteDatabaseId: DatabaseId | null;
  readonly dataWritePurpose: string;
  readonly periodicReport: PeriodicReportSchedule;
}

export type TrialQuestionId =
  | 'trial-refund-window'
  | 'trial-leather-care'
  | 'trial-unrelated-request';

export interface AssistantDraft {
  readonly templateId: AssistantTemplateId | null;
  readonly name: string;
  readonly purpose: string;
  readonly tone: AssistantTone;
  readonly audience: AssistantAudience | null;
  readonly roleInstructions: string;
  readonly sources: readonly AssistantSourceReference[];
  readonly rules: AssistantAnswerRules;
  readonly testedQuestionIds: readonly TrialQuestionId[];
  readonly currentStep: AssistantWizardStep;
}

export interface SavedAssistantDraftView {
  readonly draft: AssistantDraft;
  readonly savedAt: string;
}

export interface NamedAssistantDraftView extends SavedAssistantDraftView {
  readonly id: string;
}

export interface AssistantTemplateView {
  readonly id: AssistantTemplateId;
  readonly title: string;
  readonly description: string;
  readonly defaults: Pick<
    AssistantDraft,
    'name' | 'purpose' | 'tone' | 'roleInstructions'
  >;
}

export type ConnectableSourceStatus = 'ready' | 'processing' | 'needs-attention';

/** owner：自己建立、可管理；read-only：只能唯讀連接。 */
export type ConnectableSourcePermission = 'owner' | 'read-only';

interface ConnectableSourceBase {
  readonly name: string;
  readonly summary: string;
  readonly permission: ConnectableSourcePermission;
  readonly status: ConnectableSourceStatus;
  readonly updatedAt: string;
}

export type ConnectableSourceView =
  | (ConnectableSourceBase & {
      readonly id: KnowledgeBaseId;
      readonly type: 'knowledge-base';
    })
  | (ConnectableSourceBase & {
      readonly id: DatabaseId;
      readonly type: 'database';
    });

export interface TrialQuestionView {
  readonly id: TrialQuestionId;
  readonly text: string;
}

export interface TrialCitationView {
  readonly sourceId: KnowledgeBaseId;
  readonly sourceName: string;
  readonly excerpt: string;
}

export type TrialAnswerView =
  | {
      readonly kind: 'company-data';
      readonly questionId: TrialQuestionId;
      readonly text: string;
      readonly citation: TrialCitationView | null;
    }
  | {
      readonly kind: 'general-knowledge';
      readonly questionId: TrialQuestionId;
      readonly text: string;
    }
  | {
      readonly kind: 'no-answer';
      readonly questionId: TrialQuestionId;
      readonly text: string;
    };

export interface TrialAnswerRequest {
  readonly questionId: TrialQuestionId;
  readonly sources: readonly AssistantSourceReference[];
  readonly rules: AssistantAnswerRules;
}

export const DEFAULT_REFUSAL_MESSAGE =
  '目前的資料中找不到這個問題的答案。請留下聯絡方式，我們會由專人回覆你。';

export function createEmptyAssistantDraft(): AssistantDraft {
  return {
    templateId: null,
    name: '',
    purpose: '',
    tone: 'friendly',
    audience: null,
    roleInstructions: '',
    sources: [],
    rules: {
      knowledgeScope: 'company-data-only',
      refusalMessage: DEFAULT_REFUSAL_MESSAGE,
      showCitations: true,
      keepOwnConversations: true,
      dataWriteDatabaseId: null,
      dataWritePurpose: '',
      periodicReport: 'off',
    },
    testedQuestionIds: [],
    currentStep: 'purpose',
  };
}

export type AssistantDraftField =
  | 'name'
  | 'purpose'
  | 'audience'
  | 'sources'
  | 'refusalMessage'
  | 'dataWritePurpose'
  | 'trial';

export interface AssistantDraftFieldError {
  readonly field: AssistantDraftField;
  readonly message: string;
}

/** 每一步只檢查該步驟的必要欄位。 */
export function validateAssistantDraftStep(
  draft: AssistantDraft,
  step: AssistantWizardStep,
): readonly AssistantDraftFieldError[] {
  const errors: AssistantDraftFieldError[] = [];

  if (step === 'purpose') {
    if (draft.name.trim() === '') {
      errors.push({ field: 'name', message: '請輸入助理名稱。' });
    }
    if (draft.purpose.trim() === '') {
      errors.push({ field: 'purpose', message: '請用一句話說明助理要幫忙完成的工作。' });
    }
    if (draft.audience === null) {
      errors.push({ field: 'audience', message: '請至少選擇一種使用對象。' });
    }
  }

  if (step === 'sources' && draft.sources.length === 0) {
    errors.push({ field: 'sources', message: '請至少加入一個知識庫或資料庫。' });
  }

  if (step === 'rules') {
    if (draft.rules.refusalMessage.trim() === '') {
      errors.push({ field: 'refusalMessage', message: '請填寫找不到資料時的回覆內容。' });
    }
    if (
      draft.rules.dataWriteDatabaseId !== null &&
      draft.rules.dataWritePurpose.trim() === ''
    ) {
      errors.push({
        field: 'dataWritePurpose',
        message: '寫入資料庫前，請說明收集目的，使用者同意前會看到這段說明。',
      });
    }
  }

  if (step === 'test' && draft.testedQuestionIds.length === 0) {
    errors.push({ field: 'trial', message: '請至少試問一題，確認回答符合預期。' });
  }

  return errors;
}

export function validateAssistantDraft(
  draft: AssistantDraft,
): readonly AssistantDraftFieldError[] {
  return ASSISTANT_WIZARD_STEPS.flatMap((step) =>
    validateAssistantDraftStep(draft, step),
  );
}
