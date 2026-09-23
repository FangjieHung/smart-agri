import type { AccountId } from './account.model';
import type { AssistantId, AssistantStatus } from './assistant.model';

export type SeededDatabaseId =
  | 'database-orders'
  | 'database-customer-records'
  | 'database-staff-checkins';

/** 由模板建立的資料庫 id，格式固定為 `database-created-<序號>`。 */
export type CreatedDatabaseId = `database-created-${number}`;

export type DatabaseId = SeededDatabaseId | CreatedDatabaseId;

export type DatabaseStatus = 'connected' | 'disconnected' | 'sync-error';

export type DatabaseAccessMode = 'read-only';

export interface DatabaseView {
  readonly id: DatabaseId;
  readonly ownerAccountId: AccountId;
  readonly name: string;
  readonly status: DatabaseStatus;
  readonly accessMode: DatabaseAccessMode;
  readonly tableCount: number;
  readonly lastSyncedAt: string;
}

/* ------------------------------------------------------------------ */
/* 表單欄位：第一版只支援六種常見類型，不含條件跳題或公式。             */
/* ------------------------------------------------------------------ */

export type DatabaseFieldType =
  | 'text'
  | 'number'
  | 'date'
  | 'single-choice'
  | 'multiple-choice'
  | 'scale';

export const DATABASE_FIELD_TYPES: readonly DatabaseFieldType[] = [
  'text',
  'number',
  'date',
  'single-choice',
  'multiple-choice',
  'scale',
];

export type DatabaseFieldId = `field-${string}`;

export interface DatabaseScaleRange {
  readonly min: number;
  readonly max: number;
  readonly minLabel: string;
  readonly maxLabel: string;
}

export interface DatabaseFieldView {
  readonly id: DatabaseFieldId;
  readonly label: string;
  readonly type: DatabaseFieldType;
  readonly required: boolean;
  /** 單選、多選的選項；其他類型為空陣列。 */
  readonly options: readonly string[];
  /** 量尺範圍；只有量尺類型有值。 */
  readonly scale: DatabaseScaleRange | null;
  /** 數字欄位的單位，例如「元」；其他類型為空字串。 */
  readonly unit: string;
}

export function isChoiceFieldType(type: DatabaseFieldType): boolean {
  return type === 'single-choice' || type === 'multiple-choice';
}

const DEFAULT_SCALE: DatabaseScaleRange = { min: 1, max: 5, minLabel: '很低', maxLabel: '很高' };

/** 新增欄位或切換類型時使用的預設值（編輯用途，不涉及業務計算）。 */
export function createDatabaseField(
  id: DatabaseFieldId,
  type: DatabaseFieldType,
  base: Partial<Pick<DatabaseFieldView, 'label' | 'required'>> = {},
): DatabaseFieldView {
  return {
    id,
    label: base.label ?? '',
    type,
    required: base.required ?? false,
    options: isChoiceFieldType(type) ? ['選項一', '選項二'] : [],
    scale: type === 'scale' ? DEFAULT_SCALE : null,
    unit: '',
  };
}

/** fieldId 為 null 表示整份表單層級的錯誤。 */
export interface DatabaseFieldError {
  readonly fieldId: DatabaseFieldId | null;
  readonly message: string;
}

/* ------------------------------------------------------------------ */
/* 模板、摘要與詳情                                                   */
/* ------------------------------------------------------------------ */

export type DatabaseTemplateId =
  | 'template-customer-profile'
  | 'template-periodic-report'
  | 'template-satisfaction'
  | 'template-progress'
  | 'template-blank';

export interface DatabaseTemplateView {
  readonly id: DatabaseTemplateId;
  readonly name: string;
  readonly description: string;
  readonly fields: readonly DatabaseFieldView[];
}

export interface CreateDatabaseInput {
  readonly templateId: DatabaseTemplateId;
  readonly name: string;
}

export interface DatabaseSummaryView {
  readonly id: DatabaseId;
  readonly name: string;
  readonly purpose: string;
  readonly templateName: string;
  readonly fieldCount: number;
  /** 目前帳號不是指定資料管理者時為 null，不透露紀錄數量。 */
  readonly recordCount: number | null;
  readonly subjectCount: number | null;
  readonly connectedAssistantNames: readonly string[];
  readonly updatedAt: string;
}

export interface DatabaseConnectedAssistantView {
  readonly id: AssistantId;
  readonly name: string;
  readonly status: AssistantStatus;
}

export interface DatabaseAccountView {
  readonly id: AccountId;
  readonly displayName: string;
}

export interface DatabaseAccessView {
  readonly owner: DatabaseAccountView;
  readonly dataManagers: readonly DatabaseAccountView[];
  readonly viewerIsDataManager: boolean;
}

export interface DatabaseDetailView {
  readonly summary: DatabaseSummaryView;
  readonly fields: readonly DatabaseFieldView[];
  readonly connectedAssistants: readonly DatabaseConnectedAssistantView[];
  readonly access: DatabaseAccessView;
}

