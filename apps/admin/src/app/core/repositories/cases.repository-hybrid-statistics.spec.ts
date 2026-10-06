import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import { formatHandlingHours, type CaseStatisticsView, type CaseSummaryView } from '../domain/case.model';
import { DemoSessionService } from '../session/demo-session.service';
import { API_CASE_STATISTICS_PATH, API_CASES_PATH, CasesRepository } from './cases.repository';
import { API_DEMO_REPOSITORY_FACTORY } from './tokens';

// Recorded 2026-10-07 from the real API (issue #251): `dotnet run` of this branch on port 5262 against a
// throw-away PostgreSQL database `m7_251` (migrated, development seed `anxin`), signed in as `admin`,
// `internal` and `customer` through /connect/authorize + /connect/token, by `scratchpad/251/record.py`.
// Arranged through the API (`scratchpad/251/record_arrange.py`): 設備組 (member: internal), 採購組 (member:
// admin), the type 設備故障報修. internal accepted and completed 「冷藏庫溫度降不下來」; internal accepted
// 「溫室感測器離線」 and transferred it to 採購組, where admin accepted and completed it; 「灌溉馬達異音」 waits in
// 設備組; admin created and cancelled 「採購備用壓縮機」 in 採購組; internal accepted 「水塔漏水」. The API stamps
// "now" and refuses a due time in the past (decision H), so psql then moved 「冷藏庫溫度降不下來」 to created 10
// hours ago / completed 1 hour ago (9 hours), 「溫室感測器離線」 to created 30 hours ago / completed 2 hours ago
// (28 hours), and the due time of 「灌溉馬達異音」 two hours into the past. The statistics' default range ended
// on the UTC day 2026-10-06. The bodies are pasted unchanged; never edit them to match the types.
/** admin-statistics: HTTP 200. */
const ADMIN_STATISTICS_JSON = `{"from":"2026-09-07","to":"2026-10-06","rows":[{"type":{"id":"01a112c3-1fca-74fb-b4b0-438ef5353eb2","name":"設備故障報修"},"group":{"id":"01a112c3-1f9e-78f3-93bc-9a47959a86af","name":"採購組","archived":false},"openCount":0,"overdueCount":0,"completedCount":1,"cancelledCount":1,"averageHandlingHours":28},{"type":{"id":"01a112c3-1fca-74fb-b4b0-438ef5353eb2","name":"設備故障報修"},"group":{"id":"01a112c3-1f66-77ad-97eb-73187e88fab7","name":"設備組","archived":false},"openCount":2,"overdueCount":1,"completedCount":1,"cancelledCount":0,"averageHandlingHours":9}]}`;
/** admin-statistics-empty-range: HTTP 200. */
const ADMIN_STATISTICS_EMPTY_RANGE_JSON = `{"from":"2026-05-01","to":"2026-05-31","rows":[{"type":{"id":"01a112c3-1fca-74fb-b4b0-438ef5353eb2","name":"設備故障報修"},"group":{"id":"01a112c3-1f66-77ad-97eb-73187e88fab7","name":"設備組","archived":false},"openCount":2,"overdueCount":1,"completedCount":0,"cancelledCount":0,"averageHandlingHours":null}]}`;
/** admin-statistics-422: HTTP 422. */
const ADMIN_STATISTICS_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"invalid-date-range","message":"起始日期必須不晚於結束日期。","errors":{"to":["起始日期必須不晚於結束日期。"]}}`;
/** internal-statistics-403: HTTP 403. */
const INTERNAL_STATISTICS_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"organization-settings","message":"只有管理者可以變更組織設定。"}`;
/** customer-statistics-403: HTTP 403. */
const CUSTOMER_STATISTICS_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"organization-settings","message":"只有管理者可以變更組織設定。"}`;
/** admin-list-equipment-open: HTTP 200. */
const ADMIN_LIST_EQUIPMENT_OPEN_JSON = `[{"id":"01a112c3-20b5-76c3-ba20-40b9d910a11b","title":"水塔漏水","status":"in-progress","origin":"manual","type":{"id":"01a112c3-1fca-74fb-b4b0-438ef5353eb2","name":"設備故障報修"},"group":{"id":"01a112c3-1f66-77ad-97eb-73187e88fab7","name":"設備組","archived":false},"createdBy":{"id":"01a112c2-8905-7d77-8547-549bc44f9557","displayName":"安心商行客服同仁"},"owner":{"id":"01a112c2-8905-7d77-8547-549bc44f9557","displayName":"安心商行客服同仁"},"dueAt":"2026-10-09T19:49:00.21106+00:00","createdAt":"2026-10-06T19:49:00.21297+00:00","updatedAt":"2026-10-06T19:49:00.229478+00:00"},{"id":"01a112c3-208e-7daa-9b5f-719f9f428e0c","title":"灌溉馬達異音","status":"pending","origin":"manual","type":{"id":"01a112c3-1fca-74fb-b4b0-438ef5353eb2","name":"設備故障報修"},"group":{"id":"01a112c3-1f66-77ad-97eb-73187e88fab7","name":"設備組","archived":false},"createdBy":{"id":"01a112c2-8905-7d77-8547-549bc44f9557","displayName":"安心商行客服同仁"},"owner":null,"dueAt":"2026-10-06T17:49:09.527042+00:00","createdAt":"2026-10-06T19:49:00.17405+00:00","updatedAt":"2026-10-06T19:49:00.17405+00:00"}]`;
/** admin-list-equipment-overdue: HTTP 200. */
const ADMIN_LIST_EQUIPMENT_OVERDUE_JSON = `[{"id":"01a112c3-208e-7daa-9b5f-719f9f428e0c","title":"灌溉馬達異音","status":"pending","origin":"manual","type":{"id":"01a112c3-1fca-74fb-b4b0-438ef5353eb2","name":"設備故障報修"},"group":{"id":"01a112c3-1f66-77ad-97eb-73187e88fab7","name":"設備組","archived":false},"createdBy":{"id":"01a112c2-8905-7d77-8547-549bc44f9557","displayName":"安心商行客服同仁"},"owner":null,"dueAt":"2026-10-06T17:49:09.527042+00:00","createdAt":"2026-10-06T19:49:00.17405+00:00","updatedAt":"2026-10-06T19:49:00.17405+00:00"}]`;
/** admin-list-equipment-completed: HTTP 200. */
const ADMIN_LIST_EQUIPMENT_COMPLETED_JSON = `[{"id":"01a112c3-1fdf-7b33-bb4c-c348ecf3f078","title":"冷藏庫溫度降不下來","status":"completed","origin":"manual","type":{"id":"01a112c3-1fca-74fb-b4b0-438ef5353eb2","name":"設備故障報修"},"group":{"id":"01a112c3-1f66-77ad-97eb-73187e88fab7","name":"設備組","archived":false},"createdBy":{"id":"01a112c2-8905-7d77-8547-549bc44f9557","displayName":"安心商行客服同仁"},"owner":{"id":"01a112c2-8905-7d77-8547-549bc44f9557","displayName":"安心商行客服同仁"},"dueAt":"2026-10-09T19:48:59.989245+00:00","createdAt":"2026-10-06T09:49:09.525742+00:00","updatedAt":"2026-10-06T19:49:00.102065+00:00"}]`;
/** admin-list-equipment-cancelled: HTTP 200. */
const ADMIN_LIST_EQUIPMENT_CANCELLED_JSON = `[]`;
/** admin-list-purchasing-open: HTTP 200. */
const ADMIN_LIST_PURCHASING_OPEN_JSON = `[]`;
/** admin-list-purchasing-overdue: HTTP 200. */
const ADMIN_LIST_PURCHASING_OVERDUE_JSON = `[]`;
/** admin-list-purchasing-completed: HTTP 200. */
const ADMIN_LIST_PURCHASING_COMPLETED_JSON = `[{"id":"01a112c3-204d-744e-bcd6-03b93d76c59d","title":"溫室感測器離線","status":"completed","origin":"manual","type":{"id":"01a112c3-1fca-74fb-b4b0-438ef5353eb2","name":"設備故障報修"},"group":{"id":"01a112c3-1f9e-78f3-93bc-9a47959a86af","name":"採購組","archived":false},"createdBy":{"id":"01a112c2-8905-7d77-8547-549bc44f9557","displayName":"安心商行客服同仁"},"owner":{"id":"01a112c2-88cd-7e8b-99d7-1ad17c03fcdf","displayName":"安心商行管理者"},"dueAt":"2026-10-09T19:49:00.106916+00:00","createdAt":"2026-10-05T13:49:09.526755+00:00","updatedAt":"2026-10-06T19:49:00.168211+00:00"}]`;
/** admin-list-purchasing-cancelled: HTTP 200. */
const ADMIN_LIST_PURCHASING_CANCELLED_JSON = `[{"id":"01a112c3-2095-72c8-807f-6a6c765cdd53","title":"採購備用壓縮機","status":"cancelled","origin":"manual","type":{"id":"01a112c3-1fca-74fb-b4b0-438ef5353eb2","name":"設備故障報修"},"group":{"id":"01a112c3-1f9e-78f3-93bc-9a47959a86af","name":"採購組","archived":false},"createdBy":{"id":"01a112c2-88cd-7e8b-99d7-1ad17c03fcdf","displayName":"安心商行管理者"},"owner":null,"dueAt":"2026-10-09T19:49:00.179112+00:00","createdAt":"2026-10-06T19:49:00.180859+00:00","updatedAt":"2026-10-06T19:49:00.206788+00:00"}]`;

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

function titles(raw: string): string[] {
  return (JSON.parse(raw) as CaseSummaryView[]).map((row) => row.title);
}

describe('CasesRepository statistics (API mode, recorded responses, issue #251)', () => {
  it('reads the rows: the transferred case counts for 採購組, the average is creation to completion, cancelled never averages', async () => {
    const { repository, http } = apiRepository();

    const result = firstValueFrom(repository.statistics());
    const request = http.expectOne((candidate) => candidate.url === API_CASE_STATISTICS_PATH);
    expect(request.request.method).toBe('GET');
    expect(request.request.params.keys()).toEqual([]);
    request.flush(JSON.parse(ADMIN_STATISTICS_JSON));
    const view = await result;
    if (view.status !== 'ready') throw new Error(view.status);
    expect([view.data.from, view.data.to]).toEqual(['2026-09-07', '2026-10-06']);
    expect(view.data.rows.map((row) => [
      row.type.name, row.group.name, row.openCount, row.overdueCount, row.completedCount, row.cancelledCount, row.averageHandlingHours,
    ])).toEqual([
      ['設備故障報修', '採購組', 0, 0, 1, 1, 28],
      ['設備故障報修', '設備組', 2, 1, 1, 0, 9],
    ]);
    expect(view.data.rows.map((row) => formatHandlingHours(row.averageHandlingHours))).toEqual(['28.0 小時', '9.0 小時']);
    // Names and numbers only: no case title, description, resolution or note anywhere in the body.
    for (const text of ['冷藏庫', '溫室感測器', '灌溉馬達', '壓縮機', '水塔', '說明', '已更換']) {
      expect(ADMIN_STATISTICS_JSON).not.toContain(text);
    }
    http.verify();
  });

  it('sends the range and shows 「—」 when nothing was completed within it', async () => {
    const { repository, http } = apiRepository();
    const result = firstValueFrom(repository.statistics({ from: '2026-05-01', to: '2026-05-31' }));
    const request = http.expectOne((candidate) => candidate.url === API_CASE_STATISTICS_PATH);
    expect([request.request.params.get('from'), request.request.params.get('to')]).toEqual(['2026-05-01', '2026-05-31']);
    request.flush(JSON.parse(ADMIN_STATISTICS_EMPTY_RANGE_JSON));
    const view = await result;
    if (view.status !== 'ready') throw new Error(view.status);
    const row = (view.data satisfies CaseStatisticsView).rows[0];
    expect([row.openCount, row.overdueCount, row.completedCount, row.cancelledCount]).toEqual([2, 1, 0, 0]);
    expect(row.averageHandlingHours).toBeNull();
    expect(formatHandlingHours(row.averageHandlingHours)).toBe('—');
    http.verify();
  });

  it('turns a non-manager\'s 403 organization-settings into permission-denied and an invalid range into validation-failed', async () => {
    const { repository, http } = apiRepository();
    for (const raw of [INTERNAL_STATISTICS_403_JSON, CUSTOMER_STATISTICS_403_JSON]) {
      const denied = firstValueFrom(repository.statistics());
      http.expectOne((candidate) => candidate.url === API_CASE_STATISTICS_PATH).flush(JSON.parse(raw), { status: 403, statusText: 'Forbidden' });
      expect(await denied).toEqual({ status: 'permission-denied', reason: 'organization-settings', message: '只有管理者可以變更組織設定。' });
    }

    const invalid = firstValueFrom(repository.statistics({ from: '2026-10-07', to: '2026-01-01' }));
    http.expectOne((candidate) => candidate.url === API_CASE_STATISTICS_PATH)
      .flush(JSON.parse(ADMIN_STATISTICS_422_JSON), { status: 422, statusText: 'Unprocessable Content' });
    expect(await invalid).toMatchObject({ status: 'validation-failed', reason: 'invalid-date-range', message: '起始日期必須不晚於結束日期。' });
    http.verify();
  });

  it('opens each number as a filtered list whose length equals it', async () => {
    const { repository, http } = apiRepository();
    const statistics = JSON.parse(ADMIN_STATISTICS_JSON) as CaseStatisticsView;
    const lists: Readonly<Record<string, Readonly<Record<'open' | 'overdue' | 'completed' | 'cancelled', string>>>> = {
      設備組: {
        open: ADMIN_LIST_EQUIPMENT_OPEN_JSON,
        overdue: ADMIN_LIST_EQUIPMENT_OVERDUE_JSON,
        completed: ADMIN_LIST_EQUIPMENT_COMPLETED_JSON,
        cancelled: ADMIN_LIST_EQUIPMENT_CANCELLED_JSON,
      },
      採購組: {
        open: ADMIN_LIST_PURCHASING_OPEN_JSON,
        overdue: ADMIN_LIST_PURCHASING_OVERDUE_JSON,
        completed: ADMIN_LIST_PURCHASING_COMPLETED_JSON,
        cancelled: ADMIN_LIST_PURCHASING_CANCELLED_JSON,
      },
    };
    for (const row of statistics.rows) {
      const recorded = lists[row.group.name];
      const expected = { open: row.openCount, overdue: row.overdueCount, completed: row.completedCount, cancelled: row.cancelledCount };
      for (const measure of ['open', 'overdue', 'completed', 'cancelled'] as const) {
        const closed = measure === 'completed' || measure === 'cancelled';
        const result = firstValueFrom(repository.list({
          scope: 'all',
          status: closed ? measure : 'open',
          typeId: row.type.id,
          groupId: row.group.id,
          ...(measure === 'overdue' ? { overdue: true } : {}),
          ...(closed ? { closedFrom: statistics.from, closedTo: statistics.to } : {}),
        }));
        const request = http.expectOne((candidate) => candidate.url === API_CASES_PATH);
        expect(request.request.params.get('typeId')).toBe(row.type.id);
        expect(request.request.params.get('groupId')).toBe(row.group.id);
        expect(request.request.params.get('closedFrom')).toBe(closed ? statistics.from : null);
        expect(request.request.params.get('closedTo')).toBe(closed ? statistics.to : null);
        request.flush(JSON.parse(recorded[measure]));
        const rows = await result;
        if (rows.status !== 'ready') throw new Error(rows.status);
        expect(rows.data, `${row.group.name} ${measure}`).toHaveLength(expected[measure]);
      }
    }
    // The transferred case was completed in 採購組 and is listed there, not in 設備組.
    expect(titles(ADMIN_LIST_PURCHASING_COMPLETED_JSON)).toEqual(['溫室感測器離線']);
    expect(titles(ADMIN_LIST_EQUIPMENT_COMPLETED_JSON)).toEqual(['冷藏庫溫度降不下來']);
    expect(titles(ADMIN_LIST_EQUIPMENT_OVERDUE_JSON)).toEqual(['灌溉馬達異音']);
    http.verify();
  });
});
