import type { AccountId, AccountView } from '../domain/account.model';
import type { AssistantAcceptanceStatus } from '../domain/assistant-acceptance.model';
import { audienceAllowsRole, type AssistantConfigurationView } from '../domain/assistant.model';
import {
  LINE_FIELDS,
  MAX_LINE_WELCOME_LENGTH,
  MAX_WEBSITE_NAME_LENGTH,
  MAX_WELCOME_LENGTH,
  PUBLISHING_CHANNEL_NAMES,
  WEBSITE_BRAND_COLORS,
  WEBSITE_LAUNCHER_POSITIONS,
  normalizeDomain,
  validateAllowedDomain,
  type AssistantPublishingView,
  type LineConnectionCheckView,
  type LineField,
  type LinePublishFailure,
  type LineSettingsInput,
  type LineSetupView,
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
import {
  emptyLineRecord,
  EXPIRED_DEMO_LINE_TOKEN,
  LINE_CHECK_LABELS,
  LINE_CHECK_SKIPPED_MESSAGE,
  secretStatusOf,
  type PublishingRecord,
} from './demo-seed-publishing';

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
    line: emptyLineRecord(now),
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
    typeof value['line']['officialAccountId'] === 'string'
  );
}

/**
 * 讀回保存的記錄：舊版（#233 前）的 LINE 欄位把 Secret 與 Token 的原文存在瀏覽器裡，
 * 這裡轉成只有「已設定」與末四碼的新形狀（原文就此丟掉，連線測試結果要重測）；已經是新形狀的原樣回傳。
 * 回傳的第二個值表示有轉換過，呼叫端要把轉換後的記錄寫回去。
 */
