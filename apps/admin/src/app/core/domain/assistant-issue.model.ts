import type { components } from '../api/api-schema';

export type AssistantIssueView = components['schemas']['AssistantIssueView'];
export type AssistantIssueDetailView = components['schemas']['AssistantIssueDetailView'];
export type AssistantIssueSummaryView = components['schemas']['AssistantIssueSummaryView'];
export type AssistantIssueEventView = components['schemas']['AssistantIssueEventView'];
export type AssistantIssueStatus = components['schemas']['AssistantIssueStatus'];
export type CreateAssistantIssueRequest = components['schemas']['CreateAssistantIssueRequest'];
export type UpdateAssistantIssueRequest = components['schemas']['UpdateAssistantIssueRequest'];

export type AssistantIssueScope = 'all' | 'owned' | 'assigned' | 'forwarded';

export interface AssistantIssueFilters {
  readonly scope?: AssistantIssueScope;
  readonly status?: AssistantIssueStatus;
  readonly assistantId?: string;
}

export interface AssistantIssueActionError {
  readonly status: 'validation-failed' | 'conflict' | 'permission-denied';
  readonly reason: string;
  readonly message: string;
  readonly issueId?: string;
}

export type AssistantIssueActionResult<T> =
  | { readonly status: 'ready'; readonly data: T }
  | AssistantIssueActionError;
