import type { AccountId } from './account.model';
import type { DatabaseId } from './database.model';
import type { KnowledgeBaseId } from './knowledge-base.model';

export type AssistantId =
  | 'assistant-customer-service'
  | 'assistant-internal-onboarding';

export type AssistantStatus = 'draft' | 'ready' | 'published' | 'paused';

export type AssistantAudience =
  'account-members' | 'authorized-external-customers';

export type AssistantPermission = 'use' | 'configure' | 'publish';

export type AssistantSourceType = 'knowledge-base' | 'database';

export type AssistantSourceReference =
  | {
      readonly id: KnowledgeBaseId;
      readonly type: 'knowledge-base';
    }
  | {
      readonly id: DatabaseId;
      readonly type: 'database';
    };

export interface AssistantSummaryView {
  readonly id: AssistantId;
  readonly name: string;
  readonly purpose: string;
  readonly status: AssistantStatus;
  readonly audience: AssistantAudience;
  readonly permission: AssistantPermission;
}

export interface AssistantConfigurationView {
  readonly id: AssistantId;
  readonly ownerAccountId: AccountId;
  readonly name: string;
  readonly purpose: string;
  readonly status: AssistantStatus;
  readonly audience: AssistantAudience;
  readonly sharedWithAccountIds: readonly AccountId[];
  readonly knowledgeBaseIds: readonly KnowledgeBaseId[];
  readonly databaseIds: readonly DatabaseId[];
}
