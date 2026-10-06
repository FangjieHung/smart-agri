import { firstValueFrom } from 'rxjs';
import type { AccountId } from '../domain/account.model';
import type { LineSettingsInput, LineSetupView, WebsiteServingState } from '../domain/publishing.model';
import type { DemoScenario } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import { DEMO_LINE_CHANNEL_ID, DEMO_LINE_CHANNEL_SECRET, EXPIRED_DEMO_LINE_TOKEN } from './demo-seed-publishing';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';

/*
 * LINE 頻道在 mock 模式（issue #233）：與 API 同一個契約——Secret 與 Token 只寫（只回「已設定」與末四碼）、
 * 測試連線逐項回結果、啟用閘門逐項列原因、實際服務狀態比照官網。
 */
const ADMIN: AccountId = 'account-smb-admin';
const SERVICE = 'assistant-customer-service';
const ONBOARDING = 'assistant-internal-onboarding';
const CREATED = 'assistant-created-1';
const NOW = '2026-09-22T02:00:00.000Z';
const TOKEN = 'demo-token-not-for-production-0123456789abcdefghij';

const VALID: LineSettingsInput = {
  officialAccountId: '@anxin-demo',
  channelId: DEMO_LINE_CHANNEL_ID,
  welcomeMessage: '您好！歡迎加入。',
  channelSecret: DEMO_LINE_CHANNEL_SECRET,
  accessToken: TOKEN,
};

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

async function lineOf(repository: MockDemoRepository, assistantId: string): Promise<LineSetupView> {
  const result = await firstValueFrom(repository.getAssistantPublishing(assistantId));
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data.line;
}

