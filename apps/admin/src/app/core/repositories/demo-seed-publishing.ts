import type { AccountId } from '../domain/account.model';
import type { SeededAssistantId } from '../domain/assistant.model';
import type {
  LineSettingsInput,
  LineTestResultView,
  WebsiteEmbedSettings,
  WebsiteInstallCheck,
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
    readonly installCheck: WebsiteInstallCheck;
    readonly installCheckedAt: string | null;
    readonly paused: boolean;
    readonly updatedAt: string;
  };
  readonly line: LineSettingsInput & {
    /** 是否已執行過逐欄檢查；未檢查時清單顯示「尚未檢查」。 */
    readonly checked: boolean;
    readonly enabled: boolean;
    readonly lastTest: LineTestResultView | null;
    readonly paused: boolean;
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

export const EMPTY_LINE_SETTINGS: LineSettingsInput = {
  officialAccountId: '',
  channelId: '',
  channelSecret: '',
  accessToken: '',
};

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
      installCheck: 'detected',
      installCheckedAt: '2026-09-20T03:00:00.000Z',
      paused: false,
      updatedAt: '2026-09-20T03:00:00.000Z',
    },
    line: {
      officialAccountId: '@anxin-demo',
      channelId: DEMO_LINE_CHANNEL_ID,
      channelSecret: DEMO_LINE_CHANNEL_SECRET,
      accessToken: EXPIRED_DEMO_LINE_TOKEN,
      checked: true,
      enabled: true,
      lastTest: null,
      paused: false,
      updatedAt: '2026-09-21T06:30:00.000Z',
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
      installCheck: 'not-checked',
      installCheckedAt: null,
      paused: false,
      updatedAt: '2026-09-21T02:00:00.000Z',
    },
    line: {
      ...EMPTY_LINE_SETTINGS,
      checked: false,
      enabled: false,
      lastTest: null,
      paused: false,
      updatedAt: '2026-09-18T08:00:00.000Z',
    },
  },
};
