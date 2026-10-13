import type { components } from '../api/api-schema';

/**
 * 轉真人通知（issue #355）：官網或 LINE 的訪客收到助理的「找不到資料時的回覆」時，通知助理的擁有者。
 * 只有哪個助理、哪個管道、什麼時間與誰標記已處理——沒有訪客的任何內容或身分。
 * `GET /api/v1/handoffs`、`GET /api/v1/handoffs/attention`、`POST /api/v1/handoffs/{id}:handle`。
 */
export type VisitorHandoffView = components['schemas']['VisitorHandoffView'];

export type VisitorHandoffChannel = components['schemas']['VisitorHandoffChannel'];

/** 側欄數字：我的助理待處理的轉真人。 */
export type VisitorHandoffAttentionView = components['schemas']['VisitorHandoffAttentionView'];

/** 自己的通知設定：`GET`／`PUT /api/v1/me/notification-settings`。 */
export type NotificationSettingsView = components['schemas']['NotificationSettingsView'];

export type UpdateNotificationSettingsRequest = components['schemas']['UpdateNotificationSettingsRequest'];

/**
 * 「轉真人」頁上方的提醒（issue #362）：自己還沒填通知 Email、也沒有關掉轉真人 Email 時提醒去「我的通知」填寫。
 * 部署沒有設定寄信伺服器時不提醒：填了也不會寄出。只看自己的設定，管理者不能代填。
 */
export function needsNotificationEmailReminder(settings: NotificationSettingsView): boolean {
  return !settings.email && settings.handoffEmails && settings.emailDeliveryConfigured;
}

/** 清單的篩選，與 API 的 `status` 相同；預設是待處理。 */
export type VisitorHandoffStatusFilter = 'pending' | 'handled' | 'all';

export const VISITOR_HANDOFF_CHANNEL_LABELS: Readonly<Record<VisitorHandoffChannel, string>> = {
  website: '官網',
  line: 'LINE',
};

/** 放在中文句子裡的管道名稱：拉丁字前後加空格（「在 LINE 回覆」）。 */
export function handoffChannelInSentence(channel: VisitorHandoffChannel): string {
  return channel === 'line' ? ' LINE ' : VISITOR_HANDOFF_CHANNEL_LABELS[channel];
}

/** 與後端 `AccountNotificationSettings.EmailInvalidMessage` 相同。 */
export const NOTIFICATION_EMAIL_INVALID_MESSAGE = '請輸入有效的 Email 地址，例如 name@example.com。';

/** 與後端 `ForbiddenReason.Handoff` 相同。 */
export const HANDOFF_DENIED_MESSAGE = '你沒有這則轉真人通知的存取權限，或它已不存在。轉真人通知只給助理的擁有者。';

/**
 * 與後端 `AccountNotificationSettings.TryNormalizeEmail` 相同的判斷（mock 模式與送出前的提示用）：空白＝清除；
 * 否則必須是單一個 `local@domain`，網域有點、不以點開頭或結尾，最多 254 字，不含空白、逗號、分號、角括號或引號。
 */
export function normalizeNotificationEmail(value: string | null | undefined): { readonly valid: boolean; readonly email: string | null } {
  const trimmed = (value ?? '').trim();
  if (trimmed === '') return { valid: true, email: null };
  const at = trimmed.lastIndexOf('@');
  const local = trimmed.slice(0, at);
  const host = trimmed.slice(at + 1);
  const valid = trimmed.length <= 254
    && !/[\s,;<>"]/u.test(trimmed)
    && at > 0
    && !local.includes('@')
    && host.includes('.')
    && !host.startsWith('.')
    && !host.endsWith('.');
  return { valid, email: valid ? trimmed : null };
}
