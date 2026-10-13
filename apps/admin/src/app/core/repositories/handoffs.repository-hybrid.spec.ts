import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import type { VisitorHandoffView } from '../domain/handoff.model';
import { DemoSessionService } from '../session/demo-session.service';
import {
  API_HANDOFF_ATTENTION_PATH,
  API_HANDOFFS_PATH,
  API_NOTIFICATION_SETTINGS_PATH,
  apiHandoffHandlePath,
  HandoffsRepository,
} from './handoffs.repository';
import { API_DEMO_REPOSITORY_FACTORY } from './tokens';

// Recorded 2026-10-13 from the real API (issue #355): this branch's Api in its integration test host
// (`HandoffHostFixture`: real endpoints, serializer and PostgreSQL 18 via Testcontainers) through a
// temporary probe test that is not committed. Organization 「安心商行」: the admin owns two assistants named
// 「退貨小幫手」, one published on the website (a visitor's refusal), one on LINE (a LINE user's refusal);
// `internal` is a member who owns nothing. The bodies are pasted unchanged; never edit them to match the types.
/** admin-list-pending: HTTP 200. */
const ADMIN_LIST_PENDING_JSON = `[{"id":"01a1346d-ced6-7b7a-9ce2-813bcab2a8f5","assistantId":"01a1346d-ce7e-7229-91de-e00c6cde352a","assistantName":"退貨小幫手","channel":"line","createdAt":"2026-10-13T08:42:54.038849+00:00","handledAt":null,"handledByAccountId":null,"handledByName":null},{"id":"01a1346d-ce46-7cce-9e97-941311c15e72","assistantId":"01a1346d-cd2f-7e26-a5ce-13fa6937d2f2","assistantName":"退貨小幫手","channel":"website","createdAt":"2026-10-13T08:42:53.894403+00:00","handledAt":null,"handledByAccountId":null,"handledByName":null}]`;
/** admin-attention: HTTP 200. */
const ADMIN_ATTENTION_JSON = `{"pendingCount":2}`;
/** internal-attention: HTTP 200. */
const INTERNAL_ATTENTION_JSON = `{"pendingCount":0}`;
/** internal-handle-403: HTTP 403. */
const INTERNAL_HANDLE_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"handoff","message":"你沒有這則轉真人通知的存取權限，或它已不存在。轉真人通知只給助理的擁有者。"}`;
/** admin-handle: HTTP 200. */
const ADMIN_HANDLE_JSON = `{"id":"01a1346d-ced6-7b7a-9ce2-813bcab2a8f5","assistantId":"01a1346d-ce7e-7229-91de-e00c6cde352a","assistantName":"退貨小幫手","channel":"line","createdAt":"2026-10-13T08:42:54.038849+00:00","handledAt":"2026-10-13T08:42:54.169036+00:00","handledByAccountId":"01a1346d-c6c1-7a41-a6a4-7201fb375b87","handledByName":"安心商行管理者"}`;
/** admin-list-handled: HTTP 200. */
const ADMIN_LIST_HANDLED_JSON = `[{"id":"01a1346d-ced6-7b7a-9ce2-813bcab2a8f5","assistantId":"01a1346d-ce7e-7229-91de-e00c6cde352a","assistantName":"退貨小幫手","channel":"line","createdAt":"2026-10-13T08:42:54.038849+00:00","handledAt":"2026-10-13T08:42:54.169036+00:00","handledByAccountId":"01a1346d-c6c1-7a41-a6a4-7201fb375b87","handledByName":"安心商行管理者"}]`;
/** admin-attention-after: HTTP 200. */
const ADMIN_ATTENTION_AFTER_JSON = `{"pendingCount":1}`;
/** admin-settings-default: HTTP 200. */
const ADMIN_SETTINGS_DEFAULT_JSON = `{"email":null,"handoffEmails":true,"emailDeliveryConfigured":true}`;
/** admin-settings-put-invalid: HTTP 422. */
const ADMIN_SETTINGS_PUT_INVALID_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"message":"請輸入有效的 Email 地址，例如 name@example.com。","errors":{"email":["請輸入有效的 Email 地址，例如 name@example.com。"]}}`;
/** admin-settings-put: HTTP 200. */
const ADMIN_SETTINGS_PUT_JSON = `{"email":"owner@shop.example.org","handoffEmails":false,"emailDeliveryConfigured":true}`;

function apiRepository() {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: API_DEMO_REPOSITORY_FACTORY, useValue: () => null },
      { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>('ignored-in-api-mode') } },
    ],
  });
  return { repository: TestBed.inject(HandoffsRepository), http: TestBed.inject(HttpTestingController) };
}

