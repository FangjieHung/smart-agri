import type { AccountId } from './account.model';

export type DatabaseId = 'database-orders' | 'database-customer-records';

export type DatabaseStatus = 'connected' | 'disconnected' | 'sync-error';

export type DatabaseAccessMode = 'read-only';

export interface DatabaseView {
  readonly id: DatabaseId;
  readonly ownerAccountId: AccountId;
  readonly name: string;
  readonly status: DatabaseStatus;
  readonly accessMode: DatabaseAccessMode;
  readonly tableCount: number;
  readonly lastSyncedAt: string;
}
