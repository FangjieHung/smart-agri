/** Aggregated outcome data: no conversation, account, question, or answer fields. */
export type AnswerReplyKind = "company-data" | "general-knowledge" | "no-result";
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
export interface OperationsSummaryView {
  readonly from: string; readonly to: string; readonly assistants: readonly AssistantOperationsView[];
  readonly mostCitedDocuments: readonly CitedDocumentCountView[];
  readonly knowledge: { readonly processingFailedCount: number; readonly overduePendingReviewCount: number };
  readonly issues: { readonly openCount: number; readonly averageResolutionHours: number | null };
}
