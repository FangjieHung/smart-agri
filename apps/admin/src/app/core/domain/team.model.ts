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
 * 沒有密碼，成員清單固定就是 `demo-seed.ts` 的帳號（加上這台瀏覽器新增的成員）；停用成員
 * （issue #356）與後端規則相同，只是存在這台瀏覽器。可以變更的只有「每個
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
  /** 停用的時間（issue #356）；使用中是 `null`。停用的成員不能登入，清單標示「已停用」。 */
  readonly deactivatedAt: string | null;
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

// ---------- 停用成員並指定接手人（issue #356） ----------
// 規則與訊息與後端 `MemberOffboardingRules` 相同；mock 直接用這裡的函式，API 模式由後端判斷。

/** 為什麼現在不能停用這位成員。 */
export type MemberDeactivationBlock = 'self' | 'last-admin' | 'already-deactivated' | 'admin-required';

/** 為什麼這個帳號不能當接手人。 */
export type SuccessorIneligibleReason = 'external-customer' | 'missing-permissions' | 'deactivated';

/** 停用時會交給接手人的東西（數量）。 */
export interface MemberHoldingsView {
  readonly assistants: number;
  readonly drafts: number;
  readonly knowledgeBases: number;
  readonly databases: number;
  /** 指派給他、還沒解決的處理事項。 */
  readonly openIssues: number;
  /** 他是承辦人、還沒結案的案件。 */
  readonly openCases: number;
  /** 他的助理用到、但屬於別人的知識庫或數據庫（別人分享給他的）：不會轉移。 */
  readonly borrowedSources: number;
}

export interface SuccessorCandidateView {
  readonly id: AccountId;
  readonly displayName: string;
  readonly role: AccountRole;
  readonly eligible: boolean;
  readonly ineligibleReason: SuccessorIneligibleReason | null;
  readonly missingPermissions: readonly AccountPermission[];
  /** 具備「處理助理的處理事項」：處理事項會改指派給他，否則改成未指派。 */
  readonly canHandleIssues: boolean;
}

export interface MemberOffboardingView {
  readonly memberId: AccountId;
  readonly displayName: string;
  readonly block: MemberDeactivationBlock | null;
  readonly blockMessage: string | null;
  readonly holdings: MemberHoldingsView;
  readonly requiredPermissions: readonly AccountPermission[];
  /** 名下有東西要轉移時才需要選接手人（issue #361）；沒有時對話框不顯示「選擇接手人」。 */
  readonly successorRequired: boolean;
  /** 其他使用中的成員（依角色、名稱排序），不論是否符合資格。 */
  readonly candidates: readonly SuccessorCandidateView[];
}

/** 停用後實際交給接手人的數量。 */
export interface MemberTransferCountsView {
  readonly assistants: number;
  readonly drafts: number;
  readonly knowledgeBases: number;
  readonly databases: number;
  readonly issuesReassigned: number;
  readonly issuesUnassigned: number;
  readonly cases: number;
}

export interface DeactivatedMemberView {
  readonly team: TeamView;
  readonly transferred: MemberTransferCountsView;
}

export const MEMBER_DEACTIVATION_MESSAGES: Readonly<Record<MemberDeactivationBlock, string>> = {
  self: '不能停用自己的帳號。',
  'last-admin': '這是組織裡最後一位使用中的管理者，停用後就沒有人能管理組織設定。請先新增另一位管理者再停用。',
  'already-deactivated': '這位成員已經停用。',
  'admin-required': '只有管理者能停用或重新啟用另一位管理者。',
};

export const MEMBER_NOT_DEACTIVATED_MESSAGE = '這位成員目前沒有停用。';

/** 非管理者變更管理者的權限（issue #368；後端 `422 admin-required`，訊息相同）。 */
export const MEMBER_PERMISSIONS_ADMIN_REQUIRED_MESSAGE = '只有管理者能變更另一位管理者的權限。';

/** 非管理者新增管理者（issue #368；後端 `422 admin-required`，訊息相同）。 */
export const CREATE_ADMIN_REQUIRED_MESSAGE = '只有管理者能新增管理者。';
export const SUCCESSOR_REQUIRED_MESSAGE = '請選擇接手人。';
export const SUCCESSOR_NOT_ELIGIBLE_MESSAGE =
  '接手人必須是同一組織裡仍在使用中的內部成員，並具備管理這些資產需要的權限。';

