import type { AccountId } from './account.model';

export type KnowledgeBaseId =
  | 'knowledge-product-guide'
  | 'knowledge-refund-policy'
  | 'knowledge-shipping-faq';

export type KnowledgeBaseStatus = 'ready' | 'syncing' | 'sync-error';

export interface KnowledgeBaseView {
  readonly id: KnowledgeBaseId;
  readonly ownerAccountId: AccountId;
  readonly name: string;
  readonly status: KnowledgeBaseStatus;
  readonly documentCount: number;
  readonly lastSyncedAt: string;
}
