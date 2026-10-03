import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { DemoKeyValueStorage } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import {
  API_SUBMISSIONS_PATH,
  apiDatabaseTrackingPath,
  apiSubmissionWithdrawalPath,
  HybridDemoRepository,
} from './hybrid-demo-repository';
import { createMemoryStorage } from './memory-storage';

const DATABASE_ID = '01a10084-8346-791b-a454-95d92252b9ce';
const ACTIVE_ID = '01a10084-83df-717a-96e2-28de141c3105';
const WITHDRAWN_ID = '01a10084-843f-7125-acf5-09ec5e392b49';
const SUBMITTER_ID = '01a10084-7efb-716e-984c-285d1dbe17a6';

/*
 * 以下回應都是 2026-10-03 從 API 整合測試主機實際取得（外部客戶對「滿意度調查」模板建立的數據庫
 * 送出兩筆、撤回其中一筆；資料管理者讀時間軸），原樣保留成字串再 `JSON.parse`，不是依產生的型別
 * 手寫（issue #146）。注意 `withdrawnAt` 在有效時是 `null`（後端一律送出，不省略）。
 */
const REAL_OWN_LIST_JSON = `{"submissions":[{"id":"01a10084-843f-7125-acf5-09ec5e392b49","receiptNumber":"R-20261003-EC5E392B49","submittedAt":"2026-10-03T06:47:27.039302+00:00","databaseId":"01a10084-8346-791b-a454-95d92252b9ce","databaseName":"門市滿意度","formVersionNumber":1,"source":"form-link","withdrawnAt":"2026-10-03T06:47:27.048448+00:00"},{"id":"01a10084-83df-717a-96e2-28de141c3105","receiptNumber":"R-20261003-DE141C3105","submittedAt":"2026-10-03T06:47:26.943728+00:00","databaseId":"01a10084-8346-791b-a454-95d92252b9ce","databaseName":"門市滿意度","formVersionNumber":1,"source":"form-link","withdrawnAt":null}]}`;
const REAL_WITHDRAW_200_JSON = `{"id":"01a10084-843f-7125-acf5-09ec5e392b49","receiptNumber":"R-20261003-EC5E392B49","submittedAt":"2026-10-03T06:47:27.039302+00:00","databaseId":"01a10084-8346-791b-a454-95d92252b9ce","databaseName":"門市滿意度","purpose":"收集客戶對服務的評分與建議。","recipient":"安心商行（門市滿意度）","viewers":["安心商行管理者"],"formVersionId":"01a10084-8348-7214-9a7b-a8019080e84f","formVersionNumber":1,"source":"form-link","entries":[],"withdrawnAt":"2026-10-03T06:47:27.048448+00:00"}`;
const REAL_WITHDRAW_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"submission-withdrawal","message":"找不到這筆紀錄，或你沒有撤回它的權限。"}`;
const REAL_TRACKING_JSON = `{"databaseId":"01a10084-8346-791b-a454-95d92252b9ce","subjects":[{"subject":{"id":"01a10084-7efb-716e-984c-285d1dbe17a6","displayName":"安心商行外部客戶"},"records":[{"id":"01a10084-83df-717a-96e2-28de141c3105","receiptNumber":"R-20261003-DE141C3105","submittedAt":"2026-10-03T06:47:26.943728+00:00","source":"form-link","submitter":{"id":"01a10084-7efb-716e-984c-285d1dbe17a6","displayName":"安心商行外部客戶"},"formVersionNumber":1,"entries":[{"fieldId":"field-overall-satisfaction","label":"整體滿意度","type":"scale","display":"4 / 5"},{"fieldId":"field-liked-services","label":"喜歡的服務","type":"multiple-choice","display":"客服回應"},{"fieldId":"field-suggestion","label":"其他建議","type":"text","display":"未填寫"}]}],"withdrawals":[{"id":"01a10084-843f-7125-acf5-09ec5e392b49","submittedAt":"2026-10-03T06:47:27.039302+00:00","withdrawnAt":"2026-10-03T06:47:27.048448+00:00","source":"form-link","formVersionNumber":1}]}]}`;
const REAL_TRACKING_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"database-records","message":"只有被指定為資料管理者、且具備「查看同意提交的紀錄」權限的帳號，可以查看收集紀錄與趨勢比較。"}`;

