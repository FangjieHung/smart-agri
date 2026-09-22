import type { AccountId } from './account.model';
import type { AssistantId } from './assistant.model';
import type {
  DatabaseFieldView,
  DatabaseId,
  DatabaseRecordEntryView,
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
      readonly entries: readonly DatabaseRecordEntryView[];
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

/** 目前帳號與某個助理的私人對話；只會回傳給對話所屬的帳號。 */
export interface AssistantChatView {
  readonly assistantId: AssistantId;
  readonly assistantName: string;
  readonly purpose: string;
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
