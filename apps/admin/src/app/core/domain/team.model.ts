import type {
  AccountId,
  AccountPermission,
  AccountRole,
  AccountView,
} from './account.model';

/**
 * 團隊與權限的畫面模型。
 *
 * Demo 的「團隊」就是 `/login` 那三個 Demo 身分，**不是真實身分系統**：這裡沒有邀請、
 * 沒有離職、沒有密碼，成員清單固定就是 `demo-seed.ts` 的帳號。可以變更的只有「每個
 * 成員被允許做什麼」，而且只改得動**程式碼真的會檢查的那幾個權限**——哪些會、哪些
 * 不會，逐條寫在 `ACCOUNT_PERMISSIONS` 的 `enforcedNote` 裡，不在畫面上假裝。
 */

export const ACCOUNT_ROLE_LABELS: Readonly<Record<AccountRole, string>> = {
  'smb-admin': '管理者',
  'internal-employee': '內部同仁',
  'external-customer': '外部客戶',
};

export const ACCOUNT_ROLE_DESCRIPTIONS: Readonly<Record<AccountRole, string>> = {
  'smb-admin': '建立與設定助理、管理知識庫與資料庫、設定發布管道，並指定誰可以查看收集紀錄。',
  'internal-employee': '使用團隊分享的助理，並管理自己建立的知識庫與資料庫。',
  'external-customer': '使用分享給自己的助理、填寫授權表單，並查看自己的追蹤紀錄。',
};

/** 權限在 Demo 裡是否真的被檢查；`false` 代表目前只是宣告值。 */
export interface AccountPermissionDescriptor {
  readonly id: AccountPermission;
  readonly label: string;
  /** 勾選之後這個成員可以做什麼。 */
  readonly description: string;
  readonly enforced: boolean;
  /** 檢查點在哪裡，或為什麼還沒有檢查點。畫面直接顯示這句話。 */
  readonly enforcedNote: string;
}

export const ACCOUNT_PERMISSIONS: readonly AccountPermissionDescriptor[] = [
  {
    id: 'manage-assistants',
    label: '管理助理與團隊',
    description: '建立助理、編輯助理設定，以及變更這個頁面上的團隊成員權限。',
    enforced: true,
    enforcedNote: '已接到行為：建立與編輯助理、團隊設定都會檢查。',
  },
  {
    id: 'manage-data-sources',
    label: '管理資料來源',
    description: '建立知識庫、從模板建立資料庫、取得資料庫模板。',
    enforced: true,
    enforcedNote: '已接到行為：建立知識庫、建立資料庫與取得模板都會檢查。',
  },
  {
    id: 'manage-publishing',
    label: '管理發布管道',
    description: '設定平台內分享、官網嵌入與 LINE 三種發布管道。',
    enforced: true,
    enforcedNote: '已接到行為：發布管道的讀取與儲存都會檢查（另外仍需是助理擁有者）。',
  },
  {
    id: 'read-consented-submissions',
    label: '查看同意提交的紀錄',
    description: '查看使用者明確同意提交的結構化紀錄與趨勢比較。',
    enforced: true,
    enforcedNote:
      '已接到行為：收集紀錄與趨勢比較都會檢查（另外仍需被該資料庫指定為資料管理者）。',
  },
  {
    id: 'submit-authorized-forms',
    label: '填寫授權表單',
    description: '在取得同意後提交助理提供的授權表單。',
    enforced: true,
    enforcedNote: '已接到行為：提交授權表單會檢查。',
  },
  {
    id: 'use-shared-assistants',
    label: '使用分享的助理',
    description: '開啟別人分享的助理。',
    enforced: true,
    enforcedNote:
      '已接到行為：開啟別人的助理要有這個權限，而且在該助理的平台內分享名單內、管道沒有暫停（canOpenInPlatform()，與後端相同）。' +
      '自己的助理不受影響；助理的「使用對象」只是預計給誰用，不影響存取。',
  },
  {
    id: 'read-own-tracking',
    label: '查看自己的紀錄',
    description: '（目前不影響任何畫面）查看自己提交的追蹤紀錄。',
    enforced: false,
    enforcedNote:
      '尚未接到行為：自己的紀錄一律只依提交者本人判斷，不另外檢查權限；取消勾選不會讓任何人看不到自己的資料。',
  },
  {
    id: 'handle-assistant-issues',
    label: '處理助理的處理事項',
    description: '可以被指派助理的處理事項（未通過的測試題、成員轉給專人的問題），並更新處理狀態與紀錄。',
    enforced: true,
    enforcedNote: '已接到行為：指派處理事項時會檢查，被指派的人也要有這個權限才看得到指派給自己的事項。',
  },
];

export function permissionDescriptor(
  id: AccountPermission,
): AccountPermissionDescriptor | undefined {
  return ACCOUNT_PERMISSIONS.find((permission) => permission.id === id);
}

export interface TeamMemberView {
  readonly id: AccountId;
  readonly displayName: string;
  readonly role: AccountRole;
  readonly roleLabel: string;
  readonly roleDescription: string;
  readonly permissions: readonly AccountPermission[];
  /** 這一列是不是目前登入的 Demo 身分。 */
  readonly isViewer: boolean;
  /** 不能在這個頁面取消的權限；取消會讓操作者失去團隊設定的入口。 */
  readonly lockedPermissions: readonly AccountPermission[];
}

export interface TeamView {
  readonly members: readonly TeamMemberView[];
  readonly permissions: readonly AccountPermissionDescriptor[];
  readonly savedAt: string | null;
}

export function isAccountPermission(value: unknown): value is AccountPermission {
  return ACCOUNT_PERMISSIONS.some((permission) => permission.id === value);
}

/** 只有操作者自己的「管理助理與團隊」鎖著：取消它等於把自己關在團隊設定外面。 */
export function lockedPermissionsFor(
  member: AccountView,
  viewerAccountId: AccountId,
): readonly AccountPermission[] {
  return member.id === viewerAccountId &&
    member.permissions.includes('manage-assistants')
    ? ['manage-assistants']
    : [];
}

/** 回傳拒絕的原因，或 `null` 代表可以套用。訊息直接顯示給使用者。 */
export function validateMemberPermissions(
  member: AccountView,
  viewerAccountId: AccountId,
  next: readonly AccountPermission[],
): string | null {
  if (next.some((permission) => !isAccountPermission(permission))) {
    return '有不認得的權限值，這次變更沒有儲存。';
  }

  const locked = lockedPermissionsFor(member, viewerAccountId);
  const removed = locked.filter((permission) => !next.includes(permission));
  if (removed.length > 0) {
    return '不能移除自己的「管理助理與團隊」權限：移除後就打不開團隊設定，也沒有別的入口可以加回來。';
  }

  return null;
}

/** 依 `ACCOUNT_PERMISSIONS` 的順序正規化，讓儲存與比較有穩定順序。 */
export function normalizeMemberPermissions(
  next: readonly AccountPermission[],
): readonly AccountPermission[] {
  return ACCOUNT_PERMISSIONS.filter((permission) =>
    next.includes(permission.id),
  ).map((permission) => permission.id);
}
