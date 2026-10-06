import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { DEMO_SEED } from './demo-seed';
import {
  API_ORGANIZATION_RETENTION_ASSISTANTS_PATH,
  apiAssistantConversationPurgePath,
  apiAssistantConversationSummaryPath,
  HybridDemoRepository,
} from './hybrid-demo-repository';
import { createMemoryStorage } from './memory-storage';

/*
 * 以下是 2026-10-06 從真實 API 錄下的原始回應（#242）：自建的臨時 DB（開發種子資料）上，組織 `anxin`
 * 的管理者 `admin`、內部同仁 `internal`（錄製前在臨時 DB 加上 manage-assistants）與外部客戶 `customer`
 * 各自登入。錄製前以 SQL 放了兩個助理：管理者的「客服助理」（保存對話開啟；admin 1 串、internal 2 串、
 * customer 1 串）與 internal 的「同仁助理」（保存對話關閉；1 串），另外在組織 `control` 放了一個有 2 串
 * 對話的「對照助理」——刪除它得到與非管理者相同的 403，錄完它的 2 串仍在。每段標題是錄製時的步驟名稱與狀態碼。
 * 不要依型別手改：真實回應的鍵與文字（例如 403 的 reason 與訊息、時間的寫法）與手寫的不一樣。
 */
