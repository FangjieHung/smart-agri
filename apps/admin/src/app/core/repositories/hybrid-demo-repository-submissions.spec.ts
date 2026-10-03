import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { DemoKeyValueStorage } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import { API_SUBMISSIONS_PATH, apiDatabasePath, HybridDemoRepository } from './hybrid-demo-repository';
import { createMemoryStorage } from './memory-storage';

const DATABASE_ID = '01a10059-731e-7477-84d6-ab5d7a385a6c';
const SUBMISSION_ID = '01a10059-7419-7a16-bae3-47cb300ea02b';

/*
 * 以下回應都是 2026-10-03 從 API 整合測試主機實際取得（外部客戶對「滿意度調查」模板建立的數據庫
 * 填寫、送出），原樣保留成字串再 `JSON.parse`，不是依產生的型別手寫（issue #145）。
 */
const REAL_FORM_JSON = `{"databaseId":"01a10059-731e-7477-84d6-ab5d7a385a6c","databaseName":"門市滿意度","purpose":"收集客戶對服務的評分與建議。","recipient":"安心商行（門市滿意度）","viewers":["安心商行管理者"],"sensitiveNotice":"請勿填寫身分證字號、病歷、信用卡號或密碼等敏感資料；只填寫處理這次問題需要的內容。","withdrawalNotice":"送出後會取得一張回執。撤回功能將於後續版本開放：撤回後接收單位會移除這筆資料的內容，只保留「曾提交、已撤回」的軌跡；在那之前如需撤回，請聯絡接收單位。","form":{"id":"01a10059-7320-79ac-a844-b0f9da7ecf24","versionNumber":1,"createdAt":"2026-10-03T06:00:24.606209+00:00","fields":[{"id":"field-overall-satisfaction","label":"整體滿意度","type":"scale","required":true,"options":[],"scale":{"min":1,"max":5,"minLabel":"很不滿意","maxLabel":"非常滿意"},"unit":""},{"id":"field-liked-services","label":"喜歡的服務","type":"multiple-choice","required":false,"options":["商品品質","客服回應","配送速度"],"scale":null,"unit":""},{"id":"field-suggestion","label":"其他建議","type":"text","required":false,"options":[],"scale":null,"unit":""}]}}`;
const REAL_REVIEW_200_JSON = `{"saved":false,"formVersion":1,"entries":[{"fieldId":"field-overall-satisfaction","label":"整體滿意度","display":"4 / 5"},{"fieldId":"field-liked-services","label":"喜歡的服務","display":"客服回應"},{"fieldId":"field-suggestion","label":"其他建議","display":"未填寫"}]}`;
const REAL_CONSENT_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"consent-required","message":"尚未同意，資料沒有送出。","errors":{"consent":["請先勾選同意，才能送出資料。"]}}`;
const REAL_ANSWERS_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"message":"「整體滿意度」為必填。","errors":{"answers.field-overall-satisfaction":["「整體滿意度」為必填。"]}}`;
const REAL_SUBMIT_201_JSON = `{"id":"01a10059-7419-7a16-bae3-47cb300ea02b","receiptNumber":"R-20261003-CB300EA02B","submittedAt":"2026-10-03T06:00:24.857605+00:00","databaseId":"01a10059-731e-7477-84d6-ab5d7a385a6c","databaseName":"門市滿意度","purpose":"收集客戶對服務的評分與建議。","recipient":"安心商行（門市滿意度）","viewers":["安心商行管理者"],"formVersionId":"01a10059-7320-79ac-a844-b0f9da7ecf24","formVersionNumber":1,"source":"form-link","entries":[{"fieldId":"field-overall-satisfaction","label":"整體滿意度","type":"scale","display":"4 / 5"},{"fieldId":"field-liked-services","label":"喜歡的服務","type":"multiple-choice","display":"客服回應"},{"fieldId":"field-suggestion","label":"其他建議","type":"text","display":"未填寫"}]}`;
const REAL_KEY_REUSED_409_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Conflict","status":409,"reason":"submission-key-reused","message":"這個提交編號已經用在另一份內容上，資料沒有送出。請重新載入表單後再填寫。"}`;
const REAL_VERSION_409_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Conflict","status":409,"reason":"form-version-changed","message":"這份表單已更新，請重新載入最新的表單後再填寫。你這次填寫的內容尚未送出。"}`;
const REAL_RECEIPT_200_JSON = `{"id":"01a10059-7419-7a16-bae3-47cb300ea02b","receiptNumber":"R-20261003-CB300EA02B","submittedAt":"2026-10-03T06:00:24.857605+00:00","databaseId":"01a10059-731e-7477-84d6-ab5d7a385a6c","databaseName":"門市滿意度","purpose":"收集客戶對服務的評分與建議。","recipient":"安心商行（門市滿意度）","viewers":["安心商行管理者"],"formVersionId":"01a10059-7320-79ac-a844-b0f9da7ecf24","formVersionNumber":1,"source":"form-link","entries":[{"fieldId":"field-overall-satisfaction","label":"整體滿意度","type":"scale","display":"4 / 5"},{"fieldId":"field-liked-services","label":"喜歡的服務","type":"multiple-choice","display":"客服回應"},{"fieldId":"field-suggestion","label":"其他建議","type":"text","display":"未填寫"}]}`;
const REAL_RECEIPT_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"authorized-form","message":"找不到這張回執，或你沒有查看它的權限。"}`;
const REAL_FORM_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"authorized-form","message":"你沒有填寫這份表單的權限，或它已不存在。"}`;

