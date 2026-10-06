import { firstValueFrom } from 'rxjs';
import type { AccountId } from '../domain/account.model';
import type {
  AssistantPublishingView,
  ConfigurableAssistantPublishingView,
  LineSettingsInput,
  PublishingChannelStatus,
  PublishingChannelType,
  WebsiteEmbedSettings,
} from '../domain/publishing.model';
import type { RepositoryView } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import { DEMO_LINE_CHANNEL_ID, DEMO_LINE_CHANNEL_SECRET } from './demo-seed-publishing';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';

const ADMIN: AccountId = 'account-smb-admin';
const CUSTOMER_SERVICE = 'assistant-customer-service';
const ONBOARDING = 'assistant-internal-onboarding';
// 沿用種子資料裡同一組假 channelId/channelSecret（已是組合出來的假值，見 demo-seed-publishing.ts）。
const VALID_LINE: LineSettingsInput = {
  officialAccountId: '@anxin-demo',
  channelId: DEMO_LINE_CHANNEL_ID,
  channelSecret: DEMO_LINE_CHANNEL_SECRET,
  accessToken: 'demo-token-not-for-production-0123456789abcdefghij',
};

/** `listChannelOverview`／`getAssistantPublishing` 等的非同步契約不再接收 viewer；用這個 box 切換。 */
const viewerBoxes = new WeakMap<MockDemoRepository, { current: AccountId | null }>();

function createRepository(storage = createMemoryStorage()) {
  const box: { current: AccountId | null } = { current: ADMIN };
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage,
    now: () => new Date('2026-09-22T02:00:00.000Z'),
    viewer: () => box.current,
  });
  viewerBoxes.set(repository, box);
  return repository;
}

function boxOf(repository: MockDemoRepository) {
  const box = viewerBoxes.get(repository);
  if (box === undefined) throw new Error('unknown repository: use createRepository()');
  return box;
}

async function overviewAs(repository: MockDemoRepository, viewer: AccountId | null) {
  boxOf(repository).current = viewer;
  return firstValueFrom(repository.listChannelOverview());
}

async function publishingAs(repository: MockDemoRepository, viewer: AccountId | null, assistantId: string) {
  boxOf(repository).current = viewer;
  return firstValueFrom(repository.getAssistantPublishing(assistantId));
}

async function updatePlatformSharingAs(
  repository: MockDemoRepository,
  viewer: AccountId | null,
  assistantId: string,
  accountIds: readonly AccountId[],
) {
  boxOf(repository).current = viewer;
  return firstValueFrom(repository.updatePlatformSharing(assistantId, accountIds));
}

async function setPublishingChannelPausedAs(
  repository: MockDemoRepository,
  viewer: AccountId | null,
  assistantId: string,
  channelType: PublishingChannelType,
  paused: boolean,
) {
  boxOf(repository).current = viewer;
  return firstValueFrom(repository.setPublishingChannelPaused(assistantId, channelType, paused));
}

