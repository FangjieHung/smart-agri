import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { LineSettingsInput, LineSetupView } from '../domain/publishing.model';
import { DEMO_SEED } from './demo-seed';
import { apiAssistantLinePath, apiAssistantPublishingPath, HybridDemoRepository } from './hybrid-demo-repository';
import {
  REAL_LINE_AGGREGATE_SERVING_JSON,
  REAL_LINE_FAILED_TOKEN_JSON,
  REAL_LINE_FAILED_WEBHOOK_JSON,
  REAL_LINE_INVALID_JSON,
  REAL_LINE_NOT_PUBLISHED_JSON,
  REAL_LINE_PAUSED_JSON,
  REAL_LINE_PUBLISHED_JSON,
  REAL_LINE_PUBLISH_REFUSED_FAILED_TEST_JSON,
  REAL_LINE_PUBLISH_REFUSED_PUBLIC_BASE_URL_JSON,
  REAL_LINE_PUBLISH_REFUSED_SEVERAL_JSON,
  REAL_LINE_RESUMED_JSON,
  REAL_LINE_REVISION_CONFLICT_JSON,
  REAL_LINE_SAVED_JSON,
  REAL_LINE_SAVED_REPLACED_JSON,
  REAL_LINE_SERVING_JSON,
  REAL_LINE_SUSPENDED_KNOWLEDGE_JSON,
  REAL_LINE_TESTED_JSON,
  REAL_LINE_TEST_CONFLICT_JSON,
  REAL_LINE_TEST_PASSED_JSON,
  REAL_LINE_TEST_REFUSED_PUBLIC_BASE_URL_JSON,
  REAL_LINE_TEST_REFUSED_SETTINGS_JSON,
  REAL_LINE_UNPUBLISHED_JSON,
  REAL_LINE_UNSAVED_JSON,
  REAL_LINE_UNSAVED_NO_URL_JSON,
} from './line-channel-api.testing';
import { createMemoryStorage } from './memory-storage';

/*
 * LINE 頻道在 API 模式（issue #233）：回應都是 `line-channel-api.testing.ts` 裡實際從 API 取得的 JSON，
 * 原樣 `JSON.parse`，不是依產生的型別手寫（後端的 `JsonIgnore(WhenWritingNull)` 欄位可能省略，型別卻寫成必填）。
 */
const ASSISTANT_ID = '01a110ab-5ccd-75c6-a859-a4e7f83e17c4';
const LINE_PATH = apiAssistantLinePath(ASSISTANT_ID);

const SETTINGS: LineSettingsInput = {
  officialAccountId: '@anxin-demo',
  channelId: '1650000000',
  welcomeMessage: '您好！歡迎加入。',
};

