import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import { caseCreatedByText, caseRecordStateLabel } from '../domain/case.model';
import { DemoSessionService } from '../session/demo-session.service';
import { apiCaseTypePath, apiDatabaseAutoCasePath, CaseSettingsRepository, caseTypeInUseMessage } from './case-settings.repository';
import { apiCasePath, CasesRepository } from './cases.repository';
import { API_DEMO_REPOSITORY_FACTORY } from './tokens';

// Recorded 2026-10-07 from the real API (issue #255): `dotnet run` of this branch on port 5263 against a
// throw-away PostgreSQL database `m7_255` (migrated, development seed `anxin`), signed in as `admin`,
// `internal` and `customer` through /connect/authorize + /connect/token, by `scratchpad/255/record.py`:
// 設備組 (member: internal) and the type 設備故障報修 (72 h) created first, an inactive 倉儲盤點差異, the
// admin's database 報修客戶資料庫 (no data manager yet, so internal cannot read its records); `customer`
// submitted once after the auto case was set, then withdrew. The bodies are pasted unchanged; never edit
// them to match the types.
/** admin-auto-case-get-initial: HTTP 200. */
const AUTO_CASE_INITIAL_JSON = `{"databaseId":"01a11295-f64f-72d0-b09c-062559da402b","caseTypeId":null,"options":[{"id":"01a11295-f62e-78d8-b93c-c777cf570896","name":"設備故障報修","group":{"id":"01a11295-f5b2-7730-aa8f-b1c086cb7a57","name":"設備組","archived":false},"defaultDueHours":72,"memberCount":1,"unreadableMemberCount":1}]}`;
/** internal-auto-case-get-403: HTTP 403. */
const AUTO_CASE_INTERNAL_GET_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"organization-settings","message":"只有管理者可以變更組織設定。"}`;
/** internal-auto-case-put-403: HTTP 403. */
const AUTO_CASE_INTERNAL_PUT_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"organization-settings","message":"只有管理者可以變更組織設定。"}`;
/** admin-auto-case-put-inactive-422: HTTP 422. */
const AUTO_CASE_INACTIVE_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"case-type-inactive","message":"這個案件類型已停用或不存在，請選擇其他類型。","errors":{"caseTypeId":["這個案件類型已停用或不存在，請選擇其他類型。"]}}`;
/** admin-auto-case-put: HTTP 200. */
const AUTO_CASE_ON_JSON = `{"databaseId":"01a11295-f64f-72d0-b09c-062559da402b","caseTypeId":"01a11295-f62e-78d8-b93c-c777cf570896","options":[{"id":"01a11295-f62e-78d8-b93c-c777cf570896","name":"設備故障報修","group":{"id":"01a11295-f5b2-7730-aa8f-b1c086cb7a57","name":"設備組","archived":false},"defaultDueHours":72,"memberCount":1,"unreadableMemberCount":1}]}`;
/** internal-cases-list: HTTP 200. */
const CASES_LIST_JSON = `[{"id":"01a11295-f6c7-785b-b7e4-1e298d154f28","title":"報修客戶資料庫：新紀錄","status":"pending","origin":"database-submission","type":{"id":"01a11295-f62e-78d8-b93c-c777cf570896","name":"設備故障報修"},"group":{"id":"01a11295-f5b2-7730-aa8f-b1c086cb7a57","name":"設備組","archived":false},"createdBy":null,"owner":null,"dueAt":"2026-10-09T18:59:40.334194+00:00","createdAt":"2026-10-06T18:59:40.334194+00:00","updatedAt":"2026-10-06T18:59:40.334194+00:00"}]`;
/** internal-case-detail-auto: HTTP 200. */
const CASE_DETAIL_JSON = `{"case":{"id":"01a11295-f6c7-785b-b7e4-1e298d154f28","title":"報修客戶資料庫：新紀錄","description":"由數據庫送出自動建立，內容請開啟紀錄查看。","status":"pending","origin":"database-submission","type":{"id":"01a11295-f62e-78d8-b93c-c777cf570896","name":"設備故障報修"},"group":{"id":"01a11295-f5b2-7730-aa8f-b1c086cb7a57","name":"設備組","archived":false},"createdBy":null,"owner":null,"dueAt":"2026-10-09T18:59:40.334194+00:00","resolution":null,"cancelReason":null,"createdAt":"2026-10-06T18:59:40.334194+00:00","updatedAt":"2026-10-06T18:59:40.334194+00:00","acceptedAt":null,"completedAt":null,"cancelledAt":null,"eventCount":1},"events":[{"id":"01a11295-f6c7-78da-85fa-010182ae3139","ordinal":1,"action":"created","actor":null,"at":"2026-10-06T18:59:40.334194+00:00","note":null,"status":"pending","owner":null,"fromGroup":null,"toGroup":{"id":"01a11295-f5b2-7730-aa8f-b1c086cb7a57","name":"設備組","archived":false},"dueAt":"2026-10-09T18:59:40.334194+00:00"}],"links":{"record":{"databaseId":"01a11295-f64f-72d0-b09c-062559da402b","submissionId":"01a11295-f6ae-7a3c-bd65-26f2c820c386","state":"available","canRead":false,"databaseName":"報修客戶資料庫"},"thread":null,"assistantIssue":null,"previousCase":null},"allowedActions":["accept"],"cancelReasonRequired":true}`;
/** external-case-detail-403: HTTP 403. */
const CASE_EXTERNAL_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"case","message":"你沒有這個案件的存取權限，或它已不存在。案件功能只開放組織內部帳號使用。"}`;
/** internal-case-detail-auto-withdrawn: HTTP 200. */
const CASE_DETAIL_WITHDRAWN_JSON = `{"case":{"id":"01a11295-f6c7-785b-b7e4-1e298d154f28","title":"報修客戶資料庫：新紀錄","description":"由數據庫送出自動建立，內容請開啟紀錄查看。","status":"pending","origin":"database-submission","type":{"id":"01a11295-f62e-78d8-b93c-c777cf570896","name":"設備故障報修"},"group":{"id":"01a11295-f5b2-7730-aa8f-b1c086cb7a57","name":"設備組","archived":false},"createdBy":null,"owner":null,"dueAt":"2026-10-09T18:59:40.334194+00:00","resolution":null,"cancelReason":null,"createdAt":"2026-10-06T18:59:40.334194+00:00","updatedAt":"2026-10-06T18:59:40.334194+00:00","acceptedAt":null,"completedAt":null,"cancelledAt":null,"eventCount":1},"events":[{"id":"01a11295-f6c7-78da-85fa-010182ae3139","ordinal":1,"action":"created","actor":null,"at":"2026-10-06T18:59:40.334194+00:00","note":null,"status":"pending","owner":null,"fromGroup":null,"toGroup":{"id":"01a11295-f5b2-7730-aa8f-b1c086cb7a57","name":"設備組","archived":false},"dueAt":"2026-10-09T18:59:40.334194+00:00"}],"links":{"record":{"databaseId":"01a11295-f64f-72d0-b09c-062559da402b","submissionId":"01a11295-f6ae-7a3c-bd65-26f2c820c386","state":"withdrawn","canRead":false,"databaseName":"報修客戶資料庫"},"thread":null,"assistantIssue":null,"previousCase":null},"allowedActions":["accept"],"cancelReasonRequired":true}`;
/** admin-type-deactivate-in-use-422: HTTP 422. */
const TYPE_IN_USE_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"case-type-in-use","message":"「設備故障報修」是數據庫「報修客戶資料庫」送出後自動開案的類型。請先在這些數據庫改選其他類型或關閉自動開案，再停用。","errors":{"databases":["「設備故障報修」是數據庫「報修客戶資料庫」送出後自動開案的類型。請先在這些數據庫改選其他類型或關閉自動開案，再停用。"]}}`;
/** admin-auto-case-put-off: HTTP 200. */
const AUTO_CASE_OFF_JSON = `{"databaseId":"01a11295-f64f-72d0-b09c-062559da402b","caseTypeId":null,"options":[{"id":"01a11295-f62e-78d8-b93c-c777cf570896","name":"設備故障報修","group":{"id":"01a11295-f5b2-7730-aa8f-b1c086cb7a57","name":"設備組","archived":false},"defaultDueHours":72,"memberCount":1,"unreadableMemberCount":1}]}`;

const DATABASE_ID = '01a11295-f64f-72d0-b09c-062559da402b';
const TYPE_ID = '01a11295-f62e-78d8-b93c-c777cf570896';
const GROUP_ID = '01a11295-f5b2-7730-aa8f-b1c086cb7a57';
const CASE_ID = '01a11295-f6c7-785b-b7e4-1e298d154f28';

function apiRepositories() {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: API_DEMO_REPOSITORY_FACTORY, useValue: () => null },
      { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>('ignored-in-api-mode') } },
    ],
  });
  return {
    settings: TestBed.inject(CaseSettingsRepository),
    cases: TestBed.inject(CasesRepository),
    http: TestBed.inject(HttpTestingController),
  };
}

function flushError(http: HttpTestingController, url: string, method: string, status: number, statusText: string, body: string): void {
  const request = http.expectOne(url);
  expect(request.request.method).toBe(method);
  request.flush(JSON.parse(body), { status, statusText });
}

describe('database auto case (API mode, recorded responses, issue #255)', () => {
  it('reads the setting with each active type and its unreadable members, and an employee gets 403 organization-settings', async () => {
    const { settings, http } = apiRepositories();

    const read = firstValueFrom(settings.getDatabaseAutoCase(DATABASE_ID));
    const request = http.expectOne(apiDatabaseAutoCasePath(DATABASE_ID));
    expect(request.request.method).toBe('GET');
    request.flush(JSON.parse(AUTO_CASE_INITIAL_JSON));
    expect(await read).toEqual({
      status: 'ready',
      data: {
        databaseId: DATABASE_ID,
        caseTypeId: null,
        options: [{
          id: TYPE_ID, name: '設備故障報修', group: { id: GROUP_ID, name: '設備組', archived: false },
          defaultDueHours: 72, memberCount: 1, unreadableMemberCount: 1,
        }],
      },
    });

    const denied = firstValueFrom(settings.getDatabaseAutoCase(DATABASE_ID));
    flushError(http, apiDatabaseAutoCasePath(DATABASE_ID), 'GET', 403, 'Forbidden', AUTO_CASE_INTERNAL_GET_403_JSON);
    expect(await denied).toEqual({ status: 'permission-denied', reason: 'organization-settings', message: '只有管理者可以變更組織設定。' });
    http.verify();
  });

  it('sets a type, refuses an inactive one and a non-manager, and turns it off', async () => {
    const { settings, http } = apiRepositories();

    const on = firstValueFrom(settings.setDatabaseAutoCase(DATABASE_ID, TYPE_ID));
    const request = http.expectOne(apiDatabaseAutoCasePath(DATABASE_ID));
    expect([request.request.method, request.request.body]).toEqual(['PUT', { caseTypeId: TYPE_ID }]);
    request.flush(JSON.parse(AUTO_CASE_ON_JSON));
    const ready = await on;
    expect(ready.status === 'ready' && ready.data.caseTypeId).toBe(TYPE_ID);

    const inactive = firstValueFrom(settings.setDatabaseAutoCase(DATABASE_ID, 'inactive-type'));
    flushError(http, apiDatabaseAutoCasePath(DATABASE_ID), 'PUT', 422, 'Unprocessable Content', AUTO_CASE_INACTIVE_422_JSON);
    expect(await inactive).toEqual({ status: 'validation-failed', message: '這個案件類型已停用或不存在，請選擇其他類型。' });

    const employee = firstValueFrom(settings.setDatabaseAutoCase(DATABASE_ID, TYPE_ID));
    flushError(http, apiDatabaseAutoCasePath(DATABASE_ID), 'PUT', 403, 'Forbidden', AUTO_CASE_INTERNAL_PUT_403_JSON);
    expect(await employee).toMatchObject({ status: 'permission-denied', reason: 'organization-settings' });

    const off = firstValueFrom(settings.setDatabaseAutoCase(DATABASE_ID, null));
    const offRequest = http.expectOne(apiDatabaseAutoCasePath(DATABASE_ID));
    expect(offRequest.request.body).toEqual({ caseTypeId: null });
    offRequest.flush(JSON.parse(AUTO_CASE_OFF_JSON));
    const cleared = await off;
    expect(cleared.status === 'ready' && cleared.data.caseTypeId).toBeNull();
    http.verify();
  });

  it('shows the server\'s case-type-in-use refusal when deactivating a type a database uses', async () => {
    const { settings, http } = apiRepositories();

    const refused = firstValueFrom(settings.updateCaseType(TYPE_ID, {
      name: '設備故障報修', description: '', defaultGroupId: GROUP_ID, defaultDueHours: 72, isActive: false,
    }));
    flushError(http, apiCaseTypePath(TYPE_ID), 'PUT', 422, 'Unprocessable Content', TYPE_IN_USE_422_JSON);
    expect(await refused).toEqual({
      status: 'validation-failed', message: caseTypeInUseMessage('設備故障報修', ['報修客戶資料庫']), fieldErrors: {},
    });
    http.verify();
  });

  it('reads the auto case: no creator, created by the database, nothing submitted, and the record withdrawn later', async () => {
    const { cases, http } = apiRepositories();

    const list = firstValueFrom(cases.list());
    http.expectOne((request) => request.url === '/api/v1/cases').flush(JSON.parse(CASES_LIST_JSON));
    const rows = await list;
    expect(rows.status === 'ready' && rows.data.map((row) => [row.title, row.origin, row.createdBy])).toEqual([
      ['報修客戶資料庫：新紀錄', 'database-submission', null],
    ]);

    for (const [body, state] of [[CASE_DETAIL_JSON, 'available'], [CASE_DETAIL_WITHDRAWN_JSON, 'withdrawn']] as const) {
      for (const value of ['王小明', '0912-345-678', '企業']) expect(body).not.toContain(value);
      const detail = firstValueFrom(cases.get(CASE_ID));
      http.expectOne(apiCasePath(CASE_ID)).flush(JSON.parse(body));
      const result = await detail;
      if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
      expect(result.data.case.description).toBe('由數據庫送出自動建立，內容請開啟紀錄查看。');
      expect(result.data.events.map((event) => [event.action, event.actor])).toEqual([['created', null]]);
      expect(result.data.links.record).toMatchObject({ databaseId: DATABASE_ID, state, canRead: false, databaseName: '報修客戶資料庫' });
      expect(caseCreatedByText(result.data.case, result.data.links.record)).toBe('由數據庫「報修客戶資料庫」自動建立');
      expect(result.data.allowedActions).toContain('accept');
    }
    expect(caseRecordStateLabel('withdrawn')).toBe('紀錄已撤回');

    const external = firstValueFrom(cases.get(CASE_ID));
    flushError(http, apiCasePath(CASE_ID), 'GET', 403, 'Forbidden', CASE_EXTERNAL_403_JSON);
    expect(await external).toMatchObject({ status: 'permission-denied', reason: 'case' });
    http.verify();
  });
});
