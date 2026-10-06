import type { components } from '../api/api-schema';

/** 承辦組畫面上的一個帳號：目前的顯示名稱；找不到帳號時是「已停用的帳號」。 */
export type CaseGroupAccountView = components['schemas']['CaseGroupAccountView'];

/**
 * 一個承辦組（M7 計畫第 3 節 A，issue #246）：組織自行定義、可受理某類案件的一群帳號，
 * 不對應部門。只能建立、改名、封存／取消封存，不能刪除；封存的組不會出現在可選的清單中。
 */
export type CaseGroupView = components['schemas']['CaseGroupView'];

/** 管理者可以加入的成員：只有組織內部帳號（管理者、內部同仁）。 */
export type CaseGroupCandidateView = components['schemas']['CaseGroupCandidateView'];

/**
 * `GET /api/v1/case-groups`：內部帳號都讀得到；`canManage` 是管理者（可以建立、改名、
 * 封存與編輯成員），`candidates` 只給管理者。外部客戶是 `403 case`。
 */
export type CaseGroupListView = components['schemas']['CaseGroupListView'];

/** 成員異動歷史的一筆：每個加入或移除各一筆，最新的在前。 */
export type CaseGroupMemberChangeView = components['schemas']['CaseGroupMemberChangeView'];
