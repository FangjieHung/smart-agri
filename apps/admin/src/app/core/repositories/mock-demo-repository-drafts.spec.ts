import { firstValueFrom } from 'rxjs';
import type { AccountId } from '../domain/account.model';
import {
  createEmptyAssistantDraft,
  type AssistantDraft,
  type NamedAssistantDraftView,
} from '../domain/assistant-draft.model';
import { createMemoryStorage } from './memory-storage';
import { DRAFT_REVISION_CONFLICT_MESSAGE, MockDemoRepository } from './mock-demo-repository';
import { DEMO_SEED } from './demo-seed';

const ADMIN: AccountId = 'account-smb-admin';
const EMPLOYEE: AccountId = 'account-internal-employee';
const NOW = new Date('2026-09-22T02:00:00.000Z');
const START = NOW.getTime();

function completeDraft(): AssistantDraft {
  return {
    ...createEmptyAssistantDraft(),
    templateId: 'answer-customer-questions',
    name: '客戶問答助理',
    purpose: '回答客戶關於商品、退換貨與配送的問題',
    audience: 'members-and-external-customers',
    sources: [
      { id: 'knowledge-product-guide', type: 'knowledge-base' },
      { id: 'knowledge-refund-policy', type: 'knowledge-base' },
      { id: 'database-orders', type: 'database' },
      { id: 'database-customer-records', type: 'database' },
    ],
    hasTrialAnswer: true,
    currentStep: 'test',
  };
}

interface Options {
  readonly storage?: ReturnType<typeof createMemoryStorage>;
  readonly viewer?: AccountId | null;
  readonly clock?: { now: number };
}

function createRepository({ storage = createMemoryStorage(), viewer = ADMIN, clock = { now: START } }: Options = {}) {
  return new MockDemoRepository(DEMO_SEED, {
    storage,
    now: () => new Date(clock.now),
    viewer: () => viewer,
  });
}

