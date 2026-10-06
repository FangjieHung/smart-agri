import type { AccountId, AccountView } from '../domain/account.model';
import type { AssistantAcceptanceStatus } from '../domain/assistant-acceptance.model';
import { audienceAllowsRole, type AssistantConfigurationView } from '../domain/assistant.model';
import {
  LINE_FIELDS,
  MAX_WEBSITE_NAME_LENGTH,
  MAX_WELCOME_LENGTH,
  PUBLISHING_CHANNEL_NAMES,
  WEBSITE_BRAND_COLORS,
  WEBSITE_LAUNCHER_POSITIONS,
  normalizeDomain,
  validateAllowedDomain,
  type ConfigurableAssistantPublishingView,
  type LineField,
  type LineFieldCheckView,
  type LineSettingsInput,
  type LineSetupView,
  type LineTestResultView,
  type PlatformSharingView,
  type PublishingChannelStatus,
  type PublishingChannelType,
  type PublishingChannelView,
  type PublishingFieldError,
  type WebsiteEmbedSettings,
  type WebsiteEmbedView,
  type WebsiteKnowledgeBaseRef,
  type WebsitePublishFailure,
  type WebsiteServingState,
} from '../domain/publishing.model';
import { ACCOUNT_ROLE_LABELS } from '../domain/team.model';
import { EMPTY_LINE_SETTINGS, EXPIRED_DEMO_LINE_TOKEN, type PublishingRecord } from './demo-seed-publishing';

/** 發布管道的純函式：由保存狀態推導統一狀態、說明文字與畫面資料，不連接任何外部服務。 */

export function defaultPublishingRecord(
  assistant: AssistantConfigurationView,
  now: string,
): PublishingRecord {
  return {
    version: 1,
    platform: { allowedAccountIds: [...assistant.sharedWithAccountIds], paused: false, updatedAt: now },
    website: {
      displayName: assistant.name.slice(0, MAX_WEBSITE_NAME_LENGTH),
      welcomeMessage: `您好，我是${assistant.name}，有什麼可以協助？`.slice(0, MAX_WELCOME_LENGTH),
      brandColor: 'forest',
      position: 'bottom-right',
      allowedDomains: [],
      state: 'draft',
      publishedAt: null,
      revision: 0,
      lastSeenAt: {},
      updatedAt: now,
    },
    line: { ...EMPTY_LINE_SETTINGS, checked: false, enabled: false, lastTest: null, paused: false, updatedAt: now },
  };
}

function isObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

export function isPublishingRecord(value: unknown): value is PublishingRecord {
  return (
    isObject(value) &&
    value['version'] === 1 &&
    isObject(value['platform']) &&
    Array.isArray(value['platform']['allowedAccountIds']) &&
    isObject(value['website']) &&
    Array.isArray(value['website']['allowedDomains']) &&
    typeof value['website']['state'] === 'string' &&
    isObject(value['line']) &&
    LINE_FIELDS.every((field) => typeof (value['line'] as Record<string, unknown>)[field.id] === 'string')
  );
}

// ---------- 平台內分享 ----------

function platformStatus(record: PublishingRecord): [PublishingChannelStatus, string] {
  const count = record.platform.allowedAccountIds.length;
  if (record.platform.paused) return ['paused', '已暫停：使用連結暫時無法開啟，設定與各帳號的對話紀錄都會保留。'];
  if (count === 0) return ['not-configured', '尚未指定可使用的帳號，目前只有擁有者開得了。'];
  return [
    'published',
    `${count} 個帳號可使用；每個帳號保有獨立對話紀錄，取消勾選只收回權限、不會刪除對話。`,
  ];
}

/**
 * 這個帳號可不可以在平台內開啟這個助理。**這是平台內使用權限的唯一判斷點**。
 *
 * 1. **擁有者永遠開得了**，包含清單是空的、管道已暫停時（他要能自己測試）。
 * 2. 其他帳號必須同時滿足兩件事：
 *    - **使用對象（`audience`）決定「哪一種人」**：角色要在 `AUDIENCE_ROLES` 內。
 *    - **平台內分享的勾選清單決定「哪些帳號」**：要在 `allowedAccountIds` 內，且管道沒有暫停。
 *
 * `assistant.sharedWithAccountIds` **不是**第二條授權路徑，它只是這份清單的初始值
 * （`defaultPublishingRecord()`）；助理一旦有保存的發布設定，就以保存的清單為準。
 * 取消勾選只收回權限，不會刪除該帳號既有的對話；重新勾選就原封不動回來。
 */