const ANSWERS = { 'field-overall-satisfaction': '4', 'field-liked-services': ['客服回應'] };

function setUp() {
  TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
  const base = createMemoryStorage();
  /** mock storage 被寫入的鍵：API 模式的提交不能落到 mock 的收集紀錄上。 */
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
        accountId: '01a10059-6ca0-7415-8a50-9543c0fa631a',
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

const formPath = `${apiDatabasePath(DATABASE_ID)}/submission-form`;
const reviewPath = `${formPath}/review`;
const submitPath = `${apiDatabasePath(DATABASE_ID)}/submissions`;

describe('HybridDemoRepository consented submission (issue #145)', () => {
  it('maps the real submission form: terms, actual readers and the current version', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.getDatabaseSubmissionForm(DATABASE_ID));
    controller.expectOne({ method: 'GET', url: formPath }).flush(JSON.parse(REAL_FORM_JSON));

    const outcome = await result;
    expect(outcome.status).toBe('ready');
    if (outcome.status !== 'ready') return;
    expect(outcome.data).toMatchObject({
      databaseId: DATABASE_ID,
      databaseName: '門市滿意度',
      recipient: '安心商行（門市滿意度）',
      viewers: ['安心商行管理者'],
      formVersion: 1,
    });
    expect(outcome.data.fields.map((field) => field.id)).toEqual([
      'field-overall-satisfaction',
      'field-liked-services',
      'field-suggestion',
    ]);
    expect(outcome.data.fields[1].scale).toBeNull();
  });

  it('turns a 403 and a non-GUID 404 on the form into the same authorized-form denial', async () => {
    const { repository, controller } = setUp();
    const forbidden = firstValueFrom(repository.getDatabaseSubmissionForm(DATABASE_ID));
    flushError(controller, 'GET', formPath, REAL_FORM_403_JSON, 403);
    const notGuid = firstValueFrom(repository.getDatabaseSubmissionForm('database-orders'));
    controller.expectOne(`${apiDatabasePath('database-orders')}/submission-form`).flush(null, { status: 404, statusText: 'Not Found' });

    expect(await forbidden).toEqual({
      status: 'permission-denied',
      reason: 'authorized-form',
      message: '你沒有填寫這份表單的權限，或它已不存在。',
    });
    expect(await notGuid).toEqual(await forbidden);
  });

  it('reviews through the API with the form version, writing nothing to mock storage', async () => {
    const { repository, controller, written } = setUp();
    const result = firstValueFrom(repository.reviewDatabaseSubmission(DATABASE_ID, 1, ANSWERS));
    const request = controller.expectOne({ method: 'POST', url: reviewPath });
    expect(request.request.body).toEqual({ formVersionNumber: 1, answers: ANSWERS });
    request.flush(JSON.parse(REAL_REVIEW_200_JSON));

    expect(await result).toEqual({
      status: 'ready',
      data: {
        saved: false,
        formVersion: 1,
        entries: [
          { fieldId: 'field-overall-satisfaction', label: '整體滿意度', display: '4 / 5' },
          { fieldId: 'field-liked-services', label: '喜歡的服務', display: '客服回應' },
          { fieldId: 'field-suggestion', label: '其他建議', display: '未填寫' },
        ],
      },
    });
    expect(written).toEqual([]);
  });

  it('submits the key, version, consent and answers, and maps the real receipt', async () => {
    const { repository, controller, written } = setUp();
    const result = firstValueFrom(
      repository.submitDatabaseEntry(DATABASE_ID, { submissionId: 'key-1', formVersion: 1, consent: true, answers: ANSWERS }),
    );
    const request = controller.expectOne({ method: 'POST', url: submitPath });
    expect(request.request.body).toEqual({ submissionId: 'key-1', formVersionNumber: 1, consent: true, answers: ANSWERS });
    request.flush(JSON.parse(REAL_SUBMIT_201_JSON), { status: 201, statusText: 'Created' });

    expect(await result).toEqual({
      status: 'ready',
      data: {
        id: SUBMISSION_ID,
        receiptNumber: 'R-20261003-CB300EA02B',
        submittedAt: '2026-10-03T06:00:24.857605+00:00',
        databaseId: DATABASE_ID,
        databaseName: '門市滿意度',
        purpose: '收集客戶對服務的評分與建議。',
        recipient: '安心商行（門市滿意度）',
        viewers: ['安心商行管理者'],
        formVersion: 1,
        source: 'form-link',
        entries: [
          { fieldId: 'field-overall-satisfaction', label: '整體滿意度', display: '4 / 5' },
          { fieldId: 'field-liked-services', label: '喜歡的服務', display: '客服回應' },
          { fieldId: 'field-suggestion', label: '其他建議', display: '未填寫' },
        ],
        // #145 的回應還沒有 `withdrawnAt`：省略視為有效（null），不會被當成已撤回。
        withdrawnAt: null,
      },
    });
    expect(written).toEqual([]);
  });

  it('maps a missing consent to a form-level error and field errors to their fields', async () => {
    const { repository, controller } = setUp();
    const input = { submissionId: 'key-1', formVersion: 1, consent: false, answers: ANSWERS };
    const consent = firstValueFrom(repository.submitDatabaseEntry(DATABASE_ID, input));
    flushError(controller, 'POST', submitPath, REAL_CONSENT_422_JSON, 422);
    const answers = firstValueFrom(repository.submitDatabaseEntry(DATABASE_ID, { ...input, consent: true, answers: {} }));
    flushError(controller, 'POST', submitPath, REAL_ANSWERS_422_JSON, 422);

    expect(await consent).toEqual({
      status: 'validation-failed',
      errors: [{ fieldId: null, message: '請先勾選同意，才能送出資料。' }],
      message: '尚未同意，資料沒有送出。',
    });
    expect(await answers).toEqual({
      status: 'validation-failed',
      errors: [{ fieldId: 'field-overall-satisfaction', message: '「整體滿意度」為必填。' }],
      message: '「整體滿意度」為必填。',
    });
  });

  it('tells a changed form apart from a reused key on 409', async () => {
    const { repository, controller } = setUp();
    const input = { submissionId: 'key-1', formVersion: 1, consent: true, answers: ANSWERS };
    const version = firstValueFrom(repository.submitDatabaseEntry(DATABASE_ID, input));
    flushError(controller, 'POST', submitPath, REAL_VERSION_409_JSON, 409);
    const reused = firstValueFrom(repository.submitDatabaseEntry(DATABASE_ID, input));
    flushError(controller, 'POST', submitPath, REAL_KEY_REUSED_409_JSON, 409);
    const review = firstValueFrom(repository.reviewDatabaseSubmission(DATABASE_ID, 1, ANSWERS));
    flushError(controller, 'POST', reviewPath, REAL_VERSION_409_JSON, 409);

    expect(await version).toEqual({
      status: 'conflict',
      reason: 'form-version-changed',
      message: '這份表單已更新，請重新載入最新的表單後再填寫。你這次填寫的內容尚未送出。',
    });
    expect(await reused).toMatchObject({ status: 'conflict', reason: 'submission-key-reused' });
    expect(await review).toEqual(await version);
  });

  it('lets a server failure on submit through as an error so the screen can retry with the same key', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(
      repository.submitDatabaseEntry(DATABASE_ID, { submissionId: 'key-1', formVersion: 1, consent: true, answers: ANSWERS }),
    );
    controller.expectOne(submitPath).flush(null, { status: 500, statusText: 'Server Error' });

    await expect(result).rejects.toMatchObject({ status: 500 });
  });

  it('reopens the submitter’s own receipt and denies anyone else with the receipt message', async () => {
    const { repository, controller } = setUp();
    const own = firstValueFrom(repository.getDatabaseSubmissionReceipt(SUBMISSION_ID));
    controller.expectOne(`${API_SUBMISSIONS_PATH}/${SUBMISSION_ID}`).flush(JSON.parse(REAL_RECEIPT_200_JSON));
    const other = firstValueFrom(repository.getDatabaseSubmissionReceipt(SUBMISSION_ID));
    flushError(controller, 'GET', `${API_SUBMISSIONS_PATH}/${SUBMISSION_ID}`, REAL_RECEIPT_403_JSON, 403);

    expect(await own).toMatchObject({ status: 'ready', data: { id: SUBMISSION_ID, receiptNumber: 'R-20261003-CB300EA02B' } });
    expect(await other).toEqual({
      status: 'permission-denied',
      reason: 'authorized-form',
      message: '找不到這張回執，或你沒有查看它的權限。',
    });
  });
});