async function readyDraft(repository: MockDemoRepository, draftId: string): Promise<NamedAssistantDraftView> {
  const result = await firstValueFrom(repository.getNamedAssistantDraft(draftId));
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

async function draftNames(repository: MockDemoRepository): Promise<string[]> {
  const result = await firstValueFrom(repository.listNamedAssistantDrafts());
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data.map((item) => item.draft.name);
}

describe('MockDemoRepository assistant creation', () => {
  it('offers the purpose templates starting with answering customer questions', () => {
    const repository = createRepository();
    const result = repository.listAssistantTemplates();

    expect(result.status).toBe('ready');
    if (result.status === 'ready') {
      expect(result.data[0]).toMatchObject({
        id: 'answer-customer-questions',
        title: '回答客戶問題',
      });
      expect(result.data.map((template) => template.id)).toContain('blank');
    }
  });

  it('lists knowledge bases and databases in one mixed list with type, permission, status and update time', async () => {
    const result = await firstValueFrom(createRepository({ viewer: ADMIN }).listConnectableSources());

    expect(result.status).toBe('ready');
    if (result.status === 'ready') {
      expect(new Set(result.data.map((source) => source.type))).toEqual(
        new Set(['knowledge-base', 'database']),
      );
      expect(result.data).toContainEqual(
        expect.objectContaining({
          id: 'database-orders',
          type: 'database',
          permission: 'read-only',
          status: 'ready',
          updatedAt: '2026-09-18T07:30:00.000Z',
        }),
      );
      expect(result.data).toContainEqual(
        expect.objectContaining({
          id: 'knowledge-product-guide',
          type: 'knowledge-base',
          permission: 'owner',
          summary: '4 份文件、2 則 FAQ',
          status: 'needs-attention',
        }),
      );
    }

    expect(
      await firstValueFrom(createRepository({ viewer: EMPLOYEE }).listConnectableSources()),
    ).toEqual({
      status: 'ready',
      data: [],
    });
  });

  it('persists a named draft per account so another account never sees it', async () => {
    const storage = createMemoryStorage();
    const repository = createRepository({ storage });

    const created = await firstValueFrom(repository.createNamedAssistantDraft());
    if (created.status !== 'ready') throw new Error('expected ready');
    await firstValueFrom(
      repository.saveNamedAssistantDraft(
        created.data.id,
        { ...createEmptyAssistantDraft(), name: '草稿助理' },
        created.data.revision,
      ),
    );

    const reloaded = createRepository({ storage, viewer: ADMIN });
    expect(await readyDraft(reloaded, created.data.id)).toMatchObject({
      draft: { name: '草稿助理' },
    });

    const otherAccount = createRepository({ storage, viewer: 'account-external-customer' });
    expect(await firstValueFrom(otherAccount.getNamedAssistantDraft(created.data.id))).toMatchObject({
      status: 'permission-denied',
      reason: 'assistant-draft',
    });
  });

  it('keeps multiple named drafts sorted most-recently-saved first, and migrates the legacy single draft', async () => {
    const storage = createMemoryStorage();
    storage.setItem(
      `sme-demo:assistant-draft:${ADMIN}`,
      JSON.stringify({
        version: 1,
        savedAt: '2026-09-22T01:00:00.000Z',
        draft: { ...createEmptyAssistantDraft(), name: '舊草稿' },
      }),
    );
    const clock = { now: START };
    const repository = createRepository({ storage, clock });

    const first = await firstValueFrom(repository.createNamedAssistantDraft());
    clock.now = START + 1000;
    const second = await firstValueFrom(repository.createNamedAssistantDraft());
    expect(first.status).toBe('ready');
    expect(second.status).toBe('ready');
    if (first.status !== 'ready' || second.status !== 'ready') return;
    expect(first.data.id).not.toBe(second.data.id);

    clock.now = START + 2000;
    await firstValueFrom(
      repository.saveNamedAssistantDraft(
        first.data.id,
        { ...first.data.draft, name: '新草稿' },
        first.data.revision,
      ),
    );

    const reloaded = createRepository({ storage, clock });
    const list = await firstValueFrom(reloaded.listNamedAssistantDrafts());
    expect(list.status).toBe('ready');
    if (list.status === 'ready') {
      // 最近保存的在前：剛保存過的「新草稿」排最前，其次是後建立的空白草稿，
      // 最早保存的「舊草稿」（遷移自舊版單一草稿）排在最後。
      expect(list.data.map((item) => item.draft.name)).toEqual(['新草稿', '', '舊草稿']);
    }
    expect(
      await firstValueFrom(createRepository({ storage, viewer: EMPLOYEE }).listNamedAssistantDrafts()),
    ).toMatchObject({ status: 'permission-denied' });
  });

  it('returns no drafts instead of failing when stored data is missing or corrupted', async () => {
    const storage = createMemoryStorage();
    const repository = createRepository({ storage });

    expect(await firstValueFrom(repository.listNamedAssistantDrafts())).toEqual({
      status: 'ready',
      data: [],
    });

    storage.setItem(`sme-demo:assistant-draft:${ADMIN}`, '{broken');

    expect(await firstValueFrom(createRepository({ storage }).listNamedAssistantDrafts())).toEqual({
      status: 'ready',
      data: [],
    });
  });

  it('rejects draft writes from accounts that cannot manage assistants', async () => {
    const repository = createRepository({ viewer: EMPLOYEE });

    expect(await firstValueFrom(repository.createNamedAssistantDraft())).toMatchObject({
      status: 'permission-denied',
      reason: 'assistant-draft',
    });
  });

  it('answers trial questions from fixtures according to sources and rules (issue #82)', async () => {
    const repository = createRepository();
    const draft = completeDraft();

    const cited = await firstValueFrom(
      repository.previewTrialAnswer('draft-any', {
        question: '收到商品後幾天內可以申請退貨？',
        sources: draft.sources,
        rules: draft.rules,
      }),
    );
    const strictGeneral = await firstValueFrom(
      repository.previewTrialAnswer('draft-any', {
        question: '皮革商品平常要怎麼保養？',
        sources: draft.sources,
        rules: draft.rules,
      }),
    );
    const allowedGeneral = await firstValueFrom(
      repository.previewTrialAnswer('draft-any', {
        question: '皮革商品平常要怎麼保養？',
        sources: draft.sources,
        rules: { ...draft.rules, knowledgeScope: 'allow-general-knowledge' },
      }),
    );

    expect(cited).toMatchObject({
      status: 'ready',
      data: {
        reply: {
          kind: 'company-data',
          citations: [{ knowledgeBaseName: '退換貨政策' }],
        },
      },
    });
    expect(strictGeneral).toMatchObject({
      status: 'ready',
      data: { reply: { kind: 'no-result', text: draft.rules.refusalMessage } },
    });
    expect(allowedGeneral).toMatchObject({
      status: 'ready',
      data: { reply: { kind: 'general-knowledge' } },
    });
  });

  it('rejects a blank or over-length trial question with validation-failed (issue #82)', async () => {
    const repository = createRepository();
    const draft = completeDraft();

    const blank = await firstValueFrom(
      repository.previewTrialAnswer('draft-any', { question: '   ', sources: draft.sources, rules: draft.rules }),
    );
    const tooLong = await firstValueFrom(
      repository.previewTrialAnswer('draft-any', {
        question: '問'.repeat(2001),
        sources: draft.sources,
        rules: draft.rules,
      }),
    );

    expect(blank).toMatchObject({ status: 'validation-failed' });
    expect(tooLong).toMatchObject({ status: 'validation-failed' });
  });

  describe('saveNamedAssistantDraft revisions', () => {
    it('rejects a stale revision with a conflict and writes nothing', async () => {
      const storage = createMemoryStorage();
      const repository = createRepository({ storage });
      const created = await firstValueFrom(repository.createNamedAssistantDraft());
      if (created.status !== 'ready') throw new Error('expected ready');

      const firstSave = await firstValueFrom(
        repository.saveNamedAssistantDraft(
          created.data.id,
          { ...created.data.draft, name: '第一次保存' },
          created.data.revision,
        ),
      );
      expect(firstSave.status).toBe('ready');

      const staleSave = await firstValueFrom(
        repository.saveNamedAssistantDraft(
          created.data.id,
          { ...created.data.draft, name: '用舊版本保存' },
          created.data.revision,
        ),
      );
      expect(staleSave).toEqual({ status: 'conflict', message: DRAFT_REVISION_CONFLICT_MESSAGE });

      expect(await readyDraft(repository, created.data.id)).toMatchObject({
        draft: { name: '第一次保存' },
      });
    });

    it('lets two consecutive saves each succeed when passing the previously returned revision', async () => {
      const repository = createRepository();
      const created = await firstValueFrom(repository.createNamedAssistantDraft());
      if (created.status !== 'ready') throw new Error('expected ready');

      const firstSave = await firstValueFrom(
        repository.saveNamedAssistantDraft(
          created.data.id,
          { ...created.data.draft, name: '第一次' },
          created.data.revision,
        ),
      );
      expect(firstSave.status).toBe('ready');
      if (firstSave.status !== 'ready') return;

      const secondSave = await firstValueFrom(
        repository.saveNamedAssistantDraft(
          created.data.id,
          { ...firstSave.data.draft, name: '第二次' },
          firstSave.data.revision,
        ),
      );
      expect(secondSave.status).toBe('ready');
      expect(await readyDraft(repository, created.data.id)).toMatchObject({ draft: { name: '第二次' } });
    });

    it('is a cold Observable: nothing is written until subscribed', async () => {
      const repository = createRepository();
      const created = await firstValueFrom(repository.createNamedAssistantDraft());
      if (created.status !== 'ready') throw new Error('expected ready');

      const save = repository.saveNamedAssistantDraft(
        created.data.id,
        { ...created.data.draft, name: '未訂閱' },
        created.data.revision,
      );
      expect(await readyDraft(repository, created.data.id)).toMatchObject({ draft: { name: '' } });

      await firstValueFrom(save);
      expect(await readyDraft(repository, created.data.id)).toMatchObject({ draft: { name: '未訂閱' } });
    });
  });

  describe('unknown draft id', () => {
    it('getNamedAssistantDraft on an unknown id is permission-denied', async () => {
      const result = await firstValueFrom(createRepository().getNamedAssistantDraft('draft-does-not-exist'));

      expect(result).toMatchObject({ status: 'permission-denied', reason: 'assistant-draft' });
    });

    it('discardNamedAssistantDraft on an unknown id is permission-denied', async () => {
      const result = await firstValueFrom(createRepository().discardNamedAssistantDraft('draft-does-not-exist'));

      expect(result).toMatchObject({ status: 'permission-denied', reason: 'assistant-draft' });
    });

    it('saveNamedAssistantDraft on an unknown id is permission-denied', async () => {
      const result = await firstValueFrom(
        createRepository().saveNamedAssistantDraft('draft-does-not-exist', completeDraft(), 1),
      );

      expect(result).toMatchObject({ status: 'permission-denied', reason: 'assistant-draft' });
    });
  });

  describe('createAssistantFromDraft', () => {
    it('rejects an unknown draft id with permission-denied', async () => {
      const result = await firstValueFrom(
        createRepository().createAssistantFromDraft('draft-does-not-exist', completeDraft()),
      );

      expect(result).toMatchObject({ status: 'permission-denied', reason: 'assistant-draft' });
    });

    it('creates an assistant with mixed sources, removes the draft and lists it for its owner only', async () => {
      const storage = createMemoryStorage();
      const repository = createRepository({ storage });
      const created = await firstValueFrom(repository.createNamedAssistantDraft());
      if (created.status !== 'ready') throw new Error('expected ready');
      await firstValueFrom(
        repository.saveNamedAssistantDraft(created.data.id, completeDraft(), created.data.revision),
      );

      const createdAssistant = await firstValueFrom(
        repository.createAssistantFromDraft(created.data.id, completeDraft()),
      );

      expect(createdAssistant).toMatchObject({
        status: 'ready',
        data: {
          ownerAccountId: 'account-smb-admin',
          name: '客戶問答助理',
          status: 'ready',
          audience: 'members-and-external-customers',
          knowledgeBaseIds: ['knowledge-product-guide', 'knowledge-refund-policy'],
          databaseIds: ['database-orders', 'database-customer-records'],
        },
      });
      if (createdAssistant.status !== 'ready') return;
      expect(createdAssistant.data.id).toMatch(/^assistant-created-\d+$/);

      // 建立後草稿就消失了，列表裡再也找不到。
      expect(await draftNames(repository)).not.toContain('客戶問答助理');
      expect(await firstValueFrom(repository.getNamedAssistantDraft(created.data.id))).toMatchObject({
        status: 'permission-denied',
        reason: 'assistant-draft',
      });

      const reloaded = createRepository({ storage });
      expect(await firstValueFrom(reloaded.listAssistantConfigurations())).toMatchObject({
        status: 'ready',
        data: expect.arrayContaining([
          expect.objectContaining({ id: createdAssistant.data.id }),
        ]),
      });
      expect(reloaded.getAssistantSources('account-smb-admin', createdAssistant.data.id)).toMatchObject({
        status: 'ready',
        data: expect.arrayContaining([{ id: 'database-orders', type: 'database' }]),
      });
      expect(
        await firstValueFrom(createRepository({ storage, viewer: EMPLOYEE }).listAssistantConfigurations()),
      ).toEqual({
        status: 'ready',
        data: [],
      });
    });

    it('refuses to create an assistant from an incomplete draft', async () => {
      const repository = createRepository();
      const created = await firstValueFrom(repository.createNamedAssistantDraft());
      if (created.status !== 'ready') throw new Error('expected ready');

      const result = await firstValueFrom(
        repository.createAssistantFromDraft(created.data.id, {
          ...completeDraft(),
          sources: [],
          hasTrialAnswer: false,
        }),
      );

      expect(result).toMatchObject({
        status: 'validation-failed',
        errors: [
          { field: 'sources' },
          { field: 'trial' },
        ],
      });
    });
  });
});