function setUp() {
  TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
  const base = createMemoryStorage();
  /** mock storage 被寫入的鍵：API 模式的紀錄與撤回不能落到 mock 的收集紀錄上。 */
  const written: string[] = [];
  const storage: DemoKeyValueStorage = {
    ...base,
    setItem: (key, value) => {
      written.push(key);
      base.setItem(key, value);
    },
  };
  const repository = new HybridDemoRepository(
    DEMO_SEED,
    { storage, viewer: () => 'account-external-customer' },
    {
      http: TestBed.inject(HttpClient),
      viewerPermissions: () => ({
        accountId: SUBMITTER_ID,
        demoAccountId: 'account-external-customer',
        permissions: ['submit-authorized-forms', 'read-own-tracking'],
        organizationId: '0199a3c0-0000-7000-8000-0000000000aa',
      }),
    },
  );
  return { repository, written, controller: TestBed.inject(HttpTestingController) };
}

function flushError(controller: HttpTestingController, method: string, url: string, json: string, status: number): void {
  controller.expectOne({ method, url }).flush(JSON.parse(json), { status, statusText: String(status) });
}

describe('HybridDemoRepository records and withdrawal (issue #146)', () => {
  it('lists the submitter’s own submissions from the API, active and withdrawn, without content', async () => {
    const { repository, controller, written } = setUp();
    const result = firstValueFrom(repository.listOwnDatabaseSubmissions());
    controller.expectOne({ method: 'GET', url: API_SUBMISSIONS_PATH }).flush(JSON.parse(REAL_OWN_LIST_JSON));

    expect(await result).toEqual({
      status: 'ready',
      data: [
        {
          id: WITHDRAWN_ID,
          receiptNumber: 'R-20261003-EC5E392B49',
          submittedAt: '2026-10-03T06:47:27.039302+00:00',
          databaseId: DATABASE_ID,
          databaseName: '門市滿意度',
          formVersion: 1,
          source: 'form-link',
          withdrawnAt: '2026-10-03T06:47:27.048448+00:00',
        },
        {
          id: ACTIVE_ID,
          receiptNumber: 'R-20261003-DE141C3105',
          submittedAt: '2026-10-03T06:47:26.943728+00:00',
          databaseId: DATABASE_ID,
          databaseName: '門市滿意度',
          formVersion: 1,
          source: 'form-link',
          withdrawnAt: null,
        },
      ],
    });
    expect(written).toEqual([]);
  });

  it('withdraws through the API and maps the withdrawn receipt: no entries, the withdrawal time', async () => {
    const { repository, controller, written } = setUp();
    const result = firstValueFrom(repository.withdrawDatabaseSubmission(WITHDRAWN_ID));
    const request = controller.expectOne({ method: 'POST', url: apiSubmissionWithdrawalPath(WITHDRAWN_ID) });
    request.flush(JSON.parse(REAL_WITHDRAW_200_JSON));

    const outcome = await result;
    expect(outcome).toMatchObject({
      status: 'ready',
      data: {
        id: WITHDRAWN_ID,
        receiptNumber: 'R-20261003-EC5E392B49',
        entries: [],
        withdrawnAt: '2026-10-03T06:47:27.048448+00:00',
      },
    });
    expect(written).toEqual([]);
  });

  it('maps someone else’s or a missing submission (403) and a non-GUID id (404) to the same refusal', async () => {
    const { repository, controller } = setUp();
    const denied = firstValueFrom(repository.withdrawDatabaseSubmission(ACTIVE_ID));
    flushError(controller, 'POST', apiSubmissionWithdrawalPath(ACTIVE_ID), REAL_WITHDRAW_403_JSON, 403);
    const notGuid = firstValueFrom(repository.withdrawDatabaseSubmission('submission-form-1'));
    controller
      .expectOne({ method: 'POST', url: apiSubmissionWithdrawalPath('submission-form-1') })
      .flush(null, { status: 404, statusText: 'Not Found' });

    const expected = {
      status: 'permission-denied',
      reason: 'submission-withdrawal',
      message: '找不到這筆紀錄，或你沒有撤回它的權限。',
    };
    expect(await denied).toEqual(expected);
    expect(await notGuid).toEqual(expected);
  });

  it('lets a server failure surface as an error so the page can offer to try again', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.withdrawDatabaseSubmission(ACTIVE_ID));
    controller
      .expectOne({ method: 'POST', url: apiSubmissionWithdrawalPath(ACTIVE_ID) })
      .flush(null, { status: 503, statusText: 'Service Unavailable' });

    await expect(result).rejects.toBeTruthy();
  });

  it('maps the real timeline: subjects are submitting accounts, records and withdrawal trails stay apart', async () => {
    const { repository, controller, written } = setUp();
    const result = firstValueFrom(repository.getDatabaseTracking(DATABASE_ID));
    controller.expectOne({ method: 'GET', url: apiDatabaseTrackingPath(DATABASE_ID) }).flush(JSON.parse(REAL_TRACKING_JSON));

    const outcome = await result;
    expect(outcome.status).toBe('ready');
    if (outcome.status !== 'ready') return;
    expect(outcome.data.databaseId).toBe(DATABASE_ID);
    expect(outcome.data.periodicReports).toEqual([]);
    expect(outcome.data.subjects).toHaveLength(1);
    const [subject] = outcome.data.subjects;
    expect(subject.id).toBe(`subject-${SUBMITTER_ID}`);
    expect(subject.displayName).toBe('安心商行外部客戶');
    expect(subject.records).toEqual([
      {
        id: `record-${ACTIVE_ID}`,
        recordedAt: '2026-10-03T06:47:26.943728+00:00',
        dateLabel: '2026-10-03',
        source: 'form-link',
        entries: [
          { fieldId: 'field-overall-satisfaction', label: '整體滿意度', display: '4 / 5' },
          { fieldId: 'field-liked-services', label: '喜歡的服務', display: '客服回應' },
          { fieldId: 'field-suggestion', label: '其他建議', display: '未填寫' },
        ],
      },
    ]);
    expect(subject.withdrawals).toEqual([
      {
        id: `record-${WITHDRAWN_ID}`,
        submittedAt: '2026-10-03T06:47:27.039302+00:00',
        submittedDateLabel: '2026-10-03',
        withdrawnDateLabel: '2026-10-03',
        source: 'form-link',
      },
    ]);
    // 趨勢比較是 #147：這裡只是佔位，不會假裝算出了差異。
    expect(subject.comparison.status).toBe('insufficient-records');
    expect(written).toEqual([]);
  });

  it('keeps the two refusals of the timeline apart: database-records, and database for a missing id', async () => {
    const { repository, controller } = setUp();
    const records = firstValueFrom(repository.getDatabaseTracking(DATABASE_ID));
    flushError(controller, 'GET', apiDatabaseTrackingPath(DATABASE_ID), REAL_TRACKING_403_JSON, 403);
    const notGuid = firstValueFrom(repository.getDatabaseTracking('database-orders'));
    controller
      .expectOne({ method: 'GET', url: apiDatabaseTrackingPath('database-orders') })
      .flush(null, { status: 404, statusText: 'Not Found' });

    expect(await records).toMatchObject({ status: 'permission-denied', reason: 'database-records' });
    expect(await notGuid).toMatchObject({ status: 'permission-denied', reason: 'database' });
  });
});

