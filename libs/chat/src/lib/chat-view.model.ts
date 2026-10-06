/**
 * 對話訊息的顯示型別（從 admin 的 `conversation.model.ts`／`database.model.ts` 原樣搬來）。
 * 全是純資料：lib 不認得 admin 的帳號、助理或 repository 型別。
 */

/* ------------------------------------------------------------------ */
/* 表單欄位、收據列與統計期間：回覆裡的表單、收據與查詢結果要顯示它們。  */
/* ------------------------------------------------------------------ */

/**
 * mock 的 id 是 `SeededDatabaseId`／`CreatedDatabaseId`；API 模式是後端的 GUID（issue #142），
 * 所以和 `KnowledgeBaseId` 一樣放寬成 string。id 一律只拿來比對與組網址，不解析格式。
 */
export type DatabaseId = string;

export type DatabaseFieldType =
  | 'text'
  | 'number'
  | 'date'
  | 'single-choice'
  | 'multiple-choice'
  | 'scale';

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

export type DatabaseRecordId = `record-${string}`;

export interface DatabaseRecordEntryView {
  readonly fieldId: DatabaseFieldId;
  readonly label: string;
  readonly display: string;
}

/** 固定查詢接受的具名期間（日期一律是 UTC 曆日，週從週一開始）。 */
export type DatabasePeriodName =
  | 'this-week'
  | 'last-week'
  | 'this-month'
  | 'last-month'
  | 'last-7-days'
  | 'last-30-days';

/** 一段統計期間：起訖都含在內（YYYY-MM-DD，UTC）；前一期沒有名稱。 */
export interface DatabasePeriodRangeView {
  readonly name: DatabasePeriodName | null;
  readonly from: string;
  readonly to: string;
  readonly label: string;
}

/* ------------------------------------------------------------------ */
/* 對話訊息與回覆                                                       */
/* ------------------------------------------------------------------ */

/** mock 的固定 id 或 API 的 GUID，一律當成不透明字串（與 `ChatThreadId` 相同）。 */
export type ChatMessageId = string;

/** mock 的固定 id 或 API 的 GUID，一律當成不透明字串（與 `ChatThreadId` 相同）。 */
export type ChatCitationId = string;

export interface ChatCitationView {
  readonly id: ChatCitationId;
  readonly knowledgeBaseName: string;
  readonly documentName: string;
  readonly excerpt: string;
  /** YYYY-MM-DD，文件最後更新日期。 */
  readonly updatedLabel: string;
}

/**
 * 收據上的撤回狀態。
 * `available`：這筆紀錄還在，提交者本人可以撤回。
 * `withdrawn`：已撤回，接收單位只剩一筆不含內容的軌跡。
 * `unavailable`：這張收據指認不到紀錄，所以不提供撤回，也不假裝可以。
 */
export type SubmissionWithdrawalStatus = 'available' | 'withdrawn' | 'unavailable';

export interface SubmissionWithdrawalView {
  readonly status: SubmissionWithdrawalStatus;
  /** 已撤回時的日期（YYYY-MM-DD）；其餘狀態為空字串。 */
  readonly withdrawnDateLabel: string;
  /** 對這位發起者說明撤回的效果與限制；未登入訪客的版本會多一段分頁警告。 */
  readonly notice: string;
}

/** 送出前必須讓使用者看到的同意內容。 */
export interface ChatConsentView {
  readonly recipient: string;
  readonly purpose: string;
  readonly viewers: readonly string[];
  readonly sensitiveNotice: string;
  readonly withdrawalNotice: string;
}

export interface ChatFormView {
  readonly id: DatabaseId;
  readonly title: string;
  /**
   * 顯示這份表單時的表單版本（issue #148）。確認與送出都要帶回去：表單在這之間改版時，
   * 伺服器回 `form-version-changed`，不會把答案對到另一個版本。
   */
  readonly formVersion: number;
  readonly fields: readonly DatabaseFieldView[];
  readonly consent: ChatConsentView;
}

/**
 * 對話中數據庫查詢的結果（issue #149）。`answered`／`no-data` 有數字；`insufficient-data` 是紀錄不足以比較；
 * `not-available` 是沒有可以查詢的數據庫（未連接、無權限、已撤銷、不存在都是同一個結果，不透露任何名稱）；
 * `rejected` 是模型給了定義外的查詢條件，沒有執行；`failed` 是查詢本身失敗。
 */
