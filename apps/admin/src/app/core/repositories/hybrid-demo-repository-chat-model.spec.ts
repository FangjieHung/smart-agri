import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { DEMO_SEED } from './demo-seed';
import { API_ORGANIZATION_CHAT_MODEL_PATH, HybridDemoRepository } from './hybrid-demo-repository';
import { createMemoryStorage } from './memory-storage';

/*
 * 以下是 2026-10-06 從真實 API 錄下的原始回應（#240）：自建的臨時 DB、兩個 Fake 模型（部署預設
 * `fake-chat-dev` 與 `second`），管理者與內部同仁各自登入；「移除」是拿掉第二個模型後重啟 API 再讀。
 * 不要依型別手改：真實回應的鍵與文字（例如 409 的訊息、422 的 type）與手寫的不一樣。
 */
const ADMIN_GET_JSON = `{"options":[{"id":"fake-chat-dev","displayName":"fake-chat-dev","model":"fake-chat-dev"},{"id":"second","displayName":"fake-chat-second","model":"fake-chat-second"}],"selectedId":null,"effective":{"id":"fake-chat-dev","displayName":"fake-chat-dev","model":"fake-chat-dev"},"source":"deployment-default","canChange":true,"lastChange":null,"revision":0}`;
const ADMIN_PUT_SECOND_JSON = `{"options":[{"id":"fake-chat-dev","displayName":"fake-chat-dev","model":"fake-chat-dev"},{"id":"second","displayName":"fake-chat-second","model":"fake-chat-second"}],"selectedId":"second","effective":{"id":"second","displayName":"fake-chat-second","model":"fake-chat-second"},"source":"selected","canChange":true,"lastChange":{"actorName":"安心商行管理者","at":"2026-10-06T11:50:21.033624+00:00"},"revision":1}`;
const INTERNAL_GET_JSON = `{"options":[{"id":"fake-chat-dev","displayName":"fake-chat-dev","model":"fake-chat-dev"},{"id":"second","displayName":"fake-chat-second","model":"fake-chat-second"}],"selectedId":"second","effective":{"id":"second","displayName":"fake-chat-second","model":"fake-chat-second"},"source":"selected","canChange":false,"lastChange":{"actorName":"安心商行管理者","at":"2026-10-06T11:50:21.033624+00:00"},"revision":1}`;
const REMOVED_GET_JSON = `{"options":[{"id":"fake-chat-dev","displayName":"fake-chat-dev","model":"fake-chat-dev"}],"selectedId":"second","effective":{"id":"fake-chat-dev","displayName":"fake-chat-dev","model":"fake-chat-dev"},"source":"removed","canChange":true,"lastChange":{"actorName":"安心商行管理者","at":"2026-10-06T11:50:21.033624+00:00"},"revision":1}`;
const INTERNAL_PUT_FORBIDDEN_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"organization-settings","message":"只有管理者可以變更組織設定。"}`;
const STALE_PUT_CONFLICT_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Conflict","status":409,"reason":"organization-settings-conflict","message":"組織設定已在其他分頁或由其他管理者更新過，請重新載入後再修改。"}`;
const UNKNOWN_PUT_UNPROCESSABLE_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"message":"這個模型不在部署提供的清單中，請重新載入後再選擇。","errors":{"modelId":["這個模型不在部署提供的清單中，請重新載入後再選擇。"]}}`;

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

describe('HybridDemoRepository organization chat model (issue #240)', () => {
  it.each([
    ['the manager before any change', ADMIN_GET_JSON],
    ['a member after a change', INTERNAL_GET_JSON],
    ['a choice that is no longer offered', REMOVED_GET_JSON],
  ])('maps the response for %s as is', async (_, json) => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.getOrganizationChatModel());
    controller.expectOne({ method: 'GET', url: API_ORGANIZATION_CHAT_MODEL_PATH }).flush(JSON.parse(json));

    expect(await result).toEqual({ status: 'ready', data: JSON.parse(json) });
    controller.verify();
  });

  it('treats omitted nullable keys as null rather than undefined', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.getOrganizationChatModel());
    const omitted = JSON.parse(ADMIN_GET_JSON) as Record<string, unknown>;
    delete omitted['selectedId'];
    delete omitted['lastChange'];
    controller.expectOne(API_ORGANIZATION_CHAT_MODEL_PATH).flush(omitted);

    const outcome = await result;
    if (outcome.status !== 'ready') throw new Error(outcome.status);
    expect(outcome.data.selectedId).toBeNull();
    expect(outcome.data.lastChange).toBeNull();
  });

  it('sends the id and revision, and maps the saved view', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.updateOrganizationChatModel('second', 0));
    const request = controller.expectOne({ method: 'PUT', url: API_ORGANIZATION_CHAT_MODEL_PATH });
    expect(request.request.body).toEqual({ modelId: 'second', revision: 0 });
    request.flush(JSON.parse(ADMIN_PUT_SECOND_JSON));

    expect(await result).toEqual({ status: 'ready', data: JSON.parse(ADMIN_PUT_SECOND_JSON) });
  });

  it('sends null to go back to the deployment default', () => {
    const { repository, controller } = setUp();
    repository.updateOrganizationChatModel(null, 1).subscribe();
    expect(controller.expectOne(API_ORGANIZATION_CHAT_MODEL_PATH).request.body).toEqual({ modelId: null, revision: 1 });
  });

  it('turns 403 organization-settings into permission-denied', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.updateOrganizationChatModel(null, 1));
    controller
      .expectOne(API_ORGANIZATION_CHAT_MODEL_PATH)
      .flush(JSON.parse(INTERNAL_PUT_FORBIDDEN_JSON), { status: 403, statusText: 'Forbidden' });

    expect(await result).toEqual({
      status: 'permission-denied',
      reason: 'organization-settings',
      message: '只有管理者可以變更組織設定。',
    });
  });

  it('turns 409 into a conflict with the server message', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.updateOrganizationChatModel(null, 0));
    controller
      .expectOne(API_ORGANIZATION_CHAT_MODEL_PATH)
      .flush(JSON.parse(STALE_PUT_CONFLICT_JSON), { status: 409, statusText: 'Conflict' });

    expect(await result).toEqual({
      status: 'conflict',
      message: (JSON.parse(STALE_PUT_CONFLICT_JSON) as { message: string }).message,
    });
  });

  it('turns 422 errors.modelId into validation-failed', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.updateOrganizationChatModel('no-such-model', 1));
    controller
      .expectOne(API_ORGANIZATION_CHAT_MODEL_PATH)
      .flush(JSON.parse(UNKNOWN_PUT_UNPROCESSABLE_JSON), { status: 422, statusText: 'Unprocessable Content' });

    expect(await result).toEqual({
      status: 'validation-failed',
      message: '這個模型不在部署提供的清單中，請重新載入後再選擇。',
    });
  });

  it('lets a server failure surface as an error', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.getOrganizationChatModel());
    controller.expectOne(API_ORGANIZATION_CHAT_MODEL_PATH).flush('boom', { status: 500, statusText: 'Server Error' });

    await expect(result).rejects.toMatchObject({ status: 500 });
  });
});