function dataOf<T>(result: RepositoryView<T> | { status: 'validation-failed' | 'conflict' | 'publish-refused' }): T {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

function statusesOf(view: AssistantPublishingView): Record<string, PublishingChannelStatus> {
  return {
    platform: view.platform.channel.status,
    website: view.website.channel.status,
    line: view.line.channel.status,
  };
}

/**
 * mock 模式一律回傳完整的 `WebsiteEmbedView`／`LineSetupView`（官網與 LINE 的
 * `UnavailablePublishingChannelView` 只在 API 模式出現），所以在讀取可設定欄位時
 * 安全地窄化成 `ConfigurableAssistantPublishingView`。
 */
function asConfigurable(view: AssistantPublishingView): ConfigurableAssistantPublishingView {
  return view as ConfigurableAssistantPublishingView;
}

function websiteSettings(view: AssistantPublishingView, patch: Partial<WebsiteEmbedSettings> = {}): WebsiteEmbedSettings {
  const { displayName, welcomeMessage, brandColor, position, allowedDomains } = asConfigurable(view).website;
  return { displayName, welcomeMessage, brandColor, position, allowedDomains, ...patch };
}

describe('MockDemoRepository publishing channels', () => {
  it('lists three channels per owned assistant covering all five unified statuses', async () => {
    const overview = dataOf(await overviewAs(createRepository(), ADMIN));

    expect(overview.map((entry) => entry.assistantName)).toEqual(['客服助理', '內部教育訓練助理']);
    for (const entry of overview) {
      expect(entry.channels.map((channel) => channel.type)).toEqual(['platform', 'website', 'line']);
      expect(entry.channels.map((channel) => channel.name)).toEqual(['組織內部分享', '官網嵌入', 'LINE']);
    }
    const statuses = new Set(overview.flatMap((entry) => entry.channels.map((channel) => channel.status)));
    expect([...statuses].sort()).toEqual(['needs-attention', 'not-configured', 'paused', 'published', 'testing']);
  });

  it('isolates a failing LINE channel from the platform and website channels', async () => {
    const view = dataOf(await publishingAs(createRepository(), ADMIN, CUSTOMER_SERVICE));

    expect(statusesOf(view)).toEqual({ platform: 'published', website: 'published', line: 'needs-attention' });
    expect(view.line.channel.statusDetail).toContain('其他管道不受影響');
    expect(asConfigurable(view).line.checks.find((check) => check.field === 'accessToken')).toMatchObject({
      state: 'failed',
    });
  });

  it('marks only the website channel when the disconnected-channel scenario is active', async () => {
    const repository = createRepository();
    repository.setScenario('disconnected-channel');
    const disconnected = dataOf(await publishingAs(repository, ADMIN, CUSTOMER_SERVICE));
    repository.resetScenario();
    const restored = dataOf(await publishingAs(repository, ADMIN, CUSTOMER_SERVICE));

    expect(disconnected.website.channel.status).toBe('needs-attention');
    expect(disconnected.platform.channel.status).toBe('published');
    expect(restored.website.channel.status).toBe('published');
  });

  it('denies other accounts and unknown ids with the same message that names nothing', async () => {
    const repository = createRepository();
    const otherAccount = await publishingAs(repository, 'account-internal-employee', CUSTOMER_SERVICE);
    const unknown = await publishingAs(repository, ADMIN, 'assistant-missing');

    expect(otherAccount).toEqual(unknown);
    expect(otherAccount).toMatchObject({ status: 'permission-denied', reason: 'publishing' });
    expect(JSON.stringify(otherAccount)).not.toContain('客服助理');
    expect(dataOf(await overviewAs(repository, 'account-internal-employee'))).toEqual([]);
    expect(
      await updatePlatformSharingAs(repository, 'account-external-customer', CUSTOMER_SERVICE, []),
    ).toMatchObject({
      status: 'permission-denied',
    });
    expect(repository.saveLineSettings('account-internal-employee', CUSTOMER_SERVICE, VALID_LINE)).toMatchObject({
      status: 'permission-denied',
    });
  });

  it('restricts platform sharing to known accounts and treats an empty list as not configured', async () => {
    const repository = createRepository();
    const initial = dataOf(await publishingAs(repository, ADMIN, CUSTOMER_SERVICE)).platform;
    expect(initial.candidates.map((candidate) => candidate.id)).toEqual([
      'account-internal-employee',
      'account-external-customer',
    ]);
    expect(initial.usagePath).toBe('/use/assistant-customer-service');

    const invalid = await updatePlatformSharingAs(repository, ADMIN, CUSTOMER_SERVICE, [ADMIN]);
    expect(invalid).toMatchObject({ status: 'validation-failed' });

    const cleared = dataOf(await updatePlatformSharingAs(repository, ADMIN, CUSTOMER_SERVICE, []));
    expect(cleared.channel.status).toBe('not-configured');
    const shared = dataOf(
      await updatePlatformSharingAs(repository, ADMIN, CUSTOMER_SERVICE, ['account-internal-employee']),
    );
    expect(shared.allowedAccountIds).toEqual(['account-internal-employee']);
    expect(shared.channel.status).toBe('published');
  });

  it('validates allowed domains, saves with the revision it read and keeps what was last seen on a kept domain', async () => {
    const repository = createRepository();
    const publishing = dataOf(await publishingAs(repository, ADMIN, CUSTOMER_SERVICE));
    const view = publishing.website;
    expect(view.embedCode).toContain('Demo');
    expect(view.embedCode).toContain('.invalid');
    expect(view.revision).toBe(1);

    const invalid = await firstValueFrom(
      repository.updateWebsiteEmbed(
        CUSTOMER_SERVICE,
        websiteSettings(publishing, { displayName: ' ', allowedDomains: ['https://shop.example.com/path'] }),
        view.revision,
      ),
    );
    expect(invalid).toMatchObject({ status: 'validation-failed' });
    if (invalid.status === 'validation-failed') {
      expect(invalid.errors.map((error) => error.field)).toEqual(['displayName', 'allowedDomains']);
    }

    const saved = dataOf(
      await firstValueFrom(
        repository.updateWebsiteEmbed(
          CUSTOMER_SERVICE,
          websiteSettings(publishing, { allowedDomains: [...view.allowedDomains, 'Blog.Anxin-Demo.Example '] }),
          view.revision,
        ),
      ),
    );
    expect(saved.allowedDomains).toContain('blog.anxin-demo.example');
    expect(saved.revision).toBe(2);
    expect(saved.domains.map((domain) => [domain.domain, domain.lastSeenAt !== null])).toEqual([
      ['shop.anxin-demo.example', true],
      ['blog.anxin-demo.example', false],
    ]);
    expect(saved.channel.status).toBe('published');
  });

  it('checks LINE fields one by one, then requires a delivered test message before activation', () => {
    const repository = createRepository();
    const partial = dataOf(
      repository.saveLineSettings(ADMIN, ONBOARDING, { ...VALID_LINE, channelId: '123', channelSecret: '' }),
    );
    expect(partial.checks.map((check) => [check.field, check.state])).toEqual([
      ['officialAccountId', 'passed'],
      ['channelId', 'failed'],
      ['channelSecret', 'failed'],
      ['accessToken', 'passed'],
    ]);
    expect(partial.channel.status).toBe('needs-attention');
    expect(partial.canSendTest).toBe(false);

    const blocked = dataOf(repository.sendLineTestMessage(ADMIN, ONBOARDING));
    expect(blocked.lastTest?.outcome).toBe('failed');
    expect(repository.activateLineChannel(ADMIN, ONBOARDING)).toMatchObject({ status: 'validation-failed' });

    const valid = dataOf(repository.saveLineSettings(ADMIN, ONBOARDING, VALID_LINE));
    expect(valid.checks.every((check) => check.state === 'passed')).toBe(true);
    expect(valid.channel.status).toBe('testing');

    const tested = dataOf(repository.sendLineTestMessage(ADMIN, ONBOARDING));
    expect(tested.lastTest).toMatchObject({ outcome: 'delivered' });
    expect(tested.lastTest?.message).toContain('模擬');
    expect(tested.canActivate).toBe(true);

    const activated = dataOf(repository.activateLineChannel(ADMIN, ONBOARDING));
    expect(activated.channel.status).toBe('published');
  });

  it('pauses and resumes one channel without touching the others and keeps changes per account storage', async () => {
    const storage = createMemoryStorage();
    const repository = createRepository(storage);

    const paused = dataOf(await setPublishingChannelPausedAs(repository, ADMIN, CUSTOMER_SERVICE, 'website', true));
    expect(paused.status).toBe('paused');
    const reloaded = dataOf(await publishingAs(createRepository(storage), ADMIN, CUSTOMER_SERVICE));
    expect(statusesOf(reloaded)).toEqual({ platform: 'published', website: 'paused', line: 'needs-attention' });

    const resumed = dataOf(await setPublishingChannelPausedAs(repository, ADMIN, CUSTOMER_SERVICE, 'website', false));
    expect(resumed.status).toBe('published');
  });

  it('gives assistants created in the wizard three unconfigured channels', async () => {
    const storage = createMemoryStorage();
    const repository = createRepository(storage);
    storage.setItem(
      'sme-demo:created-assistants',
      JSON.stringify([
        {
          ...DEMO_SEED.assistants[1],
          id: 'assistant-created-1',
          name: '新助理',
          sharedWithAccountIds: [],
        },
      ]),
    );

    const view = dataOf(await publishingAs(repository, ADMIN, 'assistant-created-1'));
    expect(statusesOf(view)).toEqual({ platform: 'not-configured', website: 'not-configured', line: 'not-configured' });
    expect(asConfigurable(view).line.checks.every((check) => check.state === 'pending')).toBe(true);
  });
});
