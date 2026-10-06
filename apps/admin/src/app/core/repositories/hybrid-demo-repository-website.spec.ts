import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { WebsiteEmbedSettings } from '../domain/publishing.model';
import { DEMO_SEED } from './demo-seed';
import { apiAssistantPublishingPath, apiAssistantWebsitePath, HybridDemoRepository } from './hybrid-demo-repository';
import { createMemoryStorage } from './memory-storage';
import {
  REAL_AGGREGATE_SERVING_JSON,
  REAL_DRAFT_JSON,
  REAL_INVALID_JSON,
  REAL_NOT_PUBLISHED_JSON,
  REAL_PAUSED_JSON,
  REAL_PUBLISHED_JSON,
  REAL_PUBLISH_REFUSED_BARE_JSON,
  REAL_PUBLISH_REFUSED_SEVERAL_JSON,
  REAL_RESUMED_JSON,
  REAL_REVISION_CONFLICT_JSON,
  REAL_SAVED_JSON,
  REAL_SERVING_JSON,
  REAL_SUSPENDED_ACCEPTANCE_JSON,
  REAL_SUSPENDED_KNOWLEDGE_JSON,
  REAL_UNPUBLISHED_JSON,
  REAL_UNSAVED_JSON,
} from './website-channel-api.testing';

/*
 * 網站頻道在 API 模式（issue #202）：回應都是 `website-channel-api.testing.ts` 裡實際從 API 取得的 JSON，
 * 原樣 `JSON.parse`，不是依產生的型別手寫（後端的 `JsonIgnore(WhenWritingNull)` 欄位可能省略，型別卻寫成必填）。
 */
const ASSISTANT_ID = '01a10f6b-7b6c-75e3-a63d-6d16f88dec74';
const WEBSITE_PATH = apiAssistantWebsitePath(ASSISTANT_ID);

const SETTINGS: WebsiteEmbedSettings = {
  displayName: '安心客服',
  welcomeMessage: '您好，有什麼可以協助？',
  brandColor: 'ocean',
  position: 'bottom-right',
  allowedDomains: ['shop.example.com', 'www.example.com'],
};

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

async function website(json: string) {
  const { repository, controller } = setUp();
  const result = firstValueFrom(repository.getAssistantPublishing(ASSISTANT_ID));
  const aggregate = JSON.parse(REAL_AGGREGATE_SERVING_JSON);
  controller.expectOne({ method: 'GET', url: apiAssistantPublishingPath(ASSISTANT_ID) }).flush({ ...aggregate, website: JSON.parse(json) });
  const view = await result;
  controller.verify();
  if (view.status !== 'ready') throw new Error(`expected ready, got ${view.status}`);
  return view.data.website;
}

