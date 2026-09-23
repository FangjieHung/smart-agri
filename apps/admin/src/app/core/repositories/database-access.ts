import type { AccountId, AccountView } from '../domain/account.model';
import { ACCOUNT_ROLE_LABELS } from '../domain/team.model';
import type { DatabaseAccessCandidateView } from '../domain/database.model';

/**
 * 誰可以看到「收集紀錄」與「趨勢比較」。**這是唯一的判斷點**，
 * `getDatabaseTracking()`、資料庫摘要的紀錄數量與 `listManagedSubmissions()` 都走這裡。
 *
 * 要同時滿足兩件事，缺一不可：
 *
 * 1. **帳號層級**：團隊設定裡有「查看同意提交的紀錄」（`read-consented-submissions`）。
 *    這是組織給這個人的能力，在 `/app/settings` 變更。
 * 2. **資料庫層級**：被這個資料庫**指定為資料管理者**。這是單一資料庫的授權，
 *    在資料庫的「權限」頁籤變更。
 *
 * 擁有者**不會**自動通過：擁有者可以管理表單設定，但看得到誰的資料是另一件事，
 * 必須把自己列進資料管理者。這正是 §10 權限矩陣「僅能查看使用者明確同意提交的資料」
 * 的落點——擁有一個資料庫不等於有權讀它收到的內容。
 *
 * 兩層都只收回**查看權限**，不會刪除任何紀錄：移除之後紀錄仍在，重新指定就原封不動回來。
 */
export function canReadConsentedRecords(
  viewer: AccountView | undefined,
  dataManagerAccountIds: readonly AccountId[],
): boolean {
  if (viewer === undefined) return false;
  if (!viewer.permissions.includes('read-consented-submissions')) return false;
  return dataManagerAccountIds.includes(viewer.id);
}

/** 不存在與無權限共用同一句話，避免從差異推測資源或其他帳號的設定。 */
export const DATABASE_RECORDS_DENIED_MESSAGE =
  '只有被指定為資料管理者、且具備「查看同意提交的紀錄」權限的帳號，可以查看收集紀錄與趨勢比較。';

/**
 * 可以被指定為資料管理者的帳號：Demo 的三個身分都列出來，
 * 並標示這個帳號目前有沒有帳號層級的權限——指定了卻沒有權限仍然看不到，
 * 畫面要直說，不能讓擁有者以為指定完就生效了。
 */
export function databaseAccessCandidates(
  accounts: readonly AccountView[],
): readonly DatabaseAccessCandidateView[] {
  return accounts.map((account) => ({
    id: account.id,
    displayName: account.displayName,
    roleLabel: ACCOUNT_ROLE_LABELS[account.role],
    hasReadPermission: account.permissions.includes('read-consented-submissions'),
  }));
}

/** 只接受清單上的帳號；有不認得的 id 就整批不寫入。 */
export function normalizeDataManagers(
  accounts: readonly AccountView[],
  accountIds: readonly AccountId[],
): readonly AccountId[] | null {
  const known = accounts.map((account) => account.id);
  if (accountIds.some((id) => !known.includes(id))) return null;
  return known.filter((id) => accountIds.includes(id));
}
