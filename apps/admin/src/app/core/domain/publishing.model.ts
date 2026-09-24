import type { AccountId } from './account.model';
import type { AssistantId } from './assistant.model';

/** 三種發布管道：平台內分享、官網嵌入與 LINE。 */
export type PublishingChannelType = 'platform' | 'website' | 'line';

export const PUBLISHING_CHANNEL_TYPES: readonly PublishingChannelType[] = ['platform', 'website', 'line'];

export const PUBLISHING_CHANNEL_NAMES: Readonly<Record<PublishingChannelType, string>> = {
  platform: '組織內部分享',
  website: '官網嵌入',
  line: 'LINE',
};

/** 每個管道共用的五種狀態：尚未設定、測試中、已發布、需要處理與已暫停。 */
export type PublishingChannelStatus = 'not-configured' | 'testing' | 'published' | 'needs-attention' | 'paused';

export const PUBLISHING_CHANNEL_STATUSES: readonly PublishingChannelStatus[] = [
  'not-configured',
  'testing',
  'published',
  'needs-attention',
  'paused',
];

export type PublishingStatusTone = 'neutral' | 'info' | 'success' | 'warning';

export interface PublishingStatusMeta {
  readonly label: string;
  /** 與文字並用的形狀符號，不單靠顏色辨識狀態。 */
  readonly symbol: string;
  readonly tone: PublishingStatusTone;
}

export const PUBLISHING_STATUS_META: Readonly<Record<PublishingChannelStatus, PublishingStatusMeta>> = {
  'not-configured': { label: '尚未設定', symbol: '○', tone: 'neutral' },
  testing: { label: '測試中', symbol: '◐', tone: 'info' },
  published: { label: '已發布', symbol: '✓', tone: 'success' },
  'needs-attention': { label: '需要處理', symbol: '!', tone: 'warning' },
  paused: { label: '已暫停', symbol: '‖', tone: 'neutral' },
};

export const PUBLISHING_DEMO_LABEL = 'Demo，不會連接外部服務';

export type PublishingChannelId = `channel-${PublishingChannelType}:${AssistantId}`;

export interface PublishingChannelView {
  readonly id: PublishingChannelId;
  readonly assistantId: AssistantId;
  readonly ownerAccountId: AccountId;
  readonly name: string;
  readonly type: PublishingChannelType;
  readonly status: PublishingChannelStatus;
  /** 說明目前狀況、影響範圍與下一步。 */
  readonly statusDetail: string;
  readonly updatedAt: string;
}

export interface AssistantChannelsView {
  readonly assistantId: AssistantId;
  readonly assistantName: string;
  readonly channels: readonly PublishingChannelView[];
}

export interface PublishingFieldError {
  readonly field: string;
  readonly message: string;
}

// ---------- 平台內分享 ----------

export interface PlatformSharingCandidateView {
  readonly id: AccountId;
  readonly displayName: string;
  readonly audienceLabel: string;
}

export interface PlatformSharingView {
  readonly channel: PublishingChannelView;
  /** 平台內使用連結（站內路徑）。 */
  readonly usagePath: string;
  readonly allowedAccountIds: readonly AccountId[];
  readonly candidates: readonly PlatformSharingCandidateView[];
}

// ---------- 官網嵌入 ----------

export type WebsiteLauncherPosition = 'bottom-right' | 'bottom-left';

export const WEBSITE_LAUNCHER_POSITIONS: readonly { readonly id: WebsiteLauncherPosition; readonly label: string }[] = [
  { id: 'bottom-right', label: '右下角' },
  { id: 'bottom-left', label: '左下角' },
];

export type WebsiteBrandColor = 'forest' | 'ocean' | 'amber' | 'plum';

export const WEBSITE_BRAND_COLORS: readonly {
  readonly id: WebsiteBrandColor;
  readonly label: string;
  readonly hex: string;
}[] = [
  { id: 'forest', label: '森林綠', hex: '#1f6f5c' },
  { id: 'ocean', label: '海洋藍', hex: '#1d5fa8' },
  { id: 'amber', label: '琥珀橘', hex: '#a3520c' },
  { id: 'plum', label: '梅子紫', hex: '#6b3fa0' },
];

