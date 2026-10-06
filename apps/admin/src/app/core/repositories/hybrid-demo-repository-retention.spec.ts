import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { DEMO_SEED } from './demo-seed';
import {
  API_ORGANIZATION_RETENTION_PATH,
  API_ORGANIZATION_RETENTION_PREVIEW_PATH,
  HybridDemoRepository,
} from './hybrid-demo-repository';
import { createMemoryStorage } from './memory-storage';

/*
 * 以下是 2026-10-06 從真實 API 錄下的原始回應（#243）：自建的臨時 DB（開發種子資料），組織 `anxin`
 * 的管理者 `admin` 與內部同仁 `internal` 各自登入。預覽前先在臨時 DB 放了一個助理與 4 串內部同仁的對話
 * （最後活動 120、100、40、5 天前），所以 30 天是 3 串、90 天是 2 串。「待生效轉為生效」是在
 * Development 執行 `retention-cleanup --organization anxin --as-of 2026-10-14T03:00:00+08:00` 之後再讀
 * （revision 因此從 3 變 4）。每段標題是錄製時的步驟名稱與狀態碼。
 * 不要依型別手改：真實回應的鍵與文字（例如 409 的訊息、422 的 errors.days）與手寫的不一樣。
 */
/** admin-get-initial：200 */
const ADMIN_GET_JSON = `{"days":null,"pending":null,"options":[30,90,180,365],"canChange":true,"lastChange":null,"revision":0}`;
/** internal-get-initial：200 */
const INTERNAL_GET_JSON = `{"days":null,"pending":null,"options":[30,90,180,365],"canChange":false,"lastChange":null,"revision":0}`;
/** admin-preview-30：200 */
const ADMIN_PREVIEW_30_JSON = `{"days":30,"threadCount":3,"cutoff":"2026-09-05T16:00:00+00:00"}`;
/** admin-preview-90：200 */
const ADMIN_PREVIEW_90_JSON = `{"days":90,"threadCount":2,"cutoff":"2026-07-07T16:00:00+00:00"}`;
/** admin-preview-45-422：422 */
const PREVIEW_UNPROCESSABLE_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"message":"保存期限只能是 30、90、180、365 天或永久。","errors":{"days":["保存期限只能是 30、90、180、365 天或永久。"]}}`;
/** internal-preview-403：403 */
const INTERNAL_PREVIEW_FORBIDDEN_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"organization-settings","message":"只有管理者可以變更組織設定。"}`;
/** admin-put-shorten-30：200 */
const ADMIN_PUT_SHORTEN_FROM_FOREVER_JSON = `{"days":null,"pending":{"days":30,"effectiveAt":"2026-10-13T13:03:26.147366+00:00"},"options":[30,90,180,365],"canChange":true,"lastChange":{"actorName":"安心商行管理者","at":"2026-10-06T13:03:26.147366+00:00"},"revision":1}`;
/** internal-get-pending：200 */
const INTERNAL_GET_PENDING_JSON = `{"days":null,"pending":{"days":30,"effectiveAt":"2026-10-13T13:03:26.147366+00:00"},"options":[30,90,180,365],"canChange":false,"lastChange":{"actorName":"安心商行管理者","at":"2026-10-06T13:03:26.147366+00:00"},"revision":1}`;
/** admin-put-stale-409：409 */
const STALE_PUT_CONFLICT_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Conflict","status":409,"reason":"organization-settings-conflict","message":"組織設定已在其他分頁或由其他管理者更新過，請重新載入後再修改。"}`;
/** admin-put-45-422：422 */
const UNKNOWN_PUT_UNPROCESSABLE_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"message":"保存期限只能是 30、90、180、365 天或永久。","errors":{"days":["保存期限只能是 30、90、180、365 天或永久。"]}}`;
/** internal-put-403：403 */
const INTERNAL_PUT_FORBIDDEN_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"organization-settings","message":"只有管理者可以變更組織設定。"}`;
/** admin-put-revert-forever：200 */
const ADMIN_PUT_REVERT_FOREVER_JSON = `{"days":null,"pending":null,"options":[30,90,180,365],"canChange":true,"lastChange":{"actorName":"安心商行管理者","at":"2026-10-06T13:03:26.202454+00:00"},"revision":2}`;
/** admin-get-took-effect：200 */
const ADMIN_GET_TOOK_EFFECT_JSON = `{"days":30,"pending":null,"options":[30,90,180,365],"canChange":true,"lastChange":{"actorName":"系統","at":"2026-10-06T13:04:03.984089+00:00"},"revision":4}`;
/** admin-put-extend-90：200 */
const ADMIN_PUT_EXTEND_90_JSON = `{"days":90,"pending":null,"options":[30,90,180,365],"canChange":true,"lastChange":{"actorName":"安心商行管理者","at":"2026-10-06T13:04:04.286687+00:00"},"revision":5}`;
/** admin-put-shorten-30：200 */
const ADMIN_PUT_SHORTEN_FROM_90_JSON = `{"days":90,"pending":{"days":30,"effectiveAt":"2026-10-13T13:04:04.292776+00:00"},"options":[30,90,180,365],"canChange":true,"lastChange":{"actorName":"安心商行管理者","at":"2026-10-06T13:04:04.292776+00:00"},"revision":6}`;
/** admin-put-revert-90：200 */
const ADMIN_PUT_REVERT_90_JSON = `{"days":90,"pending":null,"options":[30,90,180,365],"canChange":true,"lastChange":{"actorName":"安心商行管理者","at":"2026-10-06T13:04:04.297848+00:00"},"revision":7}`;
/** admin-put-extend-forever：200 */
const ADMIN_PUT_EXTEND_FOREVER_JSON = `{"days":null,"pending":null,"options":[30,90,180,365],"canChange":true,"lastChange":{"actorName":"安心商行管理者","at":"2026-10-06T13:04:04.302772+00:00"},"revision":8}`;