export function canOpenInPlatform(
  assistant: AssistantConfigurationView,
  record: PublishingRecord,
  viewer: AccountView,
): boolean {
  if (assistant.ownerAccountId === viewer.id) return true;
  if (!audienceAllowsRole(assistant.audience, viewer.role)) return false;
  if (record.platform.paused) return false;
  return record.platform.allowedAccountIds.includes(viewer.id);
}

/**
 * 誰可以**設定**這個助理的發布管道。**這是發布設定權限的唯一判斷點**，
 * 三個管道的讀取與寫入、以及 `/app/channels` 的總覽都走這裡。
 *
 * 要同時滿足兩件事：**是助理的擁有者**，而且帳號有 `manage-publishing`
 * （在 `/app/settings` 的團隊設定變更）。擁有權不會自動帶出這個權限——
 * 「誰負責對外發布」在中小企業裡本來就常常不是助理的建立者。
 *
 * 這跟「誰開得了這個助理」是兩件事：收回發布設定權限不會把既有的使用者踢出去，
 * 那一題由 `canOpenInPlatform()` 判斷，已儲存的管道設定也原封不動留著。
 */
export function canManagePublishing(
  assistant: AssistantConfigurationView,
  viewer: AccountView,
): boolean {
  return (
    assistant.ownerAccountId === viewer.id &&
    viewer.permissions.includes('manage-publishing')
  );
}

export function platformCandidates(
  assistant: AssistantConfigurationView,
  accounts: readonly AccountView[],
): readonly AccountView[] {
  return accounts.filter((account) => account.id !== assistant.ownerAccountId);
}

export function normalizePlatformAccounts(
  assistant: AssistantConfigurationView,
  accounts: readonly AccountView[],
  accountIds: readonly AccountId[],
): readonly AccountId[] | null {
  const candidates = platformCandidates(assistant, accounts).map((account) => account.id);
  if (accountIds.some((id) => !candidates.includes(id))) return null;
  return candidates.filter((id) => accountIds.includes(id));
}

// ---------- 官網嵌入 ----------

export const WEBSITE_CONFLICT_MESSAGE = '設定已在其他分頁被更新過，請重新載入後再修改。';
export const WEBSITE_REFUSED_MESSAGE = '目前還不能對外發布，請先處理下列項目。';

/** 實際服務狀態需要的、不在發布設定裡的資料（驗收、連接的知識庫、用量與情境）。 */
export interface WebsiteServingContext {
  readonly acceptanceStatus: AssistantAcceptanceStatus;
  readonly nonOwnedKnowledgeBases: readonly WebsiteKnowledgeBaseRef[];
  readonly quotaExceeded: boolean;
  /** `disconnected-channel` 情境：官網管道顯示需要處理。 */
  readonly disconnected: boolean;
}

/** M5a 計畫第 3 節 C 的推導順序；mock 沒有「過期」與助理暫停（暫停的是平台內管道），所以只看驗收是否 `failed`／`not-accepted`。 */
export function websiteServingState(record: PublishingRecord, context: WebsiteServingContext): WebsiteServingState {
  const { state, allowedDomains } = record.website;
  if (state === 'draft' || allowedDomains.length === 0) return 'not-published';
  if (state === 'paused') return 'paused';
  if (context.disconnected || context.acceptanceStatus === 'failed' || context.acceptanceStatus === 'not-accepted') {
    return 'suspended-acceptance';
  }
  if (context.nonOwnedKnowledgeBases.length > 0) return 'suspended-knowledge';
  return context.quotaExceeded ? 'suspended-quota' : 'serving';
}

const NOT_PUBLISHED_DETAILS = {
  unsaved: '尚未設定官網嵌入。',
  noDomain: '尚未設定允許網域，目前沒有網站可以嵌入。',
  saved: '尚未發布：設定已儲存，驗收通過後即可發布。',
};