export type WebsiteInstallCheck = 'not-checked' | 'detected' | 'not-detected';

export interface WebsiteEmbedSettings {
  readonly displayName: string;
  readonly welcomeMessage: string;
  readonly brandColor: WebsiteBrandColor;
  readonly position: WebsiteLauncherPosition;
  readonly allowedDomains: readonly string[];
}

export interface WebsiteEmbedView extends WebsiteEmbedSettings {
  readonly channel: PublishingChannelView;
  /** 示範用嵌入碼，指向保留網域，不可用於正式環境。 */
  readonly embedCode: string;
  readonly installCheck: WebsiteInstallCheck;
  readonly installCheckedAt: string | null;
}

export const MAX_ALLOWED_DOMAINS = 5;
export const MAX_WEBSITE_NAME_LENGTH = 30;
export const MAX_WELCOME_LENGTH = 120;

const DOMAIN_PATTERN = /^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$/;

export function normalizeDomain(raw: string): string {
  return raw.trim().toLowerCase();
}

/** 驗證一個允許嵌入的網域；通過時回傳 null。 */
export function validateAllowedDomain(raw: string, existing: readonly string[]): string | null {
  const domain = normalizeDomain(raw);
  if (domain.length === 0) return '請輸入網域，例如 shop.example.com。';
  if (/^[a-z]+:\/\//.test(domain) || domain.includes('/')) {
    return '只需要填網域，不要包含 https:// 或路徑，例如 shop.example.com。';
  }
  if (!DOMAIN_PATTERN.test(domain)) return `「${domain}」不是有效的網域，例如 shop.example.com。`;
  if (existing.includes(domain)) return `「${domain}」已在允許清單中。`;
  if (existing.length >= MAX_ALLOWED_DOMAINS) return `最多可設定 ${MAX_ALLOWED_DOMAINS} 個網域。`;
  return null;
}

// ---------- LINE ----------

export type LineField = 'officialAccountId' | 'channelId' | 'channelSecret' | 'accessToken';

export interface LineFieldDefinition {
  readonly id: LineField;
  readonly label: string;
  readonly hint: string;
  /** 敏感欄位預設遮蔽。 */
  readonly sensitive: boolean;
}

export const LINE_FIELDS: readonly LineFieldDefinition[] = [
  { id: 'officialAccountId', label: '官方帳號 ID', hint: '以 @ 開頭，例如 @anxin-demo。', sensitive: false },
  { id: 'channelId', label: 'Channel ID', hint: '10 位數字。', sensitive: false },
  { id: 'channelSecret', label: 'Channel secret', hint: '32 個英數字（0–9、a–f）。', sensitive: true },
  { id: 'accessToken', label: 'Channel access token', hint: '至少 40 個字元，不含空白。', sensitive: true },
];

export type LineSettingsInput = Readonly<Record<LineField, string>>;

export type LineCheckState = 'pending' | 'passed' | 'failed';

export interface LineFieldCheckView {
  readonly field: LineField;
  readonly label: string;
  readonly state: LineCheckState;
  readonly message: string;
}

export interface LineTestResultView {
  readonly outcome: 'delivered' | 'failed';
  readonly message: string;
  readonly testedAt: string;
}

export interface LineSetupView extends LineSettingsInput {
  readonly channel: PublishingChannelView;
  /** 示範用 webhook 網址，指向保留網域。 */
  readonly webhookUrl: string;
  readonly checks: readonly LineFieldCheckView[];
  readonly lastTest: LineTestResultView | null;
  readonly canSendTest: boolean;
  readonly canActivate: boolean;
}

// ---------- 單一助理的發布設定 ----------

export interface AssistantPublishingView {
  readonly assistantId: AssistantId;
  readonly assistantName: string;
  readonly platform: PlatformSharingView;
  readonly website: WebsiteEmbedView;
  readonly line: LineSetupView;
}
