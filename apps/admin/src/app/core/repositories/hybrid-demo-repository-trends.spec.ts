import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { DEMO_SEED } from './demo-seed';
import {
  API_UPCOMING_DATABASE_FEATURES,
  apiDatabasePeriodSummaryPath,
  apiDatabaseTrackingPath,
  HybridDemoRepository,
} from './hybrid-demo-repository';
import { createMemoryStorage } from './memory-storage';

const DATABASE_ID = '01a100b5-b3f8-774f-a25a-34b757741983';
const SUBJECT_ID = '01a100b5-a974-70ed-bbf8-4ffe7a7a5475';
const OTHER_SUBJECT_ID = '01a100b5-a99b-7e67-9e48-c2e393923e9c';

/*
 * 以下回應都是 2026-10-03 從 API 整合測試主機實際取得（「定期回報」模板的數據庫，外部客戶送出 5 件與
 * 8 件、另一位外部客戶送出 3 件；資料管理者讀時間軸與期間統計），原樣保留成字串再 `JSON.parse`，不是
 * 依產生的型別手寫（issue #147）。注意不適用的 `message`／`summary` 是 `null`（後端一律送出，不省略）。
 */
const REAL_TRACKING_JSON = `{"databaseId":"01a100b5-b3f8-774f-a25a-34b757741983","subjects":[{"subject":{"id":"01a100b5-a99b-7e67-9e48-c2e393923e9c","displayName":"第二位外部客戶"},"records":[{"id":"01a100b5-b517-748f-94a6-42cbdb01bdda","receiptNumber":"R-20261003-CBDB01BDDA","submittedAt":"2026-10-03T07:41:10.807429+00:00","source":"form-link","submitter":{"id":"01a100b5-a99b-7e67-9e48-c2e393923e9c","displayName":"第二位外部客戶"},"formVersionNumber":1,"entries":[{"fieldId":"field-report-date","label":"回報日期","type":"date","display":"2026-10-03"},{"fieldId":"field-completed-count","label":"本期完成數量","type":"number","display":"3 件"},{"fieldId":"field-issue","label":"遇到的問題","type":"text","display":"無"}]}],"withdrawals":[],"comparison":{"status":"insufficient-records","recordCount":1,"message":"目前只有 1 筆紀錄，累積 2 筆以上才會顯示比較與趨勢。","summary":null,"metrics":[]}},{"subject":{"id":"01a100b5-a974-70ed-bbf8-4ffe7a7a5475","displayName":"安心商行外部客戶"},"records":[{"id":"01a100b5-b4fd-76ee-9186-44337147c7ef","receiptNumber":"R-20261003-337147C7EF","submittedAt":"2026-10-03T07:41:10.781687+00:00","source":"form-link","submitter":{"id":"01a100b5-a974-70ed-bbf8-4ffe7a7a5475","displayName":"安心商行外部客戶"},"formVersionNumber":1,"entries":[{"fieldId":"field-report-date","label":"回報日期","type":"date","display":"2026-10-03"},{"fieldId":"field-completed-count","label":"本期完成數量","type":"number","display":"8 件"},{"fieldId":"field-issue","label":"遇到的問題","type":"text","display":"無"}]},{"id":"01a100b5-b45f-76ce-911c-d44b344696f1","receiptNumber":"R-20261003-4B344696F1","submittedAt":"2026-10-03T07:41:10.623995+00:00","source":"form-link","submitter":{"id":"01a100b5-a974-70ed-bbf8-4ffe7a7a5475","displayName":"安心商行外部客戶"},"formVersionNumber":1,"entries":[{"fieldId":"field-report-date","label":"回報日期","type":"date","display":"2026-10-03"},{"fieldId":"field-completed-count","label":"本期完成數量","type":"number","display":"5 件"},{"fieldId":"field-issue","label":"遇到的問題","type":"text","display":"無"}]}],"withdrawals":[],"comparison":{"status":"available","recordCount":2,"message":null,"summary":"比較 2 筆已同意提交的紀錄（2026-10-03 至 2026-10-03）。","metrics":[{"fieldId":"field-completed-count","label":"本期完成數量","unit":"件","first":{"recordId":"01a100b5-b45f-76ce-911c-d44b344696f1","date":"2026-10-03","value":5,"display":"5 件"},"previous":{"recordId":"01a100b5-b45f-76ce-911c-d44b344696f1","date":"2026-10-03","value":5,"display":"5 件"},"current":{"recordId":"01a100b5-b4fd-76ee-9186-44337147c7ef","date":"2026-10-03","value":8,"display":"8 件"},"changeFromPrevious":3,"changeFromFirst":3,"changeFromPreviousLabel":"+3 件","changeFromFirstLabel":"+3 件","direction":"up","points":[{"recordId":"01a100b5-b45f-76ce-911c-d44b344696f1","date":"2026-10-03","value":5,"display":"5 件"},{"recordId":"01a100b5-b4fd-76ee-9186-44337147c7ef","date":"2026-10-03","value":8,"display":"8 件"}],"axis":{"min":5,"max":8},"summary":"本期完成數量：本次 8 件，較上次 +3 件，較首次 +3 件。"}]}}]}`;
const REAL_SUMMARY_JSON = `{"period":{"period":"last-30-days","from":"2026-09-04","to":"2026-10-03","label":"近 30 天（2026-09-04 至 2026-10-03）"},"previousPeriod":{"period":null,"from":"2026-08-05","to":"2026-09-03","label":"2026-08-05 至 2026-09-03"},"subjectId":"01a100b5-a974-70ed-bbf8-4ffe7a7a5475","recordCount":2,"previousRecordCount":0,"recordCountChange":2,"recordCountChangeLabel":"+2 筆","sums":[{"fieldId":"field-completed-count","label":"本期完成數量","unit":"件","sum":13,"display":"13 件","recordCount":2,"previousSum":0,"previousDisplay":"0 件","previousRecordCount":0,"change":13,"changeLabel":"+13 件"}]}`;
const REAL_SUMMARY_ALL_JSON = `{"period":{"period":"last-month","from":"2026-09-01","to":"2026-09-30","label":"上月（2026-09-01 至 2026-09-30）"},"previousPeriod":{"period":null,"from":"2026-08-01","to":"2026-08-31","label":"2026-08-01 至 2026-08-31"},"subjectId":null,"recordCount":0,"previousRecordCount":0,"recordCountChange":0,"recordCountChangeLabel":"持平","sums":[{"fieldId":"field-completed-count","label":"本期完成數量","unit":"件","sum":0,"display":"0 件","recordCount":0,"previousSum":0,"previousDisplay":"0 件","previousRecordCount":0,"change":0,"changeLabel":"持平"}]}`;
const REAL_SUMMARY_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"message":"不支援的統計期間。","errors":{"period":["不支援的統計期間。"]}}`;
const REAL_SUMMARY_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"database","message":"你沒有這個資料庫的存取權限，或它已不存在。"}`;

