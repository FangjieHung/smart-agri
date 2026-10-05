import type { AccountId } from './account.model';
import type { AssistantId, AssistantStatus } from './assistant.model';

export type SeededDatabaseId =
  | 'database-orders'
  | 'database-customer-records'
  | 'database-staff-checkins';

/** 由模板建立的資料庫 id，格式固定為 `database-created-<序號>`。 */
export type CreatedDatabaseId = `database-created-${number}`;

/**
 * mock 的 id 是 `SeededDatabaseId`／`CreatedDatabaseId`；API 模式是後端的 GUID（issue #142），
 * 所以和 `KnowledgeBaseId` 一樣放寬成 string。id 一律只拿來比對與組網址，不解析格式。
 */
export type DatabaseId = string;

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

/** 儲存表單成功後的結果：新（或沒有變動時的現有）版本號與伺服器整理過的欄位。 */
export interface DatabaseFormSavedView {
  readonly formVersion: number;
  readonly fields: readonly DatabaseFieldView[];
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
  /** 建立者，也是唯一能修改設定的人；不代表看得到收集紀錄（見 `canReadConsentedRecords`）。 */
  readonly owner: DatabaseAccountView;
  /**
   * 目前帳號能不能開啟並修改它（只有擁有者）。資料管理者看得到資料庫，但這個值是 false，
   * 畫面要改成唯讀（issue #144）。
   */
  readonly viewerCanManage: boolean;
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

/** 可被指定為資料管理者的帳號；`hasReadPermission` 是帳號層級的權限現況。 */
export interface DatabaseAccessCandidateView {
  readonly id: AccountId;
  readonly displayName: string;
  readonly roleLabel: string;
  /** 帳號層級的「查看同意提交的紀錄」；false 代表指定了也還是看不到。 */
  readonly hasReadPermission: boolean;
}

export interface DatabaseAccessView {
  readonly owner: DatabaseAccountView;
  /** 已指定的資料管理者（資料庫層級）；不代表現在讀得到紀錄。 */
  readonly dataManagers: readonly DatabaseAccountView[];
  /** 目前實際可以讀紀錄的人：已指定 **且** 現在具備帳號層級權限（擁有者的預覽）。 */
  readonly effectiveReaders: readonly DatabaseAccountView[];
  /** 有沒有被指定為資料管理者（資料庫層級）。 */
  readonly viewerIsDataManager: boolean;
  /** 實際看不看得到收集紀錄：指定 **且** 具備帳號層級權限才是 true。 */
  readonly viewerCanReadRecords: boolean;
  /** 能不能在這個頁籤改指定；只有資料庫擁有者可以。 */
  readonly viewerCanManageAccess: boolean;
  readonly candidates: readonly DatabaseAccessCandidateView[];
  /** 最後一次變更指定的時間（ISO）；從未改過為 null。 */
  readonly savedAt: string | null;
  /** 最後一次變更指定的操作人；從未改過為 null。 */
  readonly savedBy: DatabaseAccountView | null;
}

/**
 * API 模式尚未提供、畫面要改成「將於後續版本開放」的功能（M4 依工單逐步開放）。M4 的功能已全部開放
 * （定期報表是最後一項，#150），所以目前沒有任何成員；`DatabaseDetailView.upcomingFeatures` 保留給之後
 * 又有只在 mock 先做的功能時使用。
 */
export type DatabaseUpcomingFeature = never;

export interface DatabaseDetailView {
  readonly summary: DatabaseSummaryView;
  /**
   * 目前表單的版本號（由模板建立時是 1，每次儲存表單加 1）。儲存時要帶回去，伺服器發現已有人
   * 存過新版就回 409，不會蓋掉別人的修改。
   */
  readonly formVersion: number;
  readonly fields: readonly DatabaseFieldView[];
  readonly connectedAssistants: readonly DatabaseConnectedAssistantView[];
  readonly access: DatabaseAccessView;
  /** 見 `DatabaseUpcomingFeature`；mock 模式為空陣列。 */
  readonly upcomingFeatures: readonly DatabaseUpcomingFeature[];
}

/** API 模式中尚未開放的功能，畫面上顯示的說明。 */
export const DATABASE_UPCOMING_FEATURE_MESSAGE = '這項功能將於後續版本開放。';

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

/* ------------------------------------------------------------------ */
/* 期間統計（#147）：固定查詢 `period-summary`，數字由 repository／伺服器算好 */
/* ------------------------------------------------------------------ */

/** 固定查詢接受的具名期間（日期一律是 UTC 曆日，週從週一開始）。 */
export type DatabasePeriodName =
  | 'this-week'
  | 'last-week'
  | 'this-month'
  | 'last-month'
  | 'last-7-days'
  | 'last-30-days';

export const DATABASE_PERIOD_OPTIONS: readonly { readonly id: DatabasePeriodName; readonly label: string }[] = [
  { id: 'this-week', label: '本週' },
  { id: 'last-week', label: '上週' },
  { id: 'this-month', label: '本月' },
  { id: 'last-month', label: '上月' },
  { id: 'last-7-days', label: '近 7 天' },
  { id: 'last-30-days', label: '近 30 天' },
];

/** 一段統計期間：起訖都含在內（YYYY-MM-DD，UTC）；前一期沒有名稱。 */
export interface DatabasePeriodRangeView {
  readonly name: DatabasePeriodName | null;
  readonly from: string;
  readonly to: string;
  readonly label: string;
}

/** 一個數字欄位在這一期與前一期的加總；`display` 與 `changeLabel` 已加上單位。 */
export interface DatabaseFieldSumView {
  readonly fieldId: DatabaseFieldId;
  readonly label: string;
  readonly unit: string;
  readonly sum: number;
  readonly display: string;
  /** 這一期有填這個欄位的有效紀錄筆數。 */
  readonly recordCount: number;
  readonly previousSum: number;
  readonly previousDisplay: string;
  readonly previousRecordCount: number;
  readonly change: number;
  readonly changeLabel: string;
}

export interface DatabasePeriodSummaryView {
  readonly period: DatabasePeriodRangeView;
  readonly previousPeriod: DatabasePeriodRangeView;
  /** 只算這位追蹤對象；null 為整個資料庫。 */
  readonly subjectId: TrackedSubjectId | null;
  readonly recordCount: number;
  readonly previousRecordCount: number;
  readonly recordCountChange: number;
  readonly recordCountChangeLabel: string;
  readonly sums: readonly DatabaseFieldSumView[];
}

export interface DatabasePeriodSummaryQuery {
  readonly period: DatabasePeriodName;
  readonly subjectId: TrackedSubjectId | null;
}

export interface DatabaseTrackingView {
  readonly databaseId: DatabaseId;
  readonly subjects: readonly TrackedSubjectView[];
}

/* ------------------------------------------------------------------ */
/* 定期報表（#150）：排程每期產生一份快照；統計與 AI 摘要分開保存          */
/* ------------------------------------------------------------------ */

/** AI 摘要永遠用這個名稱標示，與統計分開。 */
export const DATABASE_REPORT_SUMMARY_LABEL = 'AI 摘要';

export const DATABASE_REPORT_SUMMARY_DISCLAIMER = '由 AI 根據上方已算好的統計數字撰寫，僅供參考；一切數字以統計為準。';

/** 助理規則「定期回報」的週期（`off` 是沒有排程，不屬於報表本身）。 */
export type DatabaseReportFrequency = 'weekly' | 'monthly';

export const DATABASE_REPORT_FREQUENCY_LABELS: Readonly<Record<DatabaseReportFrequency, string>> = {
  weekly: '每週',
  monthly: '每月',
};

/** `generated` 已算出統計；`skipped` 該期到了但沒有產生（見 `DatabaseReportSkipReason`）。 */
export type DatabaseReportStatus = 'generated' | 'skipped';

/** 該期沒有產生報表的原因：助理已不再連接這個資料庫，或擁有者已不能讀取它的紀錄。 */
export type DatabaseReportSkipReason = 'not-connected' | 'owner-cannot-read';

/**
 * `sufficient`：這一期與前一期都有紀錄，變化才成立；`insufficient-records`（紀錄不足）：統計照存，
 * 但不顯示變化、趨勢，也不產生 AI 摘要。
 */
export type DatabaseReportDataState = 'sufficient' | 'insufficient-records';

/**
 * AI 摘要的狀態：`not-requested` 不產生（紀錄不足或沒有產生的期間）；`pending` 排隊或產生中；`ready`
 * 完成；`failed` 模型失敗（統計與圖表不受影響，可重試）；`discarded` 摘要含有統計沒有的數字，整段捨棄
 * （不顯示，可重試）。
 */
export type DatabaseReportSummaryStatus = 'not-requested' | 'pending' | 'ready' | 'failed' | 'discarded';

/** 報表清單的一列（不含統計）。日期是統計時區（後端 `Statistics:TimeZone`）的曆日 `YYYY-MM-DD`。 */
export interface DatabaseReportListItemView {
  readonly id: string;
  readonly assistantId: string;
  /** 產生當時助理的名稱（助理刪除後報表仍在）。 */
  readonly assistantName: string;
  readonly frequency: DatabaseReportFrequency;
  readonly periodFrom: string;
  readonly periodTo: string;
  readonly periodLabel: string;
  readonly status: DatabaseReportStatus;
  readonly skipReason: DatabaseReportSkipReason | null;
  /** 沒有產生的原因（給人看的一句話）；有產生時為 null。 */
  readonly skipMessage: string | null;
  readonly dataState: DatabaseReportDataState | null;
  /** 「紀錄不足」的說明；紀錄足夠或沒有產生時為 null。 */
  readonly dataMessage: string | null;
  /** 產生時間（ISO）。 */
  readonly generatedAt: string;
  readonly summaryStatus: DatabaseReportSummaryStatus;
}

/** 一位助理在這個資料庫的有效排程：多久一次，以及哪一天產生下一份。 */
export interface DatabaseReportScheduleView {
  readonly assistantId: string;
  readonly assistantName: string;
  readonly frequency: DatabaseReportFrequency;
  /** 下一份報表所涵蓋期間的第一天。 */
  readonly nextPeriodFrom: string;
  /** 下一份報表產生的日期（該期結束後的第二天）。 */
  readonly nextReportDate: string;
}

export interface DatabaseReportListView {
  readonly databaseId: DatabaseId;
  readonly schedules: readonly DatabaseReportScheduleView[];
  /** 新到舊（依涵蓋的期間）。 */
  readonly reports: readonly DatabaseReportListItemView[];
}

/** AI 摘要：與統計分開保存，永遠標示為「AI 摘要」；`text` 只在 `ready` 時有值。 */
export interface DatabaseReportAiSummaryView {
  readonly label: string;
  readonly status: DatabaseReportSummaryStatus;
  readonly text: string | null;
  /** 失敗或捨棄的說明。 */
  readonly note: string | null;
  readonly updatedAt: string | null;
  readonly disclaimer: string;
}

/**
 * 一份報表：統計是固定查詢 `period-summary` 的結果原樣保存（快照，之後撤回紀錄也不會改動它），
 * 沒有產生的期間 `statistics` 為 null。摘要另外保存，失敗時統計與圖表照常顯示。
 */
export interface DatabaseReportView {
  readonly report: DatabaseReportListItemView;
  readonly statistics: DatabasePeriodSummaryView | null;
  readonly aiSummary: DatabaseReportAiSummaryView;
}

/* ------------------------------------------------------------------ */
/* 試填：只驗證並預覽，不會儲存任何紀錄                                 */
/* ------------------------------------------------------------------ */

export type DatabaseTrialAnswer = string | readonly string[];

export type DatabaseTrialAnswers = Readonly<Partial<Record<DatabaseFieldId, DatabaseTrialAnswer>>>;

export interface DatabaseTrialPreviewView {
  readonly saved: false;
  /** 試填驗證所依據的表單版本（儲存過的最新一版）。 */
  readonly formVersion: number;
  readonly entries: readonly DatabaseRecordEntryView[];
}

/* ------------------------------------------------------------------ */
/* 同意提交與回執（issue #145）：表單連結的獨立填寫頁                    */
/* ------------------------------------------------------------------ */

/**
 * 送出前必須讓填寫者看到的提交資訊：收集目的、接收單位、**目前實際**可查看者（已指定且具備
 * 帳號權限的資料管理者）、敏感資料提示，以及填寫所依據的表單版本。
 */
export interface DatabaseSubmissionFormView {
  readonly databaseId: DatabaseId;
  readonly databaseName: string;
  readonly purpose: string;
  /** 接收單位，例如「安心商行（客戶資料庫）」。 */
  readonly recipient: string;
  readonly viewers: readonly string[];
  readonly sensitiveNotice: string;
  readonly withdrawalNotice: string;
  /** 送出時要帶回去；伺服器發現表單已改版就回 `conflict`，不寫入。 */
  readonly formVersion: number;
  readonly fields: readonly DatabaseFieldView[];
}

/** 一次送出：`submissionId` 是這一次填寫的冪等鍵，由前端產生，重試時沿用同一個。 */
export interface DatabaseSubmissionInput {
  readonly submissionId: string;
  readonly formVersion: number;
  /** 只有明確的 true 才算同意。 */
  readonly consent: boolean;
  readonly answers: DatabaseTrialAnswers;
}

/** 送出成功的回執：內容都是送出當下的快照，表單之後改版也不會改變。 */
export interface DatabaseSubmissionReceiptView {
  /** 提交 id（重新開啟回執用），不是回執編號。 */
  readonly id: string;
  /** 給填寫者看的回執編號，例如 `R-20261003-1A2B3C4D5E`。 */
  readonly receiptNumber: string;
  readonly submittedAt: string;
  readonly databaseId: DatabaseId;
  readonly databaseName: string;
  readonly purpose: string;
  readonly recipient: string;
  /** 送出當下的實際可查看者。 */
  readonly viewers: readonly string[];
  readonly formVersion: number;
  readonly source: DatabaseRecordSource;
  /** 填寫內容；**撤回後是空陣列**（內容已刪除，issue #146）。 */
  readonly entries: readonly DatabaseRecordEntryView[];
  /** 撤回時間（ISO）；仍有效時為 null。 */
  readonly withdrawnAt: string | null;
}

/* ------------------------------------------------------------------ */
/* 查看自己的紀錄與撤回（issue #146）                                     */
/* ------------------------------------------------------------------ */

/**
 * 提交者自己送出過的一筆資料（有效或已撤回），不含填寫內容；內容請開回執。
 * `databaseName` 是送出當下的名稱快照。
 */
export interface OwnDatabaseSubmissionView {
  /** 提交 id（開回執、撤回用），不是回執編號。 */
  readonly id: string;
  readonly receiptNumber: string;
  readonly submittedAt: string;
  readonly databaseId: DatabaseId;
  readonly databaseName: string;
  readonly formVersion: number;
  readonly source: DatabaseRecordSource;
  /** 撤回時間（ISO）；仍有效時為 null。 */
  readonly withdrawnAt: string | null;
}
