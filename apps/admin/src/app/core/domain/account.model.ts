export type AccountId =
  | 'account-smb-admin'
  | 'account-internal-employee'
  | 'account-external-customer';

export type AccountRole =
  'smb-admin' | 'internal-employee' | 'external-customer';

export type AccountPermission =
  | 'manage-assistants'
  | 'manage-data-sources'
  | 'manage-publishing'
  | 'read-consented-submissions'
  | 'use-shared-assistants'
  | 'submit-authorized-forms'
  | 'read-own-tracking';

export interface AccountView {
  readonly id: AccountId;
  readonly displayName: string;
  readonly role: AccountRole;
  readonly permissions: readonly AccountPermission[];
}
