import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { DEMO_SEED } from './demo-seed';
import {
  apiAssistantSettingsPath,
  apiDatabaseReportPath,
  apiDatabaseReportsPath,
  apiDatabaseReportSummaryPath,
  HybridDemoRepository,
} from './hybrid-demo-repository';
import { createMemoryStorage } from './memory-storage';

const DATABASE_ID = '01a100ec-c145-7811-98f9-ebe88684b521';
const REPORT_ID = '01a100ec-c7ed-70a1-a1b9-05647d7d3f28';

/*
 * 以下回應都是 2026-10-03 從 API 整合測試主機實際取得（「定期回報」模板的數據庫，一位助理設定每週報表；
 * 一位外部客戶送出 5 筆，其中本週 3 筆、前一週 2 筆，資料管理者讀報表），原樣保留成字串再 `JSON.parse`，
 * 不是依產生的型別手寫（issue #150）。注意不適用的欄位是 `null`（`skipReason`、`dataMessage`、
 * `text`、`note`），前端要原樣接住。
 */
const REAL_LIST_JSON = `{"databaseId":"01a100ec-c145-7811-98f9-ebe88684b521","schedules":[{"assistantId":"01a100ec-c16e-7b08-9c72-8ce2b9d42dff","assistantName":"客服小幫手","frequency":"weekly","nextPeriodFrom":"2026-10-05","nextReportDate":"2026-10-12"}],"reports":[{"id":"01a100ec-c7ed-70a1-a1b9-05647d7d3f28","assistantId":"01a100ec-c16e-7b08-9c72-8ce2b9d42dff","assistantName":"客服小幫手","frequency":"weekly","periodFrom":"2026-09-28","periodTo":"2026-10-04","periodLabel":"2026-09-28 至 2026-10-04","status":"generated","skipReason":null,"skipMessage":null,"dataState":"sufficient","dataMessage":null,"generatedAt":"2026-10-03T08:41:20.059638+00:00","summaryStatus":"discarded"}]}`;
const REAL_DISCARDED_JSON = `{"report":{"id":"01a100ec-c7ed-70a1-a1b9-05647d7d3f28","assistantId":"01a100ec-c16e-7b08-9c72-8ce2b9d42dff","assistantName":"客服小幫手","frequency":"weekly","periodFrom":"2026-09-28","periodTo":"2026-10-04","periodLabel":"2026-09-28 至 2026-10-04","status":"generated","skipReason":null,"skipMessage":null,"dataState":"sufficient","dataMessage":null,"generatedAt":"2026-10-03T08:41:20.059638+00:00","summaryStatus":"discarded"},"statistics":{"period":{"period":null,"from":"2026-09-28","to":"2026-10-04","label":"2026-09-28 至 2026-10-04"},"previousPeriod":{"period":null,"from":"2026-09-21","to":"2026-09-27","label":"2026-09-21 至 2026-09-27"},"subjectId":null,"recordCount":3,"previousRecordCount":2,"recordCountChange":1,"recordCountChangeLabel":"+1 筆","sums":[{"fieldId":"field-completed-count","label":"本期完成數量","unit":"件","sum":15,"display":"15 件","recordCount":3,"previousSum":10,"previousDisplay":"10 件","previousRecordCount":2,"change":5,"changeLabel":"+5 件"}]},"aiSummary":{"label":"AI 摘要","status":"discarded","text":null,"note":"AI 摘要含有統計結果裡沒有的數字，已捨棄、沒有顯示。統計與圖表不受影響，可以重試。","updatedAt":"2026-10-03T08:41:20.192969+00:00","disclaimer":"由 AI 根據上方已算好的統計數字撰寫，僅供參考；一切數字以統計為準。"}}`;
const REAL_PENDING_JSON = `{"report":{"id":"01a100ec-c7ed-70a1-a1b9-05647d7d3f28","assistantId":"01a100ec-c16e-7b08-9c72-8ce2b9d42dff","assistantName":"客服小幫手","frequency":"weekly","periodFrom":"2026-09-28","periodTo":"2026-10-04","periodLabel":"2026-09-28 至 2026-10-04","status":"generated","skipReason":null,"skipMessage":null,"dataState":"sufficient","dataMessage":null,"generatedAt":"2026-10-03T08:41:20.059638+00:00","summaryStatus":"pending"},"statistics":{"period":{"period":null,"from":"2026-09-28","to":"2026-10-04","label":"2026-09-28 至 2026-10-04"},"previousPeriod":{"period":null,"from":"2026-09-21","to":"2026-09-27","label":"2026-09-21 至 2026-09-27"},"subjectId":null,"recordCount":3,"previousRecordCount":2,"recordCountChange":1,"recordCountChangeLabel":"+1 筆","sums":[{"fieldId":"field-completed-count","label":"本期完成數量","unit":"件","sum":15,"display":"15 件","recordCount":3,"previousSum":10,"previousDisplay":"10 件","previousRecordCount":2,"change":5,"changeLabel":"+5 件"}]},"aiSummary":{"label":"AI 摘要","status":"pending","text":null,"note":null,"updatedAt":"2026-10-03T08:41:20.256371+00:00","disclaimer":"由 AI 根據上方已算好的統計數字撰寫，僅供參考；一切數字以統計為準。"}}`;
const REAL_READY_JSON = `{"report":{"id":"01a100ec-c7ed-70a1-a1b9-05647d7d3f28","assistantId":"01a100ec-c16e-7b08-9c72-8ce2b9d42dff","assistantName":"客服小幫手","frequency":"weekly","periodFrom":"2026-09-28","periodTo":"2026-10-04","periodLabel":"2026-09-28 至 2026-10-04","status":"generated","skipReason":null,"skipMessage":null,"dataState":"sufficient","dataMessage":null,"generatedAt":"2026-10-03T08:41:20.059638+00:00","summaryStatus":"ready"},"statistics":{"period":{"period":null,"from":"2026-09-28","to":"2026-10-04","label":"2026-09-28 至 2026-10-04"},"previousPeriod":{"period":null,"from":"2026-09-21","to":"2026-09-27","label":"2026-09-21 至 2026-09-27"},"subjectId":null,"recordCount":3,"previousRecordCount":2,"recordCountChange":1,"recordCountChangeLabel":"+1 筆","sums":[{"fieldId":"field-completed-count","label":"本期完成數量","unit":"件","sum":15,"display":"15 件","recordCount":3,"previousSum":10,"previousDisplay":"10 件","previousRecordCount":2,"change":5,"changeLabel":"+5 件"}]},"aiSummary":{"label":"AI 摘要","status":"ready","text":"整體來看，紀錄筆數：本期 3 筆，前一期 2 筆，變化 +1 筆。","note":null,"updatedAt":"2026-10-03T08:41:20.26495+00:00","disclaimer":"由 AI 根據上方已算好的統計數字撰寫，僅供參考；一切數字以統計為準。"}}`;
const REAL_INSUFFICIENT_JSON = `{"report":{"id":"01a100ec-c8ee-7648-b744-3b36bf676df6","assistantId":"01a100ec-c8ab-74d0-a503-186055a90e36","assistantName":"客服小幫手","frequency":"monthly","periodFrom":"2026-10-01","periodTo":"2026-10-31","periodLabel":"2026-10-01 至 2026-10-31","status":"generated","skipReason":null,"skipMessage":null,"dataState":"insufficient-records","dataMessage":"紀錄不足：這一期有 0 筆、前一期有 0 筆紀錄，兩期都有紀錄才會顯示變化、趨勢與 AI 摘要。","generatedAt":"2026-10-03T08:41:20.359331+00:00","summaryStatus":"not-requested"},"statistics":{"period":{"period":null,"from":"2026-10-01","to":"2026-10-31","label":"2026-10-01 至 2026-10-31"},"previousPeriod":{"period":null,"from":"2026-09-01","to":"2026-09-30","label":"2026-09-01 至 2026-09-30"},"subjectId":null,"recordCount":0,"previousRecordCount":0,"recordCountChange":0,"recordCountChangeLabel":"持平","sums":[{"fieldId":"field-completed-count","label":"本期完成數量","unit":"件","sum":0,"display":"0 件","recordCount":0,"previousSum":0,"previousDisplay":"0 件","previousRecordCount":0,"change":0,"changeLabel":"持平"}]},"aiSummary":{"label":"AI 摘要","status":"not-requested","text":null,"note":null,"updatedAt":"2026-10-03T08:41:20.359331+00:00","disclaimer":"由 AI 根據上方已算好的統計數字撰寫，僅供參考；一切數字以統計為準。"}}`;
const REAL_SETTINGS_JSON = `{"configuration":{"id":"01a100ec-c16e-7b08-9c72-8ce2b9d42dff","ownerAccountId":"01a100ec-b9a1-735b-a16c-fe915cac20e8","name":"客服小幫手","purpose":"協助回報完成數量","status":"ready","viewerCanManage":true,"createdAt":"2026-10-03T08:41:18.44656+00:00","updatedAt":"2026-10-03T08:41:18.44656+00:00","acceptanceStatus":"not-accepted"},"knowledgeBaseIds":["01a100ec-c177-747e-a2c2-23e8eec95256"],"databaseIds":["01a100ec-c145-7811-98f9-ebe88684b521"],"tone":"friendly","roleInstructions":"","rules":{"knowledgeScope":"company-data-only","refusalMessage":"目前的資料中找不到這個問題的答案。","showCitations":true,"keepConversations":true,"dataWriteDatabaseId":"01a100ec-c145-7811-98f9-ebe88684b521","dataWritePurpose":"每週回報完成數量。","periodicReport":"weekly"}}`;
const ASSISTANT_ID = '01a100ec-c16e-7b08-9c72-8ce2b9d42dff';
const REAL_REPORT_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"database-report","message":"找不到這份報表，或你沒有查看它的權限。"}`;
const REAL_RECORDS_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"database-records","message":"只有被指定為資料管理者、且具備「查看同意提交的紀錄」權限的帳號，可以查看收集紀錄與趨勢比較。"}`;

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

