import { firstValueFrom } from 'rxjs';
import type { AccountId } from '../domain/account.model';
import {
  DATABASE_FIELD_TYPES,
  createDatabaseField,
  type DatabaseDetailView,
  type DatabaseFieldView,
  type DatabaseTrackingView,
} from '../domain/database.model';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';
import { syncValue } from './sync-value.testing';

function createRepository(
  seed = DEMO_SEED,
  storage = createMemoryStorage(),
  viewer: AccountId | null = 'account-smb-admin',
) {
  return new MockDemoRepository(seed, {
    storage,
    now: () => new Date('2026-09-22T02:00:00.000Z'),
    viewer: () => viewer,
  });
}

function trackingOf(repository: MockDemoRepository, id = 'database-customer-records'): DatabaseTrackingView {
  const result = repository.readDatabaseTracking('account-smb-admin', id);
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

function detailOf(repository: MockDemoRepository, id: string): DatabaseDetailView {
  const result = syncValue(repository.getDatabaseDetail(id));
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

function fieldsOf(repository: MockDemoRepository, id: string): readonly DatabaseFieldView[] {
  return detailOf(repository, id).fields;
}

describe('MockDemoRepository databases', () => {
  it('offers the five collection templates, starting from what to collect', () => {
    const result = syncValue(createRepository().listDatabaseTemplates());

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
    const repository = createRepository(DEMO_SEED, createMemoryStorage(), 'account-internal-employee');
    expect(syncValue(repository.listDatabaseTemplates()).status).toBe('permission-denied');
  });

  it('summarises only the owner’s databases with fields, records and connected assistants', () => {
    const result = syncValue(createRepository().listDatabaseSummaries());

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

  it('creates a database from a template, persists it and connects it for the owner only', async () => {
    const storage = createMemoryStorage();
    const repository = createRepository(DEMO_SEED, storage);

    expect(
      syncValue(repository.createDatabaseFromTemplate({ templateId: 'template-satisfaction', name: '  ' })),
    ).toMatchObject({ status: 'validation-failed', message: '請輸入資料庫名稱。' });

    const created = syncValue(
      repository.createDatabaseFromTemplate({ templateId: 'template-satisfaction', name: ' 門市滿意度調查 ' }),
    );
    expect(created.status).toBe('ready');
    if (created.status !== 'ready') return;
    expect(created.data).toMatchObject({
      name: '門市滿意度調查',
      templateName: '滿意度調查',
      fieldCount: 3,
      recordCount: 0,
      owner: { id: 'account-smb-admin', displayName: '安心商行管理者' },
    });
    expect(created.data.id).toMatch(/^database-created-\d+$/);

    const reloaded = createRepository(DEMO_SEED, storage);
    expect(fieldsOf(reloaded, created.data.id).map((field) => field.label)).toEqual([
      '整體滿意度',
      '喜歡的服務',
      '其他建議',
    ]);
    const asEmployee = createRepository(DEMO_SEED, storage, 'account-internal-employee');
    expect(syncValue(asEmployee.getDatabaseDetail(created.data.id)).status).toBe('permission-denied');
    const sources = await firstValueFrom(reloaded.listConnectableSources());
    expect(sources.status === 'ready' && sources.data.some((source) => source.id === created.data.id)).toBe(true);
  });

  it('refuses to create a database for an account without data-source permission', () => {
    expect(
      syncValue(
        createRepository(DEMO_SEED, createMemoryStorage(), 'account-external-customer').createDatabaseFromTemplate({
          templateId: 'template-blank',
          name: '測試',
        }),
      ).status,
    ).toBe('permission-denied');
  });

  it('returns the same generic denial for unknown and other-account databases without leaking names', () => {
    const repository = createRepository();
    const unknown = syncValue(repository.getDatabaseDetail('database-missing'));
    const foreign = syncValue(repository.getDatabaseDetail('database-staff-checkins'));

    expect(foreign).toEqual(unknown);
    expect(foreign).toMatchObject({ status: 'permission-denied', reason: 'database' });
    expect(JSON.stringify(foreign)).not.toContain('同仁排班回報');
    expect(repository.readDatabaseTracking('account-smb-admin', 'database-staff-checkins')).toEqual(unknown);
  });

  it('describes the detail with access rules and the assistants connected to it', () => {
    const result = syncValue(createRepository().getDatabaseDetail('database-customer-records'));

    expect(result.status).toBe('ready');
    if (result.status !== 'ready') return;
    expect(result.data.connectedAssistants.map((assistant) => assistant.name)).toEqual(['客服助理']);
    // mock 模式沒有「後續版本開放」的功能；擁有者也出現在摘要裡。
    expect(result.data.upcomingFeatures).toEqual([]);
    expect(result.data.summary.owner).toEqual({ id: 'account-smb-admin', displayName: '安心商行管理者' });
    expect(result.data.access).toMatchObject({
      owner: { id: 'account-smb-admin', displayName: '安心商行管理者' },
      dataManagers: [{ id: 'account-smb-admin', displayName: '安心商行管理者' }],
      viewerIsDataManager: true,
      // 指定 ＋ 帳號層級權限都有，才真的看得到；擁有者才改得了指定。
      viewerCanReadRecords: true,
      viewerCanManageAccess: true,
      savedAt: null,
    });
  });

  it('saves edited fields after normalising them, keeping only the six supported types', () => {
    const storage = createMemoryStorage();
    const repository = createRepository(DEMO_SEED, storage);
    const [first, ...rest] = fieldsOf(repository, 'database-orders');
    const added = { ...createDatabaseField('field-custom-1', 'scale', { label: ' 處理滿意度 ' }), options: ['殘留'] };

    const result = syncValue(
      repository.updateDatabaseFields('database-orders', [{ ...first, label: '訂單號碼', required: false }, added, ...rest], 1),
    );

    expect(result).toMatchObject({ status: 'ready', data: { formVersion: 2 } });
    const reopened = createRepository(DEMO_SEED, storage);
    const saved = fieldsOf(reopened, 'database-orders');
    expect(saved.map((field) => field.label)).toEqual(['訂單號碼', '處理滿意度', '問題類型', '回報日期']);
    expect(saved[0].required).toBe(false);
    expect(saved[1]).toMatchObject({ type: 'scale', options: [], scale: { min: 1, max: 5 } });
    expect(detailOf(reopened, 'database-orders').formVersion).toBe(2);
  });

  it('keeps field ids when fields are renamed and reordered', () => {
    const repository = createRepository();
    const [first, second, ...rest] = fieldsOf(repository, 'database-orders');

    syncValue(
      repository.updateDatabaseFields('database-orders', [{ ...second, label: '類型（改名）' }, first, ...rest], 1),
    );

    const saved = fieldsOf(repository, 'database-orders');
    expect(saved.map((field) => field.id)).toEqual([second.id, first.id, ...rest.map((field) => field.id)]);
  });

  it('refuses a save from a stale form version without writing, and adds no version for an unchanged form', () => {
    const repository = createRepository();
    const fields = fieldsOf(repository, 'database-orders');
    const renamed = [{ ...fields[0], label: '訂單編號 v2' }, ...fields.slice(1)];

    expect(syncValue(repository.updateDatabaseFields('database-orders', renamed, 1)).status).toBe('ready');
    const stale = syncValue(
      repository.updateDatabaseFields('database-orders', [{ ...fields[0], label: '過期的修改' }, ...fields.slice(1)], 1),
    );

    expect(stale).toMatchObject({ status: 'conflict', message: expect.stringContaining('重新載入') });
    expect(fieldsOf(repository, 'database-orders')[0].label).toBe('訂單編號 v2');
    // 內容與目前版本相同：版本號不變。
    expect(syncValue(repository.updateDatabaseFields('database-orders', renamed, 2))).toMatchObject({
      status: 'ready',
      data: { formVersion: 2 },
    });
    expect(detailOf(repository, 'database-orders').formVersion).toBe(2);
  });

  it('does not let a non-owner save or try a form, with the same denial as a missing database', () => {
    const repository = createRepository(DEMO_SEED, createMemoryStorage(), 'account-internal-employee');
    const field = createDatabaseField('field-x', 'text', { label: '名' });
    const unknown = syncValue(repository.updateDatabaseFields('database-missing', [field], 1));

    expect(syncValue(repository.updateDatabaseFields('database-orders', [field], 1))).toEqual(unknown);
    expect(unknown).toMatchObject({ status: 'permission-denied', reason: 'database' });
    expect(syncValue(repository.previewDatabaseEntry('database-orders', {}))).toEqual(unknown);
  });

  it('rejects empty labels, choice fields with fewer than two options, bad scales and unknown types', () => {
    const repository = createRepository();
    const fields = fieldsOf(repository, 'database-orders');
    const result = syncValue(
      repository.updateDatabaseFields(
        'database-orders',
        [
          { ...fields[0], label: ' ' },
          { ...fields[1], options: ['配送延遲', ' '] },
          { ...createDatabaseField('field-bad-scale', 'scale', { label: '分數' }), scale: { min: 5, max: 5, minLabel: '', maxLabel: '' } },
          { ...fields[2], type: 'formula' as never },
        ],
        1,
      ),
    );

    expect(result.status).toBe('validation-failed');
    if (result.status !== 'validation-failed') return;
    expect(result.errors).toEqual([
      { fieldId: 'field-order-number', message: '請填寫欄位名稱。' },
      { fieldId: 'field-issue-type', message: '單選或多選至少需要 2 個選項。' },
      { fieldId: 'field-bad-scale', message: '量尺的最小值必須小於最大值。' },
      { fieldId: 'field-reported-on', message: '不支援的欄位類型。' },
    ]);
    expect(result.message).toBe('請填寫欄位名稱。');
    expect(syncValue(repository.updateDatabaseFields('database-orders', [], 1))).toMatchObject({
      status: 'validation-failed',
      errors: [{ fieldId: null, message: '表單至少需要一個欄位。' }],
    });
    expect(detailOf(repository, 'database-orders').formVersion).toBe(1);
  });

  it('applies the same limits as the API: repeated options, over-long names, units and scale labels', () => {
    const repository = createRepository();
    const choice = createDatabaseField('field-a', 'single-choice', { label: '選' });
    const result = syncValue(
      repository.updateDatabaseFields(
        'database-orders',
        [
          { ...choice, options: ['甲', '甲'] },
          createDatabaseField('field-b', 'text', { label: '長'.repeat(101) }),
          { ...createDatabaseField('field-c', 'number', { label: '金額' }), unit: '元'.repeat(21) },
        ],
        1,
      ),
    );

    expect(result).toMatchObject({
      status: 'validation-failed',
      errors: [
        { fieldId: 'field-a', message: '選項不可重複。' },
        { fieldId: 'field-b', message: '欄位名稱請在 100 個字以內。' },
        { fieldId: 'field-c', message: '單位請在 20 個字以內。' },
      ],
    });
  });

  it('validates a trial entry against the saved form and previews it without storing a record', () => {
    const repository = createRepository();
    const missing = syncValue(
      repository.previewDatabaseEntry('database-customer-records', {
        'field-monthly-spend': 'abc',
        'field-satisfaction': '9',
      }),
    );

    expect(missing.status).toBe('validation-failed');
    if (missing.status === 'validation-failed') {
      expect(missing.errors).toEqual([
        { fieldId: 'field-visit-date', message: '「回訪日期」為必填。' },
        { fieldId: 'field-satisfaction', message: '「整體滿意度」請選擇 1 到 5 之間的分數。' },
        { fieldId: 'field-monthly-spend', message: '「本月消費金額」請輸入數字。' },
        { fieldId: 'field-membership', message: '「會員等級」為必填。' },
      ]);
      expect(missing.message).toBe('「回訪日期」為必填。');
    }

    const preview = syncValue(
      repository.previewDatabaseEntry('database-customer-records', {
        'field-visit-date': '2026-09-22',
        'field-satisfaction': '4',
        'field-monthly-spend': '1200',
        'field-membership': '銀卡',
        'field-interests': ['保養品', '配件'],
      }),
    );
    expect(preview).toMatchObject({
      status: 'ready',
      data: {
        saved: false,
        formVersion: 1,
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

  it('rejects a date that does not exist, like the API', () => {
    const result = syncValue(
      createRepository().previewDatabaseEntry('database-customer-records', { 'field-visit-date': '2026-13-45' }),
    );

    expect(result).toMatchObject({
      status: 'validation-failed',
      errors: expect.arrayContaining([{ fieldId: 'field-visit-date', message: '「回訪日期」請輸入日期。' }]),
    });
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

  describe('定期報表 (#150)', () => {
    function reportsOf(repository: MockDemoRepository, id = 'database-customer-records') {
      const result = syncValue(repository.listDatabaseReports(id));
      if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
      return result.data;
    }

    function reportOf(repository: MockDemoRepository, reportId: string) {
      const result = syncValue(repository.getDatabaseReport('database-customer-records', reportId));
      if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
      return result.data;
    }

    it('lists the assistant that has 定期回報 on with its next report day, and its last three completed months', () => {
      const list = reportsOf(createRepository());

      // 今天是 2026-09-22：這個月的報表在 10/01（這一期結束後）產生。
      expect(list.schedules).toEqual([
        {
          assistantId: 'assistant-customer-service',
          assistantName: '客服助理',
          frequency: 'monthly',
          nextPeriodFrom: '2026-09-01',
          nextReportDate: '2026-10-01',
        },
      ]);
      expect(list.reports.map((report) => [report.periodFrom, report.periodTo, report.dataState])).toEqual([
        ['2026-08-01', '2026-08-31', 'sufficient'],
        ['2026-07-01', '2026-07-31', 'sufficient'],
        ['2026-06-01', '2026-06-30', 'insufficient-records'],
      ]);
    });

    it('keeps the statistics apart from a labelled AI summary that only repeats computed numbers', () => {
      const repository = createRepository();
      const [august] = reportsOf(repository).reports;
      const report = reportOf(repository, august.id);

      expect(report.statistics?.recordCount).toBe(2);
      expect(report.statistics?.previousRecordCount).toBe(1);
      const spend = report.statistics?.sums.find((sum) => sum.fieldId === 'field-monthly-spend');
      expect([spend?.display, spend?.previousDisplay, spend?.changeLabel]).toEqual(['3,600 元', '2,400 元', '+1,200 元']);
      expect(report.aiSummary).toMatchObject({ label: 'AI 摘要', status: 'ready' });
      expect(report.aiSummary.text).toBe('整體來看，紀錄筆數：本期 2 筆，前一期 1 筆，變化 +1 筆。');
    });

    it('saves too few records as 紀錄不足: statistics kept, no change message trend or AI summary', () => {
      const repository = createRepository();
      const june = reportsOf(repository).reports.find((report) => report.periodFrom === '2026-06-01');
      if (june === undefined) throw new Error('no june report');
      const report = reportOf(repository, june.id);

      expect(report.report.dataMessage).toBe('紀錄不足：這一期有 1 筆、前一期有 0 筆紀錄，兩期都有紀錄才會顯示變化、趨勢與 AI 摘要。');
      expect(report.statistics?.recordCount).toBe(1);
      expect(report.aiSummary).toMatchObject({ status: 'not-requested', text: null });
      expect(june.summaryStatus).toBe('not-requested');
    });

    it('drops the schedule when the assistant turns 定期回報 off, keeping the reports already made', async () => {
      const repository = createRepository();
      const first = reportsOf(repository);
      await firstValueFrom(repository.updateAssistantSettings('assistant-customer-service', { rules: { periodicReport: 'off' } }));

      const list = reportsOf(repository);
      expect(list.schedules).toEqual([]);
      expect(list.reports.map((report) => report.id)).toEqual(first.reports.map((report) => report.id));
    });

    it('turns 定期回報 off when the write target is cleared, and refuses it without a target', async () => {
      const repository = createRepository();
      const cleared = await firstValueFrom(
        repository.updateAssistantSettings('assistant-customer-service', { rules: { dataWriteDatabaseId: null, dataWritePurpose: '' } }),
      );
      expect(cleared.status === 'ready' && cleared.data.rules.periodicReport).toBe('off');

      const refused = await firstValueFrom(
        repository.updateAssistantSettings('assistant-customer-service', { rules: { periodicReport: 'weekly' } }),
      );
      expect(refused).toMatchObject({
        status: 'validation-failed',
        errors: [{ field: 'periodicReport', message: '請先指定要寫入的資料庫，才能設定定期回報。' }],
      });
    });

    describe('自動停用 (#179)', () => {
      const AUTO_DISABLED = {
        disabledAt: '2026-09-15T00:00:05.000Z',
        reason: 'not-connected',
        skippedPeriods: 3,
        message: '已自動停用：連續 3 期沒有產生報表，最近一期是因為助理已不再連接這個數據庫。重新連接後可以重新啟用。',
      } as const;

      /** mock 沒有排程工作：先存一次設定，再把「已自動停用」寫進存起來的設定，模擬連續略過 3 期之後。 */
      async function disabledRepository(sources?: readonly unknown[]) {
        const base = createMemoryStorage();
        const keys = new Set<string>();
        const storage = { ...base, setItem: (key: string, value: string) => { keys.add(key); base.setItem(key, value); } };
        const repository = createRepository(DEMO_SEED, storage);
        await firstValueFrom(repository.updateAssistantSettings('assistant-customer-service', { rules: { showCitations: true } }));
        const key = [...keys].find((candidate) => candidate.endsWith('assistant-settings:assistant-customer-service'));
        if (key === undefined) throw new Error('settings were not saved');
        const stored = JSON.parse(base.getItem(key) ?? '{}');
        base.setItem(key, JSON.stringify({
          ...stored,
          ...(sources !== undefined ? { databaseIds: sources } : {}),
          periodicReportAutoDisabled: AUTO_DISABLED,
        }));
        return repository;
      }

      async function settingsOf(repository: MockDemoRepository) {
        const result = await firstValueFrom(repository.getAssistantSettings('assistant-customer-service'));
        if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
        return result.data;
      }

      it('shows the reason with the frequency kept, and makes no more reports or schedule', async () => {
        const repository = await disabledRepository();
        const settings = await settingsOf(repository);

        expect(settings.rules.periodicReport).toBe('monthly');
        expect(settings.periodicReportAutoDisabled).toEqual(AUTO_DISABLED);
        const list = reportsOf(repository);
        expect(list.schedules).toEqual([]);
        expect(list.reports).toEqual([]);
      });

      it('stays disabled when only another rule changes', async () => {
        const repository = await disabledRepository();
        const saved = await firstValueFrom(
          repository.updateAssistantSettings('assistant-customer-service', { rules: { dataWritePurpose: '改過的目的。' } }),
        );

        expect(saved.status === 'ready' && saved.data.periodicReportAutoDisabled).toEqual(AUTO_DISABLED);
      });

      it('re-enables by sending the frequency again, resuming the schedule', async () => {
        const repository = await disabledRepository();
        const saved = await firstValueFrom(
          repository.updateAssistantSettings('assistant-customer-service', { rules: { periodicReport: 'monthly' } }),
        );

        expect(saved.status === 'ready' && saved.data.periodicReportAutoDisabled).toBeNull();
        expect((await settingsOf(repository)).periodicReportAutoDisabled).toBeNull();
        expect(reportsOf(repository).schedules.map((schedule) => schedule.assistantId)).toEqual(['assistant-customer-service']);
      });

      it('refuses to re-enable while the write target is no longer connected, changing nothing', async () => {
        const repository = await disabledRepository([]);
        const refused = await firstValueFrom(
          repository.updateAssistantSettings('assistant-customer-service', { rules: { periodicReport: 'monthly' } }),
        );

        expect(refused).toEqual({
          status: 'validation-failed',
          errors: [{ field: 'periodicReport', message: '只能為已連接到這個助理、而且你仍可使用的資料庫設定定期回報。' }],
          message: '只能為已連接到這個助理、而且你仍可使用的資料庫設定定期回報。',
        });
        expect((await settingsOf(repository)).periodicReportAutoDisabled).toEqual(AUTO_DISABLED);
      });
    });

    it('has no schedule or report on a database no assistant reports into', () => {
      const list = reportsOf(createRepository(), 'database-orders');
      expect(list.schedules).toEqual([]);
      expect(list.reports).toEqual([]);
    });

    it('treats a report id that is not this database\'s as database-report, and hides everything from non-readers', () => {
      const repository = createRepository();
      const [august] = reportsOf(repository).reports;

      expect(syncValue(repository.getDatabaseReport('database-customer-records', 'report-nope'))).toMatchObject({
        status: 'permission-denied',
        reason: 'database-report',
      });
      // 別的資料庫看不到這份報表，而且與一個不存在的 id 一樣。
      expect(syncValue(repository.getDatabaseReport('database-orders', august.id))).toMatchObject({
        status: 'permission-denied',
        reason: 'database-report',
      });

      // 外部客戶看不到資料庫，也沒有任何報表內容。
      const customer = createRepository(DEMO_SEED, createMemoryStorage(), 'account-external-customer');
      expect(syncValue(customer.listDatabaseReports('database-customer-records'))).toMatchObject({
        status: 'permission-denied',
        reason: 'database',
      });
      expect(syncValue(customer.getDatabaseReport('database-customer-records', august.id))).toEqual(
        syncValue(customer.getDatabaseReport('database-customer-records', 'report-nope')),
      );
    });

    it('retries only a failed or discarded summary and leaves the statistics alone', () => {
      const repository = createRepository();
      const [august] = reportsOf(repository).reports;
      const before = reportOf(repository, august.id);

      // 已完成的摘要原樣回傳。
      expect(syncValue(repository.retryDatabaseReportSummary('database-customer-records', august.id))).toEqual({
        status: 'ready',
        data: before,
      });
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

    expect(repository.readDatabaseTracking('account-smb-admin', 'database-customer-records')).toMatchObject({
      status: 'permission-denied',
      reason: 'database-records',
    });
    const summaries = syncValue(repository.listDatabaseSummaries());
    expect(summaries.status === 'ready' && summaries.data[1]).toMatchObject({ recordCount: null, subjectCount: null });
    const detail = syncValue(repository.getDatabaseDetail('database-customer-records'));
    expect(detail.status === 'ready' && detail.data.access.viewerIsDataManager).toBe(false);
  });
});