function ready<T>(result: { readonly status: string; readonly data?: T }): T {
  if (result.status !== 'ready' || result.data === undefined) throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

/** 填好憑證並通過測試連線的草稿（驗收通過的 ONBOARDING）。 */
async function testedDraft(repository: MockDemoRepository): Promise<LineSetupView> {
  ready(await firstValueFrom(repository.saveLineSettings(ONBOARDING, VALID, 0)));
  return ready(await firstValueFrom(repository.testLineConnection(ONBOARDING)));
}

function seedAssistantWithSomeoneElsesKnowledgeBase(storage: ReturnType<typeof createMemoryStorage>): void {
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
}

describe('MockDemoRepository LINE channel', () => {
  describe('write-only credentials', () => {
    it('shows configured and the last four of the seeded credentials, never the values', async () => {
      const { repository } = setUp();
      const line = await lineOf(repository, SERVICE);

      expect(line.channelSecret).toMatchObject({ configured: true, lastFour: DEMO_LINE_CHANNEL_SECRET.slice(-4) });
      expect(line.accessToken).toMatchObject({ configured: true, lastFour: EXPIRED_DEMO_LINE_TOKEN.slice(-4) });
      expect(JSON.stringify(line)).not.toContain(DEMO_LINE_CHANNEL_SECRET);
      expect(JSON.stringify(line)).not.toContain(EXPIRED_DEMO_LINE_TOKEN);
    });

    it('answers a save with configured and the last four only, and stores no plaintext', async () => {
      const { repository, storage } = setUp();

      const saved = ready(await firstValueFrom(repository.saveLineSettings(ONBOARDING, VALID, 0)));

      expect(saved.channelSecret).toMatchObject({ configured: true, lastFour: DEMO_LINE_CHANNEL_SECRET.slice(-4), updatedAt: NOW });
      expect(saved.accessToken).toMatchObject({ configured: true, lastFour: TOKEN.slice(-4) });
      for (const secret of [DEMO_LINE_CHANNEL_SECRET, TOKEN]) {
        expect(JSON.stringify(saved)).not.toContain(secret);
        expect(storage.getItem(`sme-demo:publishing:${ONBOARDING}`)).not.toContain(secret);
      }
      expect(Object.keys(saved.channelSecret).sort()).toEqual(['configured', 'lastFour', 'updatedAt']);
    });

    it('keeps a stored credential when the new value is empty or missing, and replaces it when given', async () => {
      const { repository } = setUp();
      await testedDraft(repository);

      const kept = ready(
        await firstValueFrom(repository.saveLineSettings(ONBOARDING, { ...VALID, welcomeMessage: '新的歡迎訊息', channelSecret: '', accessToken: undefined }, 1)),
      );
      expect(kept.accessToken.lastFour).toBe(TOKEN.slice(-4));
      expect(kept.welcomeMessage).toBe('新的歡迎訊息');
      expect(kept.checks.map((check) => check.state)).toEqual(['passed', 'passed', 'passed']);

      const replaced = ready(
        await firstValueFrom(repository.saveLineSettings(ONBOARDING, { ...VALID, channelSecret: undefined, accessToken: 'B'.repeat(40) + 'wxyz' }, 2)),
      );
      expect(replaced.accessToken.lastFour).toBe('wxyz');
      expect(replaced.channelSecret.lastFour).toBe(DEMO_LINE_CHANNEL_SECRET.slice(-4));
    });

    it('turns a plaintext record saved by an older version into statuses and overwrites the plaintext', async () => {
      const { repository, storage } = setUp({
        prepare: (storage) => {
          const seeded = DEMO_SEED.publishingRecords[SERVICE];
          if (seeded === undefined) throw new Error('missing seeded record');
          storage.setItem(
            `sme-demo:publishing:${SERVICE}`,
            JSON.stringify({
              ...seeded,
              line: {
                officialAccountId: '@old-demo',
                channelId: DEMO_LINE_CHANNEL_ID,
                channelSecret: DEMO_LINE_CHANNEL_SECRET,
                accessToken: TOKEN,
                checked: true,
                enabled: true,
                lastTest: null,
                paused: false,
                updatedAt: '2026-09-21T06:30:00.000Z',
              },
            }),
          );
        },
      });

      const line = await lineOf(repository, SERVICE);

      expect(line).toMatchObject({
        officialAccountId: '@old-demo',
        channelSecret: { configured: true, lastFour: DEMO_LINE_CHANNEL_SECRET.slice(-4) },
        accessToken: { configured: true, lastFour: TOKEN.slice(-4) },
        state: 'published',
      });
      expect(storage.getItem(`sme-demo:publishing:${SERVICE}`)).not.toContain(TOKEN);
      expect(storage.getItem(`sme-demo:publishing:${SERVICE}`)).not.toContain(DEMO_LINE_CHANNEL_SECRET);
    });
  });

  describe('saving', () => {
    it('reports every broken field at once, in screen order, and writes nothing', async () => {
      const { repository } = setUp();

      const invalid = await firstValueFrom(
        repository.saveLineSettings(ONBOARDING, { officialAccountId: 'anxin', channelId: '123', welcomeMessage: ' ', channelSecret: 'xyz', accessToken: 'short' }, 0),
      );

      if (invalid.status !== 'validation-failed') throw new Error(`expected validation-failed, got ${invalid.status}`);
      expect(invalid.errors.map((error) => error.field)).toEqual(['officialAccountId', 'channelId', 'channelSecret', 'accessToken', 'welcomeMessage']);
      expect((await lineOf(repository, ONBOARDING)).revision).toBe(0);
    });

    it('requires both credentials on the first save and a welcome message of at most 120 characters', async () => {
      const { repository } = setUp();

      const first = await firstValueFrom(
        repository.saveLineSettings(ONBOARDING, { ...VALID, channelSecret: '', accessToken: undefined, welcomeMessage: 'x'.repeat(121) }, 0),
      );

      if (first.status !== 'validation-failed') throw new Error(`expected validation-failed, got ${first.status}`);
      expect(first.errors.map((error) => [error.field, error.message])).toEqual([
        ['channelSecret', '請填寫 Channel secret。'],
        ['accessToken', '請填寫 Channel access token。'],
        ['welcomeMessage', '歡迎訊息請在 120 個字以內。'],
      ]);
    });

    it('creates the channel as a draft at revision 1 and shows the default welcome message before that', async () => {
      const { repository } = setUp();
      const before = await lineOf(repository, ONBOARDING);
      expect([before.revision, before.state, before.channel.status, before.welcomeMessage]).toEqual([
        0,
        'draft',
        'not-configured',
        '您好！有任何問題都可以直接問我。在群組裡請 @ 我再提問。',
      ]);
      expect(before.checks.map((check) => check.state)).toEqual(['pending', 'pending', 'pending']);

      const saved = ready(await firstValueFrom(repository.saveLineSettings(ONBOARDING, VALID, 0)));

      expect([saved.revision, saved.state, saved.channel.status]).toEqual([1, 'draft', 'testing']);
      expect(saved.channel.statusDetail).toContain('請測試連線');
    });

    it('answers a stale revision with a conflict and does not write', async () => {
      const { repository } = setUp();
      ready(await firstValueFrom(repository.saveLineSettings(ONBOARDING, VALID, 0)));

      const stale = await firstValueFrom(repository.saveLineSettings(ONBOARDING, { ...VALID, welcomeMessage: '另一個分頁' }, 0));

      expect(stale).toMatchObject({ status: 'conflict' });
      expect((await lineOf(repository, ONBOARDING)).welcomeMessage).toBe(VALID.welcomeMessage);
    });

    it('clears the test and sends an enabled channel back to a draft when the connection changes, but not for the welcome message', async () => {
      const { repository } = setUp({ acceptance: { [ONBOARDING]: 'passed' } });
      await testedDraft(repository);
      ready(await firstValueFrom(repository.publishLine(ONBOARDING)));

      const welcomeOnly = ready(await firstValueFrom(repository.saveLineSettings(ONBOARDING, { ...VALID, welcomeMessage: '改一句', channelSecret: '', accessToken: '' }, 1)));
      expect([welcomeOnly.state, welcomeOnly.servingState]).toEqual(['published', 'serving']);

      const changed = ready(await firstValueFrom(repository.saveLineSettings(ONBOARDING, { ...VALID, channelId: '1650000001', channelSecret: '', accessToken: '' }, 2)));
      expect([changed.state, changed.servingState, changed.publishedAt, changed.connectionCheckedAt]).toEqual(['draft', 'not-published', null, null]);
      expect(changed.checks.map((check) => check.state)).toEqual(['pending', 'pending', 'pending']);
    });
  });

  describe('testing the connection', () => {
    it('is refused until settings are saved, writing nothing', async () => {
      const { repository } = setUp();

      const refused = await firstValueFrom(repository.testLineConnection(ONBOARDING));

      expect(refused).toMatchObject({ status: 'test-refused', failures: [{ reason: 'settings' }] });
      expect((await lineOf(repository, ONBOARDING)).connectionCheckedAt).toBeNull();
    });

    it('runs the three checks and shows each in Traditional Chinese with the test time', async () => {
      const { repository } = setUp();

      const tested = await testedDraft(repository);

      expect(tested.checks.map((check) => [check.check, check.state])).toEqual([
        ['access-token', 'passed'],
        ['webhook-endpoint', 'passed'],
        ['webhook-test', 'passed'],
      ]);
      expect(tested.checks.every((check) => /[一-鿿]/.test(check.message))).toBe(true);
      expect(tested.checks[0].message).toContain('模擬');
      expect(tested.connectionCheckedAt).toBe(NOW);
      expect(tested.channel.statusDetail).toContain('連線測試已通過');
    });

    it('fails the token check of an expired token and skips the other two, keeping the channel in needs-attention', async () => {
      const { repository } = setUp();

      const tested = ready(await firstValueFrom(repository.testLineConnection(SERVICE)));

      expect(tested.checks.map((check) => check.state)).toEqual(['failed', 'skipped', 'skipped']);
      expect(tested.checks[0].message).toContain('Channel access token');
      expect(tested.channel.status).toBe('needs-attention');
    });

    it('lets a channel replace an expired token and pass the next test', async () => {
      const { repository } = setUp();

      const saved = ready(await firstValueFrom(repository.saveLineSettings(SERVICE, { officialAccountId: '@anxin-demo', channelId: DEMO_LINE_CHANNEL_ID, welcomeMessage: VALID.welcomeMessage, accessToken: TOKEN }, 1)));
      expect([saved.state, saved.servingState]).toEqual(['draft', 'not-published']);
      const tested = ready(await firstValueFrom(repository.testLineConnection(SERVICE)));

      expect(tested.checks.map((check) => check.state)).toEqual(['passed', 'passed', 'passed']);
    });
  });

  describe('serving state and its card status', () => {
    it.each<[string, () => ReturnType<typeof setUp>, string, WebsiteServingState, string, string]>([
      ['an enabled channel whose test failed', () => setUp(), SERVICE, 'not-published', 'needs-attention', '連線測試未通過'],
      ['a never-saved channel', () => setUp(), ONBOARDING, 'not-published', 'not-configured', '尚未填寫'],
    ])('maps %s', async (_name, build, assistantId, servingState, status, detail) => {
      const line = await lineOf(build().repository, assistantId);

      expect(line.servingState).toBe(servingState);
      expect(line.channel.status).toBe(status);
      expect(line.channel.statusDetail).toContain(detail);
    });

    it('maps a served channel, an owner pause, a failed acceptance, the monthly limit and someone else’s knowledge base', async () => {
      const serve = async (scenario?: DemoScenario, acceptanceAfterwards?: string) => {
        const { repository, storage } = setUp({ scenario, acceptance: { [ONBOARDING]: 'passed' } });
        await testedDraft(repository);
        ready(await firstValueFrom(repository.publishLine(ONBOARDING)));
        // 啟用之後驗收才失敗：頻道自動暫停（不是被閘門擋下）。
        if (acceptanceAfterwards !== undefined) {
          storage.setItem('sme-demo:assistant-acceptance', JSON.stringify({ statuses: { [ONBOARDING]: acceptanceAfterwards } }));
          const reopened = new MockDemoRepository(DEMO_SEED, { storage, now: () => new Date(NOW), viewer: () => ADMIN });
          return { repository: reopened, line: await lineOf(reopened, ONBOARDING) };
        }
        return { repository, line: await lineOf(repository, ONBOARDING) };
      };

      const serving = await serve();
      expect([serving.line.servingState, serving.line.channel.status, serving.line.publishedAt]).toEqual(['serving', 'published', NOW]);
      expect(serving.line.channel.statusDetail).toContain('@anxin-demo 已啟用');

      await firstValueFrom(serving.repository.setPublishingChannelPaused(ONBOARDING, 'line', true));
      const paused = await lineOf(serving.repository, ONBOARDING);
      expect([paused.state, paused.servingState, paused.channel.status]).toEqual(['paused', 'paused', 'paused']);
      await firstValueFrom(serving.repository.setPublishingChannelPaused(ONBOARDING, 'line', false));
      expect((await lineOf(serving.repository, ONBOARDING)).servingState).toBe('serving');

      const quota = await serve('usage-exceeded');
      expect([quota.line.servingState, quota.line.channel.status]).toEqual(['suspended-quota', 'needs-attention']);
      expect(quota.line.channel.statusDetail).toContain('本月用量已達上限');

      const failed = await serve(undefined, 'failed');
      expect([failed.line.servingState, failed.line.channel.statusDetail]).toEqual([
        'suspended-acceptance',
        expect.stringContaining('驗收未通過'),
      ]);
    });

    it('suspends an enabled channel that connects someone else’s knowledge base, and lists it by name', async () => {
      const { repository } = setUp({
        acceptance: { [CREATED]: 'passed' },
        prepare: (storage) => {
          seedAssistantWithSomeoneElsesKnowledgeBase(storage);
          const seeded = DEMO_SEED.publishingRecords[SERVICE];
          if (seeded === undefined) throw new Error('missing seeded record');
          const passed = (['access-token', 'webhook-endpoint', 'webhook-test'] as const).map((check) => ({
            check,
            label: check,
            state: 'passed' as const,
            message: '通過',
          }));
          storage.setItem(
            `sme-demo:publishing:${CREATED}`,
            JSON.stringify({ ...seeded, line: { ...seeded.line, tokenExpired: false, checks: passed, state: 'published' } }),
          );
        },
      });

      const line = await lineOf(repository, CREATED);

      expect(line.servingState).toBe('suspended-knowledge');
      expect(line.channel.status).toBe('needs-attention');
      expect(line.channel.statusDetail).toContain('已自動暫停 LINE 回覆');
      expect(line.nonOwnedKnowledgeBases).toEqual([{ id: 'knowledge-staff-notes', name: '同仁個人筆記' }]);
    });
  });

  describe('enabling gate', () => {
    it('refuses a channel that was never tested and whose acceptance has not passed, listing both reasons and writing nothing', async () => {
      const { repository } = setUp();
      ready(await firstValueFrom(repository.saveLineSettings(ONBOARDING, VALID, 0)));

      const refused = await firstValueFrom(repository.publishLine(ONBOARDING));

      if (refused.status !== 'publish-refused') throw new Error(`expected a refusal, got ${refused.status}`);
      expect(refused.failures.map((failure) => failure.reason)).toEqual(['connection', 'acceptance']);
      expect(refused.message).toContain('目前還不能啟用 LINE 頻道');
      expect((await lineOf(repository, ONBOARDING)).state).toBe('draft');
    });

    it('lists the acceptance, the failed test and each knowledge base that is not the owner’s', async () => {
      const { repository } = setUp({ prepare: seedAssistantWithSomeoneElsesKnowledgeBase, acceptance: { [CREATED]: 'failed' } });
      ready(await firstValueFrom(repository.saveLineSettings(CREATED, VALID, 0)));

      const refused = await firstValueFrom(repository.publishLine(CREATED));

      if (refused.status !== 'publish-refused') throw new Error(`expected a refusal, got ${refused.status}`);
      expect(refused.failures.map((failure) => failure.reason)).toEqual(['connection', 'acceptance', 'knowledge-ownership']);
      expect(refused.failures[2].message).toContain('同仁個人筆記');
    });

    it('enables after a passed test and acceptance, keeps the time on a second enable, and resumes a paused channel', async () => {
      const { repository } = setUp({ acceptance: { [ONBOARDING]: 'passed' } });
      await testedDraft(repository);

      const published = ready(await firstValueFrom(repository.publishLine(ONBOARDING)));
      expect([published.state, published.servingState, published.publishedAt]).toEqual(['published', 'serving', NOW]);
      expect(ready(await firstValueFrom(repository.publishLine(ONBOARDING))).publishedAt).toBe(NOW);

      await firstValueFrom(repository.setPublishingChannelPaused(ONBOARDING, 'line', true));
      const resumed = ready(await firstValueFrom(repository.publishLine(ONBOARDING)));
      expect([resumed.state, resumed.publishedAt]).toEqual(['published', NOW]);
    });

    it('unpublishes back to a draft that keeps the settings, credentials and test results', async () => {
      const { repository } = setUp({ acceptance: { [ONBOARDING]: 'passed' } });
      await testedDraft(repository);
      ready(await firstValueFrom(repository.publishLine(ONBOARDING)));

      const draft = ready(await firstValueFrom(repository.unpublishLine(ONBOARDING)));

      expect([draft.state, draft.publishedAt, draft.officialAccountId, draft.accessToken.configured]).toEqual(['draft', null, '@anxin-demo', true]);
      expect(draft.checks.map((check) => check.state)).toEqual(['passed', 'passed', 'passed']);
      expect(draft.channel.status).toBe('testing');
    });

    it('does not turn a draft into a published channel by pausing it', async () => {
      const { repository } = setUp();
      await testedDraft(repository);

      await firstValueFrom(repository.setPublishingChannelPaused(ONBOARDING, 'line', true));

      expect((await lineOf(repository, ONBOARDING)).state).toBe('draft');
    });
  });

  describe('usage', () => {
    it('shows this month’s push fallback count', async () => {
      expect((await lineOf(setUp().repository, SERVICE)).pushFallbackCount).toBe(3);
      expect((await lineOf(setUp().repository, ONBOARDING)).pushFallbackCount).toBe(0);
    });
  });
});
