import type { AccountId } from './account.model';
import type { AssistantId } from './assistant.model';

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
