import { firstValueFrom } from 'rxjs';
import type { AccountId } from '../domain/account.model';
import type { WebsiteEmbedView, WebsiteServingState } from '../domain/publishing.model';
import type { DemoScenario } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';

const ADMIN: AccountId = 'account-smb-admin';
const SERVICE = 'assistant-customer-service';
const ONBOARDING = 'assistant-internal-onboarding';
const CREATED = 'assistant-created-1';
const NOW = '2026-09-22T02:00:00.000Z';

function setUp(
  options: {
    readonly acceptance?: Readonly<Record<string, string>>;
    readonly scenario?: DemoScenario;
    readonly prepare?: (storage: ReturnType<typeof createMemoryStorage>) => void;
  } = {},
) {
  const storage = createMemoryStorage();
  options.prepare?.(storage);
  if (options.acceptance !== undefined) {
    storage.setItem('sme-demo:assistant-acceptance', JSON.stringify({ statuses: options.acceptance }));
  }
  const repository = new MockDemoRepository(DEMO_SEED, { storage, now: () => new Date(NOW), viewer: () => ADMIN });
  if (options.scenario !== undefined) repository.setScenario(options.scenario);
  return { repository, storage };
}

async function websiteOf(repository: MockDemoRepository, assistantId: string): Promise<WebsiteEmbedView> {
  const result = await firstValueFrom(repository.getAssistantPublishing(assistantId));
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data.website;
}

