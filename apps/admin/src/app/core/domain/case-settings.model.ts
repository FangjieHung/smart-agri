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

/** 案件類型的預設承辦組：`archived` 只會出現在停用的類型上（啟用中類型的預設承辦組不能封存）。 */
export type CaseTypeGroupView = components['schemas']['CaseTypeGroupView'];

/**
 * 一個案件類型（M7 計畫第 3 節 B，issue #247）：說明（助理提議開案時靠它判斷）、預設承辦組、
 * 預設處理時限（小時，1–2,160，以日曆時間計算）。只能停用、不能刪除；停用的類型不能用來建立新案件。
 */
export type CaseTypeView = components['schemas']['CaseTypeView'];

/**
 * `GET /api/v1/case-types`：內部帳號都讀得到啟用中的類型（建立案件時要選）；`includeInactive=true`
 * 只對管理者有效。外部客戶是 `403 case`。
 */
export type CaseTypeListView = components['schemas']['CaseTypeListView'];

/** 建立與修改案件類型時送出的欄位（每次都送完整內容）。 */
export interface CaseTypeInput {
  readonly name: string;
  readonly description: string;
  readonly defaultGroupId: string;
  readonly defaultDueHours: number;
  readonly isActive: boolean;
}

/**
 * 數據庫「送出後自動開案」可選的一個案件類型（issue #255，M7 計畫第 3 節 I）：啟用中的類型、它的預設
 * 承辦組，以及承辦組中有幾人無法讀取這個數據庫的紀錄（案件看得到，不代表讀得到紀錄）。
 */
export type DatabaseAutoCaseOptionView = components['schemas']['DatabaseAutoCaseOptionView'];

/**
 * `GET`／`PUT /api/v1/databases/{id}/auto-case`（只有管理者，其他人是 `403 organization-settings`）：
 * `caseTypeId` 是目前的設定（`null` 是不開案），`options` 是所有啟用中的類型。
 */
export type DatabaseAutoCaseView = components['schemas']['DatabaseAutoCaseView'];
