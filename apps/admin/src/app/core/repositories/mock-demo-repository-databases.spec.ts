import {
  DATABASE_FIELD_TYPES,
  createDatabaseField,
  type DatabaseFieldView,
  type DatabaseTrackingView,
} from '../domain/database.model';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';

function createRepository(seed = DEMO_SEED, storage = createMemoryStorage()) {
  return new MockDemoRepository(seed, {
    storage,
    now: () => new Date('2026-09-22T02:00:00.000Z'),
  });
}

function trackingOf(repository: MockDemoRepository, id = 'database-customer-records'): DatabaseTrackingView {
  const result = repository.getDatabaseTracking('account-smb-admin', id);
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

function fieldsOf(repository: MockDemoRepository, id: string): readonly DatabaseFieldView[] {
  const result = repository.getDatabaseDetail('account-smb-admin', id);
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data.fields;
}

describe('MockDemoRepository databases', () => {
  it('offers the five collection templates, starting from what to collect', () => {
    const result = createRepository().listDatabaseTemplates('account-smb-admin');

    expect(result.status).toBe('ready');
    if (result.status !== 'ready') return;
    expect(result.data.map((template) => template.name)).toEqual([
      '客戶基本資料',
      '定期回報',
      '滿意度調查',
      '症狀或進度追蹤',
      '空白模板',
    ]);
    const types = new Set(result.data.flatMap((template) => template.fields.map((field) => field.type)));
    types.forEach((type) => expect(DATABASE_FIELD_TYPES).toContain(type));
  });

  it('does not offer templates to an account that cannot manage data sources', () => {
    expect(createRepository().listDatabaseTemplates('account-internal-employee').status).toBe(
      'permission-denied',
    );
  });

  it('summarises only the owner’s databases with fields, records and connected assistants', () => {
    const result = createRepository().listDatabaseSummaries('account-smb-admin');

    expect(result.status).toBe('ready');
    if (result.status !== 'ready') return;
    expect(result.data.map((item) => item.id)).toEqual(['database-orders', 'database-customer-records']);
    expect(result.data[1]).toMatchObject({
      name: '客戶資料庫',
      fieldCount: 6,
      subjectCount: 3,
      recordCount: 7,
      connectedAssistantNames: ['客服助理'],
    });
    expect(Object.isFrozen(result.data)).toBe(true);
  });

  it('creates a database from a template, persists it and connects it for the owner only', () => {
    const storage = createMemoryStorage();
    const repository = createRepository(DEMO_SEED, storage);

    expect(
      repository.createDatabaseFromTemplate('account-smb-admin', { templateId: 'template-satisfaction', name: '  ' }),
    ).toMatchObject({ status: 'validation-failed', message: '請輸入資料庫名稱。' });

    const created = repository.createDatabaseFromTemplate('account-smb-admin', {
      templateId: 'template-satisfaction',
      name: ' 門市滿意度調查 ',
    });
    expect(created.status).toBe('ready');
    if (created.status !== 'ready') return;
    expect(created.data).toMatchObject({
      name: '門市滿意度調查',
      templateName: '滿意度調查',
      fieldCount: 3,
      recordCount: 0,
    });
    expect(created.data.id).toMatch(/^database-created-\d+$/);

    const reloaded = createRepository(DEMO_SEED, storage);
    expect(fieldsOf(reloaded, created.data.id).map((field) => field.label)).toEqual([
      '整體滿意度',
      '喜歡的服務',
      '其他建議',
    ]);
    expect(reloaded.getDatabaseDetail('account-internal-employee', created.data.id).status).toBe(
      'permission-denied',
    );
    const sources = reloaded.listConnectableSources('account-smb-admin');
    expect(sources.status === 'ready' && sources.data.some((source) => source.id === created.data.id)).toBe(true);
  });

  it('refuses to create a database for an account without data-source permission', () => {
    expect(
      createRepository().createDatabaseFromTemplate('account-external-customer', {
        templateId: 'template-blank',
        name: '測試',
      }).status,
    ).toBe('permission-denied');
  });

  it('returns the same generic denial for unknown and other-account databases without leaking names', () => {
    const repository = createRepository();
    const unknown = repository.getDatabaseDetail('account-smb-admin', 'database-missing');
    const foreign = repository.getDatabaseDetail('account-smb-admin', 'database-staff-checkins');

    expect(foreign).toEqual(unknown);
    expect(foreign).toMatchObject({ status: 'permission-denied', reason: 'database' });
    expect(JSON.stringify(foreign)).not.toContain('同仁排班回報');
    expect(repository.getDatabaseTracking('account-smb-admin', 'database-staff-checkins')).toEqual(unknown);
  });

  it('describes the detail with access rules and the assistants connected to it', () => {
    const result = createRepository().getDatabaseDetail('account-smb-admin', 'database-customer-records');

    expect(result.status).toBe('ready');
    if (result.status !== 'ready') return;
    expect(result.data.connectedAssistants.map((assistant) => assistant.name)).toEqual(['客服助理']);
    expect(result.data.access).toEqual({
      owner: { id: 'account-smb-admin', displayName: '安心商行管理者' },
      dataManagers: [{ id: 'account-smb-admin', displayName: '安心商行管理者' }],
      viewerIsDataManager: true,
    });
  });

  it('saves edited fields after normalising them, keeping only the six supported types', () => {
    const storage = createMemoryStorage();
    const repository = createRepository(DEMO_SEED, storage);
    const [first, ...rest] = fieldsOf(repository, 'database-orders');
    const added = { ...createDatabaseField('field-custom-1', 'scale', { label: ' 處理滿意度 ' }), options: ['殘留'] };

    const result = repository.updateDatabaseFields('account-smb-admin', 'database-orders', [
      { ...first, label: '訂單號碼', required: false },
      added,
      ...rest,
    ]);

    expect(result.status).toBe('ready');
    const saved = fieldsOf(createRepository(DEMO_SEED, storage), 'database-orders');
    expect(saved.map((field) => field.label)).toEqual(['訂單號碼', '處理滿意度', '問題類型', '回報日期']);
    expect(saved[0].required).toBe(false);
    expect(saved[1]).toMatchObject({ type: 'scale', options: [], scale: { min: 1, max: 5 } });
  });

  it('rejects empty labels, choice fields with fewer than two options, bad scales and unknown types', () => {
    const repository = createRepository();
    const fields = fieldsOf(repository, 'database-orders');
    const result = repository.updateDatabaseFields('account-smb-admin', 'database-orders', [
      { ...fields[0], label: ' ' },
      { ...fields[1], options: ['配送延遲', ' '] },
      { ...createDatabaseField('field-bad-scale', 'scale', { label: '分數' }), scale: { min: 5, max: 5, minLabel: '', maxLabel: '' } },
      { ...fields[2], type: 'formula' as never },
    ]);

    expect(result.status).toBe('validation-failed');
    if (result.status !== 'validation-failed') return;
    expect(result.errors).toEqual([
      { fieldId: 'field-order-number', message: '請填寫欄位名稱。' },
      { fieldId: 'field-issue-type', message: '單選或多選至少需要 2 個選項。' },
      { fieldId: 'field-bad-scale', message: '量尺的最小值必須小於最大值。' },
      { fieldId: 'field-reported-on', message: '不支援的欄位類型。' },
    ]);
    expect(
      repository.updateDatabaseFields('account-smb-admin', 'database-orders', []),
    ).toMatchObject({ status: 'validation-failed', errors: [{ fieldId: null, message: '表單至少需要一個欄位。' }] });
  });

  it('validates a trial entry against the saved form and previews it without storing a record', () => {
    const repository = createRepository();
    const missing = repository.previewDatabaseEntry('account-smb-admin', 'database-customer-records', {
      'field-monthly-spend': 'abc',
      'field-satisfaction': '9',
    });

    expect(missing.status).toBe('validation-failed');
    if (missing.status === 'validation-failed') {
      expect(missing.errors).toEqual([
        { fieldId: 'field-visit-date', message: '「回訪日期」為必填。' },
        { fieldId: 'field-satisfaction', message: '「整體滿意度」請選擇 1 到 5 之間的分數。' },
        { fieldId: 'field-monthly-spend', message: '「本月消費金額」請輸入數字。' },
        { fieldId: 'field-membership', message: '「會員等級」為必填。' },
      ]);
    }

    const preview = repository.previewDatabaseEntry('account-smb-admin', 'database-customer-records', {
      'field-visit-date': '2026-09-22',
      'field-satisfaction': '4',
      'field-monthly-spend': '1200',
      'field-membership': '銀卡',
      'field-interests': ['保養品', '配件'],
    });
    expect(preview).toMatchObject({
      status: 'ready',
      data: {
        saved: false,
        entries: [
          { fieldId: 'field-visit-date', display: '2026-09-22' },
          { fieldId: 'field-satisfaction', display: '4 / 5' },
          { fieldId: 'field-monthly-spend', display: '1,200 元' },
          { fieldId: 'field-membership', display: '銀卡' },
          { fieldId: 'field-interests', display: '保養品、配件' },
          { fieldId: 'field-note', display: '未填寫' },
        ],
      },
    });
    expect(trackingOf(repository).subjects[0].records).toHaveLength(4);
  });

  it('builds a newest-first timeline per tracked subject from consented records only', () => {
    const tracking = trackingOf(createRepository());

    expect(tracking.subjects.map((subject) => subject.displayName)).toEqual(['王小姐', '林小姐', '陳先生']);
    const wang = tracking.subjects[0];
    expect(wang.records.map((record) => record.dateLabel)).toEqual([
      '2026-09-15',
      '2026-08-15',
      '2026-07-15',
      '2026-06-15',
    ]);
    expect(wang.records[0].entries).toContainEqual({ fieldId: 'field-monthly-spend', label: '本月消費金額', display: '3,200 元' });
    const lin = tracking.subjects[1];
    expect(lin.records.map((record) => record.id)).toEqual(['record-lin-2', 'record-lin-1']);
  });

  it('precomputes current, previous and first comparisons with signed labels and a text summary', () => {
    const wang = trackingOf(createRepository()).subjects[0];

    expect(wang.comparison.status).toBe('available');
    if (wang.comparison.status !== 'available') return;
    const [satisfaction, spend] = wang.comparison.metrics;
    expect(satisfaction).toMatchObject({
      fieldId: 'field-satisfaction',
      first: { display: '3 / 5', dateLabel: '2026-06-15' },
      previous: { display: '3 / 5', dateLabel: '2026-08-15' },
      current: { display: '5 / 5', dateLabel: '2026-09-15' },
      changeFromPrevious: 2,
      changeFromFirst: 2,
      changeFromPreviousLabel: '+2 分',
      changeFromFirstLabel: '+2 分',
      direction: 'up',
      axis: { min: 1, max: 5 },
      summary: '整體滿意度：本次 5 / 5，較上次 +2 分，較首次 +2 分。',
    });
    expect(satisfaction.points.map((point) => point.value)).toEqual([3, 4, 3, 5]);
    expect(spend).toMatchObject({
      changeFromPreviousLabel: '+1,100 元',
      changeFromFirstLabel: '+1,400 元',
      axis: { min: 1800, max: 3200 },
    });

    const lin = trackingOf(createRepository()).subjects[1].comparison;
    expect(lin.status === 'available' && lin.metrics[0].changeFromPreviousLabel).toBe('-1 分');
    expect(lin.status === 'available' && lin.metrics[0].direction).toBe('down');
  });

  it('does not produce a trend conclusion with fewer than two records', () => {
    const chen = trackingOf(createRepository()).subjects[2];

    expect(chen.records).toHaveLength(1);
    expect(chen.comparison).toEqual({
      status: 'insufficient-records',
      recordCount: 1,
      message: '目前只有 1 筆紀錄，累積 2 筆以上才會顯示比較與趨勢。',
    });
  });

  it('only lets designated data managers read structured records', () => {
    const seed = {
      ...DEMO_SEED,
      databaseCollections: {
        ...DEMO_SEED.databaseCollections,
        'database-customer-records': {
          ...DEMO_SEED.databaseCollections['database-customer-records'],
          dataManagerAccountIds: [],
        },
      },
    };
    const repository = createRepository(seed);

    expect(repository.getDatabaseTracking('account-smb-admin', 'database-customer-records')).toMatchObject({
      status: 'permission-denied',
      reason: 'database-records',
    });
    const summaries = repository.listDatabaseSummaries('account-smb-admin');
    expect(summaries.status === 'ready' && summaries.data[1]).toMatchObject({ recordCount: null, subjectCount: null });
    const detail = repository.getDatabaseDetail('account-smb-admin', 'database-customer-records');
    expect(detail.status === 'ready' && detail.data.access.viewerIsDataManager).toBe(false);
  });
});
