import type { AccountId } from './account.model';
import type { AssistantAcceptanceStatus } from './assistant-acceptance.model';
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

/** 擁有者對網站頻道的選擇：已存設定（草稿）、已發布、擁有者暫停。 */
export type WebsiteChannelState = 'draft' | 'published' | 'paused';

/**
 * 每次讀取時推導的「實際服務狀態」（M5a 計畫第 3 節 C），不儲存。對應管道卡的狀態（第 3 節 H）：
 * `not-published` → `testing`、`serving` → `published`、`paused` → `paused`，
 * 三個 `suspended-*` → `needs-attention`（附原因）。
 */
export type WebsiteServingState =
  | 'not-published'
  | 'paused'
  | 'suspended-acceptance'
  | 'suspended-knowledge'
  | 'suspended-quota'
  | 'serving';

/** 允許網域與被動偵測到的最後載入時間（只供參考，不是安全判斷；沒有偵測過是 `null`）。 */
export interface WebsiteDomainView {
  readonly domain: string;
  readonly lastSeenAt: string | null;
}

/** 助理連接、但不是助理擁有者自己的知識庫（決定 B：對外發布時不能使用）。 */
export interface WebsiteKnowledgeBaseRef {
  readonly id: string;
  readonly name: string;
}

export interface WebsiteEmbedSettings {
  readonly displayName: string;
  readonly welcomeMessage: string;
  readonly brandColor: WebsiteBrandColor;
  readonly position: WebsiteLauncherPosition;
  readonly allowedDomains: readonly string[];
}

/** 官網管道：API 與 mock 共用同一個形狀。 */
export interface WebsiteEmbedView extends WebsiteEmbedSettings {
  readonly channel: PublishingChannelView;
  readonly state: WebsiteChannelState;
  readonly servingState: WebsiteServingState;
  readonly acceptanceStatus: AssistantAcceptanceStatus;
  readonly domains: readonly WebsiteDomainView[];
  /** 連接的、不是助理擁有者自己的知識庫；有任何一個就不能發布，已發布的會被暫停。 */
  readonly nonOwnedKnowledgeBases: readonly WebsiteKnowledgeBaseRef[];
  /** 伺服器尚未設定對外網址時是 null。 */
  readonly embedCode: string | null;
  readonly publishedAt: string | null;
  /** 儲存設定時帶回去的版本；還沒存過是 0。 */
  readonly revision: number;
}

/** 發布閘門 `422` 的 `errors` 鍵（M5a 計畫第 3 節 C、決定 B）。 */
export type WebsitePublishFailureReason =
  | 'acceptance'
  | 'allowed-domains'
  | 'assistant-paused'
  | 'knowledge-ownership'
  | 'public-base-url'
  /** 後端日後新增、前端還不認得的原因：照樣顯示訊息。 */
  | 'other';

