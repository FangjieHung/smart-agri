import type { AccountId } from './account.model';
import type { AssistantId } from './assistant.model';
import type { DatabaseId, DatabaseRecordEntryView, DatabaseTrialAnswers } from './database.model';
import type {
  ChatCaseProposalStatus,
  ChatCaseProposalView,
  ChatCitationId,
  ChatCitationView,
  ChatConsentView,
  ChatDatabaseQueryFigure,
  ChatDatabaseQueryStatus,
  ChatDatabaseQueryView,
  ChatFormView,
  ChatMessageId,
  ChatMessageView,
  ChatReplyKind,
  ChatReplyView,
  ChatThreadId,
  SubmissionWithdrawalStatus,
  SubmissionWithdrawalView,
} from '@smart-agri/chat';

// 回覆與訊息的顯示型別住在 `@smart-agri/chat`（官網訪客的對話視窗也要用）；
// admin 沿用原本的名稱與匯入路徑，這裡只是重新匯出。
export { REPLY_KIND_LABELS } from '@smart-agri/chat';
export type {
  ChatCaseProposalStatus,
  ChatCaseProposalView,
  ChatCitationId,
  ChatCitationView,
  ChatConsentView,
  ChatDatabaseQueryFigure,
  ChatDatabaseQueryStatus,
  ChatDatabaseQueryView,
  ChatFormView,
  ChatMessageId,
  ChatMessageView,
  ChatReplyKind,
  ChatReplyView,
  ChatThreadId,
  SubmissionWithdrawalStatus,
  SubmissionWithdrawalView,
};

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

/** 預先準備的回覆 id；新問題以關鍵字對應到其中一則，對應不到時回覆查無資料。 */
export type ChatResponseId =
  | 'chat-refund-window'
  | 'chat-leather-wash'
  | 'chat-leather-care'
  | 'chat-order-count'
  | 'chat-order-issue';

export interface ChatSuggestedPromptView {
  readonly id: ChatResponseId;
  readonly text: string;
}

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