/* ------------------------------------------------------------------ */
/* 收集紀錄、時間軸與比較（差異值一律由 repository 預先算好）           */
/* ------------------------------------------------------------------ */

export type TrackedSubjectId = `subject-${string}`;

export type DatabaseRecordId = `record-${string}`;

export type DatabaseRecordSource = 'assistant-conversation' | 'form-link';

/** fixture 保存的原始值；label 為提交當下的欄位名稱快照。 */
export type DatabaseRecordValue =
  | {
      readonly fieldId: DatabaseFieldId;
      readonly label: string;
      readonly type: 'text' | 'date' | 'single-choice';
      readonly value: string;
    }
  | {
      readonly fieldId: DatabaseFieldId;
      readonly label: string;
      readonly type: 'number';
      readonly value: number;
      readonly unit: string;
    }
  | {
      readonly fieldId: DatabaseFieldId;
      readonly label: string;
      readonly type: 'scale';
      readonly value: number;
      readonly min: number;
      readonly max: number;
    }
  | {
      readonly fieldId: DatabaseFieldId;
      readonly label: string;
      readonly type: 'multiple-choice';
      readonly value: readonly string[];
    };

export interface DatabaseRecordEntryView {
  readonly fieldId: DatabaseFieldId;
  readonly label: string;
  readonly display: string;
}

export interface DatabaseRecordView {
  readonly id: DatabaseRecordId;
  readonly recordedAt: string;
  /** YYYY-MM-DD，與時區無關的顯示日期。 */
  readonly dateLabel: string;
  readonly source: DatabaseRecordSource;
  readonly entries: readonly DatabaseRecordEntryView[];
}

export interface ComparisonPointView {
  readonly recordId: DatabaseRecordId;
  readonly dateLabel: string;
  readonly value: number;
  readonly display: string;
}

export type TrendDirection = 'up' | 'down' | 'flat';

export interface MetricComparisonView {
  readonly fieldId: DatabaseFieldId;
  readonly label: string;
  readonly first: ComparisonPointView;
  readonly previous: ComparisonPointView;
  readonly current: ComparisonPointView;
  readonly changeFromPrevious: number;
  readonly changeFromFirst: number;
  readonly changeFromPreviousLabel: string;
  readonly changeFromFirstLabel: string;
  readonly direction: TrendDirection;
  /** 依時間先後排列，供趨勢圖與資料表使用。 */
  readonly points: readonly ComparisonPointView[];
  /** 圖表縱軸範圍：量尺使用量尺上下限，數字使用紀錄的最小與最大值。 */
  readonly axis: { readonly min: number; readonly max: number };
  /** 已算好的差異轉成的文字摘要。 */
  readonly summary: string;
}

export type SubjectComparisonView =
  | {
      readonly status: 'insufficient-records';
      readonly recordCount: number;
      readonly message: string;
    }
  | {
      readonly status: 'available';
      readonly recordCount: number;
      readonly summary: string;
      readonly metrics: readonly MetricComparisonView[];
    };

/**
 * 已撤回同意的紀錄軌跡：內容已從收集紀錄移除，只留下曾經提交與撤回的時間與來源，
 * 讓資料管理者仍能查對「有一筆資料存在過、後來被撤回」，而不是無聲消失。
 */
export interface WithdrawnRecordView {
  readonly id: DatabaseRecordId;
  readonly submittedAt: string;
  /** YYYY-MM-DD，與時區無關的顯示日期。 */
  readonly submittedDateLabel: string;
  /** 撤回日期（YYYY-MM-DD）；Demo 種子資料沒有記錄時為空字串。 */
  readonly withdrawnDateLabel: string;
  readonly source: DatabaseRecordSource;
}

export interface TrackedSubjectView {
  readonly id: TrackedSubjectId;
  readonly displayName: string;
  /** 由新到舊排列，只包含使用者明確同意提交的紀錄。 */
  readonly records: readonly DatabaseRecordView[];
  /** 由新到舊排列的撤回軌跡；不含任何填寫內容，也不計入 `comparison`。 */
  readonly withdrawals: readonly WithdrawnRecordView[];
  readonly comparison: SubjectComparisonView;
}

export interface DatabaseTrackingView {
  readonly databaseId: DatabaseId;
  readonly subjects: readonly TrackedSubjectView[];
}

/* ------------------------------------------------------------------ */
/* 試填：只驗證並預覽，不會儲存任何紀錄                                 */
/* ------------------------------------------------------------------ */

export type DatabaseTrialAnswer = string | readonly string[];

export type DatabaseTrialAnswers = Readonly<Partial<Record<DatabaseFieldId, DatabaseTrialAnswer>>>;

export interface DatabaseTrialPreviewView {
  readonly saved: false;
  readonly entries: readonly DatabaseRecordEntryView[];
}
