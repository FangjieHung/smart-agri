/** 組織每月 token 用量的狀態：未滿 80% 是 `normal`、80% 起 `near`、100% 起 `exceeded`（M5a 計畫第 3 節 F）。 */
export type OrganizationUsageState = 'normal' | 'near' | 'exceeded';

/** `GET /api/v1/organization/usage`；只有 `manage-publishing` 的帳號讀得到。 */
export interface OrganizationUsageView {
  /** 目前的月份（依組織的統計時區），`YYYY-MM`。 */
  readonly month: string;
  readonly usedTokens: number;
  readonly limitTokens: number;
  readonly state: OrganizationUsageState;
}
