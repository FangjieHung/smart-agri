import type { AccountId } from './account.model';
import type { AssistantId } from './assistant.model';
import type {
  DatabaseFieldView,
  DatabaseId,
  DatabasePeriodRangeView,
  DatabaseRecordEntryView,
  DatabaseRecordId,
  DatabaseTrialAnswers,
} from './database.model';

export type ConversationId =
  'conversation-employee-private' | 'conversation-customer-private';

export type ConversationMessageId =
  | 'message-employee-question'
  | 'message-employee-answer'
  | 'message-customer-question'
  | 'message-customer-answer';

export type ConversationStatus = 'active' | 'resolved';

export type MessageAuthor = 'account' | 'assistant';

export interface ConversationMessageView {
  readonly id: ConversationMessageId;
  readonly author: MessageAuthor;
  readonly text: string;
  readonly createdAt: string;
}

export interface PrivateConversationView {
  readonly id: ConversationId;
  readonly accountId: AccountId;
  readonly assistantId: AssistantId;
  readonly status: ConversationStatus;
  readonly messages: readonly ConversationMessageView[];
  readonly updatedAt: string;
}

export type StructuredSubmissionId = 'submission-customer-authorized';

export type SubmissionConsentStatus =
  'consented' | 'withdrawn' | 'not-consented';

export type SubmissionTrackingStatus = 'received' | 'in-review' | 'completed';

export type SubmissionFieldId = 'order-number' | 'contact-email' | 'issue';

export interface StructuredSubmissionFieldView {
  readonly id: SubmissionFieldId;
  readonly label: string;
  readonly value: string;
}

export interface StructuredSubmissionView {
  readonly id: StructuredSubmissionId;
  readonly assistantId: AssistantId;
  readonly submittedByAccountId: AccountId;
  readonly dataManagerAccountId: AccountId;
  readonly consentStatus: SubmissionConsentStatus;
  readonly trackingStatus: SubmissionTrackingStatus;
  readonly fields: readonly StructuredSubmissionFieldView[];
  readonly submittedAt: string;
}

export interface AuthorizedFormInput {
  readonly assistantId: AssistantId;
  readonly orderNumber: string;
  readonly contactEmail: string;
  readonly issue: string;
  readonly consent: boolean;
}

export type AnalyticsPeriod = 'last-7-days';

export interface AssistantAnalyticsView {
  readonly assistantId: AssistantId;
  readonly period: AnalyticsPeriod;
  readonly conversationCount: number;
  readonly resolvedCount: number;
  readonly helpfulRatingPercent: number;
}

/* ------------------------------------------------------------------ */
/* 終端使用者對話：所有回覆都來自 fixtures，不連接真實 AI。              */
/* ------------------------------------------------------------------ */

/** mock 的固定 id 或 API 的 GUID，一律當成不透明字串（與 `ChatThreadId` 相同）。 */
export type ChatMessageId = string;

/** mock 的固定 id 或 API 的 GUID，一律當成不透明字串（與 `ChatThreadId` 相同）。 */
export type ChatCitationId = string;

/** 預先準備的回覆 id；新問題以關鍵字對應到其中一則，對應不到時回覆查無資料。 */
export type ChatResponseId =
  | 'chat-refund-window'
  | 'chat-leather-wash'
  | 'chat-leather-care'
  | 'chat-order-count'
  | 'chat-order-issue';

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

export interface ChatSuggestedPromptView {
  readonly id: ChatResponseId;
  readonly text: string;
}

/**
 * 同一個帳號與同一個助理可以有多段對話；每一段是一個 thread。
 * 與 `KnowledgeBaseId` 相同：mock 的固定 id 或 API 的 GUID，一律當成不透明字串。
 */
export type ChatThreadId = string;

/**
 * 對話紀錄是否保存：`saved` 會寫入這個帳號專屬的儲存並列在側欄；
 * `not-saved` 代表助理的規則關閉了「保存自己的對話」，只有一段暫時對話。
 */
export type ChatHistoryMode = 'saved' | 'not-saved';

/** 側欄用的對話摘要；只含標題與數量，不含任何訊息文字。 */
export interface ChatThreadSummaryView {
  readonly id: ChatThreadId;
  /** 預設由第一則提問推導，使用者可以改名。 */
  readonly title: string;
  readonly messageCount: number;
  readonly updatedAt: string;
}

/** 目前帳號與某個助理的對話清單；依最後活動時間由新到舊排序。 */
export interface ChatThreadListView {
  readonly assistantId: AssistantId;
  readonly assistantName: string;
  readonly historyMode: ChatHistoryMode;
  /** `historyMode` 為 `not-saved` 時固定為空陣列。 */
  readonly threads: readonly ChatThreadSummaryView[];
  /** 側欄顯示的說明：保存規則與隱私範圍。 */
  readonly historyNotice: string;
}

/** 目前帳號與某個助理的一段私人對話；只會回傳給對話所屬的帳號。 */
export interface AssistantChatView {
  readonly assistantId: AssistantId;
  readonly assistantName: string;
  readonly purpose: string;
  /** 尚未建立任何對話、或助理不保存對話時為 null。 */
  readonly threadId: ChatThreadId | null;
  readonly title: string;
  readonly historyMode: ChatHistoryMode;
  readonly welcome: string;
  readonly privacyNotice: string;
  readonly suggestedPrompts: readonly ChatSuggestedPromptView[];
  readonly messages: readonly ChatMessageView[];
}

/** 同意前的確認內容：已驗證的填寫值，尚未建立任何紀錄。 */
export interface ChatFormReviewView {
  readonly formId: DatabaseId;
  readonly saved: false;
  readonly entries: readonly DatabaseRecordEntryView[];
}

export interface ChatFormSubmission {
  readonly formId: DatabaseId;
  /** 填寫時看到的表單版本（`ChatFormView.formVersion`）。 */
  readonly formVersion: number;
  /**
   * 冪等鍵（issue #148）：開始填寫時產生一次，失敗重試沿用同一個，伺服器不會重複建立紀錄；
   * 成功或衝突後才換新的。
   */
  readonly submissionId: string;
  readonly answers: DatabaseTrialAnswers;
  readonly consent: boolean;
}

/**
 * 對話中送出成功：要接在對話後面的收據訊息，以及它所在的對話（不保存對話的助理為 null，
 * 收據只在這一頁出現，紀錄本身仍存進資料庫）。
 */
export interface ChatFormSubmissionResultView {
  readonly message: ChatMessageView;
  readonly threadId: ChatThreadId | null;
}

/**
 * 側欄「最近 10 個對話串」用的一列；跨助理，只列出目前仍可使用的助理
 * （對應後端 `GET /api/v1/chat/recent-conversations`）。
 */
export interface RecentConversationView {
  readonly assistantId: AssistantId;
  readonly assistantName: string;
  readonly threadId: ChatThreadId;
  readonly title: string;
  readonly messageCount: number;
  readonly updatedAt: string;
}
