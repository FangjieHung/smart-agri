import type { AccountId, AccountView } from '../domain/account.model';
import type { AssistantConfigurationView } from '../domain/assistant.model';
import {
  LINE_FIELDS,
  MAX_WEBSITE_NAME_LENGTH,
  MAX_WELCOME_LENGTH,
  PUBLISHING_CHANNEL_NAMES,
  WEBSITE_BRAND_COLORS,
  WEBSITE_LAUNCHER_POSITIONS,
  normalizeDomain,
  validateAllowedDomain,
  type AssistantPublishingView,
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
} from '../domain/publishing.model';
import { EMPTY_LINE_SETTINGS, EXPIRED_DEMO_LINE_TOKEN, type PublishingRecord } from './demo-seed-publishing';

/** 發布管道的純函式：由保存狀態推導統一狀態、說明文字與畫面資料，不連接任何外部服務。 */

const AUDIENCE_LABELS: Readonly<Record<AccountView['role'], string>> = {
  'smb-admin': '管理者',
  'internal-employee': '內部同仁',
  'external-customer': '外部客戶',
};

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
      installCheck: 'not-checked',
      installCheckedAt: null,
      paused: false,
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
    isObject(value['line']) &&
    LINE_FIELDS.every((field) => typeof (value['line'] as Record<string, unknown>)[field.id] === 'string')
  );
}

// ---------- 平台內分享 ----------

function platformStatus(record: PublishingRecord): [PublishingChannelStatus, string] {
  const count = record.platform.allowedAccountIds.length;
  if (record.platform.paused) return ['paused', '已暫停：使用連結暫時無法開啟，設定與各帳號的對話紀錄都會保留。'];
  if (count === 0) return ['not-configured', '尚未指定可使用的帳號。'];
  return ['published', `${count} 個帳號可使用；每個帳號保有獨立對話紀錄。`];
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

function websiteStatus(record: PublishingRecord, disconnected: boolean): [PublishingChannelStatus, string] {
  const website = record.website;
  if (website.paused) return ['paused', '已暫停：網站上的對話按鈕暫時隱藏，設定會保留。'];
  if (website.allowedDomains.length === 0) return ['not-configured', '尚未設定允許嵌入的網域。'];
  if (website.installCheck === 'detected' && disconnected) {
    return ['needs-attention', '官網連線中斷（模擬情境）：只有官網嵌入受影響，其他管道不受影響。請確認網站後重新檢查安裝狀態。'];
  }
  if (website.installCheck === 'not-detected') {
    return ['needs-attention', '在允許的網域上偵測不到嵌入碼（模擬結果），其他管道不受影響。請確認已放上嵌入碼後重新檢查。'];
  }
  if (website.installCheck === 'not-checked') return ['testing', '已設定允許的網域，尚未檢查安裝狀態。'];
  return ['published', `已在 ${website.allowedDomains[0]} 偵測到安裝（模擬結果）。`];
}

export function demoEmbedCode(assistantId: string, position: string): string {
  return [
    '<!-- Demo 嵌入碼：僅供展示，不可用於正式環境，也不會連接外部服務 -->',
    '<script',
    '  src="https://widget.demo.invalid/assistant.js"',
    `  data-assistant="demo-${assistantId}"`,
    `  data-position="${position}"`,
    '  data-demo-only="true"',
    '  async></script>',
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
  websiteDisconnected: boolean,
): AssistantPublishingView {
  const platform: PlatformSharingView = {
    channel: channelView(assistant, 'platform', platformStatus(record), record.platform.updatedAt),
    usagePath: `/use/${assistant.id}`,
    allowedAccountIds: record.platform.allowedAccountIds,
    candidates: platformCandidates(assistant, accounts).map((account) => ({
      id: account.id,
      displayName: account.displayName,
      audienceLabel: AUDIENCE_LABELS[account.role],
    })),
  };
  const { displayName, welcomeMessage, brandColor, position, allowedDomains, installCheck, installCheckedAt } =
    record.website;
  const website: WebsiteEmbedView = {
    displayName,
    welcomeMessage,
    brandColor,
    position,
    allowedDomains,
    channel: channelView(assistant, 'website', websiteStatus(record, websiteDisconnected), record.website.updatedAt),
    embedCode: demoEmbedCode(assistant.id, position),
    installCheck: websiteDisconnected && installCheck === 'detected' ? 'not-detected' : installCheck,
    installCheckedAt,
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
