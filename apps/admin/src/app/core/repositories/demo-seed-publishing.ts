import type { AccountId } from '../domain/account.model';
import type { SeededAssistantId } from '../domain/assistant.model';
import type {
  LineChannelState,
  LineConnectionCheckView,
  SecretStatusView,
  WebsiteChannelState,
  WebsiteEmbedSettings,
} from '../domain/publishing.model';

/**
 * 發布管道的保存狀態。所有 token、secret 與網域皆為示範資料，
 * 不可用於正式環境，也不會連接任何外部服務。
 */
export interface PublishingRecord {
  readonly version: 1;
  readonly platform: {
    readonly allowedAccountIds: readonly AccountId[];
    readonly paused: boolean;
    readonly updatedAt: string;
  };
  readonly website: WebsiteEmbedSettings & {
    readonly state: WebsiteChannelState;
    readonly publishedAt: string | null;
    /** 儲存設定時遞增（樂觀鎖）；還沒存過是 0。 */
    readonly revision: number;
    /** 被動偵測：網域 → 最後一次偵測到嵌入的時間（只供參考）。 */
    readonly lastSeenAt: Readonly<Record<string, string>>;
    readonly updatedAt: string;
  };
  readonly line: {
    readonly officialAccountId: string;
    readonly channelId: string;
    readonly welcomeMessage: string;
    /** 收到非文字訊息時的回覆（#291）；#291 前存的記錄沒有這個欄位，讀回時補上預設值。 */
    readonly nonTextReply: string;
    /** 與 API 相同：憑證只保存「已設定」與末四碼，mock 也從不保存、回傳原文。 */
    readonly channelSecret: SecretStatusView;
    readonly accessToken: SecretStatusView;
    /** 模擬：存進去的是「已失效」的示範權杖（儲存時判斷），測試連線的第一項會未通過。 */
    readonly tokenExpired: boolean;
    /** 最近一次測試連線的三項結果；還沒測試（或設定改了）是空陣列，畫面顯示 `pending`。 */
    readonly checks: readonly LineConnectionCheckView[];
    readonly checkedAt: string | null;
    readonly state: LineChannelState;
    readonly publishedAt: string | null;
    /** 本月以 push 補送的回答數（mock 只有示範數字）。 */
    readonly pushFallbackCount: number;
    /** 儲存設定時遞增（樂觀鎖）；還沒存過是 0。 */
    readonly revision: number;
    readonly updatedAt: string;
  };
}

/** 模擬已失效的 LINE 權杖：格式正確，但檢查時會回報已失效。 */
export const EXPIRED_DEMO_LINE_TOKEN = 'demo-expired-token-not-for-production-000000000000';

// 以組合方式產生，而非直接寫死字面值，避免密鑰掃描器（如 GitGuardian）把這組
// 假的 channelId + channelSecret 誤判為真的 LINE Messaging OAuth2 憑證。
/** 假 LINE channel ID，符合 10 位數字格式，僅供示範資料使用。 */
export const DEMO_LINE_CHANNEL_ID = '1650'.padEnd(10, '0');
/** 假 LINE channel secret，符合 32 位英數字格式，僅供示範資料使用。 */
export const DEMO_LINE_CHANNEL_SECRET = '0123456789abcdef'.repeat(2);

export const DEFAULT_LINE_WELCOME_MESSAGE = '您好！有任何問題都可以直接問我。在群組裡請 @ 我再提問。';

/** 與 API 的 `AssistantLineChannel.DefaultNonTextReply` 相同。 */
export const DEFAULT_LINE_NON_TEXT_REPLY = '目前只能回答文字問題。';

export const LINE_CHECK_LABELS = {
  'access-token': 'Channel access token 與官方帳號',
  'webhook-endpoint': '設定 Webhook 網址',
  'webhook-test': 'Webhook 連線測試',
} as const;

export const LINE_CHECK_SKIPPED_MESSAGE = '前一項檢查未通過，這一項沒有執行。';

/** 憑證原文 → 狀態（只留末四碼）；原文在這裡之後就丟掉。 */
export function secretStatusOf(value: string, updatedAt: string): SecretStatusView {
  return { configured: true, lastFour: value.slice(-4), updatedAt };
}

