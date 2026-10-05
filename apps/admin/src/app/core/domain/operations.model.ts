/** Aggregated outcome data: no conversation, account, question, or answer fields. */
/** `database-query` (#178) is never in an assistant's analytics; it is counted on its own in the operations summary. */
export type AnswerReplyKind = "company-data" | "general-knowledge" | "no-result" | "database-query";
export type AnswerRejectionReason = "below-threshold" | "citation-out-of-range" | "no-citation" | "cannot-answer" | "empty-answer";
export interface ReplyKindCountView { readonly kind: AnswerReplyKind; readonly count: number; }
export interface RejectionReasonCountView { readonly reason: AnswerRejectionReason; readonly count: number; }
export interface CitedDocumentCountView { readonly documentId: string; readonly documentName: string; readonly count: number; }
export interface AssistantAnalyticsSummaryView {
  readonly from: string; readonly to: string; readonly totalReplies: number;
  readonly replyKinds: readonly ReplyKindCountView[]; readonly rejectionReasons: readonly RejectionReasonCountView[];
  readonly mostCitedDocuments: readonly CitedDocumentCountView[];
}
export interface AssistantOperationsView { readonly assistantId: string; readonly assistantName: string; readonly totalReplies: number; readonly noResultRate: number; readonly rejectedCitationRate: number; }
/** Conversation database query answers by result (#178): never mixed into the assistants' reply counts or rates. */
export interface DatabaseQueryOperationsView {
  readonly totalCount: number; readonly answeredCount: number; readonly notPermittedCount: number;
  readonly insufficientRecordsCount: number; readonly failedCount: number; readonly failureRate: number;
}
export interface OperationsSummaryView {
  readonly from: string; readonly to: string; readonly assistants: readonly AssistantOperationsView[];
  readonly mostCitedDocuments: readonly CitedDocumentCountView[];
  readonly knowledge: { readonly processingFailedCount: number; readonly overduePendingReviewCount: number };
  readonly issues: { readonly openCount: number; readonly averageResolutionHours: number | null };
  readonly databaseQueries: DatabaseQueryOperationsView;
}
