import type { AccountId } from '../domain/account.model';
import type { SubmissionConsentStatus } from '../domain/conversation.model';
import type {
  DatabaseFieldView,
  DatabaseId,
  DatabaseRecordId,
  DatabaseRecordSource,
  DatabaseRecordValue,
  DatabaseTemplateView,
  SeededDatabaseId,
  TrackedSubjectId,
} from '../domain/database.model';

/** 資料庫的收集設定：用途、表單欄位與指定資料管理者。 */
export interface DatabaseCollectionFixture {
  readonly purpose: string;
  readonly templateName: string;
  readonly dataManagerAccountIds: readonly AccountId[];
  readonly fields: readonly DatabaseFieldView[];
}

export interface TrackedSubjectFixture {
  readonly id: TrackedSubjectId;
  readonly databaseId: DatabaseId;
  readonly displayName: string;
}

export interface DatabaseRecordFixture {
  readonly id: DatabaseRecordId;
  readonly databaseId: DatabaseId;
  readonly subjectId: TrackedSubjectId;
  readonly recordedAt: string;
  readonly source: DatabaseRecordSource;
  readonly consentStatus: SubmissionConsentStatus;
  /** 撤回同意的時間（ISO）；只有 `withdrawn` 的紀錄才有，Demo 舊資料可能沒有。 */
  readonly withdrawnAt?: string;
  /** 已撤回的紀錄一律是空陣列：撤回時內容就從收集紀錄移除了。 */
  readonly values: readonly DatabaseRecordValue[];
}

const text = (id: `field-${string}`, label: string, required = false): DatabaseFieldView => ({
  id, label, type: 'text', required, options: [], scale: null, unit: '',
});
const date = (id: `field-${string}`, label: string, required = false): DatabaseFieldView => ({
  id, label, type: 'date', required, options: [], scale: null, unit: '',
});
const number = (id: `field-${string}`, label: string, unit: string, required = false): DatabaseFieldView => ({
  id, label, type: 'number', required, options: [], scale: null, unit,
});
const single = (id: `field-${string}`, label: string, options: readonly string[], required = false): DatabaseFieldView => ({
  id, label, type: 'single-choice', required, options, scale: null, unit: '',
});
const multiple = (id: `field-${string}`, label: string, options: readonly string[], required = false): DatabaseFieldView => ({
  id, label, type: 'multiple-choice', required, options, scale: null, unit: '',
});
const scale = (
  id: `field-${string}`,
  label: string,
  range: DatabaseFieldView['scale'],
  required = false,
): DatabaseFieldView => ({ id, label, type: 'scale', required, options: [], scale: range, unit: '' });

export const DATABASE_TEMPLATES: readonly DatabaseTemplateView[] = [
  {
    id: 'template-customer-profile',
    name: '客戶基本資料',
    description: '整理客戶聯絡方式與類型，方便後續服務。',
    fields: [
      text('field-customer-name', '客戶姓名', true),
      text('field-phone', '聯絡電話'),
      date('field-first-visit', '首次來店日期'),
      single('field-customer-type', '客戶類型', ['個人', '企業'], true),
    ],
  },
  {
    id: 'template-periodic-report',
    name: '定期回報',
    description: '每週或每月收集同一格式的回報，比較每一期的變化。',
    fields: [
      date('field-report-date', '回報日期', true),
      number('field-completed-count', '本期完成數量', '件', true),
      text('field-issue', '遇到的問題'),
    ],
  },
  {
    id: 'template-satisfaction',
    name: '滿意度調查',
    description: '收集客戶對服務的評分與建議。',
    fields: [
      scale('field-overall-satisfaction', '整體滿意度', { min: 1, max: 5, minLabel: '很不滿意', maxLabel: '非常滿意' }, true),
      multiple('field-liked-services', '喜歡的服務', ['商品品質', '客服回應', '配送速度']),
      text('field-suggestion', '其他建議'),
    ],
  },
  {
    id: 'template-progress',
    name: '症狀或進度追蹤',
    description: '為每位追蹤對象建立時間軸，比較本次、上次與首次的變化。',
    fields: [
      date('field-check-date', '紀錄日期', true),
      scale('field-condition-score', '狀況分數', { min: 0, max: 10, minLabel: '最差', maxLabel: '最好' }, true),
      text('field-progress-note', '備註'),
    ],
  },
  {
    id: 'template-blank',
    name: '空白模板',
    description: '從一個文字欄位開始，自行設計要收集的欄位。',
    fields: [text('field-item', '項目名稱', true)],
  },
];

