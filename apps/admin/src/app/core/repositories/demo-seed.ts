import type { AccountView } from '../domain/account.model';
import type {
  AssistantTemplateView,
  TrialQuestionView,
} from '../domain/assistant-draft.model';
import type { AssistantConfigurationView } from '../domain/assistant.model';
import type {
  DatabaseTemplateView,
  DatabaseView,
  SeededDatabaseId,
} from '../domain/database.model';
import type {
  AssistantAnalyticsView,
  PrivateConversationView,
  StructuredSubmissionView,
} from '../domain/conversation.model';
import type {
  KnowledgeBaseId,
  KnowledgeBaseView,
  KnowledgeDocumentView,
  KnowledgeSharingView,
} from '../domain/knowledge-base.model';
import type { PublishingChannelView } from '../domain/publishing.model';
import {
  DATABASE_COLLECTIONS,
  DATABASE_RECORDS,
  DATABASE_TEMPLATES,
  TRACKED_SUBJECTS,
  type DatabaseCollectionFixture,
  type DatabaseRecordFixture,
  type TrackedSubjectFixture,
} from './demo-seed-databases';

export interface DemoSeed {
  readonly accounts: readonly AccountView[];
  readonly assistants: readonly AssistantConfigurationView[];
  readonly knowledgeBases: readonly KnowledgeBaseView[];
  readonly knowledgeDocuments: Readonly<Record<KnowledgeBaseId, readonly KnowledgeDocumentView[]>>;
  readonly knowledgeSharing: Readonly<Record<KnowledgeBaseId, KnowledgeSharingView>>;
  readonly databases: readonly DatabaseView[];
  readonly databaseTemplates: readonly DatabaseTemplateView[];
  readonly databaseCollections: Readonly<Record<SeededDatabaseId, DatabaseCollectionFixture>>;
  readonly trackedSubjects: readonly TrackedSubjectFixture[];
  readonly databaseRecords: readonly DatabaseRecordFixture[];
  readonly privateConversations: readonly PrivateConversationView[];
  readonly structuredSubmissions: readonly StructuredSubmissionView[];
  readonly analytics: readonly AssistantAnalyticsView[];
  readonly publishingChannels: readonly PublishingChannelView[];
  readonly assistantTemplates: readonly AssistantTemplateView[];
  readonly trialQuestions: readonly TrialQuestionFixture[];
}

