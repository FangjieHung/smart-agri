import type { AccountId } from './account.model';
import type { AssistantId } from './assistant.model';
import type {
  DatabaseFieldView,
  DatabaseId,
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

export type ChatMessageId = `chat-message-${number}`;

export type ChatCitationId = `citation-${string}`;

/** 預先準備的回覆 id；新問題以關鍵字對應到其中一則，對應不到時回覆查無資料。 */
export type ChatResponseId =
  | 'chat-refund-window'
  | 'chat-leather-wash'
  | 'chat-leather-care'
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
  readonly fields: readonly DatabaseFieldView[];
  readonly consent: ChatConsentView;
}

export type ChatReplyView =
  | {
      readonly kind: 'company-data';
      readonly text: string;
      readonly citations: readonly ChatCitationView[];
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
      readonly form: ChatFormView;
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
    };

export type ChatReplyKind = ChatReplyView['kind'];

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

/** 同一個帳號與同一個助理可以有多段對話；每一段是一個 thread。 */
export type ChatThreadId = `chat-thread-${number}`;

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
  readonly answers: DatabaseTrialAnswers;
  readonly consent: boolean;
}