/** 實際服務狀態 → 管道卡狀態與說明（第 3 節 H；文字比 API 的 `Describe` 精簡）。 */
const SERVING_STATUS: Readonly<Record<Exclude<WebsiteServingState, 'not-published'>, [PublishingChannelStatus, string]>> = {
  paused: ['paused', '已暫停：官網訪客看到「暫停服務」，設定會保留。'],
  'suspended-acceptance': ['needs-attention', '驗收未通過，已自動暫停對外回覆；請到題組頁處理。'],
  'suspended-knowledge': ['needs-attention', '連接了別人的知識庫，已自動暫停對外回覆；解除連接後會恢復。'],
  'suspended-quota': ['needs-attention', '本月用量已達上限，已暫停對外回覆。'],
  serving: ['published', '已發布，官網訪客可以使用。'],
};

function websiteStatus(
  record: PublishingRecord,
  serving: WebsiteServingState,
  disconnected: boolean,
): [PublishingChannelStatus, string] {
  const { state, allowedDomains, revision } = record.website;
  if (disconnected && serving === 'suspended-acceptance') {
    return ['needs-attention', '官網連線中斷（模擬）：只有官網嵌入受影響，其他管道不受影響。'];
  }
  if (serving !== 'not-published') return SERVING_STATUS[serving];
  if (state !== 'draft') return ['testing', NOT_PUBLISHED_DETAILS.noDomain];
  return revision === 0 && allowedDomains.length === 0
    ? ['not-configured', NOT_PUBLISHED_DETAILS.unsaved]
    : ['testing', NOT_PUBLISHED_DETAILS.saved];
}

/** 發布閘門（與 API 的 `WebsiteChannelRules.PublishFailures` 同一組判斷與訊息；mock 沒有 `public-base-url`、`assistant-paused`）。 */
export function websitePublishFailures(
  record: PublishingRecord,
  context: WebsiteServingContext,
): readonly WebsitePublishFailure[] {
  const failures: WebsitePublishFailure[] = [];
  if (context.acceptanceStatus !== 'passed') {
    failures.push({ reason: 'acceptance', message: '驗收狀態必須是「通過」才能發布；請先到題組頁讓所有題目通過。' });
  }
  if (record.website.allowedDomains.length === 0) {
    failures.push({ reason: 'allowed-domains', message: '請先設定至少一個允許嵌入的網域。' });
  }
  for (const knowledgeBase of context.nonOwnedKnowledgeBases) {
    failures.push({
      reason: 'knowledge-ownership',
      message: `「${knowledgeBase.name}」不是你自己的知識庫，對外發布時不能使用；請解除連接。`,
    });
  }
  return failures;
}

/** 示範嵌入碼：網址只供畫面展示（對應 API 的 `PublicChannels:PublicBaseUrl`），不可用於正式環境。 */
export function demoEmbedCode(assistantId: string): string {
  return [
    '<!-- Demo 嵌入碼：僅供展示，不可用於正式環境，也不會連接外部服務 -->',
    `<script src="https://widget.demo.invalid/embed.js" data-assistant="${assistantId}" async></script>`,
  ].join('\n');
}