describe('HandoffsRepository (API mode, recorded responses, issue #355)', () => {
  it('lists the owner\'s pending handoffs and the side-nav count', async () => {
    const { repository, http } = apiRepository();

    const pending = firstValueFrom(repository.list());
    const request = http.expectOne((candidate) => candidate.url === API_HANDOFFS_PATH);
    expect(request.request.method).toBe('GET');
    expect(request.request.params.get('status')).toBe('pending');
    request.flush(JSON.parse(ADMIN_LIST_PENDING_JSON));
    const view = await pending;
    expect(view.status).toBe('ready');
    const rows = view.status === 'ready' ? view.data : [];
    expect(rows.map((row) => [row.channel, row.assistantName, row.handledAt, row.handledByName])).toEqual([
      ['line', '退貨小幫手', null, null],
      ['website', '退貨小幫手', null, null],
    ]);

    const attention = firstValueFrom(repository.attention());
    http.expectOne(API_HANDOFF_ATTENTION_PATH).flush(JSON.parse(ADMIN_ATTENTION_JSON));
    expect(await attention).toEqual({ status: 'ready', data: { pendingCount: 2 } });

    const member = firstValueFrom(repository.attention());
    http.expectOne(API_HANDOFF_ATTENTION_PATH).flush(JSON.parse(INTERNAL_ATTENTION_JSON));
    expect(await member).toEqual({ status: 'ready', data: { pendingCount: 0 } });
    http.verify();
  });

  it('marks one handled, and turns another member\'s 403 into permission-denied', async () => {
    const { repository, http } = apiRepository();
    const id = (JSON.parse(ADMIN_LIST_PENDING_JSON) as VisitorHandoffView[])[0].id;

    const refused = firstValueFrom(repository.markHandled(id));
    http.expectOne(apiHandoffHandlePath(id)).flush(JSON.parse(INTERNAL_HANDLE_403_JSON), { status: 403, statusText: 'Forbidden' });
    expect(await refused).toEqual({
      status: 'permission-denied', reason: 'handoff', message: '你沒有這則轉真人通知的存取權限，或它已不存在。轉真人通知只給助理的擁有者。',
    });

    const handled = firstValueFrom(repository.markHandled(id));
    const request = http.expectOne(apiHandoffHandlePath(id));
    expect(request.request.method).toBe('POST');
    request.flush(JSON.parse(ADMIN_HANDLE_JSON));
    const view = await handled;
    expect(view.status === 'ready' ? [view.data.handledByName, view.data.handledAt !== null] : null).toEqual(['安心商行管理者', true]);

    const list = firstValueFrom(repository.list('handled'));
    const listed = http.expectOne((candidate) => candidate.url === API_HANDOFFS_PATH);
    expect(listed.request.params.get('status')).toBe('handled');
    listed.flush(JSON.parse(ADMIN_LIST_HANDLED_JSON));
    expect((await list).status === 'ready').toBe(true);

    const after = firstValueFrom(repository.attention());
    http.expectOne(API_HANDOFF_ATTENTION_PATH).flush(JSON.parse(ADMIN_ATTENTION_AFTER_JSON));
    expect(await after).toEqual({ status: 'ready', data: { pendingCount: 1 } });
    http.verify();
  });

  it('reads and saves the account\'s own notification settings, with the 422 on the address', async () => {
    const { repository, http } = apiRepository();

    const initial = firstValueFrom(repository.notificationSettings());
    http.expectOne(API_NOTIFICATION_SETTINGS_PATH).flush(JSON.parse(ADMIN_SETTINGS_DEFAULT_JSON));
    expect(await initial).toEqual({ status: 'ready', data: { email: null, handoffEmails: true, emailDeliveryConfigured: true } });

    const invalid = firstValueFrom(repository.updateNotificationSettings({ email: 'a@b', handoffEmails: true }));
    http.expectOne(API_NOTIFICATION_SETTINGS_PATH).flush(JSON.parse(ADMIN_SETTINGS_PUT_INVALID_JSON), { status: 422, statusText: 'Unprocessable Content' });
    expect(await invalid).toEqual({ status: 'validation-failed', message: '請輸入有效的 Email 地址，例如 name@example.com。' });

    const saved = firstValueFrom(repository.updateNotificationSettings({ email: ' owner@shop.example.org ', handoffEmails: false }));
    const request = http.expectOne(API_NOTIFICATION_SETTINGS_PATH);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ email: ' owner@shop.example.org ', handoffEmails: false });
    request.flush(JSON.parse(ADMIN_SETTINGS_PUT_JSON));
    expect(await saved).toEqual({ status: 'ready', data: { email: 'owner@shop.example.org', handoffEmails: false, emailDeliveryConfigured: true } });
    http.verify();
  });
});