export type ChatDatabaseQueryStatus =
  | 'answered'
  | 'no-data'
  | 'insufficient-data'
  | 'not-available'
  | 'rejected'
  | 'failed';

/** 一個數字：`display`／`previousDisplay`／`changeLabel` 都是伺服器（或 mock）算好的文字，畫面不重算。 */
export interface ChatDatabaseQueryFigure {
  readonly metric: string;
  readonly value: number;
  readonly display: string;
  readonly previousDisplay: string | null;
  readonly changeLabel: string | null;
}

/** 查詢回答的結構化內容：資料來源（數據庫與查詢種類）、統計期間與數字。 */
export interface ChatDatabaseQueryView {
  readonly status: ChatDatabaseQueryStatus;
  readonly databaseId: DatabaseId | null;
  readonly databaseName: string | null;
  /** 固定查詢的名稱（`record-count`、`field-sum`、`period-summary`、`subject-comparison`）。 */
  readonly query: string | null;
  readonly queryLabel: string | null;
  readonly period: DatabasePeriodRangeView | null;
  readonly previousPeriod: DatabasePeriodRangeView | null;
  /** 只算一位追蹤對象。 */
  readonly subjectOnly: boolean;
  readonly figures: readonly ChatDatabaseQueryFigure[];
  readonly message: string | null;
}

export type ChatReplyView =
  | {
      readonly kind: 'company-data';
      readonly text: string;
      /**
       * 助理關閉「顯示引用出處」時為空陣列——回答仍然標示成組織資料，
       * 只是不提供原文片段，`citationNotice` 會說明少了什麼。
       */
      readonly citations: readonly ChatCitationView[];
      /** 引用出處被規則關閉時的說明；照常顯示出處時為 null。 */
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
    }
  | {
      readonly kind: 'form-request';
      readonly text: string;
      /**
       * null：這份表單現在已無法使用（助理解除了連接、資料庫的分享或權限被收回、或已不是
       * 助理的表單），只會出現在重新讀取的舊訊息上（issue #148，API 模式每次讀取都重新授權）。
       */
      readonly form: ChatFormView | null;
    }
  | {
      readonly kind: 'submission-receipt';
      readonly text: string;
      readonly recipient: string;
      /**
       * 這次寫入收集紀錄的紀錄 id，撤回時用它指認。
       * null 代表這張收據無法指認紀錄（此版本之前保存的舊收據），因此不提供撤回。
       */
      readonly recordId: DatabaseRecordId | null;
      readonly entries: readonly DatabaseRecordEntryView[];
      readonly withdrawal: SubmissionWithdrawalView;
    }
  | {
      /**
       * 數據庫查詢的回答（issue #149）：模型只選固定查詢與參數，文字與數字都由伺服器依查詢結果組成。
       * 不能轉人工（數字來自只有提問者能讀的紀錄）。
       */
      readonly kind: 'database-query';
      readonly text: string;
      readonly query: ChatDatabaseQueryView;
    };

export type ChatReplyKind = ChatReplyView['kind'];

/**
 * 三種回答狀態使用不同標示，組織資料、一般知識與查無資料不混寫；精靈的試問（`TrialAnswerView`）
 * 只用得到前三個 key，`form-request`／`submission-receipt` 不牽涉試問（issue #82）。
 */
export const REPLY_KIND_LABELS: Readonly<Record<ChatReplyKind, string>> = {
  'company-data': '根據你的資料',
  'general-knowledge': '一般知識補充',
  'no-result': '查無資料',
  'form-request': '需要填寫資料',
  'submission-receipt': '資料已送出',
  'database-query': '數據庫查詢',
};

export type ChatMessageView =
  | {
      readonly id: ChatMessageId;
      readonly author: 'account';
      readonly text: string;
      readonly createdAt: string;
    }
  | {
      readonly id: ChatMessageId;
      readonly author: 'assistant';
      readonly reply: ChatReplyView;
      readonly createdAt: string;
    };

/**
 * 同一個帳號與同一個助理可以有多段對話；每一段是一個 thread。
 * 與 `KnowledgeBaseId` 相同：mock 的固定 id 或 API 的 GUID，一律當成不透明字串。
 */
export type ChatThreadId = string;
