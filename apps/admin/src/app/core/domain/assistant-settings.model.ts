import type {
  AssistantAnswerRules,
  AssistantTone,
} from './assistant-draft.model';
import type {
  AssistantAudience,
  AssistantConfigurationView,
  AssistantSourceReference,
} from './assistant.model';
import type { DatabaseReportSkipReason } from './database.model';

/**
 * 定期報表連續略過幾期後自動停用（#179，負責人決定 2026-10-05）。停用後不再產生略過紀錄，
 * 有權限的人可以重新啟用（重新檢查權限，從目前這一期重新開始，不補做停用期間）。
 */
export const PERIODIC_REPORT_AUTO_DISABLE_AFTER = 3;

/** 定期報表排程為什麼、何時自動停用；`message` 由伺服器（或 mock）組好，直接顯示。 */
export interface PeriodicReportAutoDisabledView {
  readonly disabledAt: string;
  /** 最後一期（使排程停用的那一期）的略過原因。 */
  readonly reason: DatabaseReportSkipReason;
  readonly skippedPeriods: number;
  readonly message: string;
}

/** 與後端 `ReportScheduleRules.AutoDisabledMessage` 同一段文字（mock 用）。 */
export function periodicReportAutoDisabledMessage(reason: DatabaseReportSkipReason): string {
  return reason === 'not-connected'
    ? `已自動停用：連續 ${PERIODIC_REPORT_AUTO_DISABLE_AFTER} 期沒有產生報表，最近一期是因為助理已不再連接這個數據庫。重新連接後可以重新啟用。`
    : `已自動停用：連續 ${PERIODIC_REPORT_AUTO_DISABLE_AFTER} 期沒有產生報表，最近一期是因為助理擁有者無法讀取這個數據庫的紀錄。恢復權限後可以重新啟用。`;
}

/**
 * 助理建立後可編輯的完整設定。和建立精靈的草稿不同：這裡的每一次變更都直接套用到
 * 已存在的助理身上，沒有「下一步」也沒有「建立」按鈕，所以每個欄位都各自驗證、
 * 各自保存，其中一項不合格不會擋住其他項目。
 */
export interface AssistantSettingsView {
  readonly configuration: AssistantConfigurationView;
  /** 已連接的來源，只有 id 與類型；不含來源內容。 */
  readonly sources: readonly AssistantSourceReference[];
  /**
   * 助理在對話中可以提議的案件類型（issue #254，決定 U）：擁有者從啟用中的類型挑，預設為空。
   * 包含之後被停用的類型（仍列出，才能移除；重新啟用前不會提議）。
   */
  readonly caseTypeIds: readonly string[];
  readonly tone: AssistantTone;
  readonly roleInstructions: string;
  readonly rules: AssistantAnswerRules;
  /**
   * 定期報表已自動停用（#179）時的原因；沒有停用（或沒有排程）時為 null。停用期間
   * `rules.periodicReport` 仍是原本的週期；再送一次週期（`rules.periodicReport`）就是重新啟用。
   */
  readonly periodicReportAutoDisabled: PeriodicReportAutoDisabledView | null;
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
  | 'tone'
  | 'roleInstructions'
  | 'knowledgeScope'
  | 'refusalMessage'
  | 'dataWritePurpose'
  | 'periodicReport'
  | 'sources'
  | 'caseTypeIds';

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
  // 定期報表（#150）報告的是「寫入的資料庫」；沒有寫入對象就沒有報表可以產生（與後端相同的說明）。
  if (settings.rules.periodicReport !== 'off' && settings.rules.dataWriteDatabaseId === null) {
    errors.push({
      field: 'periodicReport',
      message: '請先指定要寫入的資料庫，才能設定定期回報。',
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
