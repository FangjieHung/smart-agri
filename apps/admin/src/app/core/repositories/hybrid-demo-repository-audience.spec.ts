import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { DEMO_SEED } from './demo-seed';
import {
  API_ASSISTANTS_PATH,
  API_USABLE_ASSISTANTS_PATH,
  apiAssistantSettingsPath,
  HybridDemoRepository,
} from './hybrid-demo-repository';
import { createMemoryStorage } from './memory-storage';

/*
 * 使用對象（issue #224）在 API 模式：下面的 JSON 是 2026-10-07 以本分支的 API（臨時資料庫、開發用種子帳號）
 * 實際錄下的回應：以三種使用對象各建立一個助理、讀清單與設定、把使用對象改成「內部員工與外部客戶」、
 * 送出不認得的值（422），以及分享給內部員工後他讀到的「可以使用的助理」。原樣 `JSON.parse`，不依型別手寫。
 */
const EXTERNAL_ID = '01a115eb-bd80-73ca-92b7-7d2ba79a7c93';

const REAL_CREATED_EXTERNAL_JSON = `{"id":"01a115eb-bd80-73ca-92b7-7d2ba79a7c93","ownerAccountId":"01a115eb-85e9-7131-88c7-13482f606084","name":"官網客服助理","purpose":"回答客戶的退換貨問題","status":"ready","audience":"authorized-external-customers","viewerCanManage":true,"createdAt":"2026-10-07T10:32:13.440982+00:00","updatedAt":"2026-10-07T10:32:13.440982+00:00","acceptanceStatus":"not-accepted"}`;
const REAL_LIST_JSON = `[{"id":"01a115eb-bd38-707c-ae34-880cb4fc7d1c","ownerAccountId":"01a115eb-85e9-7131-88c7-13482f606084","name":"內部助理","purpose":"回答客戶的退換貨問題","status":"ready","audience":"account-members","viewerCanManage":true,"createdAt":"2026-10-07T10:32:13.368779+00:00","updatedAt":"2026-10-07T10:32:13.368779+00:00","acceptanceStatus":"not-accepted"},{"id":"01a115eb-bd80-73ca-92b7-7d2ba79a7c93","ownerAccountId":"01a115eb-85e9-7131-88c7-13482f606084","name":"官網客服助理","purpose":"回答客戶的退換貨問題","status":"ready","audience":"authorized-external-customers","viewerCanManage":true,"createdAt":"2026-10-07T10:32:13.440982+00:00","updatedAt":"2026-10-07T10:32:13.440982+00:00","acceptanceStatus":"not-accepted"},{"id":"01a115eb-bd88-795e-ac82-7c002071398c","ownerAccountId":"01a115eb-85e9-7131-88c7-13482f606084","name":"全通路助理","purpose":"回答客戶的退換貨問題","status":"ready","audience":"members-and-external-customers","viewerCanManage":true,"createdAt":"2026-10-07T10:32:13.448869+00:00","updatedAt":"2026-10-07T10:32:13.448869+00:00","acceptanceStatus":"not-accepted"}]`;
const REAL_SETTINGS_EXTERNAL_JSON = `{"configuration":{"id":"01a115eb-bd80-73ca-92b7-7d2ba79a7c93","ownerAccountId":"01a115eb-85e9-7131-88c7-13482f606084","name":"官網客服助理","purpose":"回答客戶的退換貨問題","status":"ready","audience":"authorized-external-customers","viewerCanManage":true,"createdAt":"2026-10-07T10:32:13.440982+00:00","updatedAt":"2026-10-07T10:32:13.440982+00:00","acceptanceStatus":"not-accepted"},"knowledgeBaseIds":["01a115eb-bced-7784-a78a-bd1aa11b8c16"],"databaseIds":[],"caseTypeIds":[],"tone":"friendly","roleInstructions":"","rules":{"knowledgeScope":"company-data-only","refusalMessage":"目前的資料中找不到這個問題的答案。","showCitations":true,"keepConversations":true,"dataWriteDatabaseId":null,"dataWritePurpose":"","periodicReport":"off"},"periodicReportAutoDisabled":null}`;
const REAL_PATCHED_TO_BOTH_JSON = `{"configuration":{"id":"01a115eb-bd80-73ca-92b7-7d2ba79a7c93","ownerAccountId":"01a115eb-85e9-7131-88c7-13482f606084","name":"官網客服助理","purpose":"回答客戶的退換貨問題","status":"ready","audience":"members-and-external-customers","viewerCanManage":true,"createdAt":"2026-10-07T10:32:13.440982+00:00","updatedAt":"2026-10-07T10:32:13.510549+00:00","acceptanceStatus":"not-accepted"},"knowledgeBaseIds":["01a115eb-bced-7784-a78a-bd1aa11b8c16"],"databaseIds":[],"caseTypeIds":[],"tone":"friendly","roleInstructions":"","rules":{"knowledgeScope":"company-data-only","refusalMessage":"目前的資料中找不到這個問題的答案。","showCitations":true,"keepConversations":true,"dataWriteDatabaseId":null,"dataWritePurpose":"","periodicReport":"off"},"periodicReportAutoDisabled":null}`;
const REAL_PATCH_UNKNOWN_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"message":"使用對象設定不正確。","errors":{"audience":["使用對象設定不正確。"]}}`;
const REAL_INTERNAL_USABLE_JSON = `[{"id":"01a115eb-bd80-73ca-92b7-7d2ba79a7c93","name":"官網客服助理","purpose":"回答客戶的退換貨問題","status":"ready","audience":"members-and-external-customers","viewerIsOwner":false}]`;

function setUp(permissions: readonly ('manage-assistants' | 'use-shared-assistants')[] = ['manage-assistants']) {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
  const repository = new HybridDemoRepository(
    DEMO_SEED,
    { storage: createMemoryStorage(), viewer: () => 'account-smb-admin' },
    {
      http: TestBed.inject(HttpClient),
      viewerPermissions: () => ({
        accountId: '01a115eb-85e9-7131-88c7-13482f606084',
        demoAccountId: 'account-smb-admin',
        permissions: [...permissions],
        organizationId: '0199a3c0-0000-7000-8000-0000000000aa',
      }),
    },
  );
  return { repository, controller: TestBed.inject(HttpTestingController) };
}

function respond(controller: HttpTestingController, method: string, url: string, json: string, status = 200) {
  const request = controller.expectOne({ method, url });
  request.flush(JSON.parse(json), { status, statusText: String(status) });
  return request.request;
}

describe('HybridDemoRepository — assistant audience (#224)', () => {
  it('keeps the external audience of an assistant created from a draft', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.createAssistantFromDraft('draft-1'));

    respond(controller, 'POST', API_ASSISTANTS_PATH, REAL_CREATED_EXTERNAL_JSON, 201);

    expect(await result).toMatchObject({
      status: 'ready',
      data: { id: EXTERNAL_ID, audience: 'authorized-external-customers' },
    });
  });

  it('lists each assistant with the audience it was created with', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.listAssistantConfigurations());

    respond(controller, 'GET', API_ASSISTANTS_PATH, REAL_LIST_JSON);

    const view = await result;
    expect(view.status).toBe('ready');
    expect(view.status === 'ready' ? view.data.map((assistant) => assistant.audience) : []).toEqual([
      'account-members',
      'authorized-external-customers',
      'members-and-external-customers',
    ]);
  });

  it('reads the audience from the settings', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.getAssistantSettings(EXTERNAL_ID));

    respond(controller, 'GET', apiAssistantSettingsPath(EXTERNAL_ID), REAL_SETTINGS_EXTERNAL_JSON);

    expect(await result).toMatchObject({
      status: 'ready',
      data: { configuration: { audience: 'authorized-external-customers' } },
    });
  });

  it('sends only the changed audience in the PATCH and reads the new one back', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(
      repository.updateAssistantSettings(EXTERNAL_ID, { audience: 'members-and-external-customers' }),
    );

    const request = respond(controller, 'PATCH', apiAssistantSettingsPath(EXTERNAL_ID), REAL_PATCHED_TO_BOTH_JSON);

    expect(request.body).toEqual({ audience: 'members-and-external-customers' });
    expect(await result).toMatchObject({
      status: 'ready',
      data: { configuration: { audience: 'members-and-external-customers' } },
    });
  });

  it('shows the 422 for an unknown audience on the audience field', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(
      repository.updateAssistantSettings(EXTERNAL_ID, { audience: 'everyone' as never }),
    );

    respond(controller, 'PATCH', apiAssistantSettingsPath(EXTERNAL_ID), REAL_PATCH_UNKNOWN_422_JSON, 422);

    expect(await result).toEqual({
      status: 'validation-failed',
      message: '使用對象設定不正確。',
      errors: [{ field: 'audience', message: '使用對象設定不正確。' }],
    });
  });

  it('shows the real audience of an assistant shared with the viewer', async () => {
    const { repository, controller } = setUp(['use-shared-assistants']);
    const result = firstValueFrom(repository.listUsableAssistants());

    respond(controller, 'GET', API_USABLE_ASSISTANTS_PATH, REAL_INTERNAL_USABLE_JSON);

    expect(await result).toEqual({
      status: 'ready',
      data: [
        {
          id: EXTERNAL_ID,
          name: '官網客服助理',
          purpose: '回答客戶的退換貨問題',
          status: 'ready',
          audience: 'members-and-external-customers',
          permission: 'use',
        },
      ],
    });
  });

  it('treats a response recorded before #224 (no audience key) as account-members', async () => {
    const { repository, controller } = setUp();
    const result = firstValueFrom(repository.getAssistantSettings(EXTERNAL_ID));
    const old = JSON.parse(REAL_SETTINGS_EXTERNAL_JSON) as { configuration: Record<string, unknown> };
    delete old.configuration['audience'];

    controller.expectOne(apiAssistantSettingsPath(EXTERNAL_ID)).flush(old);

    expect(await result).toMatchObject({ status: 'ready', data: { configuration: { audience: 'account-members' } } });
  });
});
