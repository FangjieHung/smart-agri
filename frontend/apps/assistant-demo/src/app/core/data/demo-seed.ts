import type { AccountView } from '../domain/account.model';
import type { AssistantConfigurationView } from '../domain/assistant.model';
import type { DatabaseView } from '../domain/database.model';
import type {
  AssistantAnalyticsView,
  PrivateConversationView,
  StructuredSubmissionView,
} from '../domain/conversation.model';
import type { KnowledgeBaseView } from '../domain/knowledge-base.model';
import type { PublishingChannelView } from '../domain/publishing.model';

export interface DemoSeed {
  readonly accounts: readonly AccountView[];
  readonly assistants: readonly AssistantConfigurationView[];
  readonly knowledgeBases: readonly KnowledgeBaseView[];
  readonly databases: readonly DatabaseView[];
  readonly privateConversations: readonly PrivateConversationView[];
  readonly structuredSubmissions: readonly StructuredSubmissionView[];
  readonly analytics: readonly AssistantAnalyticsView[];
  readonly publishingChannels: readonly PublishingChannelView[];
}

export const DEMO_SEED: DemoSeed = {
  accounts: [
    {
      id: 'account-smb-admin',
      displayName: '安心商行管理者',
      role: 'smb-admin',
      permissions: [
        'manage-assistants',
        'manage-data-sources',
        'manage-publishing',
        'read-consented-submissions',
      ],
    },
    {
      id: 'account-internal-employee',
      displayName: '安心商行客服同仁',
      role: 'internal-employee',
      permissions: ['use-shared-assistants'],
    },
    {
      id: 'account-external-customer',
      displayName: '外部客戶',
      role: 'external-customer',
      permissions: ['submit-authorized-forms', 'read-own-tracking'],
    },
  ],
  assistants: [
    {
      id: 'assistant-customer-service',
      ownerAccountId: 'account-smb-admin',
      name: '客服助理',
      purpose: '回答商品、退貨與配送問題',
      status: 'published',
      audience: 'authorized-external-customers',
      sharedWithAccountIds: ['account-internal-employee'],
      knowledgeBaseIds: [
        'knowledge-product-guide',
        'knowledge-refund-policy',
        'knowledge-shipping-faq',
      ],
      databaseIds: ['database-orders', 'database-customer-records'],
    },
  ],
  knowledgeBases: [
    {
      id: 'knowledge-product-guide',
      ownerAccountId: 'account-smb-admin',
      name: '商品使用指南',
      status: 'ready',
      documentCount: 12,
      lastSyncedAt: '2026-09-18T08:00:00.000Z',
    },
    {
      id: 'knowledge-refund-policy',
      ownerAccountId: 'account-smb-admin',
      name: '退換貨政策',
      status: 'ready',
      documentCount: 4,
      lastSyncedAt: '2026-09-18T08:05:00.000Z',
    },
    {
      id: 'knowledge-shipping-faq',
      ownerAccountId: 'account-smb-admin',
      name: '配送常見問題',
      status: 'ready',
      documentCount: 7,
      lastSyncedAt: '2026-09-18T08:10:00.000Z',
    },
  ],
  databases: [
    {
      id: 'database-orders',
      ownerAccountId: 'account-smb-admin',
      name: '訂單資料庫',
      status: 'connected',
      accessMode: 'read-only',
      tableCount: 3,
    },
    {
      id: 'database-customer-records',
      ownerAccountId: 'account-smb-admin',
      name: '客戶資料庫',
      status: 'connected',
      accessMode: 'read-only',
      tableCount: 2,
    },
  ],
  privateConversations: [
    {
      id: 'conversation-employee-private',
      accountId: 'account-internal-employee',
      assistantId: 'assistant-customer-service',
      status: 'resolved',
      messages: [
        {
          id: 'message-employee-question',
          author: 'account',
          text: '如何處理退貨申請？',
          createdAt: '2026-09-18T09:00:00.000Z',
        },
        {
          id: 'message-employee-answer',
          author: 'assistant',
          text: '請先確認購買日期與商品狀態。',
          createdAt: '2026-09-18T09:00:10.000Z',
        },
      ],
      updatedAt: '2026-09-18T09:00:10.000Z',
    },
    {
      id: 'conversation-customer-private',
      accountId: 'account-external-customer',
      assistantId: 'assistant-customer-service',
      status: 'active',
      messages: [
        {
          id: 'message-customer-question',
          author: 'account',
          text: '我的訂單何時出貨？',
          createdAt: '2026-09-18T10:00:00.000Z',
        },
        {
          id: 'message-customer-answer',
          author: 'assistant',
          text: '請使用授權表單提供訂單編號。',
          createdAt: '2026-09-18T10:00:10.000Z',
        },
      ],
      updatedAt: '2026-09-18T10:00:10.000Z',
    },
  ],
  structuredSubmissions: [
    {
      id: 'submission-customer-authorized',
      assistantId: 'assistant-customer-service',
      submittedByAccountId: 'account-external-customer',
      dataManagerAccountId: 'account-smb-admin',
      consentStatus: 'consented',
      trackingStatus: 'in-review',
      fields: [
        { id: 'order-number', label: '訂單編號', value: 'DEMO-1042' },
        {
          id: 'contact-email',
          label: '聯絡信箱',
          value: 'customer@example.test',
        },
        { id: 'issue', label: '問題', value: '尚未收到出貨通知' },
      ],
      submittedAt: '2026-09-18T10:02:00.000Z',
    },
  ],
  analytics: [
    {
      assistantId: 'assistant-customer-service',
      period: 'last-7-days',
      conversationCount: 18,
      resolvedCount: 12,
      helpfulRatingPercent: 89,
    },
  ],
  publishingChannels: [
    {
      id: 'channel-website',
      assistantId: 'assistant-customer-service',
      ownerAccountId: 'account-smb-admin',
      name: '官網嵌入',
      type: 'website-embed',
      connectionStatus: 'connected',
      visibility: 'authorized-users-only',
    },
    {
      id: 'channel-public-link',
      assistantId: 'assistant-customer-service',
      ownerAccountId: 'account-smb-admin',
      name: '專屬連結',
      type: 'public-link',
      connectionStatus: 'connected',
      visibility: 'authorized-users-only',
    },
    {
      id: 'channel-qr-code',
      assistantId: 'assistant-customer-service',
      ownerAccountId: 'account-smb-admin',
      name: 'QR Code',
      type: 'qr-code',
      connectionStatus: 'connected',
      visibility: 'authorized-users-only',
    },
  ],
};