export interface WebsitePublishFailure {
  readonly reason: WebsitePublishFailureReason;
  readonly message: string;
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

/** 儲存時可能被指出錯誤的欄位：四個連接欄位、歡迎訊息與收到非文字訊息時的回覆。 */
export type LineErrorField = LineField | 'welcomeMessage' | 'nonTextReply';

export interface LineFieldDefinition {
  readonly id: LineField;
  readonly label: string;
  readonly hint: string;
  /** 憑證欄位只寫不讀：已設定時只顯示「已設定・末四碼」，要改就「更換」。 */
  readonly sensitive: boolean;
}

export const LINE_FIELDS: readonly LineFieldDefinition[] = [
  { id: 'officialAccountId', label: '官方帳號 ID', hint: '以 @ 開頭，例如 @anxin-demo。', sensitive: false },
  { id: 'channelId', label: 'Channel ID', hint: '10 位數字。', sensitive: false },
  { id: 'channelSecret', label: 'Channel secret', hint: '32 個英數字（0–9、a–f）。', sensitive: true },
  { id: 'accessToken', label: 'Channel access token', hint: '至少 40 個字元，不含空白。', sensitive: true },
];

export const MAX_LINE_WELCOME_LENGTH = 120;

/** 一對一聊天收到圖片、貼圖等非文字訊息時的回覆（#291）：與 API 的 `AssistantLineChannel.NonTextReplyMaxLength` 相同。 */
export const MAX_LINE_NON_TEXT_REPLY_LENGTH = 500;

/**
 * 儲存 LINE 設定的輸入。`channelSecret`、`accessToken` 是只寫的：省略或空字串表示「不變更」，
 * 有值才取代（第一次儲存兩個都必填）。
 */
export interface LineSettingsInput {
  readonly officialAccountId: string;
  readonly channelId: string;
  readonly welcomeMessage: string;
  readonly nonTextReply: string;
  readonly channelSecret?: string;
  readonly accessToken?: string;
}

/** 憑證的狀態：只有「是否設定」與末四碼，前端任何地方都拿不到原文。 */
export interface SecretStatusView {
  readonly configured: boolean;
  readonly lastFour: string | null;
  readonly updatedAt: string | null;
}

/** 擁有者對 LINE 頻道的選擇：草稿、已啟用、擁有者暫停。 */
export type LineChannelState = 'draft' | 'published' | 'paused';

export type LineConnectionCheckKind = 'access-token' | 'webhook-endpoint' | 'webhook-test';

export type LineConnectionCheckState = 'pending' | 'passed' | 'failed' | 'skipped';

/** 「測試連線」的三項檢查之一；`label` 與 `message` 都是伺服器給的繁體中文。 */
export interface LineConnectionCheckView {
  readonly check: LineConnectionCheckKind;
  readonly label: string;
  readonly state: LineConnectionCheckState;
  readonly message: string;
}

/** LINE 頻道：API 與 mock 共用同一個形狀；Secret 與 Token 只有狀態，沒有原文。 */
export interface LineSetupView {
  readonly channel: PublishingChannelView;
  readonly officialAccountId: string;
  readonly channelId: string;
  readonly welcomeMessage: string;
  /** 一對一聊天收到非文字訊息（圖片、貼圖、影片、語音、檔案、位置）時的回覆；群組與多人聊天室不回覆。 */
  readonly nonTextReply: string;
  readonly channelSecret: SecretStatusView;
  readonly accessToken: SecretStatusView;
  /** 伺服器實際的 Webhook 網址（測試連線時由系統設定到 LINE）；伺服器沒有對外網址時是 null。 */
  readonly webhookUrl: string | null;
  /** 固定三項、依序：Token 與官方帳號、設定 Webhook 網址、Webhook 連線測試。 */
  readonly checks: readonly LineConnectionCheckView[];
  readonly connectionCheckedAt: string | null;
  readonly state: LineChannelState;
  /** 與官網管道同一組推導的實際服務狀態（「三項檢查都通過」取代「有允許網域」）。 */
  readonly servingState: WebsiteServingState;
  readonly acceptanceStatus: AssistantAcceptanceStatus;
  readonly nonOwnedKnowledgeBases: readonly WebsiteKnowledgeBaseRef[];
  readonly publishedAt: string | null;
  /** 本月以 push 補送（而不是 reply）的回答數。 */
  readonly pushFallbackCount: number;
  /** 儲存設定時帶回去的版本；還沒存過是 0。 */
  readonly revision: number;
}

/** 啟用閘門 `422` 的 `errors` 鍵（M5b 計畫第 3 節 B）。 */
export type LinePublishFailureReason =
  | 'connection'
  | 'acceptance'
  | 'assistant-paused'
  | 'knowledge-ownership'
  | 'public-base-url'
  /** 後端日後新增、前端還不認得的原因：照樣顯示訊息。 */
  | 'other';

export interface LinePublishFailure {
  readonly reason: LinePublishFailureReason;
  readonly message: string;
}

/** 「測試連線」被伺服器自己的前提拒絕（`422 line-test-refused`）的原因：還沒儲存設定、沒有對外網址。 */
export type LineTestRefusalReason = 'settings' | 'public-base-url' | 'other';

export interface LineTestRefusal {
  readonly reason: LineTestRefusalReason;
  readonly message: string;
}

// ---------- 單一助理的發布設定 ----------

export interface AssistantPublishingView {
  readonly assistantId: AssistantId;
  readonly assistantName: string;
  readonly platform: PlatformSharingView;
  readonly website: WebsiteEmbedView;
  readonly line: LineSetupView;
}