/** 處理事項與案件的數量只在對應的 repository 裡（mock 存在這次工作階段），這裡先放 0。 */
export const NO_HOLDINGS: MemberHoldingsView = {
  assistants: 0, drafts: 0, knowledgeBases: 0, databases: 0, openIssues: 0, openCases: 0, borrowedSources: 0,
};

/** 沒有選接手人（名下沒有東西要轉移）時的轉移數量。 */
export const NO_TRANSFERS: MemberTransferCountsView = {
  assistants: 0, drafts: 0, knowledgeBases: 0, databases: 0, issuesReassigned: 0, issuesUnassigned: 0, cases: 0,
};

/**
 * 接手人需要的權限：有助理或草稿就要「管理助理與團隊」，有知識庫或數據庫就要「管理資料來源」。
 * 其他依權限而定的行為各自會降級（例如沒有「處理助理的處理事項」時處理事項改成未指派），不擋。
 */
export function requiredSuccessorPermissions(holdings: MemberHoldingsView): readonly AccountPermission[] {
  const required: AccountPermission[] = [];
  if (holdings.assistants + holdings.drafts > 0) required.push('manage-assistants');
  if (holdings.knowledgeBases + holdings.databases > 0) required.push('manage-data-sources');
  return normalizeMemberPermissions(required);
}

/** 順序與後端相同：自己 → 已停用 → 管理者只能由管理者停用（issue #361） → 最後一位使用中的管理者。 */
export function checkMemberDeactivation(
  viewer: { readonly id: AccountId; readonly role: AccountRole },
  member: { readonly id: AccountId; readonly role: AccountRole; readonly deactivated: boolean },
  otherActiveAdmins: number,
): MemberDeactivationBlock | null {
  if (member.id === viewer.id) return 'self';
  if (member.deactivated) return 'already-deactivated';
  if (!mayManageMemberRole(viewer.role, member.role)) return 'admin-required';
  return member.role === 'smb-admin' && otherActiveAdmins === 0 ? 'last-admin' : null;
}

/**
 * 停用、重新啟用（issue #361）、變更權限與以這個角色新增成員（issue #368）：管理者只能由管理者操作，
 * 其他角色由看得到團隊設定（具備「管理助理與團隊」）的人操作（與後端 `MemberOffboardingRules.MayManageRole`
 * 相同）。團隊頁用它決定要不要顯示按鈕、勾選框能不能改、角色選單列不列出管理者。
 */
export function mayManageMemberRole(viewerRole: AccountRole, memberRole: AccountRole): boolean {
  return memberRole !== 'smb-admin' || viewerRole === 'smb-admin';
}

/**
 * 要不要選接手人：名下有助理、草稿、知識庫、數據庫、未解決的處理事項或承辦中的案件時才要（issue #361）。
 * 別人分享給他的來源不會轉移，不算。
 */
export function isSuccessorRequired(holdings: MemberHoldingsView): boolean {
  return holdings.assistants + holdings.drafts + holdings.knowledgeBases + holdings.databases
    + holdings.openIssues + holdings.openCases > 0;
}

/** 接手人：使用中的內部成員（管理者或內部同仁），而且具備 `required` 的每一項權限。 */
export function checkSuccessor(
  candidate: { readonly role: AccountRole; readonly deactivated: boolean; readonly permissions: readonly AccountPermission[] },
  required: readonly AccountPermission[],
): { readonly reason: SuccessorIneligibleReason | null; readonly missingPermissions: readonly AccountPermission[] } {
  if (candidate.deactivated) return { reason: 'deactivated', missingPermissions: [] };
  if (candidate.role === 'external-customer') return { reason: 'external-customer', missingPermissions: [] };
  const missing = normalizeMemberPermissions(required.filter((permission) => !candidate.permissions.includes(permission)));
  return missing.length === 0 ? { reason: null, missingPermissions: [] } : { reason: 'missing-permissions', missingPermissions: missing };
}

/** 移交案件時記在「受理」紀錄上的說明（與後端相同）。 */
export function caseHandOverNote(memberDisplayName: string, successorDisplayName: string): string {
  return `「${memberDisplayName}」的帳號已停用，案件移交給「${successorDisplayName}」。`;
}