/** 驗證並正規化官網設定；錯誤依畫面欄位順序排列。 */
export function validateWebsiteSettings(settings: WebsiteEmbedSettings): {
  readonly errors: readonly PublishingFieldError[];
  readonly normalized: WebsiteEmbedSettings;
} {
  const errors: PublishingFieldError[] = [];
  const displayName = settings.displayName.trim();
  const welcomeMessage = settings.welcomeMessage.trim();
  if (displayName.length === 0) errors.push({ field: 'displayName', message: '請填寫顯示名稱。' });
  else if (displayName.length > MAX_WEBSITE_NAME_LENGTH) {
    errors.push({ field: 'displayName', message: `顯示名稱請在 ${MAX_WEBSITE_NAME_LENGTH} 個字以內。` });
  }
  if (welcomeMessage.length === 0) errors.push({ field: 'welcomeMessage', message: '請填寫歡迎語。' });
  else if (welcomeMessage.length > MAX_WELCOME_LENGTH) {
    errors.push({ field: 'welcomeMessage', message: `歡迎語請在 ${MAX_WELCOME_LENGTH} 個字以內。` });
  }
  if (!WEBSITE_BRAND_COLORS.some((color) => color.id === settings.brandColor)) {
    errors.push({ field: 'brandColor', message: '請選擇品牌色。' });
  }
  if (!WEBSITE_LAUNCHER_POSITIONS.some((position) => position.id === settings.position)) {
    errors.push({ field: 'position', message: '請選擇顯示位置。' });
  }
  const domains: string[] = [];
  for (const raw of settings.allowedDomains) {
    const error = validateAllowedDomain(raw, domains);
    if (error !== null) {
      errors.push({ field: 'allowedDomains', message: error });
      break;
    }
    domains.push(normalizeDomain(raw));
  }

  return { errors, normalized: { ...settings, displayName, welcomeMessage, allowedDomains: domains } };
}

// ---------- LINE ----------

const LINE_PATTERNS: Readonly<Record<LineField, { readonly pattern: RegExp; readonly message: string }>> = {
  officialAccountId: { pattern: /^@[a-z0-9._-]{3,20}$/i, message: '需以 @ 開頭，接 3–20 個英數字，例如 @anxin-demo。' },
  channelId: { pattern: /^\d{10}$/, message: '應為 10 位數字。' },
  channelSecret: { pattern: /^[a-f0-9]{32}$/i, message: '應為 32 個英數字（0–9、a–f）。' },
  accessToken: { pattern: /^\S{40,}$/, message: '至少 40 個字元且不含空白。' },
};

export function lineChecks(line: PublishingRecord['line']): readonly LineFieldCheckView[] {
  return LINE_FIELDS.map(({ id, label }): LineFieldCheckView => {
    if (!line.checked) return { field: id, label, state: 'pending', message: '尚未檢查。' };
    const value = line[id];
    if (value.length === 0) return { field: id, label, state: 'failed', message: `請填寫 ${label}。` };
    const rule = LINE_PATTERNS[id];
    if (!rule.pattern.test(value)) return { field: id, label, state: 'failed', message: `${label}${rule.message}` };
    if (id === 'accessToken' && value === EXPIRED_DEMO_LINE_TOKEN) {
      return { field: id, label, state: 'failed', message: '此權杖已失效（模擬結果），請重新發行後貼上新的權杖。' };
    }
    return { field: id, label, state: 'passed', message: '通過檢查（模擬）。' };
  });
}

function lineStatus(line: PublishingRecord['line'], checks: readonly LineFieldCheckView[]): [PublishingChannelStatus, string] {
  const failed = checks.filter((check) => check.state === 'failed').length;
  if (line.paused) return ['paused', '已暫停：LINE 使用者暫時收不到助理回覆，設定會保留。'];
  if (!line.checked && LINE_FIELDS.every((field) => line[field.id].length === 0)) {
    return ['not-configured', '尚未填寫 LINE 官方帳號連接資訊。'];
  }
  if (failed > 0) return ['needs-attention', `${failed} 個欄位未通過檢查，LINE 使用者可能收不到回覆；其他管道不受影響。`];
  if (!line.checked) return ['testing', '連接資訊尚未檢查，請先儲存並檢查。'];
  if (line.enabled) return ['published', `${line.officialAccountId} 已啟用（模擬），可在 LINE 對話中使用。`];
  return ['testing', '欄位已通過檢查，傳送測試訊息並確認後即可啟用。'];
}

export function canSendLineTest(line: PublishingRecord['line']): boolean {
  return line.checked && !line.paused && lineChecks(line).every((check) => check.state === 'passed');
}

export function lineTestResult(line: PublishingRecord['line'], testedAt: string): LineTestResultView {
  if (line.paused) return { outcome: 'failed', message: '管道已暫停，恢復後才能傳送測試訊息。', testedAt };
  if (!canSendLineTest(line)) {
    return { outcome: 'failed', message: '請先讓所有欄位通過檢查，再傳送測試訊息。', testedAt };
  }
  return {
    outcome: 'delivered',
    message: `測試訊息已送達 ${line.officialAccountId}（模擬結果，未實際連接 LINE）。`,
    testedAt,
  };
}

