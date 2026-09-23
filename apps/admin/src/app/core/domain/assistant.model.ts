import type { AccountId } from './account.model';
import type { DatabaseId } from './database.model';
import type { KnowledgeBaseId } from './knowledge-base.model';

export type SeededAssistantId =
  | 'assistant-customer-service'
  | 'assistant-internal-onboarding';

/** 由建立精靈產生的助理 id，格式固定為 `assistant-created-<序號>`。 */
export type CreatedAssistantId = `assistant-created-${number}`;

export type AssistantId = SeededAssistantId | CreatedAssistantId;

export type AssistantStatus = 'draft' | 'ready' | 'published' | 'paused';

export type AssistantAudience =
  | 'account-members'
  | 'authorized-external-customers'
  | 'members-and-external-customers';

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
  /**
   * 建立精靈「保存自己的對話」的結果。false 時使用者的對話不寫入任何儲存，
   * 也不會出現在對話紀錄側欄；離開頁面就消失。
   */
  readonly keepOwnConversations: boolean;
}
