import type { AccountId, AccountRole } from '../../domain/account.model';

/**
 * API 帳號 → 同角色的 Demo 身分。其他功能區仍是 mock，mock 資料以 Demo 身分 id 為鍵，
 * 所以登入後把這個 id 交給 `DemoSessionService`，現有的 `activeAccountId()` 呼叫點都不用改。
 *
 * 只在「每個角色恰好一個帳號」時成立（M1 的種子資料如此）——同組織有兩位同角色成員時，
 * 這裡仍然只能對應到其中一個 Demo 身分。M2（見 issue #36）已把這個對應**從團隊面板移除**，
 * 因為那裡本來就會踩到上述限制（改到錯的成員）；這裡刻意保留，因為 `activeAccountId()`
 * 只需要「目前登入者自己」這一筆，不受多人同角色影響。隨 M3 助理與對話換成 API 時，
 * `activeAccountId()` 這個呼叫點也會跟著移除，屆時整個檔案應可刪除。
 */
export const DEMO_ACCOUNT_BY_ROLE: Readonly<Record<AccountRole, AccountId>> = Object.freeze({
  'smb-admin': 'account-smb-admin',
  'internal-employee': 'account-internal-employee',
  'external-customer': 'account-external-customer',
});