export function canActivateLine(line: PublishingRecord['line']): boolean {
  return canSendLineTest(line) && !line.enabled && line.lastTest?.outcome === 'delivered';
}

export function trimLineSettings(input: LineSettingsInput): LineSettingsInput {
  return {
    officialAccountId: input.officialAccountId.trim(),
    channelId: input.channelId.trim(),
    channelSecret: input.channelSecret.trim(),
    accessToken: input.accessToken.trim(),
  };
}

// ---------- 對外開放判斷 ----------

/**
 * 這個助理是否真的對外開放，也就是未登入訪客可不可以開啟它。
 *
 * 只有**官網嵌入**或 **LINE** 處於 `published` 才算數：平台內分享不算對外，
 * 它仍然需要一個已登入的帳號。尚未設定、測試中、需要處理與已暫停一律不開放，
 * 所以預設（新建立的助理）是關著的。
 */
export function isExternallyPublished(
  record: PublishingRecord,
  website: WebsiteServingContext,
): boolean {
  const websiteServing = websiteServingState(record, website) === 'serving';
  const [line] = lineStatus(record.line, lineChecks(record.line));
  return websiteServing || line === 'published';
}

// ---------- 組裝 ----------

function channelView(
  assistant: AssistantConfigurationView,
  type: PublishingChannelType,
  [status, statusDetail]: [PublishingChannelStatus, string],
  updatedAt: string,
): PublishingChannelView {
  return {
    id: `channel-${type}:${assistant.id}`,
    assistantId: assistant.id,
    ownerAccountId: assistant.ownerAccountId,
    name: PUBLISHING_CHANNEL_NAMES[type],
    type,
    status,
    statusDetail,
    updatedAt,
  };
}

export function toAssistantPublishingView(
  assistant: AssistantConfigurationView,
  record: PublishingRecord,
  accounts: readonly AccountView[],
  websiteContext: WebsiteServingContext,
): ConfigurableAssistantPublishingView {
  const platform: PlatformSharingView = {
    channel: channelView(assistant, 'platform', platformStatus(record), record.platform.updatedAt),
    usagePath: `/use/${assistant.id}`,
    allowedAccountIds: record.platform.allowedAccountIds,
    candidates: platformCandidates(assistant, accounts).map((account) => ({
      id: account.id,
      displayName: account.displayName,
      audienceLabel: ACCOUNT_ROLE_LABELS[account.role],
    })),
  };
  const { displayName, welcomeMessage, brandColor, position, allowedDomains, state, publishedAt, revision, lastSeenAt } =
    record.website;
  const servingState = websiteServingState(record, websiteContext);
  const website: WebsiteEmbedView = {
    displayName,
    welcomeMessage,
    brandColor,
    position,
    allowedDomains,
    channel: channelView(
      assistant,
      'website',
      websiteStatus(record, servingState, websiteContext.disconnected),
      record.website.updatedAt,
    ),
    state,
    servingState,
    acceptanceStatus: websiteContext.acceptanceStatus,
    domains: allowedDomains.map((domain) => ({ domain, lastSeenAt: lastSeenAt[domain] ?? null })),
    nonOwnedKnowledgeBases: websiteContext.nonOwnedKnowledgeBases,
    embedCode: demoEmbedCode(assistant.id),
    publishedAt,
    revision,
  };
  const checks = lineChecks(record.line);
  const { officialAccountId, channelId, channelSecret, accessToken } = record.line;
  const line: LineSetupView = {
    officialAccountId,
    channelId,
    channelSecret,
    accessToken,
    channel: channelView(assistant, 'line', lineStatus(record.line, checks), record.line.updatedAt),
    webhookUrl: `https://webhook.demo.invalid/line/${assistant.id}`,
    checks,
    lastTest: record.line.lastTest,
    canSendTest: canSendLineTest(record.line),
    canActivate: canActivateLine(record.line),
  };

  return { assistantId: assistant.id, assistantName: assistant.name, platform, website, line };
}
