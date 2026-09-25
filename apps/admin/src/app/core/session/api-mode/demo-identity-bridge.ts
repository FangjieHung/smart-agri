import type { AccountId, AccountRole } from '../../domain/account.model';

/**
 * API 帳號 → 同角色的 Demo 身分。其他功能區仍是 mock，mock 資料以 Demo 身分 id 為鍵，
 * 所以登入後把這個 id 交給 `DemoSessionService`，現有的 `activeAccountId()` 呼叫點都不用改。
 *
 * 只在「每個角色恰好一個帳號」時成立（M1 的種子資料如此）；各功能區換成真 API 後逐步移除。
 */
export const DEMO_ACCOUNT_BY_ROLE: Readonly<Record<AccountRole, AccountId>> = Object.freeze({
  'smb-admin': 'account-smb-admin',
  'internal-employee': 'account-internal-employee',
  'external-customer': 'account-external-customer',
});
