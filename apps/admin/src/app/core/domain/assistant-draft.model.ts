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
  /** 已至少試問一次（自由輸入或固定題組皆可）；建立助理前的必要條件。 */
  readonly hasTrialAnswer: boolean;
  readonly currentStep: AssistantWizardStep;
}

export interface SavedAssistantDraftView {
  readonly draft: AssistantDraft;
  readonly savedAt: string;
}

export interface NamedAssistantDraftView extends SavedAssistantDraftView {
  readonly id: string;
  /**
   * 樂觀鎖版本：下一次保存要帶回去的值（API 的 `revision`）。兩個分頁以同一個值先後保存，
   * 第二個會得到 `conflict`，避免默默蓋掉另一個分頁的修改。
   */
  readonly revision: number;
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

/**
 * `empty`：沒有任何文件或 FAQ（issue #115）；與 `ready`（有內容且都可用）分開顯示，
 * 避免讓人誤以為已經有可用內容。
 */
export type ConnectableSourceStatus = 'ready' | 'processing' | 'needs-attention' | 'empty';

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

/**
 * 試問的引用來源；沒有 `updatedLabel`（版本的 `EffectiveFrom` 要等 #76 補上，
 * 見 PR #92 的說明），其餘欄位與 `ChatCitationView` 相同意義。
 */
export interface TrialAnswerCitationView {
  readonly knowledgeBaseName: KnowledgeBaseId | string;
  readonly documentName: string;
  readonly locationLabel: string;
  readonly excerpt: string;
  readonly score: number;
}

/**
 * 試問與正式對話用同一套回覆類型（issue #82，M3 計畫 Slice 12）：`ChatReplyView` 拿掉
 * `form-request`、`submission-receipt`（試問不牽涉表單）的子集，`citations` 換成試問專用的
 * `TrialAnswerCitationView`（多一個 `score`）。
 */
export type TrialAnswerView =
  | {
      readonly kind: 'company-data';
      readonly text: string;
      readonly citations: readonly TrialAnswerCitationView[];
      readonly citationNotice: string | null;
    }
  | {
      readonly kind: 'general-knowledge';
      readonly text: string;
      readonly notice: string;
    }
  | {
      readonly kind: 'no-result';
      readonly text: string;
      readonly nextSteps: readonly string[];
    };

export type TrialAnswerKind = TrialAnswerView['kind'];

/** 檢索到的段落與分數；讓建立者對照 `threshold` 判斷門檻是否合適，不限於命中的段落。 */
export interface TrialAnswerPassageView {
  readonly knowledgeBaseName: string;
  readonly documentName: string;
  readonly locationLabel: string;
  readonly excerpt: string;
  readonly score: number;
}

/** 一次試問的完整結果：回覆本身，加上檢索到的段落與門檻。 */
export interface TrialAnswerResultView {
  readonly question: string;
  readonly reply: TrialAnswerView;
  readonly passages: readonly TrialAnswerPassageView[];
  readonly threshold: number;
}

/**
 * API 模式下試問可以自由輸入問題（固定題組仍保留作為建議按鈕）；Mock 模式另外需要草稿的
 * 來源與回答規則才能模擬結果，API 模式以伺服器上保存的草稿為準，不會送出這兩個欄位。
 */
export interface TrialAnswerRequest {
  readonly question: string;
  readonly sources: readonly AssistantSourceReference[];
  readonly rules: AssistantAnswerRules;
}

/** 問題空白或超過長度限制（422）。 */
export interface TrialAnswerValidationFailedView {
  readonly status: 'validation-failed';
  readonly message: string;
}

/** 嵌入或對話模型未設定、或呼叫失敗（503）。 */
export interface TrialAnswerUnavailableView {
  readonly status: 'unavailable';
  readonly message: string;
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
    hasTrialAnswer: false,
    currentStep: 'purpose',
  };
}

export type AssistantDraftField =
  | 'name'
  | 'purpose'
  | 'audience'
  | 'tone'
  | 'roleInstructions'
  | 'sources'
  | 'knowledgeScope'
  | 'refusalMessage'
  | 'dataWritePurpose'
  | 'trial';

/** 每個欄位屬於精靈的哪一步：伺服器回傳的逐欄錯誤要顯示在對應的步驟上。 */
export const ASSISTANT_DRAFT_FIELD_STEPS: Readonly<Record<AssistantDraftField, AssistantWizardStep>> = {
  name: 'purpose',
  purpose: 'purpose',
  audience: 'purpose',
  tone: 'purpose',
  roleInstructions: 'purpose',
  sources: 'sources',
  knowledgeScope: 'rules',
  refusalMessage: 'rules',
  dataWritePurpose: 'rules',
  trial: 'test',
};

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

  if (step === 'test' && !draft.hasTrialAnswer) {
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