function ready<T>(result: { readonly status: string; readonly data?: T }): T {
  if (result.status !== 'ready' || result.data === undefined) throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

/** 一個擁有者是管理者、連接了「同仁個人筆記」（內部同仁擁有）的助理，官網已發布；驗收通過。 */
function seedAssistantWithSomeoneElsesKnowledgeBase(storage: ReturnType<typeof createMemoryStorage>, domains: readonly string[]): void {
  storage.setItem(
    'sme-demo:created-assistants',
    JSON.stringify([
      {
        ...DEMO_SEED.assistants[1],
        id: CREATED,
        name: '新助理',
        sharedWithAccountIds: [],
        knowledgeBaseIds: ['knowledge-product-guide', 'knowledge-staff-notes'],
        acceptanceStatus: 'passed',
      },
    ]),
  );
  const seeded = DEMO_SEED.publishingRecords[SERVICE];
  if (seeded === undefined) throw new Error('missing seeded record');
  storage.setItem(`sme-demo:publishing:${CREATED}`, JSON.stringify({ ...seeded, website: { ...seeded.website, allowedDomains: domains } }));
}

describe('MockDemoRepository website channel', () => {
  describe('serving state and its card status (M5a plan §3 C and H)', () => {
    it.each<[string, () => ReturnType<typeof setUp>, string, WebsiteServingState, string, string]>([
      ['a published channel that passed acceptance', () => setUp(), SERVICE, 'serving', 'published', '已發布，官網訪客可以使用'],
      ['a saved draft', () => setUp(), ONBOARDING, 'not-published', 'testing', '尚未發布'],
      ['a failed acceptance', () => setUp({ acceptance: { [SERVICE]: 'failed' } }), SERVICE, 'suspended-acceptance', 'needs-attention', '驗收未通過'],
      ['an assistant never accepted', () => setUp({ acceptance: { [SERVICE]: 'not-accepted' } }), SERVICE, 'suspended-acceptance', 'needs-attention', '驗收未通過'],
      ['the monthly limit reached', () => setUp({ scenario: 'usage-exceeded' }), SERVICE, 'suspended-quota', 'needs-attention', '本月用量已達上限'],
      ['the disconnected-channel scenario', () => setUp({ scenario: 'disconnected-channel' }), SERVICE, 'suspended-acceptance', 'needs-attention', '官網連線中斷'],
    ])('maps %s', async (_name, build, assistantId, servingState, status, detail) => {
      const { repository } = build();
      const website = await websiteOf(repository, assistantId);

      expect(website.servingState).toBe(servingState);
      expect(website.channel.status).toBe(status);
      expect(website.channel.statusDetail).toContain(detail);
    });

    it('maps the owner pausing to paused, and resuming back to serving', async () => {
      const { repository } = setUp();

      const paused = ready(await firstValueFrom(repository.setPublishingChannelPaused(SERVICE, 'website', true)));
      expect(paused.status).toBe('paused');
      const view = await websiteOf(repository, SERVICE);
      expect([view.state, view.servingState]).toEqual(['paused', 'paused']);

      await firstValueFrom(repository.setPublishingChannelPaused(SERVICE, 'website', false));
      expect((await websiteOf(repository, SERVICE)).servingState).toBe('serving');
    });

    it('suspends a published channel that connects someone else’s knowledge base, and lists it by name', async () => {
      const { repository } = setUp({
        prepare: (storage) => seedAssistantWithSomeoneElsesKnowledgeBase(storage, ['shop.anxin-demo.example']),
      });

      const website = await websiteOf(repository, CREATED);

      expect(website.servingState).toBe('suspended-knowledge');
      expect(website.channel.status).toBe('needs-attention');
      expect(website.nonOwnedKnowledgeBases).toEqual([{ id: 'knowledge-staff-notes', name: '同仁個人筆記' }]);
    });

    it('does not take a draft with no domain for a published one', async () => {
      const { repository } = setUp();
      await firstValueFrom(repository.unpublishWebsite(SERVICE));
      const draft = await websiteOf(repository, SERVICE);

      expect([draft.state, draft.servingState, draft.channel.status]).toEqual(['draft', 'not-published', 'testing']);
      expect(draft.publishedAt).toBeNull();
    });
  });

  describe('publishing gate', () => {
    it('refuses a draft whose acceptance has not passed, and writes nothing', async () => {
      const { repository } = setUp();

      const refused = await firstValueFrom(repository.publishWebsite(ONBOARDING));

      expect(refused).toMatchObject({ status: 'publish-refused' });
      if (refused.status !== 'publish-refused') return;
      expect(refused.failures.map((failure) => failure.reason)).toEqual(['acceptance']);
      expect((await websiteOf(repository, ONBOARDING)).state).toBe('draft');
    });

    it('lists every reason at once: acceptance, no domain and each knowledge base that is not the owner’s', async () => {
      const { repository } = setUp({
        acceptance: { [CREATED]: 'failed' },
        prepare: (storage) => seedAssistantWithSomeoneElsesKnowledgeBase(storage, []),
      });

      const refused = await firstValueFrom(repository.publishWebsite(CREATED));

      if (refused.status !== 'publish-refused') throw new Error(`expected a refusal, got ${refused.status}`);
      expect(refused.failures.map((failure) => failure.reason)).toEqual(['acceptance', 'allowed-domains', 'knowledge-ownership']);
      expect(refused.failures[2].message).toContain('同仁個人筆記');
    });

    it('publishes once the gate passes: published at the current time, serving, and a second publish changes nothing', async () => {
      const { repository } = setUp();
      await firstValueFrom(repository.unpublishWebsite(SERVICE));

      const published = ready(await firstValueFrom(repository.publishWebsite(SERVICE)));
      expect([published.state, published.servingState, published.channel.status, published.publishedAt]).toEqual([
        'published',
        'serving',
        'published',
        NOW,
      ]);

      const again = ready(await firstValueFrom(repository.publishWebsite(SERVICE)));
      expect(again.publishedAt).toBe(NOW);
    });

    it('resumes a paused channel when it is published again, keeping the publication time', async () => {
      const { repository } = setUp();
      const before = await websiteOf(repository, SERVICE);
      await firstValueFrom(repository.setPublishingChannelPaused(SERVICE, 'website', true));

      const resumed = ready(await firstValueFrom(repository.publishWebsite(SERVICE)));

      expect(resumed.state).toBe('published');
      expect(resumed.publishedAt).toBe(before.publishedAt);
    });
  });

  describe('settings, revision and passive detection', () => {
    it('shows when each domain was last seen, and drops the time of a removed domain', async () => {
      const { repository } = setUp();
      const view = await websiteOf(repository, SERVICE);
      expect(view.domains).toEqual([{ domain: 'shop.anxin-demo.example', lastSeenAt: '2026-10-05T06:03:00.000Z' }]);

      const replaced = await firstValueFrom(
        repository.updateWebsiteEmbed(SERVICE, { ...view, allowedDomains: ['blog.anxin-demo.example'] }, view.revision),
      );
      expect(ready(replaced).domains).toEqual([{ domain: 'blog.anxin-demo.example', lastSeenAt: null }]);

      const back = await firstValueFrom(
        repository.updateWebsiteEmbed(SERVICE, { ...view, allowedDomains: ['shop.anxin-demo.example'] }, ready(replaced).revision),
      );
      expect(ready(back).domains).toEqual([{ domain: 'shop.anxin-demo.example', lastSeenAt: null }]);
    });

    it('answers a stale revision with a conflict and does not write', async () => {
      const { repository } = setUp();
      const view = await websiteOf(repository, SERVICE);
      await firstValueFrom(repository.updateWebsiteEmbed(SERVICE, view, view.revision));

      const stale = await firstValueFrom(
        repository.updateWebsiteEmbed(SERVICE, { ...view, displayName: '另一個分頁的名稱' }, view.revision),
      );

      expect(stale).toMatchObject({ status: 'conflict' });
      const after = await websiteOf(repository, SERVICE);
      expect(after.displayName).toBe(view.displayName);
      expect(after.revision).toBe(view.revision + 1);
    });

    it('treats a never-saved assistant as not configured at revision 0', async () => {
      const { repository } = setUp({
        prepare: (storage) =>
          storage.setItem(
            'sme-demo:created-assistants',
            JSON.stringify([{ ...DEMO_SEED.assistants[1], id: CREATED, name: '新助理', sharedWithAccountIds: [] }]),
          ),
      });

      const view = await websiteOf(repository, CREATED);

      expect([view.revision, view.state, view.servingState, view.channel.status]).toEqual([0, 'draft', 'not-published', 'not-configured']);
    });
  });
});