function setUp() {
  // 一個測試裡可能設定多次（例如分別看兩種 422）。
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
  const repository = new HybridDemoRepository(
    DEMO_SEED,
    { storage: createMemoryStorage(), viewer: () => 'account-smb-admin' },
    {
      http: TestBed.inject(HttpClient),
      viewerPermissions: () => ({
        accountId: '0199a3c0-0000-7000-8000-0000000000a1',
        demoAccountId: 'account-smb-admin',
        permissions: ['manage-assistants', 'manage-publishing'],
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

/** 讀 `GET …/publishing`，其中 `line` 換成指定的實際回應。 */
async function line(json: string): Promise<LineSetupView> {
  const { repository, controller } = setUp();
  const result = firstValueFrom(repository.getAssistantPublishing(ASSISTANT_ID));
  const aggregate = JSON.parse(REAL_LINE_AGGREGATE_SERVING_JSON);
  controller.expectOne({ method: 'GET', url: apiAssistantPublishingPath(ASSISTANT_ID) }).flush({ ...aggregate, line: JSON.parse(json) });
  const view = await result;
  controller.verify();
  if (view.status !== 'ready') throw new Error(`expected ready, got ${view.status}`);
  return view.data.line;
}

function readyData(outcome: { readonly status: string; readonly data?: LineSetupView }): LineSetupView {
  if (outcome.status !== 'ready' || outcome.data === undefined) throw new Error(`expected ready, got ${outcome.status}`);
  return outcome.data;
}

describe('HybridDemoRepository LINE channel (issue #233)', () => {
  describe('reading', () => {
    it('reads the real publishing response: a served LINE channel, with the real website and platform channels beside it', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.getAssistantPublishing(ASSISTANT_ID));
      respond(controller, 'GET', apiAssistantPublishingPath(ASSISTANT_ID), REAL_LINE_AGGREGATE_SERVING_JSON);

      const view = await result;
      if (view.status !== 'ready') throw new Error(`expected ready, got ${view.status}`);
      expect('availability' in view.data.line).toBe(false);
      expect(view.data.line).toMatchObject({
        channel: { type: 'line', name: 'LINE', status: 'published', statusDetail: '@anxin-demo 已啟用，可在 LINE 對話中使用。' },
        state: 'published',
        servingState: 'serving',
        acceptanceStatus: 'passed',
        revision: 1,
      });
      expect(view.data.website.channel.type).toBe('website');
      expect(JSON.stringify(view.data)).not.toContain('將於後續版本開放');
      controller.verify();
    });

    it('maps an assistant that never saved LINE settings: revision 0, default welcome message, three pending checks, the real webhook URL', async () => {
      const view = await line(REAL_LINE_UNSAVED_JSON);

      expect([view.revision, view.state, view.servingState, view.channel.status]).toEqual([0, 'draft', 'not-published', 'not-configured']);
      expect(view.officialAccountId).toBe('');
      expect(view.welcomeMessage).toBe('您好！有任何問題都可以直接問我。在群組裡請 @ 我再提問。');
      expect(view.checks.map((check) => [check.check, check.state])).toEqual([
        ['access-token', 'pending'],
        ['webhook-endpoint', 'pending'],
        ['webhook-test', 'pending'],
      ]);
      expect(view.connectionCheckedAt).toBeNull();
      expect(view.webhookUrl).toBe(`http://localhost:5153/api/v1/line/webhook/${ASSISTANT_ID}`);
      expect(view.channelSecret).toEqual({ configured: false, lastFour: null, updatedAt: null });
    });

    it('maps a server without a public address: webhookUrl null', async () => {
      expect((await line(REAL_LINE_UNSAVED_NO_URL_JSON)).webhookUrl).toBeNull();
    });

    it('maps saved settings with configured credentials as status only: configured, last four and time — nothing else', async () => {
      const view = await line(REAL_LINE_SAVED_JSON);

      expect(view).toMatchObject({
        officialAccountId: '@anxin-demo',
        channelId: '1650000000',
        welcomeMessage: '您好！歡迎加入。',
        channelSecret: { configured: true, lastFour: 'aaa1' },
        accessToken: { configured: true },
        channel: { status: 'testing' },
        revision: 1,
      });
      expect(Object.keys(view.channelSecret).sort()).toEqual(['configured', 'lastFour', 'updatedAt']);
      expect(Object.keys(view.accessToken).sort()).toEqual(['configured', 'lastFour', 'updatedAt']);
      expect(view.accessToken.lastFour).toHaveLength(4);
    });

    it('maps a failed token check with the other two skipped, in Traditional Chinese from the server', async () => {
      const view = await line(REAL_LINE_FAILED_TOKEN_JSON);

      expect(view.checks.map((check) => [check.check, check.state])).toEqual([
        ['access-token', 'failed'],
        ['webhook-endpoint', 'skipped'],
        ['webhook-test', 'skipped'],
      ]);
      expect(view.checks[0]).toMatchObject({ label: 'Channel access token 與官方帳號' });
      expect(view.checks[0].message).toContain('LINE 不接受這個 Channel access token');
      expect(view.checks[1].message).toBe('前一項檢查未通過，這一項沒有執行。');
      expect([view.channel.status, view.state, view.servingState]).toEqual(['needs-attention', 'draft', 'not-published']);
      expect(view.connectionCheckedAt).toBe('2026-10-06T10:03:48.605155+00:00');
    });

    it('maps a failed webhook test after two passed checks', async () => {
      const view = await line(REAL_LINE_FAILED_WEBHOOK_JSON);

      expect(view.checks.map((check) => check.state)).toEqual(['passed', 'passed', 'failed']);
      expect(view.checks[2].message).toContain('Channel secret');
    });

    it.each<[string, string, string, string, string]>([
      ['a draft whose three checks passed', REAL_LINE_TESTED_JSON, 'draft', 'not-published', 'testing'],
      ['a served channel', REAL_LINE_SERVING_JSON, 'published', 'serving', 'published'],
      ['a paused channel', REAL_LINE_PAUSED_JSON, 'paused', 'paused', 'paused'],
      ['a resumed channel', REAL_LINE_RESUMED_JSON, 'published', 'serving', 'published'],
      ['a published channel suspended for someone else’s knowledge base', REAL_LINE_SUSPENDED_KNOWLEDGE_JSON, 'published', 'suspended-knowledge', 'needs-attention'],
      ['an enabled channel whose credential was replaced', REAL_LINE_SAVED_REPLACED_JSON, 'draft', 'not-published', 'testing'],
      ['an unpublished channel', REAL_LINE_UNPUBLISHED_JSON, 'draft', 'not-published', 'testing'],
    ])('maps %s (state, serving state, card status)', async (_name, json, state, servingState, status) => {
      const view = await line(json);

      expect([view.state, view.servingState, view.channel.status]).toEqual([state, servingState, status]);
    });

    it('lists the knowledge base that is not the owner’s by name when the channel is suspended for it', async () => {
      const view = await line(REAL_LINE_SUSPENDED_KNOWLEDGE_JSON);

      expect(view.nonOwnedKnowledgeBases).toEqual([{ id: '01a110ab-5e9e-7b8b-9fc8-3c53a30e0dca', name: '同仁的公開知識庫' }]);
      expect(view.channel.statusDetail).toContain('已自動暫停 LINE 回覆');
    });

    it('shows the push fallback count the server reports', async () => {
      const body = JSON.parse(REAL_LINE_SERVING_JSON) as Record<string, unknown>;
      body['pushFallbackCount'] = 3;

      expect((await line(JSON.stringify(body))).pushFallbackCount).toBe(3);
      expect((await line(REAL_LINE_SERVING_JSON)).pushFallbackCount).toBe(0);
    });

    it.each(['webhookUrl', 'connectionCheckedAt', 'publishedAt'])('does not break when the server omits %s (a null the API leaves out)', async (field) => {
      const body = JSON.parse(REAL_LINE_FAILED_TOKEN_JSON) as Record<string, unknown>;
      delete body[field];
      const secret = body['channelSecret'] as Record<string, unknown>;
      delete secret['updatedAt'];

      const view = await line(JSON.stringify(body));

      expect(view[field as 'webhookUrl' | 'connectionCheckedAt' | 'publishedAt']).toBeNull();
      expect(view.channelSecret.updatedAt).toBeNull();
    });
  });

  describe('saving settings', () => {
    it('PUTs the form with the revision it read, sends a new credential only when typed, and never returns it', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.saveLineSettings(ASSISTANT_ID, { ...SETTINGS, channelSecret: '', accessToken: 'N'.repeat(40) }, 1));

      const request = respond(controller, 'PUT', LINE_PATH, REAL_LINE_SAVED_REPLACED_JSON);

      expect(request.body).toEqual({ ...SETTINGS, revision: 1, accessToken: 'N'.repeat(40) });
      expect('channelSecret' in (request.body as object)).toBe(false);
      const saved = readyData(await result);
      expect(saved).toMatchObject({ revision: 2, state: 'draft', accessToken: { configured: true, lastFour: '777a' } });
      expect(JSON.stringify(saved)).not.toContain('N'.repeat(40));
      controller.verify();
    });

    it('sends neither credential when both are empty (keep what is stored)', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.saveLineSettings(ASSISTANT_ID, { ...SETTINGS, channelSecret: '', accessToken: undefined }, 1));

      const request = respond(controller, 'PUT', LINE_PATH, REAL_LINE_SAVED_JSON);

      expect(request.body).toEqual({ ...SETTINGS, revision: 1 });
      expect(await result).toMatchObject({ status: 'ready' });
    });

    it('turns the real 409 into a conflict carrying the server’s sentence', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.saveLineSettings(ASSISTANT_ID, SETTINGS, 0));
      respond(controller, 'PUT', LINE_PATH, REAL_LINE_REVISION_CONFLICT_JSON, 409);

      expect(await result).toEqual({ status: 'conflict', message: 'LINE 頻道的設定已在其他分頁被更新過，請重新載入後再修改。' });
    });

    it('turns the real 422 into one error per field, keeping every message', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(
        repository.saveLineSettings(ASSISTANT_ID, { officialAccountId: 'anxin', channelId: '123', welcomeMessage: '', channelSecret: 'xyz', accessToken: 'short' }, 0),
      );
      respond(controller, 'PUT', LINE_PATH, REAL_LINE_INVALID_JSON, 422);

      const outcome = await result;
      if (outcome.status !== 'validation-failed') throw new Error(`expected validation-failed, got ${outcome.status}`);
      expect(outcome.errors).toEqual([
        { field: 'accessToken', message: 'Channel access token至少 40 個字元且不含空白。' },
        { field: 'channelId', message: 'Channel ID應為 10 位數字。' },
        { field: 'channelSecret', message: 'Channel secret應為 32 個英數字（0–9、a–f）。' },
        { field: 'officialAccountId', message: '官方帳號 ID需以 @ 開頭，接 3–20 個英數字，例如 @anxin-demo。' },
        { field: 'welcomeMessage', message: '請填寫歡迎訊息。' },
      ]);
    });

    it('lets a server failure surface as an error, and maps 403 to the publishing permission-denied view', async () => {
      const first = setUp();
      const failed = firstValueFrom(first.repository.saveLineSettings(ASSISTANT_ID, SETTINGS, 1));
      first.controller.expectOne(LINE_PATH).flush('boom', { status: 500, statusText: 'Server Error' });
      await expect(failed).rejects.toMatchObject({ status: 500 });

      const second = setUp();
      const denied = firstValueFrom(second.repository.saveLineSettings(ASSISTANT_ID, SETTINGS, 1));
      second.controller.expectOne(LINE_PATH).flush({ reason: 'publishing', message: '沒有權限' }, { status: 403, statusText: 'Forbidden' });
      expect(await denied).toMatchObject({ status: 'permission-denied', reason: 'publishing' });
    });
  });

  describe('testing the connection', () => {
    it('POSTs :test and maps three passed checks', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.testLineConnection(ASSISTANT_ID));

      const request = respond(controller, 'POST', `${LINE_PATH}:test`, REAL_LINE_TEST_PASSED_JSON);

      expect(request.body).toEqual({});
      const view = readyData(await result);
      expect(view.checks.map((check) => check.state)).toEqual(['passed', 'passed', 'passed']);
      expect(view.checks[1].message).toContain(`/api/v1/line/webhook/${ASSISTANT_ID}`);
      expect(view.channel.status).toBe('testing');
    });

    it('treats LINE failing as a ready view with failed and skipped checks, not as an error', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.testLineConnection(ASSISTANT_ID));
      respond(controller, 'POST', `${LINE_PATH}:test`, REAL_LINE_FAILED_TOKEN_JSON);

      expect(readyData(await result).checks.map((check) => check.state)).toEqual(['failed', 'skipped', 'skipped']);
    });

    it('turns the real 422 line-test-refused into one failure per reason (settings, public address)', async () => {
      const first = setUp();
      const noSettings = firstValueFrom(first.repository.testLineConnection(ASSISTANT_ID));
      respond(first.controller, 'POST', `${LINE_PATH}:test`, REAL_LINE_TEST_REFUSED_SETTINGS_JSON, 422);
      expect(await noSettings).toEqual({
        status: 'test-refused',
        message: '目前還不能測試連線，請先處理下列項目。',
        failures: [{ reason: 'settings', message: '請先填寫並儲存 LINE 官方帳號的連接資訊，再測試連線。' }],
      });

      const second = setUp();
      const noUrl = firstValueFrom(second.repository.testLineConnection(ASSISTANT_ID));
      respond(second.controller, 'POST', `${LINE_PATH}:test`, REAL_LINE_TEST_REFUSED_PUBLIC_BASE_URL_JSON, 422);
      const refused = await noUrl;
      if (refused.status !== 'test-refused') throw new Error(`expected test-refused, got ${refused.status}`);
      expect(refused.failures).toEqual([{ reason: 'public-base-url', message: expect.stringContaining('請洽系統管理者') }]);
    });

    it('turns the real 409 (settings saved while LINE was being called) into a conflict', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.testLineConnection(ASSISTANT_ID));
      respond(controller, 'POST', `${LINE_PATH}:test`, REAL_LINE_TEST_CONFLICT_JSON, 409);

      expect(await result).toEqual({
        status: 'conflict',
        message: '測試連線期間 LINE 頻道的設定已在其他分頁被更新過，請重新載入後再測試一次。',
      });
    });

    it('lets any other failure surface as an error', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.testLineConnection(ASSISTANT_ID));
      controller.expectOne(`${LINE_PATH}:test`).flush({ message: 'x' }, { status: 422, statusText: 'Unprocessable' });

      await expect(result).rejects.toMatchObject({ status: 422 });
    });
  });

  describe('enabling gate', () => {
    it('POSTs :publish and maps the published channel', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.publishLine(ASSISTANT_ID));

      const request = respond(controller, 'POST', `${LINE_PATH}:publish`, REAL_LINE_PUBLISHED_JSON);

      expect(request.body).toEqual({});
      expect(await result).toMatchObject({
        status: 'ready',
        data: { state: 'published', servingState: 'serving', channel: { status: 'published' } },
      });
    });

    it('turns the real 422 with several reasons into one failure per message, in the server’s order', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.publishLine(ASSISTANT_ID));
      respond(controller, 'POST', `${LINE_PATH}:publish`, REAL_LINE_PUBLISH_REFUSED_SEVERAL_JSON, 422);

      const outcome = await result;
      if (outcome.status !== 'publish-refused') throw new Error(`expected a refusal, got ${outcome.status}`);
      expect(outcome.message).toBe('目前還不能啟用 LINE 頻道，請先處理下列項目。');
      expect(outcome.failures.map((failure) => failure.reason)).toEqual([
        'connection',
        'acceptance',
        'assistant-paused',
        'knowledge-ownership',
        'knowledge-ownership',
      ]);
      expect(outcome.failures[0].message).toContain('請先測試連線');
      expect(outcome.failures[1].message).toContain('驗收狀態必須是「通過」');
      expect(outcome.failures[3].message).toContain('「同仁的筆記」');
      expect(outcome.failures[4].message).toContain('「同仁的手冊」');
    });

    it('maps the real 422 of a failed test (only connection) and of a server without a public address (never tested either)', async () => {
      const first = setUp();
      const failed = firstValueFrom(first.repository.publishLine(ASSISTANT_ID));
      respond(first.controller, 'POST', `${LINE_PATH}:publish`, REAL_LINE_PUBLISH_REFUSED_FAILED_TEST_JSON, 422);
      const failedTest = await failed;
      if (failedTest.status !== 'publish-refused') throw new Error(`expected a refusal, got ${failedTest.status}`);
      expect(failedTest.failures.map((failure) => failure.reason)).toEqual(['connection']);

      const second = setUp();
      const noUrl = firstValueFrom(second.repository.publishLine(ASSISTANT_ID));
      respond(second.controller, 'POST', `${LINE_PATH}:publish`, REAL_LINE_PUBLISH_REFUSED_PUBLIC_BASE_URL_JSON, 422);
      const noPublicUrl = await noUrl;
      if (noPublicUrl.status !== 'publish-refused') throw new Error(`expected a refusal, got ${noPublicUrl.status}`);
      expect(noPublicUrl.failures.map((failure) => failure.reason)).toEqual(['connection', 'public-base-url']);
      expect(noPublicUrl.failures[1].message).toContain('PublicChannels:PublicBaseUrl');
    });

    it('keeps a reason added later as other, with its message', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.publishLine(ASSISTANT_ID));
      controller
        .expectOne(`${LINE_PATH}:publish`)
        .flush({ message: '目前還不能啟用 LINE 頻道，請先處理下列項目。', errors: { 'a-reason-added-later': ['新的原因'] } }, { status: 422, statusText: 'Unprocessable' });

      const outcome = await result;
      if (outcome.status !== 'publish-refused') throw new Error(`expected a refusal, got ${outcome.status}`);
      expect(outcome.failures).toEqual([{ reason: 'other', message: '新的原因' }]);
    });

    it('lets a 422 without reasons or a server failure surface as an error', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.publishLine(ASSISTANT_ID));
      controller.expectOne(`${LINE_PATH}:publish`).flush({ message: 'x' }, { status: 422, statusText: 'Unprocessable' });

      await expect(result).rejects.toMatchObject({ status: 422 });
    });
  });

  describe('pause, resume and unpublish', () => {
    it('pauses with PUT …/line/paused — really sent, no longer refused — and returns the channel card from the real response', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.setPublishingChannelPaused(ASSISTANT_ID, 'line', true));

      const request = respond(controller, 'PUT', `${LINE_PATH}/paused`, REAL_LINE_PAUSED_JSON);

      expect(request.body).toEqual({ paused: true });
      expect(await result).toMatchObject({
        status: 'ready',
        data: { type: 'line', name: 'LINE', status: 'paused', statusDetail: '已暫停：LINE 使用者目前會收到「暫停服務」，設定會保留。' },
      });
    });

    it('resumes with the same endpoint', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.setPublishingChannelPaused(ASSISTANT_ID, 'line', false));

      const request = respond(controller, 'PUT', `${LINE_PATH}/paused`, REAL_LINE_RESUMED_JSON);

      expect(request.body).toEqual({ paused: false });
      expect(await result).toMatchObject({ status: 'ready', data: { status: 'published' } });
    });

    it('lets the real 422 for a channel that is not enabled surface as an error', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.setPublishingChannelPaused(ASSISTANT_ID, 'line', true));
      respond(controller, 'PUT', `${LINE_PATH}/paused`, REAL_LINE_NOT_PUBLISHED_JSON, 422);

      await expect(result).rejects.toMatchObject({ status: 422 });
    });

    it('unpublishes with POST :unpublish and maps the draft that keeps its settings and test results', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.unpublishLine(ASSISTANT_ID));

      respond(controller, 'POST', `${LINE_PATH}:unpublish`, REAL_LINE_UNPUBLISHED_JSON);

      expect(await result).toMatchObject({
        status: 'ready',
        data: {
          state: 'draft',
          servingState: 'not-published',
          channel: { status: 'testing' },
          officialAccountId: '@anxin-demo',
          checks: [{ state: 'passed' }, { state: 'passed' }, { state: 'passed' }],
        },
      });
    });
  });
});