export function upgradePublishingRecord(record: PublishingRecord): readonly [PublishingRecord, boolean] {
  const line = record.line as unknown as Record<string, unknown>;
  if (typeof line['channelSecret'] !== 'string' || typeof line['accessToken'] !== 'string') return [record, false];

  const old = line as unknown as {
    officialAccountId: string;
    channelId: string;
    channelSecret: string;
    accessToken: string;
    enabled?: boolean;
    paused?: boolean;
    updatedAt: string;
  };
  const updatedAt = old.updatedAt;
  const base = emptyLineRecord(updatedAt);
  const saved = old.officialAccountId.length > 0 || old.channelId.length > 0;
  return [
    {
      ...record,
      line: {
        ...base,
        officialAccountId: old.officialAccountId,
        channelId: old.channelId,
        channelSecret: old.channelSecret.length > 0 ? secretStatusOf(old.channelSecret, updatedAt) : base.channelSecret,
        accessToken: old.accessToken.length > 0 ? secretStatusOf(old.accessToken, updatedAt) : base.accessToken,
        tokenExpired: old.accessToken === EXPIRED_DEMO_LINE_TOKEN,
        state: old.enabled !== true ? 'draft' : old.paused === true ? 'paused' : 'published',
        revision: saved ? 1 : 0,
      },
    },
    true,
  ];
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

export const LINE_CONFLICT_MESSAGE = 'LINE 頻道的設定已在其他分頁被更新過，請重新載入後再修改。';
export const LINE_PUBLISH_REFUSED_MESSAGE = '目前還不能啟用 LINE 頻道，請先處理下列項目。';
export const LINE_TEST_SETTINGS_MESSAGE = '請先填寫並儲存 LINE 官方帳號的連接資訊，再測試連線。';
export const LINE_TEST_REFUSED_MESSAGE = '目前還不能測試連線，請先處理下列項目。';

const LINE_CONNECTION_NOT_PASSED_MESSAGE =
  '請先測試連線，三項檢查（Token 與官方帳號、Webhook 網址、Webhook 連線測試）都通過後才能啟用。';

/** 與 API 的 `LineChannelRules.Normalize` 相同：JavaScript 的 `trim()`。 */
export function trimLineValue(raw: string): string {
  return raw.trim();
}

/** 一個連接欄位的格式檢查（與 API 的 `LineChannelRules.ValidateField` 同規則與訊息）；通過時回傳 null。 */
export function validateLineField(field: LineField, raw: string): string | null {
  const label = LINE_FIELDS.find((candidate) => candidate.id === field)?.label ?? field;
  const value = trimLineValue(raw);
  if (value.length === 0) return `請填寫 ${label}。`;
  const rule = LINE_PATTERNS[field];
  return rule.pattern.test(value) ? null : `${label}${rule.message}`;
}

/**
 * 驗證並正規化 LINE 設定（與 API 的 `LineChannelRules.ForUpdate` 同規則）：官方帳號 ID、Channel ID 與歡迎訊息一定要填；
 * Secret 與 Token 空白表示「不變更」，只有還沒設定過時才必填。錯誤依畫面欄位順序排列。
 */
export function validateLineSettings(
  input: LineSettingsInput,
  stored: Pick<PublishingRecord['line'], 'channelSecret' | 'accessToken'>,
): {
  readonly errors: readonly PublishingFieldError[];
  readonly normalized: Required<LineSettingsInput>;
} {
  const errors: PublishingFieldError[] = [];
  const check = (field: LineField, raw: string) => {
    const message = validateLineField(field, raw);
    if (message !== null) errors.push({ field, message });
  };
  check('officialAccountId', input.officialAccountId);
  check('channelId', input.channelId);
  const channelSecret = trimLineValue(input.channelSecret ?? '');
  if (channelSecret.length > 0 || !stored.channelSecret.configured) check('channelSecret', channelSecret);
  const accessToken = trimLineValue(input.accessToken ?? '');
  if (accessToken.length > 0 || !stored.accessToken.configured) check('accessToken', accessToken);
  const welcomeMessage = trimLineValue(input.welcomeMessage);
  if (welcomeMessage.length === 0) errors.push({ field: 'welcomeMessage', message: '請填寫歡迎訊息。' });
  else if (welcomeMessage.length > MAX_LINE_WELCOME_LENGTH) {
    errors.push({ field: 'welcomeMessage', message: `歡迎訊息請在 ${MAX_LINE_WELCOME_LENGTH} 個字以內。` });
  }

  return {
    errors,
    normalized: {
      officialAccountId: trimLineValue(input.officialAccountId),
      channelId: trimLineValue(input.channelId),
      welcomeMessage,
      channelSecret,
      accessToken,
    },
  };
}

/**
 * 儲存設定後的記錄：憑證只留狀態（原文在這裡就丟掉）；官方帳號 ID、Channel ID 或憑證有變，
 * 清除連線測試結果，已啟用的退回草稿（與 API 的 `TryApplySettings` 相同）；只改歡迎訊息不影響。
 */
export function applyLineSettings(
  line: PublishingRecord['line'],
  settings: Required<LineSettingsInput>,
  now: string,
): PublishingRecord['line'] {
  const newSecret = settings.channelSecret.length > 0;
  const newToken = settings.accessToken.length > 0;
  const connectionChanged =
    line.revision === 0 ||
    settings.officialAccountId !== line.officialAccountId ||
    settings.channelId !== line.channelId ||
    newSecret ||
    newToken;
  return {
    ...line,
    officialAccountId: settings.officialAccountId,
    channelId: settings.channelId,
    welcomeMessage: settings.welcomeMessage,
    channelSecret: newSecret ? secretStatusOf(settings.channelSecret, now) : line.channelSecret,
    accessToken: newToken ? secretStatusOf(settings.accessToken, now) : line.accessToken,
    tokenExpired: newToken ? settings.accessToken === EXPIRED_DEMO_LINE_TOKEN : line.tokenExpired,
    ...(connectionChanged
      ? {
          checks: [],
          checkedAt: null,
          state: 'draft' as const,
          publishedAt: null,
        }
      : {}),
    revision: line.revision + 1,
    updatedAt: now,
  };
}

const LINE_CHECK_KINDS = ['access-token', 'webhook-endpoint', 'webhook-test'] as const;

/** 畫面上的三項檢查：沒有測試結果時都是 `pending`。 */
export function lineConnectionChecks(line: PublishingRecord['line']): readonly LineConnectionCheckView[] {
  return LINE_CHECK_KINDS.map((check): LineConnectionCheckView =>
    line.checks.find((stored) => stored.check === check) ?? {
      check,
      label: LINE_CHECK_LABELS[check],
      state: 'pending',
      message: '尚未測試。',
    },
  );
}

export function lineChecksPassed(line: PublishingRecord['line']): boolean {
  return line.checks.length === LINE_CHECK_KINDS.length && line.checks.every((check) => check.state === 'passed');
}

/**
 * 模擬「測試連線」（決定 B）：不會連接 LINE。Token 是已失效的示範權杖時第一項未通過、後兩項略過；
 * 其餘三項通過。訊息與 API 同樣是繁體中文，註明是模擬結果。
 */
export function simulateLineConnectionTest(line: PublishingRecord['line'], webhookUrl: string): readonly LineConnectionCheckView[] {
  const label = LINE_CHECK_LABELS;
  if (line.tokenExpired) {
    return [
      {
        check: 'access-token',
        label: label['access-token'],
        state: 'failed',
        message:
          'LINE 不接受這個 Channel access token（此權杖已失效，模擬結果）；請重新發行長效型 Token，重新填寫後儲存。',
      },
      { check: 'webhook-endpoint', label: label['webhook-endpoint'], state: 'skipped', message: LINE_CHECK_SKIPPED_MESSAGE },
      { check: 'webhook-test', label: label['webhook-test'], state: 'skipped', message: LINE_CHECK_SKIPPED_MESSAGE },
    ];
  }
  return [
    {
      check: 'access-token',
      label: label['access-token'],
      state: 'passed',
      message: `Token 有效，屬於官方帳號 ${line.officialAccountId}（模擬結果，未實際連接 LINE）。`,
    },
    {
      check: 'webhook-endpoint',
      label: label['webhook-endpoint'],
      state: 'passed',
      message: `已將 LINE 的 Webhook 網址設為 ${webhookUrl}（模擬）。`,
    },
    {
      check: 'webhook-test',
      label: label['webhook-test'],
      state: 'passed',
      message: 'LINE 送出的測試事件已送達，伺服器以 Channel secret 驗證簽章通過（模擬）。',
    },
  ];
}

/** LINE 的實際服務狀態：與官網同一組推導，「三項檢查都通過」取代「有允許網域」（M5b 計畫第 4 節）。 */
export function lineServingState(line: PublishingRecord['line'], context: WebsiteServingContext): WebsiteServingState {
  if (line.state === 'draft' || !lineChecksPassed(line)) return 'not-published';
  if (line.state === 'paused') return 'paused';
  if (context.acceptanceStatus === 'failed' || context.acceptanceStatus === 'not-accepted') return 'suspended-acceptance';
  if (context.nonOwnedKnowledgeBases.length > 0) return 'suspended-knowledge';
  return context.quotaExceeded ? 'suspended-quota' : 'serving';
}

/** 實際服務狀態 → 管道卡狀態與說明（與 API 的 `Describe` 同一套文字）。 */
function lineStatus(line: PublishingRecord['line'], serving: WebsiteServingState): [PublishingChannelStatus, string] {
  switch (serving) {
    case 'paused':
      return ['paused', '已暫停：LINE 使用者目前會收到「暫停服務」，設定會保留。'];
    case 'suspended-acceptance':
      return ['needs-attention', '驗收未通過，已自動暫停 LINE 回覆；請到題組頁處理，重跑通過後會自動恢復。'];
    case 'suspended-knowledge':
      return ['needs-attention', '連接了不是助理擁有者自己的知識庫，已自動暫停 LINE 回覆；解除連接這些知識庫後會自動恢復。'];
    case 'suspended-quota':
      return ['needs-attention', '本月用量已達上限，已暫停 LINE 回覆；下個月或調高上限後會自動恢復。'];
    case 'serving':
      return ['published', `${line.officialAccountId} 已啟用，可在 LINE 對話中使用。`];
    default:
      break;
  }
  if (line.revision === 0 && line.officialAccountId.length === 0) {
    return ['not-configured', '尚未填寫 LINE 官方帳號連接資訊。'];
  }
  if (line.state !== 'draft') {
    return ['needs-attention', '連線測試未通過，LINE 使用者目前收不到回覆；請依檢查結果修正後重新測試連線。'];
  }
  if (line.checks.length === 0) return ['testing', '連接資訊已儲存，請測試連線；三項檢查都通過、驗收通過後即可啟用。'];
  if (!lineChecksPassed(line)) return ['needs-attention', '連線測試未通過，請依檢查結果修正後重新測試連線。'];
  return ['testing', '連線測試已通過，驗收通過後即可啟用。'];
}

/** 啟用閘門（與 API 的 `LineChannelRules.PublishFailures` 同一組判斷與訊息；mock 沒有 `assistant-paused`、`public-base-url`）。 */
export function linePublishFailures(
  line: PublishingRecord['line'],
  context: WebsiteServingContext,
): readonly LinePublishFailure[] {
  const failures: LinePublishFailure[] = [];
  if (!lineChecksPassed(line)) failures.push({ reason: 'connection', message: LINE_CONNECTION_NOT_PASSED_MESSAGE });
  if (context.acceptanceStatus !== 'passed') {
    failures.push({
      reason: 'acceptance',
      message: '驗收狀態必須是「通過」才能對外發布；請先到題組頁執行驗收並讓所有題目通過。',
    });
  }
  for (const knowledgeBase of context.nonOwnedKnowledgeBases) {
    failures.push({
      reason: 'knowledge-ownership',
      message: `「${knowledgeBase.name}」不是助理擁有者自己的知識庫，對外發布時不能使用；請解除連接後再發布。`,
    });
  }
  return failures;
}

/** 示範 Webhook 網址，指向保留網域，只供畫面展示。 */
export function demoLineWebhookUrl(assistantId: string): string {
  return `https://webhook.demo.invalid/line/${assistantId}`;
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
  return websiteServing || lineServingState(record.line, website) === 'serving';
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
): AssistantPublishingView {
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
  const lineServing = lineServingState(record.line, websiteContext);
  const line: LineSetupView = {
    channel: channelView(assistant, 'line', lineStatus(record.line, lineServing), record.line.updatedAt),
    officialAccountId: record.line.officialAccountId,
    channelId: record.line.channelId,
    welcomeMessage: record.line.welcomeMessage,
    channelSecret: record.line.channelSecret,
    accessToken: record.line.accessToken,
    webhookUrl: demoLineWebhookUrl(assistant.id),
    checks: lineConnectionChecks(record.line),
    connectionCheckedAt: record.line.checkedAt,
    state: record.line.state,
    servingState: lineServing,
    acceptanceStatus: websiteContext.acceptanceStatus,
    nonOwnedKnowledgeBases: websiteContext.nonOwnedKnowledgeBases,
    publishedAt: record.line.publishedAt,
    pushFallbackCount: record.line.pushFallbackCount,
    revision: record.line.revision,
  };

  return { assistantId: assistant.id, assistantName: assistant.name, platform, website, line };
}