describe('HybridDemoRepository periodic reports (issue #150)', () => {
  it('maps the real list: the schedule, and a report whose summary was discarded, with the null fields kept null', async () => {
    const { repository, controller, written } = setUp();
    const result = firstValueFrom(repository.listDatabaseReports(DATABASE_ID));
    controller.expectOne({ method: 'GET', url: apiDatabaseReportsPath(DATABASE_ID) }).flush(JSON.parse(REAL_LIST_JSON));

    const outcome = await result;
    expect(outcome.status).toBe('ready');
    if (outcome.status !== 'ready') return;
    expect(outcome.data.databaseId).toBe(DATABASE_ID);
    expect(outcome.data.schedules).toEqual([
      {
        assistantId: ASSISTANT_ID,
        assistantName: '客服小幫手',
        frequency: 'weekly',
        nextPeriodFrom: '2026-10-05',
        nextReportDate: '2026-10-12',
      },
    ]);
    expect(outcome.data.reports).toEqual([
      {
        id: REPORT_ID,
        assistantId: ASSISTANT_ID,
        assistantName: '客服小幫手',
        frequency: 'weekly',
        periodFrom: '2026-09-28',
        periodTo: '2026-10-04',
        periodLabel: '2026-09-28 至 2026-10-04',
        status: 'generated',
        skipReason: null,
        skipMessage: null,
        dataState: 'sufficient',
        dataMessage: null,
        generatedAt: '2026-10-03T08:41:20.059638+00:00',
        summaryStatus: 'discarded',
      },
    ]);
    // 報表來自伺服器：沒有任何東西寫進 mock 的儲存。
    expect(written).toEqual([]);
  });

  it('maps the saved statistics as the query returned them, apart from a discarded AI summary that has no text', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.getDatabaseReport(DATABASE_ID, REPORT_ID));
    controller.expectOne({ method: 'GET', url: apiDatabaseReportPath(DATABASE_ID, REPORT_ID) }).flush(JSON.parse(REAL_DISCARDED_JSON));

    const outcome = await result;
    expect(outcome.status).toBe('ready');
    if (outcome.status !== 'ready') return;
    expect(outcome.data.statistics).toEqual({
      period: { name: null, from: '2026-09-28', to: '2026-10-04', label: '2026-09-28 至 2026-10-04' },
      previousPeriod: { name: null, from: '2026-09-21', to: '2026-09-27', label: '2026-09-21 至 2026-09-27' },
      subjectId: null,
      recordCount: 3,
      previousRecordCount: 2,
      recordCountChange: 1,
      recordCountChangeLabel: '+1 筆',
      sums: [
        {
          fieldId: 'field-completed-count',
          label: '本期完成數量',
          unit: '件',
          sum: 15,
          display: '15 件',
          recordCount: 3,
          previousSum: 10,
          previousDisplay: '10 件',
          previousRecordCount: 2,
          change: 5,
          changeLabel: '+5 件',
        },
      ],
    });
    expect(outcome.data.aiSummary).toEqual({
      label: 'AI 摘要',
      status: 'discarded',
      text: null,
      note: 'AI 摘要含有統計結果裡沒有的數字，已捨棄、沒有顯示。統計與圖表不受影響，可以重試。',
      updatedAt: '2026-10-03T08:41:20.192969+00:00',
      disclaimer: '由 AI 根據上方已算好的統計數字撰寫，僅供參考；一切數字以統計為準。',
    });
  });

  it('keeps 紀錄不足 as it is: statistics kept, a message, and no AI summary requested', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.getDatabaseReport(DATABASE_ID, REPORT_ID));
    controller.expectOne(apiDatabaseReportPath(DATABASE_ID, REPORT_ID)).flush(JSON.parse(REAL_INSUFFICIENT_JSON));

    const outcome = await result;
    expect(outcome.status).toBe('ready');
    if (outcome.status !== 'ready') return;
    expect(outcome.data.report).toMatchObject({
      dataState: 'insufficient-records',
      dataMessage: '紀錄不足：這一期有 0 筆、前一期有 0 筆紀錄，兩期都有紀錄才會顯示變化、趨勢與 AI 摘要。',
      summaryStatus: 'not-requested',
    });
    expect(outcome.data.statistics?.recordCount).toBe(0);
    expect(outcome.data.aiSummary).toMatchObject({ status: 'not-requested', text: null });
  });

  it('POSTs the retry with no body and maps pending, then the ready text on the next read', async () => {
    const { repository, controller } = setUp();
    const retry = firstValueFrom(repository.retryDatabaseReportSummary(DATABASE_ID, REPORT_ID));
    const request = controller.expectOne({ method: 'POST', url: apiDatabaseReportSummaryPath(DATABASE_ID, REPORT_ID) });
    expect(request.request.body).toBeNull();
    request.flush(JSON.parse(REAL_PENDING_JSON));
    const pending = await retry;
    expect(pending.status === 'ready' && pending.data.aiSummary).toMatchObject({ status: 'pending', text: null, note: null });

    const read = firstValueFrom(repository.getDatabaseReport(DATABASE_ID, REPORT_ID));
    controller.expectOne(apiDatabaseReportPath(DATABASE_ID, REPORT_ID)).flush(JSON.parse(REAL_READY_JSON));
    const ready = await read;
    expect(ready.status === 'ready' && ready.data.aiSummary).toMatchObject({
      status: 'ready',
      text: '整體來看，紀錄筆數：本期 3 筆，前一期 2 筆，變化 +1 筆。',
    });
    expect(ready.status === 'ready' && ready.data.statistics).toEqual(
      (pending.status === 'ready' ? pending.data.statistics : null),
    );
  });

  it('turns the refusals into the same permission-denied reasons the server gave, and a mock id (404) into database', async () => {
    const { repository, controller } = setUp();
    const records = firstValueFrom(repository.listDatabaseReports(DATABASE_ID));
    controller.expectOne(apiDatabaseReportsPath(DATABASE_ID)).flush(JSON.parse(REAL_RECORDS_403_JSON), { status: 403, statusText: 'Forbidden' });
    expect(await records).toMatchObject({ status: 'permission-denied', reason: 'database-records' });

    const missing = firstValueFrom(repository.getDatabaseReport(DATABASE_ID, REPORT_ID));
    controller.expectOne(apiDatabaseReportPath(DATABASE_ID, REPORT_ID)).flush(JSON.parse(REAL_REPORT_403_JSON), { status: 403, statusText: 'Forbidden' });
    expect(await missing).toMatchObject({
      status: 'permission-denied',
      reason: 'database-report',
      message: '找不到這份報表，或你沒有查看它的權限。',
    });

    const mockId = firstValueFrom(repository.listDatabaseReports('database-customer-records'));
    controller.expectOne(apiDatabaseReportsPath('database-customer-records')).flush(null, { status: 404, statusText: 'Not Found' });
    expect(await mockId).toMatchObject({ status: 'permission-denied', reason: 'database' });
  });

  it('surfaces a 5xx as an error, not as "no reports"', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.listDatabaseReports(DATABASE_ID));
    controller.expectOne(apiDatabaseReportsPath(DATABASE_ID)).flush(null, { status: 503, statusText: 'Service Unavailable' });

    await expect(result).rejects.toBeDefined();
  });

  it('sends rules.periodicReport and reads it back from the real settings', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.updateAssistantSettings(ASSISTANT_ID, { rules: { periodicReport: 'weekly' } }));
    const request = controller.expectOne({ method: 'PATCH', url: apiAssistantSettingsPath(ASSISTANT_ID) });
    expect(request.request.body).toEqual({ rules: { periodicReport: 'weekly' } });
    request.flush(JSON.parse(REAL_SETTINGS_JSON));

    const outcome = await result;
    expect(outcome.status === 'ready' && outcome.data.rules).toMatchObject({
      dataWritePurpose: '每週回報完成數量。',
      periodicReport: 'weekly',
    });
  });

  it('shows the server’s periodicReport refusal under its own field', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.updateAssistantSettings(ASSISTANT_ID, { rules: { periodicReport: 'monthly' } }));
    controller.expectOne(apiAssistantSettingsPath(ASSISTANT_ID)).flush(
      {
        title: 'Unprocessable Content',
        status: 422,
        message: '請先指定要寫入的資料庫，才能設定定期回報。',
        errors: { periodicReport: ['請先指定要寫入的資料庫，才能設定定期回報。'] },
      },
      { status: 422, statusText: 'Unprocessable Entity' },
    );

    expect(await result).toEqual({
      status: 'validation-failed',
      message: '請先指定要寫入的資料庫，才能設定定期回報。',
      errors: [{ field: 'periodicReport', message: '請先指定要寫入的資料庫，才能設定定期回報。' }],
    });
  });
});