/** 試問固定回答：不連接真實 AI，依來源與規則挑選其中一種回答。 */
export interface TrialQuestionFixture extends TrialQuestionView {
  readonly companyAnswer: {
    readonly sourceId: KnowledgeBaseView['id'];
    readonly text: string;
    readonly excerpt: string;
  } | null;
  readonly generalAnswer: string | null;
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
    {
      id: 'assistant-internal-onboarding',
      ownerAccountId: 'account-smb-admin',
      name: '內部教育訓練助理',
      purpose: '協助同仁熟悉商品與操作流程',
      status: 'ready',
      audience: 'account-members',
      sharedWithAccountIds: [],
      knowledgeBaseIds: ['knowledge-product-guide'],
      databaseIds: [],
    },
  ],
  knowledgeBases: [
    {
      id: 'knowledge-product-guide',
      ownerAccountId: 'account-smb-admin',
      name: '商品使用指南',
      purpose: '商品規格、操作步驟與保養方式',
      lastSyncedAt: '2026-09-18T08:00:00.000Z',
    },
    {
      id: 'knowledge-refund-policy',
      ownerAccountId: 'account-smb-admin',
      name: '退換貨政策',
      purpose: '退貨期限、換貨條件與退款流程',
      lastSyncedAt: '2026-09-18T08:05:00.000Z',
    },
    {
      id: 'knowledge-shipping-faq',
      ownerAccountId: 'account-smb-admin',
      name: '配送常見問題',
      purpose: '運費、配送時間與離島配送說明',
      lastSyncedAt: '2026-09-18T08:10:00.000Z',
    },
    {
      id: 'knowledge-staff-notes',
      ownerAccountId: 'account-internal-employee',
      name: '同仁個人筆記',
      purpose: '客服同仁自己整理的回覆筆記',
      lastSyncedAt: '2026-09-17T09:00:00.000Z',
    },
  ],
  knowledgeDocuments: {
    'knowledge-product-guide': [
      { id: 'document-guide-specs', kind: 'document', name: '商品規格總表.pdf', status: 'ready', issue: null, updatedAt: '2026-09-18T07:40:00.000Z' },
      { id: 'document-guide-setup', kind: 'document', name: '初次使用設定步驟.docx', status: 'ready', issue: null, updatedAt: '2026-09-18T07:45:00.000Z' },
      { id: 'document-guide-scan', kind: 'document', name: '舊版說明書掃描檔.pdf', status: 'partially-readable', issue: '第 3–5 頁為掃描影像，無法讀取文字；其餘頁面可正常引用。', updatedAt: '2026-09-18T07:50:00.000Z' },
      { id: 'document-guide-locked', kind: 'document', name: '保固條款（加密）.pdf', status: 'failed', issue: '檔案設有開啟密碼，無法讀取內容。請移除密碼後重新處理。', updatedAt: '2026-09-18T07:55:00.000Z' },
      { id: 'document-guide-faq-care', kind: 'faq', name: '皮革商品可以用水清洗嗎？', status: 'ready', issue: null, updatedAt: '2026-09-18T08:00:00.000Z' },
      { id: 'document-guide-faq-warranty', kind: 'faq', name: '保固期間是多久？', status: 'ready', issue: null, updatedAt: '2026-09-18T08:00:00.000Z' },
    ],
    'knowledge-refund-policy': [
      { id: 'document-refund-policy', kind: 'document', name: '退換貨辦法 2026 版.pdf', status: 'ready', issue: null, updatedAt: '2026-09-18T08:05:00.000Z' },
      { id: 'document-refund-flow', kind: 'document', name: '退款作業流程.docx', status: 'ready', issue: null, updatedAt: '2026-09-18T08:05:00.000Z' },
      { id: 'document-refund-faq-window', kind: 'faq', name: '收到商品幾天內可以退貨？', status: 'ready', issue: null, updatedAt: '2026-09-18T08:05:00.000Z' },
    ],
    'knowledge-shipping-faq': [
      { id: 'document-shipping-rates', kind: 'document', name: '運費與配送時間表.xlsx', status: 'ready', issue: null, updatedAt: '2026-09-18T08:10:00.000Z' },
      { id: 'document-shipping-islands', kind: 'document', name: '離島配送說明.pdf', status: 'processing', issue: null, updatedAt: '2026-09-18T08:12:00.000Z' },
      { id: 'document-shipping-faq-holiday', kind: 'faq', name: '連假期間會出貨嗎？', status: 'queued', issue: null, updatedAt: '2026-09-18T08:14:00.000Z' },
    ],
    'knowledge-staff-notes': [
      { id: 'document-staff-notes', kind: 'document', name: '個人回覆範本.docx', status: 'ready', issue: null, updatedAt: '2026-09-17T09:00:00.000Z' },
    ],
  },
  knowledgeSharing: {
    'knowledge-product-guide': {
      scope: 'specific-accounts',
      sharedWithAccountIds: ['account-internal-employee'],
      allowOriginalDownload: false,
    },
    'knowledge-refund-policy': { scope: 'private', sharedWithAccountIds: [], allowOriginalDownload: false },
    'knowledge-shipping-faq': { scope: 'public', sharedWithAccountIds: [], allowOriginalDownload: false },
    'knowledge-staff-notes': { scope: 'private', sharedWithAccountIds: [], allowOriginalDownload: false },
  },
  databases: [
    {
      id: 'database-orders',
      ownerAccountId: 'account-smb-admin',
      name: '訂單資料庫',
      status: 'connected',
      accessMode: 'read-only',
      tableCount: 3,
      lastSyncedAt: '2026-09-18T07:30:00.000Z',
    },
    {
      id: 'database-customer-records',
      ownerAccountId: 'account-smb-admin',
      name: '客戶資料庫',
      status: 'connected',
      accessMode: 'read-only',
      tableCount: 2,
      lastSyncedAt: '2026-09-17T16:00:00.000Z',
    },
    {
      id: 'database-staff-checkins',
      ownerAccountId: 'account-internal-employee',
      name: '同仁排班回報',
      status: 'connected',
      accessMode: 'read-only',
      tableCount: 1,
      lastSyncedAt: '2026-09-16T09:00:00.000Z',
    },
  ],
  databaseTemplates: DATABASE_TEMPLATES,
  databaseCollections: DATABASE_COLLECTIONS,
  trackedSubjects: TRACKED_SUBJECTS,
  databaseRecords: DATABASE_RECORDS,
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
  assistantTemplates: [
    {
      id: 'answer-customer-questions',
      title: '回答客戶問題',
      description: '依據商品、退換貨與配送資料回覆客戶，找不到答案時轉由專人處理。',
      defaults: {
        name: '客戶問答助理',
        purpose: '回答客戶關於商品、退換貨與配送的問題',
        tone: 'friendly',
        roleInstructions: '回答時先確認客戶的訂單或商品，再引用公司資料說明。',
      },
    },
    {
      id: 'search-company-data',
      title: '查詢公司資料',
      description: '讓同仁快速查到制度、流程與商品規格。',
      defaults: {
        name: '公司資料查詢助理',
        purpose: '協助同仁查詢公司制度、流程與商品資料',
        tone: 'concise',
        roleInstructions: '',
      },
    },
    {
      id: 'onboard-new-employees',
      title: '協助新員工',
      description: '回答新進同仁常見問題，帶他們熟悉工作流程。',
      defaults: {
        name: '新人導覽助理',
        purpose: '回答新進同仁常見問題並說明工作流程',
        tone: 'friendly',
        roleInstructions: '',
      },
    },
    {
      id: 'collect-periodic-reports',
      title: '收集定期回報',
      description: '定期請使用者填寫回報表，整理成可比較的紀錄。',
      defaults: {
        name: '定期回報助理',
        purpose: '定期收集回報內容並整理成紀錄',
        tone: 'professional',
        roleInstructions: '',
      },
    },
    {
      id: 'compare-changes',
      title: '比較歷次變化',
      description: '依據歷次紀錄說明數值的前後變化。',
      defaults: {
        name: '變化追蹤助理',
        purpose: '比較歷次紀錄並說明有意義的變化',
        tone: 'professional',
        roleInstructions: '',
      },
    },
    {
      id: 'blank',
      title: '空白助理',
      description: '從空白開始，自己填寫用途與回答方式。',
      defaults: { name: '', purpose: '', tone: 'friendly', roleInstructions: '' },
    },
  ],
  trialQuestions: [
    {
      id: 'trial-refund-window',
      text: '收到商品後幾天內可以申請退貨？',
      companyAnswer: {
        sourceId: 'knowledge-refund-policy',
        text: '收到商品後 7 天內可以申請退貨，商品需保持完整包裝。',
        excerpt: '消費者於收受商品後七日內，得申請退貨，商品應保持原包裝完整。',
      },
      generalAnswer: null,
    },
    {
      id: 'trial-leather-care',
      text: '皮革商品平常要怎麼保養？',
      companyAnswer: null,
      generalAnswer: '一般建議避免長時間日曬與潮濕，並定期使用皮革保養油。這不是公司資料，僅供參考。',
    },
    {
      id: 'trial-unrelated-request',
      text: '可以幫我訂下週的機票嗎？',
      companyAnswer: null,
      generalAnswer: null,
    },
  ],
};
