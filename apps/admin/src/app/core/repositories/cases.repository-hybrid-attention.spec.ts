import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import type { CaseAttentionView, CaseSummaryView } from '../domain/case.model';
import { DemoSessionService } from '../session/demo-session.service';
import { API_CASE_ATTENTION_PATH, API_CASES_PATH, CasesRepository } from './cases.repository';
import { API_DEMO_REPOSITORY_FACTORY } from './tokens';

// Recorded 2026-10-07 from the real API (issue #250): `dotnet run` of this branch on port 5262 against a
// throw-away PostgreSQL database `m7_250` (migrated, development seed `anxin`), signed in as `admin`,
// `internal` and `customer` through /connect/authorize + /connect/token, by `scratchpad/250/record.py`.
// Arranged through the API: 設備組 (member: internal), 採購組 (member: admin), four cases due in 72 hours —
// internal accepted 「冷藏庫溫度降不下來」; 「溫室感測器離線」 and 「灌溉馬達異音」 wait in 設備組; 「採購備用壓縮機」
// waits in 採購組. The API refuses a due time in the past (decision H), so psql then moved the due time of
// 「冷藏庫溫度降不下來」 and 「溫室感測器離線」 two hours into the past (what `case-set-due`, M7-11, will do).
// The bodies are pasted unchanged; never edit them to match the types.
/** internal-attention: HTTP 200. */
const INTERNAL_ATTENTION_JSON = `{"overdueCount":2,"ownedOverdueCount":1,"groupPendingOverdueCount":1,"pendingForMeCount":2}`;
/** admin-attention: HTTP 200. */
const ADMIN_ATTENTION_JSON = `{"overdueCount":0,"ownedOverdueCount":0,"groupPendingOverdueCount":0,"pendingForMeCount":1}`;
/** customer-attention-403: HTTP 403. */
const CUSTOMER_ATTENTION_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"case","message":"你沒有這個案件的存取權限，或它已不存在。案件功能只開放組織內部帳號使用。"}`;
/** internal-list-owned-overdue: HTTP 200. */
const INTERNAL_OWNED_OVERDUE_JSON = `[{"id":"01a11297-5d7b-7d46-8047-0965f0d3537b","title":"冷藏庫溫度降不下來","status":"in-progress","origin":"manual","type":{"id":"01a11297-5d66-73b6-82a2-c8d1ed98120b","name":"設備故障報修"},"group":{"id":"01a11297-5d05-73a4-a047-3b5aec2c361c","name":"設備組","archived":false},"createdBy":{"id":"01a11297-13c6-7cc0-99cd-9528d9af3784","displayName":"安心商行客服同仁"},"owner":{"id":"01a11297-13c6-7cc0-99cd-9528d9af3784","displayName":"安心商行客服同仁"},"dueAt":"2026-10-06T17:01:18.070723+00:00","createdAt":"2026-10-06T19:01:12.182234+00:00","updatedAt":"2026-10-06T19:01:12.284978+00:00"}]`;
/** internal-list-group-pending-overdue: HTTP 200. */
const INTERNAL_GROUP_PENDING_OVERDUE_JSON = `[{"id":"01a11297-5dc1-7e43-a177-b27972c81f5d","title":"溫室感測器離線","status":"pending","origin":"manual","type":{"id":"01a11297-5d66-73b6-82a2-c8d1ed98120b","name":"設備故障報修"},"group":{"id":"01a11297-5d05-73a4-a047-3b5aec2c361c","name":"設備組","archived":false},"createdBy":{"id":"01a11297-1387-758d-bc4e-86747201805d","displayName":"安心商行管理者"},"owner":null,"dueAt":"2026-10-06T17:01:18.070723+00:00","createdAt":"2026-10-06T19:01:12.256862+00:00","updatedAt":"2026-10-06T19:01:12.256862+00:00"}]`;
/** internal-list-group-pending: HTTP 200. */
const INTERNAL_GROUP_PENDING_JSON = `[{"id":"01a11297-5dc9-7557-bd70-0a3b41930d6a","title":"灌溉馬達異音","status":"pending","origin":"manual","type":{"id":"01a11297-5d66-73b6-82a2-c8d1ed98120b","name":"設備故障報修"},"group":{"id":"01a11297-5d05-73a4-a047-3b5aec2c361c","name":"設備組","archived":false},"createdBy":{"id":"01a11297-13c6-7cc0-99cd-9528d9af3784","displayName":"安心商行客服同仁"},"owner":null,"dueAt":"2026-10-09T19:01:12.262316+00:00","createdAt":"2026-10-06T19:01:12.264234+00:00","updatedAt":"2026-10-06T19:01:12.264234+00:00"},{"id":"01a11297-5dc1-7e43-a177-b27972c81f5d","title":"溫室感測器離線","status":"pending","origin":"manual","type":{"id":"01a11297-5d66-73b6-82a2-c8d1ed98120b","name":"設備故障報修"},"group":{"id":"01a11297-5d05-73a4-a047-3b5aec2c361c","name":"設備組","archived":false},"createdBy":{"id":"01a11297-1387-758d-bc4e-86747201805d","displayName":"安心商行管理者"},"owner":null,"dueAt":"2026-10-06T17:01:18.070723+00:00","createdAt":"2026-10-06T19:01:12.256862+00:00","updatedAt":"2026-10-06T19:01:12.256862+00:00"}]`;
/** admin-list-overdue: HTTP 200. */
const ADMIN_OVERDUE_JSON = `[{"id":"01a11297-5dc1-7e43-a177-b27972c81f5d","title":"溫室感測器離線","status":"pending","origin":"manual","type":{"id":"01a11297-5d66-73b6-82a2-c8d1ed98120b","name":"設備故障報修"},"group":{"id":"01a11297-5d05-73a4-a047-3b5aec2c361c","name":"設備組","archived":false},"createdBy":{"id":"01a11297-1387-758d-bc4e-86747201805d","displayName":"安心商行管理者"},"owner":null,"dueAt":"2026-10-06T17:01:18.070723+00:00","createdAt":"2026-10-06T19:01:12.256862+00:00","updatedAt":"2026-10-06T19:01:12.256862+00:00"},{"id":"01a11297-5d7b-7d46-8047-0965f0d3537b","title":"冷藏庫溫度降不下來","status":"in-progress","origin":"manual","type":{"id":"01a11297-5d66-73b6-82a2-c8d1ed98120b","name":"設備故障報修"},"group":{"id":"01a11297-5d05-73a4-a047-3b5aec2c361c","name":"設備組","archived":false},"createdBy":{"id":"01a11297-13c6-7cc0-99cd-9528d9af3784","displayName":"安心商行客服同仁"},"owner":{"id":"01a11297-13c6-7cc0-99cd-9528d9af3784","displayName":"安心商行客服同仁"},"dueAt":"2026-10-06T17:01:18.070723+00:00","createdAt":"2026-10-06T19:01:12.182234+00:00","updatedAt":"2026-10-06T19:01:12.284978+00:00"}]`;

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

describe('CasesRepository attention (API mode, recorded responses, issue #250)', () => {
  it('reads the attention counts: the case owner\'s overdue case plus the group\'s overdue pending one', async () => {
    const { repository, http } = apiRepository();

    const internal = firstValueFrom(repository.attention());
    const request = http.expectOne(API_CASE_ATTENTION_PATH);
    expect(request.request.method).toBe('GET');
    request.flush(JSON.parse(INTERNAL_ATTENTION_JSON));
    expect(await internal).toEqual({
      status: 'ready',
      data: { overdueCount: 2, ownedOverdueCount: 1, groupPendingOverdueCount: 1, pendingForMeCount: 2 } satisfies CaseAttentionView,
    });

    // The manager sees both overdue cases in the list but gets no count for being the manager.
    const admin = firstValueFrom(repository.attention());
    http.expectOne(API_CASE_ATTENTION_PATH).flush(JSON.parse(ADMIN_ATTENTION_JSON));
    expect(await admin).toEqual({
      status: 'ready',
      data: { overdueCount: 0, ownedOverdueCount: 0, groupPendingOverdueCount: 0, pendingForMeCount: 1 },
    });
    expect(titles(ADMIN_OVERDUE_JSON)).toEqual(['溫室感測器離線', '冷藏庫溫度降不下來']);
    http.verify();
  });

  it('turns an external customer\'s 403 into permission-denied', async () => {
    const { repository, http } = apiRepository();
    const customer = firstValueFrom(repository.attention());
    http.expectOne(API_CASE_ATTENTION_PATH).flush(JSON.parse(CUSTOMER_ATTENTION_403_JSON), { status: 403, statusText: 'Forbidden' });
    expect(await customer).toEqual({
      status: 'permission-denied',
      reason: 'case',
      message: '你沒有這個案件的存取權限，或它已不存在。案件功能只開放組織內部帳號使用。',
    });
    http.verify();
  });

  it('sends overdue=true, and the owned and my-groups pending overdue lists add up to the side navigation\'s number', async () => {
    const { repository, http } = apiRepository();

    const owned = firstValueFrom(repository.list({ scope: 'owned', overdue: true }));
    const ownedRequest = http.expectOne((candidate) => candidate.url === API_CASES_PATH);
    expect([ownedRequest.request.params.get('scope'), ownedRequest.request.params.get('overdue')]).toEqual(['owned', 'true']);
    ownedRequest.flush(JSON.parse(INTERNAL_OWNED_OVERDUE_JSON));

    const groupPending = firstValueFrom(repository.list({ scope: 'my-groups', status: 'pending', overdue: true }));
    const groupRequest = http.expectOne((candidate) => candidate.url === API_CASES_PATH);
    expect(groupRequest.request.params.keys().sort()).toEqual(['overdue', 'scope', 'status']);
    groupRequest.flush(JSON.parse(INTERNAL_GROUP_PENDING_OVERDUE_JSON));

    const ownedRows = await owned;
    const groupRows = await groupPending;
    if (ownedRows.status !== 'ready' || groupRows.status !== 'ready') throw new Error('expected ready');
    expect(ownedRows.data.map((row) => [row.title, row.status, row.owner?.displayName])).toEqual([['冷藏庫溫度降不下來', 'in-progress', '安心商行客服同仁']]);
    expect(groupRows.data.map((row) => [row.title, row.status, row.owner])).toEqual([['溫室感測器離線', 'pending', null]]);
    const attention = JSON.parse(INTERNAL_ATTENTION_JSON) as CaseAttentionView;
    expect(ownedRows.data.length + groupRows.data.length).toBe(attention.overdueCount);

    // 待我受理 is the my-groups pending list without the overdue filter; overdue=false is never sent.
    const pending = firstValueFrom(repository.list({ scope: 'my-groups', status: 'pending', overdue: false }));
    const pendingRequest = http.expectOne((candidate) => candidate.url === API_CASES_PATH);
    expect(pendingRequest.request.params.has('overdue')).toBe(false);
    pendingRequest.flush(JSON.parse(INTERNAL_GROUP_PENDING_JSON));
    const pendingRows = await pending;
    if (pendingRows.status !== 'ready') throw new Error(pendingRows.status);
    expect(pendingRows.data.map((row) => row.title)).toEqual(['灌溉馬達異音', '溫室感測器離線']);
    expect(pendingRows.data).toHaveLength(attention.pendingForMeCount);
    http.verify();
  });
});
