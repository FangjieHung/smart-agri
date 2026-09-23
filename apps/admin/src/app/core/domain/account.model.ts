export type AccountId =
  | 'account-smb-admin'
  | 'account-internal-employee'
  | 'account-external-customer';

/**
 * 未登入官網訪客的識別碼。它**不是帳號**：只存在於單一瀏覽器分頁，
 * 沒有任何權限，也不會出現在 `listAccounts()` 的清單中。
 */
export type VisitorId = `visitor-${string}`;

/** 對話的發起者：已選擇的 Demo 身分，或從嵌入頁進來的未登入訪客。 */
export type ChatViewerId = AccountId | VisitorId;

/** 未登入訪客與 Demo 帳號的唯一區分方式；兩邊的 id 命名空間不重疊。 */
export function isVisitorId(viewerId: ChatViewerId): viewerId is VisitorId {
  return viewerId.startsWith('visitor-');
}

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