function setUp() {
  TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
  const repository = new HybridDemoRepository(
    DEMO_SEED,
    { storage: createMemoryStorage(), viewer: () => 'account-smb-admin' },
    {
      http: TestBed.inject(HttpClient),
      viewerPermissions: () => ({
        accountId: '0199a3c0-0000-7000-8000-0000000000a1',
        demoAccountId: 'account-smb-admin',
        permissions: ['manage-assistants'],
        organizationId: '0199a3c0-0000-7000-8000-0000000000aa',
      }),
    },
  );
  return { repository, controller: TestBed.inject(HttpTestingController) };
}

function messageOf(json: string): string {
  return (JSON.parse(json) as { message: string }).message;
}

describe('HybridDemoRepository conversation retention (issue #243)', () => {
  it.each([
    ['the manager before any change', ADMIN_GET_JSON],
    ['a member before any change', INTERNAL_GET_JSON],
    ['a member while a shorter period is pending', INTERNAL_GET_PENDING_JSON],
    ['the manager after the pending period took effect', ADMIN_GET_TOOK_EFFECT_JSON],
  ])('maps the response for %s as is', async (_, json) => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.getOrganizationRetention());
    controller.expectOne({ method: 'GET', url: API_ORGANIZATION_RETENTION_PATH }).flush(JSON.parse(json));

    expect(await result).toEqual({ status: 'ready', data: JSON.parse(json) });
    controller.verify();
  });

  it('treats omitted nullable keys as null (forever, nothing pending, never changed)', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.getOrganizationRetention());
    const omitted = JSON.parse(ADMIN_GET_TOOK_EFFECT_JSON) as Record<string, unknown>;
    delete omitted['days'];
    delete omitted['pending'];
    delete omitted['lastChange'];
    controller.expectOne(API_ORGANIZATION_RETENTION_PATH).flush(omitted);

    const outcome = await result;
    if (outcome.status !== 'ready') throw new Error(outcome.status);
    expect(outcome.data.days).toBeNull();
    expect(outcome.data.pending).toBeNull();
    expect(outcome.data.lastChange).toBeNull();
  });

  it.each([
    [30, ADMIN_PREVIEW_30_JSON],
    [90, ADMIN_PREVIEW_90_JSON],
  ])('asks for the %i-day preview and maps the count', async (days, json) => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.previewOrganizationRetention(days));
    const request = controller.expectOne(
      (candidate) => candidate.method === 'GET' && candidate.url === API_ORGANIZATION_RETENTION_PREVIEW_PATH,
    );
    expect(request.request.params.get('days')).toBe(String(days));
    request.flush(JSON.parse(json));

    expect(await result).toEqual({ status: 'ready', data: JSON.parse(json) });
  });

  it('turns a preview of an unoffered period (422 errors.days) into validation-failed', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.previewOrganizationRetention(45));
    controller
      .expectOne((candidate) => candidate.url === API_ORGANIZATION_RETENTION_PREVIEW_PATH)
      .flush(JSON.parse(PREVIEW_UNPROCESSABLE_JSON), { status: 422, statusText: 'Unprocessable Content' });

    expect(await result).toEqual({ status: 'validation-failed', message: messageOf(PREVIEW_UNPROCESSABLE_JSON) });
  });

  it('turns a member’s preview (403 organization-settings) into permission-denied', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.previewOrganizationRetention(30));
    controller
      .expectOne((candidate) => candidate.url === API_ORGANIZATION_RETENTION_PREVIEW_PATH)
      .flush(JSON.parse(INTERNAL_PREVIEW_FORBIDDEN_JSON), { status: 403, statusText: 'Forbidden' });

    expect(await result).toEqual({
      status: 'permission-denied',
      reason: 'organization-settings',
      message: '只有管理者可以變更組織設定。',
    });
  });

  it.each([
    ['shortens forever to 30 days (pending)', 30, 0, ADMIN_PUT_SHORTEN_FROM_FOREVER_JSON],
    ['goes back to forever', null, 1, ADMIN_PUT_REVERT_FOREVER_JSON],
    ['extends 30 days to 90 at once', 90, 4, ADMIN_PUT_EXTEND_90_JSON],
    ['shortens 90 days to 30 (pending)', 30, 5, ADMIN_PUT_SHORTEN_FROM_90_JSON],
    ['goes back to 90 days', 90, 6, ADMIN_PUT_REVERT_90_JSON],
    ['extends 90 days to forever', null, 7, ADMIN_PUT_EXTEND_FOREVER_JSON],
  ])('%s: sends the days and revision and maps the saved view', async (_, days, revision, json) => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.updateOrganizationRetention(days, revision));
    const request = controller.expectOne({ method: 'PUT', url: API_ORGANIZATION_RETENTION_PATH });
    expect(request.request.body).toEqual({ days, revision });
    request.flush(JSON.parse(json));

    expect(await result).toEqual({ status: 'ready', data: JSON.parse(json) });
  });

  it('turns 409 into a conflict with the server message', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.updateOrganizationRetention(null, 0));
    controller
      .expectOne(API_ORGANIZATION_RETENTION_PATH)
      .flush(JSON.parse(STALE_PUT_CONFLICT_JSON), { status: 409, statusText: 'Conflict' });

    expect(await result).toEqual({ status: 'conflict', message: messageOf(STALE_PUT_CONFLICT_JSON) });
  });

  it('turns 422 errors.days into validation-failed', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.updateOrganizationRetention(45, 1));
    controller
      .expectOne(API_ORGANIZATION_RETENTION_PATH)
      .flush(JSON.parse(UNKNOWN_PUT_UNPROCESSABLE_JSON), { status: 422, statusText: 'Unprocessable Content' });

    expect(await result).toEqual({
      status: 'validation-failed',
      message: '保存期限只能是 30、90、180、365 天或永久。',
    });
  });

  it('turns a member’s 403 organization-settings into permission-denied', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.updateOrganizationRetention(null, 1));
    controller
      .expectOne(API_ORGANIZATION_RETENTION_PATH)
      .flush(JSON.parse(INTERNAL_PUT_FORBIDDEN_JSON), { status: 403, statusText: 'Forbidden' });

    expect(await result).toEqual({
      status: 'permission-denied',
      reason: 'organization-settings',
      message: '只有管理者可以變更組織設定。',
    });
  });

  it('lets a server failure surface as an error', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.getOrganizationRetention());
    controller.expectOne(API_ORGANIZATION_RETENTION_PATH).flush('boom', { status: 500, statusText: 'Server Error' });

    await expect(result).rejects.toMatchObject({ status: 500 });
  });
});
