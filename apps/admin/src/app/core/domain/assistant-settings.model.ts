import type {
  AssistantAnswerRules,
  AssistantTone,
} from './assistant-draft.model';
import type {
  AssistantAudience,
  AssistantConfigurationView,
  AssistantSourceReference,
} from './assistant.model';

/**
 * 助理建立後可編輯的完整設定。和建立精靈的草稿不同：這裡的每一次變更都直接套用到
 * 已存在的助理身上，沒有「下一步」也沒有「建立」按鈕，所以每個欄位都各自驗證、
 * 各自保存，其中一項不合格不會擋住其他項目。
 */
export interface AssistantSettingsView {
  readonly configuration: AssistantConfigurationView;
  /** 已連接的來源，只有 id 與類型；不含來源內容。 */
  readonly sources: readonly AssistantSourceReference[];
  readonly tone: AssistantTone;
  readonly roleInstructions: string;
  readonly rules: AssistantAnswerRules;
  /** 最後一次自動保存的時間；從未編輯過時為 null。 */
  readonly savedAt: string | null;
}

/** 一次只送出實際改動的欄位；`rules` 可以只帶其中一條規則。 */
export interface AssistantSettingsPatch {
  readonly name?: string;
  readonly purpose?: string;
  readonly audience?: AssistantAudience;
  readonly tone?: AssistantTone;
  readonly roleInstructions?: string;
  readonly rules?: Partial<AssistantAnswerRules>;
}

export type AssistantSettingsField =
  | 'name'
  | 'purpose'
  | 'audience'
  | 'refusalMessage'
  | 'dataWritePurpose'
  | 'sources';

export interface AssistantSettingsFieldError {
  readonly field: AssistantSettingsField;
  readonly message: string;
}

/**
 * 已上線的助理必須隨時能回答，所以驗證比草稿更嚴格：不接受把必要欄位清空，
 * 也不接受解除最後一個資料來源。
 */
export function validateAssistantSettings(
  settings: AssistantSettingsView,
): readonly AssistantSettingsFieldError[] {
  const errors: AssistantSettingsFieldError[] = [];

  if (settings.configuration.name.trim() === '') {
    errors.push({ field: 'name', message: '請輸入助理名稱。' });
  }
  if (settings.configuration.purpose.trim() === '') {
    errors.push({
      field: 'purpose',
      message: '請用一句話說明助理要幫忙完成的工作。',
    });
  }
  if (settings.rules.refusalMessage.trim() === '') {
    errors.push({
      field: 'refusalMessage',
      message: '請填寫找不到資料時的回覆內容。',
    });
  }
  if (
    settings.rules.dataWriteDatabaseId !== null &&
    settings.rules.dataWritePurpose.trim() === ''
  ) {
    errors.push({
      field: 'dataWritePurpose',
      message: '寫入資料庫前，請說明收集目的，使用者同意前會看到這段說明。',
    });
  }
  if (settings.sources.length === 0) {
    errors.push({
      field: 'sources',
      message: '助理至少要連接一個知識庫或資料庫才能回答問題。',
    });
  }

  return errors;
}