export const DATABASE_COLLECTIONS: Readonly<Record<SeededDatabaseId, DatabaseCollectionFixture>> = {
  'database-orders': {
    purpose: '收集訂單問題回報，讓客服追蹤處理進度。',
    templateName: '空白模板',
    dataManagerAccountIds: ['account-smb-admin'],
    fields: [
      text('field-order-number', '訂單編號', true),
      single('field-issue-type', '問題類型', ['配送延遲', '商品瑕疵', '退換貨'], true),
      date('field-reported-on', '回報日期', true),
    ],
  },
  'database-customer-records': {
    purpose: '記錄回訪客戶的滿意度與消費變化，比較每次回訪的差異。',
    templateName: '定期回報',
    dataManagerAccountIds: ['account-smb-admin'],
    fields: [
      date('field-visit-date', '回訪日期', true),
      scale('field-satisfaction', '整體滿意度', { min: 1, max: 5, minLabel: '很不滿意', maxLabel: '非常滿意' }, true),
      number('field-monthly-spend', '本月消費金額', '元', true),
      single('field-membership', '會員等級', ['一般', '銀卡', '金卡'], true),
      multiple('field-interests', '關注商品', ['保養品', '清潔用品', '配件']),
      text('field-note', '備註'),
    ],
  },
  'database-staff-checkins': {
    purpose: '同仁每週回報排班與支援需求。',
    templateName: '定期回報',
    dataManagerAccountIds: ['account-internal-employee'],
    fields: [date('field-week', '回報週次', true), text('field-support', '需要的支援')],
  },
};

export const TRACKED_SUBJECTS: readonly TrackedSubjectFixture[] = [
  { id: 'subject-wang', databaseId: 'database-customer-records', displayName: '王小姐' },
  { id: 'subject-lin', databaseId: 'database-customer-records', displayName: '林小姐' },
  { id: 'subject-chen', databaseId: 'database-customer-records', displayName: '陳先生' },
];

function visit(
  visitDate: string,
  satisfaction: number,
  spend: number,
  membership: string,
  interests: readonly string[],
  note: string,
): readonly DatabaseRecordValue[] {
  return [
    { fieldId: 'field-visit-date', label: '回訪日期', type: 'date', value: visitDate },
    { fieldId: 'field-satisfaction', label: '整體滿意度', type: 'scale', value: satisfaction, min: 1, max: 5 },
    { fieldId: 'field-monthly-spend', label: '本月消費金額', type: 'number', value: spend, unit: '元' },
    { fieldId: 'field-membership', label: '會員等級', type: 'single-choice', value: membership },
    { fieldId: 'field-interests', label: '關注商品', type: 'multiple-choice', value: interests },
    { fieldId: 'field-note', label: '備註', type: 'text', value: note },
  ];
}

export const DATABASE_RECORDS: readonly DatabaseRecordFixture[] = [
  {
    id: 'record-wang-1',
    databaseId: 'database-customer-records',
    subjectId: 'subject-wang',
    recordedAt: '2026-06-15T02:00:00.000Z',
    source: 'form-link',
    consentStatus: 'consented',
    values: visit('2026-06-15', 3, 1800, '一般', ['保養品'], '第一次回訪'),
  },
  {
    id: 'record-wang-2',
    databaseId: 'database-customer-records',
    subjectId: 'subject-wang',
    recordedAt: '2026-07-15T02:00:00.000Z',
    source: 'assistant-conversation',
    consentStatus: 'consented',
    values: visit('2026-07-15', 4, 2400, '銀卡', ['保養品', '配件'], '升級銀卡'),
  },
  {
    id: 'record-wang-3',
    databaseId: 'database-customer-records',
    subjectId: 'subject-wang',
    recordedAt: '2026-08-15T02:00:00.000Z',
    source: 'assistant-conversation',
    consentStatus: 'consented',
    values: visit('2026-08-15', 3, 2100, '銀卡', ['保養品'], '反映配送較慢'),
  },
  {
    id: 'record-wang-4',
    databaseId: 'database-customer-records',
    subjectId: 'subject-wang',
    recordedAt: '2026-09-15T02:00:00.000Z',
    source: 'form-link',
    consentStatus: 'consented',
    values: visit('2026-09-15', 5, 3200, '金卡', ['保養品', '清潔用品'], '升級金卡'),
  },
  {
    id: 'record-lin-1',
    databaseId: 'database-customer-records',
    subjectId: 'subject-lin',
    recordedAt: '2026-08-20T02:00:00.000Z',
    source: 'form-link',
    consentStatus: 'consented',
    values: visit('2026-08-20', 4, 1500, '一般', ['清潔用品'], ''),
  },
  {
    id: 'record-lin-2',
    databaseId: 'database-customer-records',
    subjectId: 'subject-lin',
    recordedAt: '2026-09-10T02:00:00.000Z',
    source: 'assistant-conversation',
    consentStatus: 'consented',
    values: visit('2026-09-10', 3, 1200, '一般', ['清潔用品'], '詢問退貨流程'),
  },
  // 已撤回同意：內容已移除，只留下曾經提交與撤回的時間軌跡。
  {
    id: 'record-lin-3',
    databaseId: 'database-customer-records',
    subjectId: 'subject-lin',
    recordedAt: '2026-09-18T02:00:00.000Z',
    source: 'assistant-conversation',
    consentStatus: 'withdrawn',
    withdrawnAt: '2026-09-19T02:00:00.000Z',
    values: [],
  },
  {
    id: 'record-chen-1',
    databaseId: 'database-customer-records',
    subjectId: 'subject-chen',
    recordedAt: '2026-09-12T02:00:00.000Z',
    source: 'form-link',
    consentStatus: 'consented',
    values: visit('2026-09-12', 4, 900, '一般', ['配件'], '新客戶'),
  },
];
