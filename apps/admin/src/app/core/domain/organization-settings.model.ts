import type { components } from '../api/api-schema';

/** 部署提供的一個對話模型（`GET /api/v1/organization/chat-model` 的 `options[]`）；永遠不含金鑰與 Endpoint。 */
export type ChatModelOptionView = components['schemas']['ChatModelOptionView'];

/** `selected`（組織選的）、`deployment-default`（沒選）、`removed`（選的已不再提供，改用部署預設）。 */
export type ChatModelSource = components['schemas']['ChatModelSource'];

/** 組織設定的「上次變更」：帳號顯示名稱與時間（找不到帳號時是「已停用的帳號」）。 */
export type OrganizationSettingChangeView = components['schemas']['OrganizationSettingChangeView'];

/**
 * 組織的對話模型（M6 計畫第 3 節 D，issue #239／#240）。組織內任何帳號都讀得到；
 * 只有 `canChange`（管理者）可以變更。`effective` 只有在部署完全沒有對話模型時才是 `null`。
 */
export type OrganizationChatModelView = components['schemas']['OrganizationChatModelView'];

/**
 * 驗收頁的提示（issue #240）：最近一次有紀錄模型的題組重跑，用的模型與目前生效的不同時，
 * 回傳兩個模型名稱；相同、沒有紀錄、或部署沒有對話模型時回傳 `null`。比對以模型名稱為準
 * （與後端 `AssistantTestRun.Model` 相同）。
 */
export function chatModelChangedSinceRun(
  runModel: string | null | undefined,
  effective: ChatModelOptionView | null | undefined,
): { readonly tested: string; readonly current: string } | null {
  if (!runModel || !effective) return null;
  return runModel === effective.model ? null : { tested: runModel, current: effective.model };
}

/**
 * 組織的對話保存期限（M6 計畫第 3 節 F，issue #241／#243）。`days` 是目前生效的期限（`null` = 永久）；
 * `pending` 是縮短後還在 7 天緩衝期的期限。組織內任何帳號都讀得到；只有 `canChange`（管理者）可以
 * 預覽與變更。`revision` 與對話模型共用（`Organizations.SettingsRevision`）。
 */
export type OrganizationRetentionView = components['schemas']['OrganizationRetentionView'];

/** 縮短後等待生效的期限：`effectiveAt` 之後的第一次每日清理才改用它。 */
export type OrganizationRetentionPendingView = components['schemas']['OrganizationRetentionPendingView'];

/**
 * `GET …/retention/preview?days=N`（管理者）：以「現在」計算，期限是 N 天時清理會刪除的對話串數。
 * 緩衝期過後實際數量會更多，所以畫面寫「大約」。`cutoff` 是統計時區 N 天前的 00:00。
 */
export type OrganizationRetentionPreviewView = components['schemas']['OrganizationRetentionPreviewView'];

/** 縮短保存期限後的緩衝期天數（與後端 `OrganizationRetention.BufferPeriod` 相同）。 */
export const RETENTION_BUFFER_DAYS = 7;

/** 保存期限的顯示文字：`null` 是「永久」，其餘是「N 天」。 */
export function retentionLabel(days: number | null): string {
  return days === null ? '永久' : `${days} 天`;
}

/**
 * `next` 是否比 `current` 短（與後端 `OrganizationRetention.IsShorter` 相同）：永久最長；
 * 從永久改成任何天數都算縮短，要經過緩衝期。
 */
export function isShorterRetention(next: number | null, current: number | null): boolean {
  if (next === null) return false;
  return current === null || next < current;
}