function setUp() {
  TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
  const base = createMemoryStorage();
  const written: string[] = [];
  const storage = {
    ...base,
    setItem: (key: string, value: string) => {
      written.push(key);
      base.setItem(key, value);
    },
  };
  const repository = new HybridDemoRepository(
    DEMO_SEED,
    { storage, viewer: () => 'account-smb-admin' },
    {
      http: TestBed.inject(HttpClient),
      viewerPermissions: () => ({
        accountId: '0199a3c0-0000-7000-8000-0000000000a1',
        demoAccountId: 'account-smb-admin',
        permissions: ['read-consented-submissions'],
        organizationId: '0199a3c0-0000-7000-8000-0000000000aa',
      }),
    },
  );
  return { repository, written, controller: TestBed.inject(HttpTestingController) };
}

describe('HybridDemoRepository trends and period statistics (issue #147)', () => {
  it('no longer lists trends as upcoming: only the periodic report (#150) and assistant connections (#148) are', () => {
    expect(API_UPCOMING_DATABASE_FEATURES).toEqual(['periodic-reports', 'assistant-connections']);
  });

  it('maps the server-computed comparison as is: available with metrics, and insufficient without any', async () => {
    const { repository, controller, written } = setUp();
    const result = firstValueFrom(repository.getDatabaseTracking(DATABASE_ID));
    controller.expectOne({ method: 'GET', url: apiDatabaseTrackingPath(DATABASE_ID) }).flush(JSON.parse(REAL_TRACKING_JSON));

    const outcome = await result;
    expect(outcome.status).toBe('ready');
    if (outcome.status !== 'ready') return;
    const bySubject = new Map(outcome.data.subjects.map((subject) => [subject.id, subject.comparison]));

    expect(bySubject.get(`subject-${OTHER_SUBJECT_ID}`)).toEqual({
      status: 'insufficient-records',
      recordCount: 1,
      message: '目前只有 1 筆紀錄，累積 2 筆以上才會顯示比較與趨勢。',
    });
    const comparison = bySubject.get(`subject-${SUBJECT_ID}`);
    expect(comparison?.status).toBe('available');
    if (comparison?.status !== 'available') return;
    expect(comparison.recordCount).toBe(2);
    expect(comparison.summary).toBe('比較 2 筆已同意提交的紀錄（2026-10-03 至 2026-10-03）。');
    const [metric] = comparison.metrics;
    expect(comparison.metrics).toHaveLength(1);
    expect(metric).toMatchObject({
      fieldId: 'field-completed-count',
      label: '本期完成數量',
      changeFromPrevious: 3,
      changeFromPreviousLabel: '+3 件',
      changeFromFirstLabel: '+3 件',
      direction: 'up',
      axis: { min: 5, max: 8 },
      summary: '本期完成數量：本次 8 件，較上次 +3 件，較首次 +3 件。',
    });
    expect(metric.current).toEqual({
      recordId: 'record-01a100b5-b4fd-76ee-9186-44337147c7ef',
      dateLabel: '2026-10-03',
      value: 8,
      display: '8 件',
    });
    expect(metric.points.map((point) => point.value)).toEqual([5, 8]);
    // 比較來自伺服器：沒有任何一筆被寫進 mock 的儲存。
    expect(written).toEqual([]);
  });

  it('keeps the periodic reports empty: they are #150', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.getDatabaseTracking(DATABASE_ID));
    controller.expectOne({ method: 'GET', url: apiDatabaseTrackingPath(DATABASE_ID) }).flush(JSON.parse(REAL_TRACKING_JSON));

    const outcome = await result;
    expect(outcome.status === 'ready' && outcome.data.periodicReports).toEqual([]);
  });

  it('asks the period-summary query with only the defined parameters and maps the real answer', async () => {
    const { repository, controller, written } = setUp();
    const result = firstValueFrom(
      repository.getDatabasePeriodSummary(DATABASE_ID, { period: 'last-30-days', subjectId: `subject-${SUBJECT_ID}` }),
    );
    const request = controller.expectOne({
      method: 'GET',
      url: apiDatabasePeriodSummaryPath(DATABASE_ID, 'last-30-days', `subject-${SUBJECT_ID}`),
    });
    expect(request.request.url).toBe(
      `/api/v1/databases/${DATABASE_ID}/queries/period-summary?period=last-30-days&subjectId=${SUBJECT_ID}`,
    );
    request.flush(JSON.parse(REAL_SUMMARY_JSON));

    const outcome = await result;
    expect(outcome.status).toBe('ready');
    if (outcome.status !== 'ready') return;
    expect(outcome.data).toEqual({
      period: { name: 'last-30-days', from: '2026-09-04', to: '2026-10-03', label: '近 30 天（2026-09-04 至 2026-10-03）' },
      previousPeriod: { name: null, from: '2026-08-05', to: '2026-09-03', label: '2026-08-05 至 2026-09-03' },
      subjectId: `subject-${SUBJECT_ID}`,
      recordCount: 2,
      previousRecordCount: 0,
      recordCountChange: 2,
      recordCountChangeLabel: '+2 筆',
      sums: [
        {
          fieldId: 'field-completed-count',
          label: '本期完成數量',
          unit: '件',
          sum: 13,
          display: '13 件',
          recordCount: 2,
          previousSum: 0,
          previousDisplay: '0 件',
          previousRecordCount: 0,
          change: 13,
          changeLabel: '+13 件',
        },
      ],
    });
    expect(written).toEqual([]);
  });

  it('asks the whole database when no subject is given, and zero records are zeros, not an error', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.getDatabasePeriodSummary(DATABASE_ID, { period: 'last-month', subjectId: null }));
    controller
      .expectOne({ method: 'GET', url: `/api/v1/databases/${DATABASE_ID}/queries/period-summary?period=last-month` })
      .flush(JSON.parse(REAL_SUMMARY_ALL_JSON));

    const outcome = await result;
    expect(outcome.status).toBe('ready');
    if (outcome.status !== 'ready') return;
    expect(outcome.data.subjectId).toBeNull();
    expect(outcome.data.recordCount).toBe(0);
    expect(outcome.data.recordCountChangeLabel).toBe('持平');
    expect(outcome.data.sums[0]).toMatchObject({ sum: 0, display: '0 件', recordCount: 0, changeLabel: '持平' });
  });

  it('maps a refusal to permission-denied for the database, and lets a 422 or a 5xx surface as an error', async () => {
    const { repository, controller } = setUp();
    const denied = firstValueFrom(repository.getDatabasePeriodSummary(DATABASE_ID, { period: 'last-month', subjectId: null }));
    controller
      .expectOne({ method: 'GET', url: apiDatabasePeriodSummaryPath(DATABASE_ID, 'last-month', null) })
      .flush(JSON.parse(REAL_SUMMARY_403_JSON), { status: 403, statusText: 'Forbidden' });
    expect(await denied).toMatchObject({ status: 'permission-denied', reason: 'database' });

    const invalid = firstValueFrom(repository.getDatabasePeriodSummary(DATABASE_ID, { period: 'last-month', subjectId: null }));
    controller
      .expectOne({ method: 'GET', url: apiDatabasePeriodSummaryPath(DATABASE_ID, 'last-month', null) })
      .flush(JSON.parse(REAL_SUMMARY_422_JSON), { status: 422, statusText: 'Unprocessable Content' });
    await expect(invalid).rejects.toBeTruthy();

    const down = firstValueFrom(repository.getDatabasePeriodSummary(DATABASE_ID, { period: 'last-month', subjectId: null }));
    controller
      .expectOne({ method: 'GET', url: apiDatabasePeriodSummaryPath(DATABASE_ID, 'last-month', null) })
      .flush(null, { status: 503, statusText: 'Service Unavailable' });
    await expect(down).rejects.toBeTruthy();
  });
});