describe('HybridDemoRepository withdrawal from an in-chat receipt (issues #146 and #148)', () => {
  /** 對話來源的回執：與表單連結的撤回回應同形，只差 `source`。 */
  const CHAT_WITHDRAW_200_JSON = REAL_WITHDRAW_200_JSON.replace('"source":"form-link"', '"source":"assistant-conversation"');

  it('withdraws the receipt’s submission through the same withdrawal endpoint and returns the withdrawn state', async () => {
    const { repository, controller, written } = setUp();
    const result = firstValueFrom(
      repository.withdrawChatSubmission('account-external-customer', 'assistant-any', `record-${WITHDRAWN_ID}`),
    );
    controller
      .expectOne({ method: 'POST', url: apiSubmissionWithdrawalPath(WITHDRAWN_ID) })
      .flush(JSON.parse(CHAT_WITHDRAW_200_JSON));

    expect(await result).toEqual({
      status: 'ready',
      data: {
        status: 'withdrawn',
        withdrawnDateLabel: '2026-10-03',
        notice: expect.stringContaining('你已撤回這筆資料'),
      },
    });
    // 撤回不寫進 mock 的收集紀錄。
    expect(written).toEqual([]);
  });

  it('gives someone else’s receipt the same refusal as the withdrawal page', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(
      repository.withdrawChatSubmission('account-external-customer', 'assistant-any', `record-${ACTIVE_ID}`),
    );
    flushError(controller, 'POST', apiSubmissionWithdrawalPath(ACTIVE_ID), REAL_WITHDRAW_403_JSON, 403);

    expect(await result).toEqual({
      status: 'permission-denied',
      reason: 'submission-withdrawal',
      message: '找不到這筆紀錄，或你沒有撤回它的權限。',
    });
  });

  it('lets a server failure surface as an error: nothing changed, the receipt can be withdrawn again', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(
      repository.withdrawChatSubmission('account-external-customer', 'assistant-any', `record-${ACTIVE_ID}`),
    );
    controller
      .expectOne({ method: 'POST', url: apiSubmissionWithdrawalPath(ACTIVE_ID) })
      .flush(null, { status: 503, statusText: 'Service Unavailable' });

    await expect(result).rejects.toBeDefined();
  });
});
