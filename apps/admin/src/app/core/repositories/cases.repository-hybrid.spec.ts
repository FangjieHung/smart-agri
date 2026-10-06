import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import type { CaseDetailView, CaseSummaryView, CreateCaseRequest } from '../domain/case.model';
import { DemoSessionService } from '../session/demo-session.service';
import { API_CASES_PATH, apiCasePath, CasesRepository } from './cases.repository';
import { API_DEMO_REPOSITORY_FACTORY } from './tokens';

// Recorded 2026-10-07 from the real API (issue #248): `dotnet run` of this branch on port 5262 against
// a throw-away PostgreSQL database `m7_248` (migrated, development seed `anxin`, plus one assistant owned
// by `internal` inserted with psql so it could keep a conversation), signed in as `admin`, `internal` and
// `customer` through /connect/authorize + /connect/token, by `scratchpad/248/record.py`. The bodies are
// pasted unchanged; never edit them to match the types. The thread is the one `internal` created through
// POST …/chat/conversations; `internal-detail-thread-deleted` is after DELETE …/conversations/{id}.
/** internal-create: HTTP 201. */
const CREATE_JSON = `{"case":{"id":"01a111fc-ac0a-7f27-8757-51fb96db3a71","title":"冷藏庫溫度降不下來","description":"二號冷藏庫從早上開始維持在 9 度。","status":"pending","origin":"manual","type":{"id":"01a111fc-aba4-7f02-b578-2a8b2efcb0a7","name":"設備故障報修"},"group":{"id":"01a111fc-ab32-74c5-a124-a847aff353a7","name":"設備組","archived":false},"createdBy":{"id":"01a111fc-7b6a-73b3-85f8-1d588b31c4cf","displayName":"安心商行客服同仁"},"owner":null,"dueAt":"2026-10-09T16:12:14+00:00","resolution":null,"cancelReason":null,"createdAt":"2026-10-06T16:12:14.210442+00:00","updatedAt":"2026-10-06T16:12:14.210442+00:00","acceptedAt":null,"completedAt":null,"cancelledAt":null,"eventCount":1},"events":[{"id":"01a111fc-ac0a-72a4-a9da-9a4cdf9c9eb7","ordinal":1,"action":"created","actor":{"id":"01a111fc-7b6a-73b3-85f8-1d588b31c4cf","displayName":"安心商行客服同仁"},"at":"2026-10-06T16:12:14.210442+00:00","note":null,"status":"pending","owner":null,"fromGroup":null,"toGroup":{"id":"01a111fc-ab32-74c5-a124-a847aff353a7","name":"設備組","archived":false},"dueAt":"2026-10-09T16:12:14+00:00"}],"links":{"record":null,"thread":{"assistantId":"0199f000-0000-7000-8000-0000000a0248","threadId":"01a111fc-abeb-7167-b18a-458c43674ce1","canOpen":true},"assistantIssue":null,"previousCase":null}}`;
/** internal-create-due-in-past-422: HTTP 422. */
const DUE_IN_PAST_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"due-in-past","message":"時限不能早於現在。","errors":{"dueAt":["時限不能早於現在。"]}}`;
/** internal-create-inactive-type-422: HTTP 422. */
const INACTIVE_TYPE_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"case-type-inactive","message":"這個案件類型已停用或不存在，請選擇其他類型。","errors":{"typeId":["這個案件類型已停用或不存在，請選擇其他類型。"]}}`;
/** internal-create-archived-group-422: HTTP 422. */
const ARCHIVED_GROUP_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"case-group-archived","message":"這個承辦組已封存，請選擇其他承辦組。","errors":{"groupId":["這個承辦組已封存，請選擇其他承辦組。"]}}`;
/** internal-create-blank-422: HTTP 422. */
const BLANK_TITLE_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"message":"請輸入案件標題。","errors":{"title":["請輸入案件標題。"]}}`;
/** admin-create-other-thread-422: HTTP 422. */
const OTHER_THREAD_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"link-not-available","message":"只能連結你自己的對話，而且對話必須還在。","errors":{"threadId":["只能連結你自己的對話，而且對話必須還在。"]}}`;
/** internal-create-unreadable-record-422: HTTP 422. */
const UNREADABLE_RECORD_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"link-not-available","message":"這筆紀錄目前無法連結：你沒有讀取它的權限，或它已撤回、不存在。","errors":{"submissionId":["這筆紀錄目前無法連結：你沒有讀取它的權限，或它已撤回、不存在。"]}}`;
/** internal-list: HTTP 200. */
const INTERNAL_LIST_JSON = `[{"id":"01a111fc-ac0a-7f27-8757-51fb96db3a71","title":"冷藏庫溫度降不下來","status":"pending","origin":"manual","type":{"id":"01a111fc-aba4-7f02-b578-2a8b2efcb0a7","name":"設備故障報修"},"group":{"id":"01a111fc-ab32-74c5-a124-a847aff353a7","name":"設備組","archived":false},"createdBy":{"id":"01a111fc-7b6a-73b3-85f8-1d588b31c4cf","displayName":"安心商行客服同仁"},"owner":null,"dueAt":"2026-10-09T16:12:14+00:00","createdAt":"2026-10-06T16:12:14.210442+00:00","updatedAt":"2026-10-06T16:12:14.210442+00:00"}]`;
/** internal-list-closed: HTTP 200. */
const INTERNAL_LIST_CLOSED_JSON = `[]`;
/** internal-detail-other-403: HTTP 403. */
const DETAIL_OTHER_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"case","message":"你沒有這個案件的存取權限，或它已不存在。案件功能只開放組織內部帳號使用。"}`;
/** admin-list-purchasing: HTTP 200. */
const ADMIN_LIST_PURCHASING_JSON = `[{"id":"01a111fc-acc9-768c-afdb-d631215a5f54","title":"採購備用壓縮機","status":"pending","origin":"manual","type":{"id":"01a111fc-aba4-7f02-b578-2a8b2efcb0a7","name":"設備故障報修"},"group":{"id":"01a111fc-ab69-79bb-91f5-c2b7b0b33a68","name":"採購組","archived":false},"createdBy":{"id":"01a111fc-7b31-71f3-a793-77c049a9ca29","displayName":"安心商行管理者"},"owner":null,"dueAt":"2026-10-09T16:12:14+00:00","createdAt":"2026-10-06T16:12:14.405958+00:00","updatedAt":"2026-10-06T16:12:14.405958+00:00"}]`;
/** admin-detail-record: HTTP 200. */
const ADMIN_DETAIL_RECORD_JSON = `{"case":{"id":"01a111fc-acc9-768c-afdb-d631215a5f54","title":"採購備用壓縮機","description":"二號冷藏庫從早上開始維持在 9 度。","status":"pending","origin":"manual","type":{"id":"01a111fc-aba4-7f02-b578-2a8b2efcb0a7","name":"設備故障報修"},"group":{"id":"01a111fc-ab69-79bb-91f5-c2b7b0b33a68","name":"採購組","archived":false},"createdBy":{"id":"01a111fc-7b31-71f3-a793-77c049a9ca29","displayName":"安心商行管理者"},"owner":null,"dueAt":"2026-10-09T16:12:14+00:00","resolution":null,"cancelReason":null,"createdAt":"2026-10-06T16:12:14.405958+00:00","updatedAt":"2026-10-06T16:12:14.405958+00:00","acceptedAt":null,"completedAt":null,"cancelledAt":null,"eventCount":1},"events":[{"id":"01a111fc-acc9-7938-8e0f-c6d96812a3a7","ordinal":1,"action":"created","actor":{"id":"01a111fc-7b31-71f3-a793-77c049a9ca29","displayName":"安心商行管理者"},"at":"2026-10-06T16:12:14.405958+00:00","note":null,"status":"pending","owner":null,"fromGroup":null,"toGroup":{"id":"01a111fc-ab69-79bb-91f5-c2b7b0b33a68","name":"採購組","archived":false},"dueAt":"2026-10-09T16:12:14+00:00"}],"links":{"record":{"databaseId":"01a111fc-ac5e-7916-97b3-1ca84cb061dc","submissionId":"01a111fc-ac87-7580-bf56-f3a881d96107","state":"available","canRead":true},"thread":null,"assistantIssue":null,"previousCase":null}}`;
/** external-list-403: HTTP 403. */
const EXTERNAL_LIST_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"case","message":"你沒有這個案件的存取權限，或它已不存在。案件功能只開放組織內部帳號使用。"}`;
/** external-create-403: HTTP 403. */
const EXTERNAL_CREATE_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"case","message":"你沒有這個案件的存取權限，或它已不存在。案件功能只開放組織內部帳號使用。"}`;
/** internal-detail-thread-deleted: HTTP 200. */
const DETAIL_THREAD_DELETED_JSON = `{"case":{"id":"01a111fc-ac0a-7f27-8757-51fb96db3a71","title":"冷藏庫溫度降不下來","description":"二號冷藏庫從早上開始維持在 9 度。","status":"pending","origin":"manual","type":{"id":"01a111fc-aba4-7f02-b578-2a8b2efcb0a7","name":"設備故障報修"},"group":{"id":"01a111fc-ab32-74c5-a124-a847aff353a7","name":"設備組","archived":false},"createdBy":{"id":"01a111fc-7b6a-73b3-85f8-1d588b31c4cf","displayName":"安心商行客服同仁"},"owner":null,"dueAt":"2026-10-09T16:12:14+00:00","resolution":null,"cancelReason":null,"createdAt":"2026-10-06T16:12:14.210442+00:00","updatedAt":"2026-10-06T16:12:14.210442+00:00","acceptedAt":null,"completedAt":null,"cancelledAt":null,"eventCount":1},"events":[{"id":"01a111fc-ac0a-72a4-a9da-9a4cdf9c9eb7","ordinal":1,"action":"created","actor":{"id":"01a111fc-7b6a-73b3-85f8-1d588b31c4cf","displayName":"安心商行客服同仁"},"at":"2026-10-06T16:12:14.210442+00:00","note":null,"status":"pending","owner":null,"fromGroup":null,"toGroup":{"id":"01a111fc-ab32-74c5-a124-a847aff353a7","name":"設備組","archived":false},"dueAt":"2026-10-09T16:12:14+00:00"}],"links":{"record":null,"thread":{"assistantId":"0199f000-0000-7000-8000-0000000a0248","threadId":"01a111fc-abeb-7167-b18a-458c43674ce1","canOpen":false},"assistantIssue":null,"previousCase":null}}`;

function apiRepository() {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: API_DEMO_REPOSITORY_FACTORY, useValue: () => null },
      { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>('ignored-in-api-mode') } },
    ],
  });
  return { repository: TestBed.inject(CasesRepository), http: TestBed.inject(HttpTestingController) };
}

function flushError(http: HttpTestingController, url: string, status: number, statusText: string, body: string): void {
  http.expectOne((request) => request.url === url).flush(JSON.parse(body), { status, statusText });
}

const created = JSON.parse(CREATE_JSON) as CaseDetailView;
const request: CreateCaseRequest = {
  typeId: created.case.type.id,
  groupId: created.case.group.id,
  dueAt: created.case.dueAt,
  title: created.case.title,
  description: created.case.description,
  assistantId: created.links.thread?.assistantId,
  threadId: created.links.thread?.threadId,
};

const DENIED_MESSAGE = '你沒有這個案件的存取權限，或它已不存在。案件功能只開放組織內部帳號使用。';

describe('CasesRepository (API mode, recorded responses)', () => {
  it('creates a case and reads it back with its created event and an openable thread link', async () => {
    const { repository, http } = apiRepository();

    const create = firstValueFrom(repository.create(request));
    const post = http.expectOne(API_CASES_PATH);
    expect(post.request.method).toBe('POST');
    expect(post.request.body).toEqual(request);
    post.flush(JSON.parse(CREATE_JSON));
    const result = await create;
    if (result.status !== 'ready') throw new Error(result.status);
    expect(result.data.case).toMatchObject({ title: '冷藏庫溫度降不下來', status: 'pending', origin: 'manual', owner: null, eventCount: 1 });
    expect(result.data.case.createdBy?.displayName).toBe('安心商行客服同仁');
    expect(result.data.events.map((event) => [event.action, event.status, event.toGroup?.name])).toEqual([['created', 'pending', '設備組']]);
    expect(result.data.links.thread?.canOpen).toBe(true);
    http.verify();
  });

  it('never carries conversation text, and a deleted thread can no longer be opened', async () => {
    const { repository, http } = apiRepository();
    for (const raw of [CREATE_JSON, DETAIL_THREAD_DELETED_JSON]) {
      // The raw JSON holds no title, messages or anything else of the thread: only its ids and canOpen.
      expect(Object.keys(JSON.parse(raw).links.thread)).toEqual(['assistantId', 'threadId', 'canOpen']);
      expect(raw).not.toContain('新的對話');
    }

    const get = firstValueFrom(repository.get(created.case.id));
    http.expectOne(apiCasePath(created.case.id)).flush(JSON.parse(DETAIL_THREAD_DELETED_JSON));
    const detail = await get;
    if (detail.status !== 'ready') throw new Error(detail.status);
    expect(detail.data.links.thread).toEqual({ ...created.links.thread, canOpen: false });
    http.verify();
  });

  it('lists with the filters as query parameters, open cases by default', async () => {
    const { repository, http } = apiRepository();

    const list = firstValueFrom(repository.list());
    const plain = http.expectOne((candidate) => candidate.url === API_CASES_PATH);
    expect(plain.request.params.keys()).toEqual([]);
    plain.flush(JSON.parse(INTERNAL_LIST_JSON));
    const rows = await list;
    if (rows.status !== 'ready') throw new Error(rows.status);
    expect(rows.data.map((row: CaseSummaryView) => [row.title, row.status, row.group.name])).toEqual([['冷藏庫溫度降不下來', 'pending', '設備組']]);

    const closed = firstValueFrom(repository.list({ status: 'closed' }));
    const closedRequest = http.expectOne((candidate) => candidate.url === API_CASES_PATH);
    expect(closedRequest.request.params.get('status')).toBe('closed');
    closedRequest.flush(JSON.parse(INTERNAL_LIST_CLOSED_JSON));
    expect(await closed).toEqual({ status: 'ready', data: [] });

    const purchasing = JSON.parse(ADMIN_LIST_PURCHASING_JSON) as CaseSummaryView[];
    const filtered = firstValueFrom(repository.list({ scope: 'all', status: 'open', groupId: purchasing[0].group.id }));
    const filteredRequest = http.expectOne((candidate) => candidate.url === API_CASES_PATH);
    expect(filteredRequest.request.params.get('scope')).toBe('all');
    expect(filteredRequest.request.params.get('groupId')).toBe(purchasing[0].group.id);
    expect(filteredRequest.request.params.has('typeId')).toBe(false);
    filteredRequest.flush(purchasing);
    const filteredRows = await filtered;
    if (filteredRows.status !== 'ready') throw new Error(filteredRows.status);
    expect(filteredRows.data.map((row) => row.title)).toEqual(['採購備用壓縮機']);
    http.verify();
  });

  it('shows a linked record\'s state and whether the caller may read it', async () => {
    const { repository, http } = apiRepository();
    const detail = JSON.parse(ADMIN_DETAIL_RECORD_JSON) as CaseDetailView;

    const get = firstValueFrom(repository.get(detail.case.id));
    http.expectOne(apiCasePath(detail.case.id)).flush(detail);
    const result = await get;
    if (result.status !== 'ready') throw new Error(result.status);
    expect(result.data.links.record).toMatchObject({ state: 'available', canRead: true });
    expect(result.data.links.thread).toBeNull();
    http.verify();
  });

  it('turns every 403 case — someone else\'s case, an external customer — into the same permission-denied', async () => {
    const { repository, http } = apiRepository();
    expect(DETAIL_OTHER_403_JSON).toBe(EXTERNAL_LIST_403_JSON);
    expect(EXTERNAL_CREATE_403_JSON).toBe(EXTERNAL_LIST_403_JSON);
    const denied = { status: 'permission-denied', reason: 'case', message: DENIED_MESSAGE };

    const other = firstValueFrom(repository.get('01a111fc-0000-7000-8000-000000000000'));
    flushError(http, apiCasePath('01a111fc-0000-7000-8000-000000000000'), 403, 'Forbidden', DETAIL_OTHER_403_JSON);
    expect(await other).toEqual(denied);

    const list = firstValueFrom(repository.list());
    flushError(http, API_CASES_PATH, 403, 'Forbidden', EXTERNAL_LIST_403_JSON);
    expect(await list).toEqual(denied);

    const create = firstValueFrom(repository.create(request));
    flushError(http, API_CASES_PATH, 403, 'Forbidden', EXTERNAL_CREATE_403_JSON);
    expect(await create).toEqual(denied);
    http.verify();
  });

  it('turns each 422 into validation-failed with its reason under its field', async () => {
    const { repository, http } = apiRepository();
    const cases: readonly (readonly [string, string | null, string, string])[] = [
      [DUE_IN_PAST_422_JSON, 'due-in-past', 'dueAt', '時限不能早於現在。'],
      [INACTIVE_TYPE_422_JSON, 'case-type-inactive', 'typeId', '這個案件類型已停用或不存在，請選擇其他類型。'],
      [ARCHIVED_GROUP_422_JSON, 'case-group-archived', 'groupId', '這個承辦組已封存，請選擇其他承辦組。'],
      [BLANK_TITLE_422_JSON, null, 'title', '請輸入案件標題。'],
      [OTHER_THREAD_422_JSON, 'link-not-available', 'threadId', '只能連結你自己的對話，而且對話必須還在。'],
      [UNREADABLE_RECORD_422_JSON, 'link-not-available', 'submissionId', '這筆紀錄目前無法連結：你沒有讀取它的權限，或它已撤回、不存在。'],
    ];
    for (const [body, reason, field, message] of cases) {
      const create = firstValueFrom(repository.create(request));
      flushError(http, API_CASES_PATH, 422, 'Unprocessable Content', body);
      expect(await create).toEqual({ status: 'validation-failed', reason, message, fieldErrors: { [field]: message } });
    }
    http.verify();
  });
});