export const NO_SECRET: SecretStatusView = { configured: false, lastFour: null, updatedAt: null };

/** 還沒設定過的 LINE 頻道（草稿、revision 0）。 */
export function emptyLineRecord(updatedAt: string): PublishingRecord['line'] {
  return {
    officialAccountId: '',
    channelId: '',
    welcomeMessage: DEFAULT_LINE_WELCOME_MESSAGE,
    nonTextReply: DEFAULT_LINE_NON_TEXT_REPLY,
    channelSecret: NO_SECRET,
    accessToken: NO_SECRET,
    tokenExpired: false,
    checks: [],
    checkedAt: null,
    state: 'draft',
    publishedAt: null,
    pushFallbackCount: 0,
    revision: 0,
    updatedAt,
  };
}

export const PUBLISHING_RECORDS: Readonly<Partial<Record<SeededAssistantId, PublishingRecord>>> = {
  'assistant-customer-service': {
    version: 1,
    platform: {
      allowedAccountIds: ['account-internal-employee', 'account-external-customer'],
      paused: false,
      updatedAt: '2026-09-18T09:00:00.000Z',
    },
    website: {
      displayName: '安心商行線上客服',
      welcomeMessage: '您好，我是安心商行客服助理，可以協助查詢商品、退換貨與配送。',
      brandColor: 'forest',
      position: 'bottom-right',
      allowedDomains: ['shop.anxin-demo.example'],
      state: 'published',
      publishedAt: '2026-09-20T03:00:00.000Z',
      revision: 1,
      lastSeenAt: { 'shop.anxin-demo.example': '2026-10-05T06:03:00.000Z' },
      updatedAt: '2026-09-20T03:00:00.000Z',
    },
    // 示範資料：已啟用，但 Token 已失效，最近一次測試連線未通過（對應「需要處理」）。
    line: {
      ...emptyLineRecord('2026-09-21T06:30:00.000Z'),
      officialAccountId: '@anxin-demo',
      channelId: DEMO_LINE_CHANNEL_ID,
      channelSecret: secretStatusOf(DEMO_LINE_CHANNEL_SECRET, '2026-09-21T06:30:00.000Z'),
      accessToken: secretStatusOf(EXPIRED_DEMO_LINE_TOKEN, '2026-09-21T06:30:00.000Z'),
      tokenExpired: true,
      checks: [
        {
          check: 'access-token',
          label: LINE_CHECK_LABELS['access-token'],
          state: 'failed',
          message: 'LINE 不接受這個 Channel access token（此權杖已失效，模擬結果）；請重新發行長效型 Token，重新填寫後儲存。',
        },
        { check: 'webhook-endpoint', label: LINE_CHECK_LABELS['webhook-endpoint'], state: 'skipped', message: LINE_CHECK_SKIPPED_MESSAGE },
        { check: 'webhook-test', label: LINE_CHECK_LABELS['webhook-test'], state: 'skipped', message: LINE_CHECK_SKIPPED_MESSAGE },
      ],
      checkedAt: '2026-10-05T01:00:00.000Z',
      state: 'published',
      publishedAt: '2026-09-21T06:30:00.000Z',
      pushFallbackCount: 3,
      revision: 1,
    },
  },
  'assistant-internal-onboarding': {
    version: 1,
    platform: {
      allowedAccountIds: ['account-internal-employee'],
      paused: true,
      updatedAt: '2026-09-19T01:00:00.000Z',
    },
    website: {
      displayName: '新人訓練小幫手',
      welcomeMessage: '歡迎加入安心商行！想先了解哪個商品或流程？',
      brandColor: 'ocean',
      position: 'bottom-left',
      allowedDomains: ['intranet.anxin-demo.example'],
      state: 'draft',
      publishedAt: null,
      revision: 1,
      lastSeenAt: {},
      updatedAt: '2026-09-21T02:00:00.000Z',
    },
    line: emptyLineRecord('2026-09-18T08:00:00.000Z'),
  },
};