/** admin-list：200 */
const ADMIN_LIST_JSON = `[{"assistantId":"01a1118e-0000-7000-8000-00000000a001","assistantName":"客服助理","keepConversations":true,"threadCount":4,"accountCount":3,"lastActivityAt":"2026-10-06T01:30:00+00:00"},{"assistantId":"01a1118e-0000-7000-8000-00000000a002","assistantName":"同仁助理","keepConversations":false,"threadCount":1,"accountCount":1,"lastActivityAt":"2026-10-02T10:00:00+00:00"}]`;
/** internal-list-403：403 */
const INTERNAL_LIST_FORBIDDEN_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"organization-settings","message":"只有管理者可以變更組織設定。"}`;
/** internal-summary-own：200 */
const OWNER_SUMMARY_JSON = `{"threadCount":1,"accountCount":1,"canPurge":false}`;
/** internal-summary-not-owned-403：403 */
const NOT_OWNER_SUMMARY_FORBIDDEN_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"assistant-configuration","message":"你沒有這個助理的存取權限，或它已不存在。"}`;
/** admin-summary：200 */
const ADMIN_SUMMARY_JSON = `{"threadCount":4,"accountCount":3,"canPurge":true}`;
/** customer-summary-403：403 */
const CUSTOMER_SUMMARY_FORBIDDEN_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"assistant-configuration","message":"你沒有這個助理的存取權限，或它已不存在。"}`;
/** internal-purge-own-403：403 */
const OWNER_PURGE_FORBIDDEN_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"organization-settings","message":"只有管理者可以變更組織設定。"}`;
/** admin-purge-other-organization-403：403 */
const OTHER_ORGANIZATION_PURGE_FORBIDDEN_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"organization-settings","message":"只有管理者可以變更組織設定。"}`;
/** admin-purge：200 */
const ADMIN_PURGE_JSON = `{"deletedThreadCount":4}`;
/** admin-summary-after：200 */
const ADMIN_SUMMARY_AFTER_JSON = `{"threadCount":0,"accountCount":0,"canPurge":true}`;
/** admin-list-after：200 */
const ADMIN_LIST_AFTER_JSON = `[{"assistantId":"01a1118e-0000-7000-8000-00000000a001","assistantName":"客服助理","keepConversations":true,"threadCount":0,"accountCount":0,"lastActivityAt":null},{"assistantId":"01a1118e-0000-7000-8000-00000000a002","assistantName":"同仁助理","keepConversations":false,"threadCount":1,"accountCount":1,"lastActivityAt":"2026-10-02T10:00:00+00:00"}]`;
/** admin-purge-switch-off：200 */
const ADMIN_PURGE_SWITCH_OFF_JSON = `{"deletedThreadCount":1}`;

const CUSTOMER_SERVICE = '01a1118e-0000-7000-8000-00000000a001';
const STAFF = '01a1118e-0000-7000-8000-00000000a002';
const OTHER_ORGANIZATION = '01a1118e-0000-7000-8000-00000000c001';

function setUp() {
  TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
  const repository = new HybridDemoRepository(
    DEMO_SEED,
    { storage: createMemoryStorage(), viewer: () => 'account-smb-admin' },
    {
      http: TestBed.inject(HttpClient),
      viewerPermissions: () => ({
        accountId: '01a1118e-56d8-7f48-9f67-140d495184b1',
        demoAccountId: 'account-smb-admin',
        permissions: ['manage-assistants'],
        organizationId: '01a1118e-56a9-7053-9773-e14f55273993',
      }),
    },
  );
  return { repository, controller: TestBed.inject(HttpTestingController) };
}

function forbidden(json: string) {
  return { body: JSON.parse(json) as object, options: { status: 403, statusText: 'Forbidden' } };
}

function messageOf(json: string): string {
  return (JSON.parse(json) as { message: string }).message;
}

describe('HybridDemoRepository immediate purge (issue #242)', () => {
  it.each([
    ['before', ADMIN_LIST_JSON],
    ['after the purge', ADMIN_LIST_AFTER_JSON],
  ])('maps the recorded per-assistant list (%s) unchanged', async (_, json) => {
    const { repository, controller } = setUp();
    const pending = firstValueFrom(repository.listRetentionAssistants());
    controller.expectOne({ method: 'GET', url: API_ORGANIZATION_RETENTION_ASSISTANTS_PATH }).flush(JSON.parse(json));

    expect(await pending).toEqual({ status: 'ready', data: JSON.parse(json) });
    controller.verify();
  });

  it('keeps the recorded counts, the null last activity and the switch state', async () => {
    const { repository, controller } = setUp();
    const before = firstValueFrom(repository.listRetentionAssistants());
    controller.expectOne(API_ORGANIZATION_RETENTION_ASSISTANTS_PATH).flush(JSON.parse(ADMIN_LIST_JSON));
    const after = firstValueFrom(repository.listRetentionAssistants());
    controller.expectOne(API_ORGANIZATION_RETENTION_ASSISTANTS_PATH).flush(JSON.parse(ADMIN_LIST_AFTER_JSON));

    const rows = await before;
    if (rows.status !== 'ready') throw new Error(rows.status);
    expect(rows.data.map((row) => [row.assistantName, row.keepConversations, row.threadCount, row.accountCount])).toEqual([
      ['客服助理', true, 4, 3],
      ['同仁助理', false, 1, 1],
    ]);
    const emptied = await after;
    if (emptied.status !== 'ready') throw new Error(emptied.status);
    expect(emptied.data[0]).toMatchObject({ assistantId: CUSTOMER_SERVICE, threadCount: 0, accountCount: 0, lastActivityAt: null });
    expect(emptied.data[1]).toMatchObject({ assistantId: STAFF, threadCount: 1 });
  });

  it.each([
    ['the owner', STAFF, OWNER_SUMMARY_JSON, { threadCount: 1, accountCount: 1, canPurge: false }],
    ['the manager', CUSTOMER_SERVICE, ADMIN_SUMMARY_JSON, { threadCount: 4, accountCount: 3, canPurge: true }],
    ['the manager after the purge', CUSTOMER_SERVICE, ADMIN_SUMMARY_AFTER_JSON, { threadCount: 0, accountCount: 0, canPurge: true }],
  ])('maps the recorded summary for %s', async (_, assistantId, json, data) => {
    const { repository, controller } = setUp();
    const pending = firstValueFrom(repository.getAssistantConversationSummary(assistantId));
    controller.expectOne({ method: 'GET', url: apiAssistantConversationSummaryPath(assistantId) }).flush(JSON.parse(json));

    expect(await pending).toEqual({ status: 'ready', data });
    controller.verify();
  });

  it.each([
    ['not the owner', CUSTOMER_SERVICE, NOT_OWNER_SUMMARY_FORBIDDEN_JSON],
    ['an external customer', STAFF, CUSTOMER_SUMMARY_FORBIDDEN_JSON],
  ])('turns the recorded summary 403 for %s into assistant-configuration', async (_, assistantId, json) => {
    const { repository, controller } = setUp();
    const pending = firstValueFrom(repository.getAssistantConversationSummary(assistantId));
    const { body, options } = forbidden(json);
    controller.expectOne(apiAssistantConversationSummaryPath(assistantId)).flush(body, options);

    expect(await pending).toEqual({ status: 'permission-denied', reason: 'assistant-configuration', message: messageOf(json) });
  });

  it.each([
    ['the switch on', CUSTOMER_SERVICE, ADMIN_PURGE_JSON, 4],
    ['the switch off', STAFF, ADMIN_PURGE_SWITCH_OFF_JSON, 1],
  ])('posts the purge with %s and maps the recorded count', async (_, assistantId, json, count) => {
    const { repository, controller } = setUp();
    const pending = firstValueFrom(repository.purgeAssistantConversations(assistantId));
    const request = controller.expectOne({ method: 'POST', url: apiAssistantConversationPurgePath(assistantId) });
    expect(request.request.url).toBe('/api/v1/assistants/' + assistantId + '/chat/conversations:purge');
    request.flush(JSON.parse(json));

    expect(await pending).toEqual({ status: 'ready', data: { deletedThreadCount: count } });
    controller.verify();
  });

  it.each([
    ['the owner purging their own assistant', STAFF, OWNER_PURGE_FORBIDDEN_JSON],
    ['another organization’s assistant', OTHER_ORGANIZATION, OTHER_ORGANIZATION_PURGE_FORBIDDEN_JSON],
  ])('turns the recorded purge 403 for %s into the same organization-settings denial', async (_, assistantId, json) => {
    const { repository, controller } = setUp();
    const pending = firstValueFrom(repository.purgeAssistantConversations(assistantId));
    const { body, options } = forbidden(json);
    controller.expectOne(apiAssistantConversationPurgePath(assistantId)).flush(body, options);

    expect(await pending).toEqual({ status: 'permission-denied', reason: 'organization-settings', message: messageOf(json) });
  });

  it('answers the owner and another organization with byte-identical 403s, and the list too', async () => {
    expect(OWNER_PURGE_FORBIDDEN_JSON).toBe(OTHER_ORGANIZATION_PURGE_FORBIDDEN_JSON);
    expect(INTERNAL_LIST_FORBIDDEN_JSON).toBe(OWNER_PURGE_FORBIDDEN_JSON);

    const { repository, controller } = setUp();
    const pending = firstValueFrom(repository.listRetentionAssistants());
    const { body, options } = forbidden(INTERNAL_LIST_FORBIDDEN_JSON);
    controller.expectOne(API_ORGANIZATION_RETENTION_ASSISTANTS_PATH).flush(body, options);

    expect(await pending).toEqual({
      status: 'permission-denied',
      reason: 'organization-settings',
      message: messageOf(INTERNAL_LIST_FORBIDDEN_JSON),
    });
  });

  it('never carries any conversation content in the recorded responses', () => {
    const all = [ADMIN_LIST_JSON, ADMIN_LIST_AFTER_JSON, OWNER_SUMMARY_JSON, ADMIN_SUMMARY_JSON, ADMIN_PURGE_JSON].join('');
    for (const title of ['退貨要多久', '配送幾天到', '排班怎麼查']) expect(all).not.toContain(title);
  });
});
