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