describe('HybridDemoRepository website channel (issue #202)', () => {
  describe('reading', () => {
    it('reads the real publishing response: real website channel, LINE not available, nothing of the #194 shim left', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.getAssistantPublishing(ASSISTANT_ID));
      respond(controller, 'GET', apiAssistantPublishingPath(ASSISTANT_ID), REAL_AGGREGATE_SERVING_JSON);

      const view = await result;
      if (view.status !== 'ready') throw new Error(`expected ready, got ${view.status}`);
      expect('availability' in view.data.website).toBe(false);
      expect(view.data.website).toMatchObject({
        channel: { type: 'website', name: '官網嵌入', status: 'published', statusDetail: '已發布，官網訪客可以使用。' },
        state: 'published',
        servingState: 'serving',
        acceptanceStatus: 'passed',
        revision: 1,
      });
      expect(view.data.line).toMatchObject({ availability: 'not-available', channel: { type: 'line', status: 'not-configured' } });
      expect(JSON.stringify(view.data)).not.toContain('官網嵌入與 LINE');
      controller.verify();
    });

    it('maps a served channel as is: settings, last-seen time per domain, the server’s embed code and publication', async () => {
      const view = await website(REAL_SERVING_JSON);

      expect(view).toMatchObject({
        displayName: '安心客服二號',
        welcomeMessage: '您好，有什麼可以協助？',
        brandColor: 'ocean',
        position: 'bottom-right',
        allowedDomains: ['shop.example.com', 'www.example.com'],
        state: 'published',
        servingState: 'serving',
        nonOwnedKnowledgeBases: [],
        embedCode: `<script src="http://localhost:5153/embed.js" data-assistant="${ASSISTANT_ID}" async></script>`,
        publishedAt: '2026-10-06T04:15:57.634032+00:00',
        revision: 1,
      });
      expect(view.domains).toEqual([
        { domain: 'shop.example.com', lastSeenAt: '2026-10-06T04:10:57.637115+00:00' },
        { domain: 'www.example.com', lastSeenAt: null },
      ]);
      expect(view.channel).toMatchObject({ id: `channel-website:${ASSISTANT_ID}`, name: '官網嵌入', type: 'website' });
    });

    it('maps a draft to the testing card with nothing published', async () => {
      const view = await website(REAL_DRAFT_JSON);

      expect([view.state, view.servingState, view.channel.status, view.acceptanceStatus, view.publishedAt]).toEqual([
        'draft',
        'not-published',
        'testing',
        'not-accepted',
        null,
      ]);
      expect(view.channel.statusDetail).toContain('尚未發布');
    });

    it('maps an assistant that never saved the settings: revision 0, not configured, default values', async () => {
      const view = await website(REAL_UNSAVED_JSON);

      expect([view.revision, view.state, view.channel.status, view.allowedDomains, view.domains]).toEqual([
        0,
        'draft',
        'not-configured',
        [],
        [],
      ]);
      expect(view.displayName).toBe('客服助理');
    });

    it('maps a published channel suspended for someone else’s knowledge base to needs-attention, with the knowledge base named', async () => {
      const view = await website(REAL_SUSPENDED_KNOWLEDGE_JSON);

      expect([view.state, view.servingState, view.channel.status]).toEqual(['published', 'suspended-knowledge', 'needs-attention']);
      expect(view.nonOwnedKnowledgeBases).toEqual([{ id: '01a10f6b-7b9e-7567-be1a-3ed6855985a6', name: '同仁的公開知識庫三' }]);
      expect(view.channel.statusDetail).toContain('不是助理擁有者自己的知識庫');
    });

    it('maps a published channel suspended for a failed acceptance to needs-attention', async () => {
      const view = await website(REAL_SUSPENDED_ACCEPTANCE_JSON);

      expect([view.servingState, view.channel.status, view.acceptanceStatus]).toEqual(['suspended-acceptance', 'needs-attention', 'failed']);
      expect(view.channel.statusDetail).toContain('驗收未通過');
    });

    it('maps a paused channel to the paused card', async () => {
      const view = await website(REAL_PAUSED_JSON);

      expect([view.state, view.servingState, view.channel.status]).toEqual(['paused', 'paused', 'paused']);
    });

    it.each(['embedCode', 'publishedAt'])('does not break when the server omits %s (a null the API leaves out)', async (field) => {
      const body = JSON.parse(REAL_DRAFT_JSON) as Record<string, unknown>;
      delete body[field];
      const domains = body['domains'] as Record<string, unknown>[];
      delete domains[0]['lastSeenAt'];

      const view = await website(JSON.stringify(body));

      expect(view[field as 'embedCode' | 'publishedAt']).toBeNull();
      expect(view.domains[0].lastSeenAt).toBeNull();
    });
  });

  describe('saving settings', () => {
    it('PUTs the whole form with the revision it read, and maps the saved channel', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.updateWebsiteEmbed(ASSISTANT_ID, SETTINGS, 0));

      const request = respond(controller, 'PUT', WEBSITE_PATH, REAL_SAVED_JSON);

      expect(request.body).toEqual({ ...SETTINGS, revision: 0 });
      const saved = await result;
      expect(saved).toMatchObject({ status: 'ready', data: { revision: 1, state: 'draft', channel: { status: 'testing' } } });
      controller.verify();
    });

    it('turns the real 409 into a conflict carrying the server’s sentence', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.updateWebsiteEmbed(ASSISTANT_ID, SETTINGS, 0));
      respond(controller, 'PUT', WEBSITE_PATH, REAL_REVISION_CONFLICT_JSON, 409);

      expect(await result).toEqual({ status: 'conflict', message: '官網嵌入的設定已在其他分頁被更新過，請重新載入後再修改。' });
    });

    it('turns the real 422 into one error per field, keeping every message', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.updateWebsiteEmbed(ASSISTANT_ID, SETTINGS, 1));
      respond(controller, 'PUT', WEBSITE_PATH, REAL_INVALID_JSON, 422);

      const outcome = await result;
      expect(outcome.status).toBe('validation-failed');
      if (outcome.status !== 'validation-failed') return;
      expect(outcome.errors).toEqual([
        { field: 'allowedDomains', message: '只需要填網域，不要包含 https:// 或路徑，例如 shop.example.com。' },
        { field: 'brandColor', message: '請選擇品牌色。' },
        { field: 'displayName', message: '請填寫顯示名稱。' },
        { field: 'position', message: '請選擇顯示位置。' },
        { field: 'welcomeMessage', message: '請填寫歡迎語。' },
      ]);
    });

    it('lets a server failure surface as an error', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.updateWebsiteEmbed(ASSISTANT_ID, SETTINGS, 1));
      controller.expectOne(WEBSITE_PATH).flush('boom', { status: 500, statusText: 'Server Error' });

      await expect(result).rejects.toMatchObject({ status: 500 });
    });

    it('maps 403 to the publishing permission-denied view', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.updateWebsiteEmbed(ASSISTANT_ID, SETTINGS, 1));
      controller.expectOne(WEBSITE_PATH).flush({ reason: 'publishing', message: '沒有權限' }, { status: 403, statusText: 'Forbidden' });

      expect(await result).toMatchObject({ status: 'permission-denied', reason: 'publishing' });
    });
  });

  describe('publishing gate', () => {
    it('POSTs :publish and maps the published channel', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.publishWebsite(ASSISTANT_ID));

      const request = respond(controller, 'POST', `${WEBSITE_PATH}:publish`, REAL_PUBLISHED_JSON);

      expect(request.body).toEqual({});
      expect(await result).toMatchObject({
        status: 'ready',
        data: { state: 'published', servingState: 'serving', channel: { status: 'published' }, publishedAt: '2026-10-06T04:15:57.634032+00:00' },
      });
    });

    it('turns the real 422 with several reasons into one failure per message (each other-people knowledge base named)', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.publishWebsite(ASSISTANT_ID));
      respond(controller, 'POST', `${WEBSITE_PATH}:publish`, REAL_PUBLISH_REFUSED_SEVERAL_JSON, 422);

      const outcome = await result;
      expect(outcome.status).toBe('publish-refused');
      if (outcome.status !== 'publish-refused') return;
      expect(outcome.message).toBe('目前還不能對外發布，請先處理下列項目。');
      expect(outcome.failures.map((failure) => failure.reason)).toEqual(['acceptance', 'knowledge-ownership', 'knowledge-ownership']);
      expect(outcome.failures[0].message).toContain('驗收狀態必須是「通過」');
      expect(outcome.failures[1].message).toContain('「同仁的公開知識庫」');
      expect(outcome.failures[2].message).toContain('「同仁的筆記」');
    });

    it('maps the real 422 for a draft with no allowed domain', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.publishWebsite(ASSISTANT_ID));
      respond(controller, 'POST', `${WEBSITE_PATH}:publish`, REAL_PUBLISH_REFUSED_BARE_JSON, 422);

      const outcome = await result;
      if (outcome.status !== 'publish-refused') throw new Error(`expected a refusal, got ${outcome.status}`);
      expect(outcome.failures.map((failure) => failure.reason)).toEqual(['acceptance', 'allowed-domains']);
    });

    it('maps the other reasons the gate can give: assistant paused, no public address, and a reason added later', async () => {
      const { repository, controller } = setUp();
      const other = firstValueFrom(repository.publishWebsite(ASSISTANT_ID));
      controller.expectOne(`${WEBSITE_PATH}:publish`).flush(
        {
          message: '目前還不能對外發布，請先處理下列項目。',
          errors: {
            'assistant-paused': ['助理目前已暫停，請先恢復助理再發布。'],
            'public-base-url': ['伺服器尚未設定對外網址（PublicChannels:PublicBaseUrl），無法產生嵌入程式碼；請洽系統管理者。'],
            'a-reason-added-later': ['新的原因'],
          },
        },
        { status: 422, statusText: 'Unprocessable' },
      );
      const mixed = await other;
      if (mixed.status !== 'publish-refused') throw new Error(`expected a refusal, got ${mixed.status}`);
      expect(mixed.failures.map((failure) => [failure.reason, failure.message])).toEqual([
        ['assistant-paused', '助理目前已暫停，請先恢復助理再發布。'],
        ['public-base-url', '伺服器尚未設定對外網址（PublicChannels:PublicBaseUrl），無法產生嵌入程式碼；請洽系統管理者。'],
        ['other', '新的原因'],
      ]);
    });

    it('lets any other 422 or server failure surface as an error', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.publishWebsite(ASSISTANT_ID));
      controller.expectOne(`${WEBSITE_PATH}:publish`).flush({ message: 'x' }, { status: 422, statusText: 'Unprocessable' });

      await expect(result).rejects.toMatchObject({ status: 422 });
    });
  });

  describe('pause, resume and unpublish', () => {
    it('pauses with PUT …/paused and returns the channel card from the real response', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.setPublishingChannelPaused(ASSISTANT_ID, 'website', true));

      const request = respond(controller, 'PUT', `${WEBSITE_PATH}/paused`, REAL_PAUSED_JSON);

      expect(request.body).toEqual({ paused: true });
      expect(await result).toMatchObject({
        status: 'ready',
        data: { type: 'website', name: '官網嵌入', status: 'paused', statusDetail: '已暫停：官網訪客目前看到「暫停服務」，設定會保留。' },
      });
    });

    it('resumes with the same endpoint', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.setPublishingChannelPaused(ASSISTANT_ID, 'website', false));

      const request = respond(controller, 'PUT', `${WEBSITE_PATH}/paused`, REAL_RESUMED_JSON);

      expect(request.body).toEqual({ paused: false });
      expect(await result).toMatchObject({ status: 'ready', data: { status: 'published' } });
    });

    it('lets the real 422 for a channel that is not published surface as an error', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.setPublishingChannelPaused(ASSISTANT_ID, 'website', true));
      respond(controller, 'PUT', `${WEBSITE_PATH}/paused`, REAL_NOT_PUBLISHED_JSON, 422);

      await expect(result).rejects.toMatchObject({ status: 422 });
    });

    it('unpublishes with POST :unpublish and maps the draft', async () => {
      const { repository, controller } = setUp();
      const result = firstValueFrom(repository.unpublishWebsite(ASSISTANT_ID));

      respond(controller, 'POST', `${WEBSITE_PATH}:unpublish`, REAL_UNPUBLISHED_JSON);

      expect(await result).toMatchObject({
        status: 'ready',
        data: { state: 'draft', servingState: 'not-published', channel: { status: 'testing' }, allowedDomains: ['shop.example.com', 'www.example.com'] },
      });
    });

    it('still refuses LINE without calling the API', async () => {
      const { repository, controller } = setUp();

      expect(await firstValueFrom(repository.setPublishingChannelPaused(ASSISTANT_ID, 'line', true))).toMatchObject({
        status: 'permission-denied',
        reason: 'publishing',
      });
      controller.verify();
    });
  });
});
