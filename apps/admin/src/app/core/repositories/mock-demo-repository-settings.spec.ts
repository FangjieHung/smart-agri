import { firstValueFrom } from 'rxjs';
import type { AccountId } from '../domain/account.model';
import type { AssistantSettingsView } from '../domain/assistant-settings.model';
import { createEmptyAssistantDraft } from '../domain/assistant-draft.model';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';

const SEEDED = 'assistant-customer-service';
const ADMIN: AccountId = 'account-smb-admin';
const EMPLOYEE: AccountId = 'account-internal-employee';
const NOW = new Date('2026-09-23T02:00:00.000Z');

interface Options {
  readonly storage?: ReturnType<typeof createMemoryStorage>;
  readonly viewer?: AccountId | null;
}

function createRepository({ storage = createMemoryStorage(), viewer = ADMIN }: Options = {}) {
  return new MockDemoRepository(DEMO_SEED, {
    storage,
    now: () => NOW,
    viewer: () => viewer,
  });
}

async function assistantIds(repository: MockDemoRepository): Promise<string[]> {
  const result = await firstValueFrom(repository.listAssistantConfigurations());
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data.map((assistant) => assistant.id);
}

async function usableAssistantIds(repository: MockDemoRepository): Promise<string[]> {
  const result = await firstValueFrom(repository.listUsableAssistants());
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data.map((assistant) => assistant.id);
}

describe('MockDemoRepository assistant settings', () => {
  let storage: ReturnType<typeof createMemoryStorage>;
  let repository: MockDemoRepository;

  beforeEach(() => {
    storage = createMemoryStorage();
    repository = createRepository({ storage });
  });

  function lastReply(question: string, assistantId = SEEDED) {
    const result = repository.sendChatMessage('account-smb-admin', assistantId, question);
    if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
    const last = result.data.messages.at(-1);
    if (last?.author !== 'assistant') throw new Error('expected assistant reply');
    return last.reply;
  }

  function lastReplyKind(question: string): string | undefined {
    return lastReply(question).kind;
  }

  async function settings(assistantId = SEEDED, viewer: AccountId | null = ADMIN): Promise<AssistantSettingsView> {
    const result = await firstValueFrom(createRepository({ storage, viewer }).getAssistantSettings(assistantId));
    if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
    return result.data;
  }

  it('reads a seeded assistant as an editable settings view before anything is edited', async () => {
    const view = await settings();

    expect(view.configuration.name).toBe('客服助理');
    expect(view.configuration.audience).toBe('members-and-external-customers');
    expect(view.sources).toHaveLength(5);
    expect(view.rules.keepOwnConversations).toBe(true);
    expect(view.savedAt).toBeNull();
  });

  it('gives the same permission-denied answer for another account and for an unknown id', async () => {
    const otherAccount = await firstValueFrom(
      createRepository({ storage, viewer: EMPLOYEE }).getAssistantSettings(SEEDED),
    );
    const unknown = await firstValueFrom(repository.getAssistantSettings('assistant-created-999'));

    expect(otherAccount.status).toBe('permission-denied');
    expect(unknown.status).toBe('permission-denied');
    if (otherAccount.status === 'permission-denied' && unknown.status === 'permission-denied') {
      expect(otherAccount.message).toBe(unknown.message);
      expect(otherAccount.message).not.toContain('客服助理');
    }
  });

  it('refuses to edit a seeded assistant from an account that cannot manage assistants', async () => {
    const result = await firstValueFrom(
      createRepository({ storage, viewer: EMPLOYEE }).updateAssistantSettings(SEEDED, {
        name: '被改掉的名字',
      }),
    );

    expect(result.status).toBe('permission-denied');
    expect((await settings()).configuration.name).toBe('客服助理');
  });

  it('saves the audience of a seeded assistant so a new repository instance reads it back', async () => {
    const result = await firstValueFrom(
      repository.updateAssistantSettings(SEEDED, { audience: 'account-members' }),
    );

    expect(result.status).toBe('ready');
    const reread = await firstValueFrom(createRepository({ storage }).getAssistantSettings(SEEDED));
    expect(reread.status).toBe('ready');
    if (reread.status === 'ready') {
      expect(reread.data.configuration.audience).toBe('account-members');
      expect(reread.data.savedAt).toBe('2026-09-23T02:00:00.000Z');
    }
  });

  it('shows the edited name everywhere the assistant is listed', async () => {
    await firstValueFrom(repository.updateAssistantSettings(SEEDED, { name: '售後服務助理' }));

    const configurations = await firstValueFrom(repository.listAssistantConfigurations());
    expect(configurations.status).toBe('ready');
    if (configurations.status === 'ready') {
      expect(configurations.data.map((assistant) => assistant.name)).toContain('售後服務助理');
    }
  });

  it('rejects an empty name without writing anything', async () => {
    const result = await firstValueFrom(repository.updateAssistantSettings(SEEDED, { name: '  ' }));

    expect(result.status).toBe('validation-failed');
    if (result.status === 'validation-failed') {
      expect(result.errors.map((error) => error.field)).toContain('name');
    }
    expect((await settings()).configuration.name).toBe('客服助理');
  });

  it('connects a knowledge base by reference only and disconnects it again', async () => {
    await firstValueFrom(
      repository.setAssistantSourceConnection(
        SEEDED,
        { id: 'knowledge-product-guide', type: 'knowledge-base' },
        false,
      ),
    );

    const afterDisconnect = await settings();
    expect(afterDisconnect.configuration.knowledgeBaseIds).not.toContain('knowledge-product-guide');
    expect(afterDisconnect.sources).toHaveLength(4);

    await firstValueFrom(
      repository.setAssistantSourceConnection(
        SEEDED,
        { id: 'knowledge-product-guide', type: 'knowledge-base' },
        true,
      ),
    );

    expect((await settings()).configuration.knowledgeBaseIds).toContain('knowledge-product-guide');
  });

  it('refuses to connect a source the account cannot see', async () => {
    const result = await firstValueFrom(
      repository.setAssistantSourceConnection(
        SEEDED,
        { id: 'knowledge-not-mine', type: 'knowledge-base' } as never,
        true,
      ),
    );

    expect(result.status).toBe('validation-failed');
    expect((await settings()).sources).toHaveLength(5);
  });

  it('refuses to disconnect the last remaining source', async () => {
    const remaining = (await settings()).sources;
    for (const source of remaining.slice(0, remaining.length - 1)) {
      await firstValueFrom(repository.setAssistantSourceConnection(SEEDED, source, false));
    }

    const result = await firstValueFrom(
      repository.setAssistantSourceConnection(SEEDED, (await settings()).sources[0], false),
    );

    expect(result.status).toBe('validation-failed');
    expect((await settings()).sources).toHaveLength(1);
  });

  it('clears the write target when its database is disconnected', async () => {
    await firstValueFrom(
      repository.updateAssistantSettings(SEEDED, {
        rules: { dataWriteDatabaseId: 'database-orders', dataWritePurpose: '處理訂單問題回報' },
      }),
    );
    expect((await settings()).rules.dataWriteDatabaseId).toBe('database-orders');

    await firstValueFrom(
      repository.setAssistantSourceConnection(SEEDED, { id: 'database-orders', type: 'database' }, false),
    );

    expect((await settings()).rules.dataWriteDatabaseId).toBeNull();
  });

  it('keeps existing conversations in storage when 保存自己的對話 is turned off, and shows them again when it is turned back on', async () => {
    repository.sendChatMessage('account-smb-admin', SEEDED, '收到商品後幾天內可以退貨？');
    const before = await firstValueFrom(repository.listChatThreads(SEEDED));
    expect(before.status).toBe('ready');
    if (before.status === 'ready') expect(before.data.threads).toHaveLength(1);

    await firstValueFrom(repository.updateAssistantSettings(SEEDED, { rules: { keepOwnConversations: false } }));

    const off = await firstValueFrom(repository.listChatThreads(SEEDED));
    expect(off.status).toBe('ready');
    if (off.status === 'ready') {
      expect(off.data.historyMode).toBe('not-saved');
      expect(off.data.threads).toHaveLength(0);
    }

    await firstValueFrom(repository.updateAssistantSettings(SEEDED, { rules: { keepOwnConversations: true } }));

    const back = await firstValueFrom(repository.listChatThreads(SEEDED));
    expect(back.status).toBe('ready');
    if (back.status === 'ready') expect(back.data.threads).toHaveLength(1);
  });

  it('seeds one assistant with 顯示引用出處 on and another with it off', async () => {
    expect((await settings()).rules.showCitations).toBe(true);
    expect((await settings('assistant-internal-onboarding')).rules.showCitations).toBe(false);
  });

  it('keeps the company-data answer but removes its citations when 顯示引用出處 is off', async () => {
    const shown = lastReply('收到商品後幾天內可以退貨？');
    expect(shown.kind).toBe('company-data');
    if (shown.kind === 'company-data') {
      expect(shown.citations.length).toBeGreaterThan(0);
      expect(shown.citationNotice).toBeNull();
    }

    await firstValueFrom(repository.updateAssistantSettings(SEEDED, { rules: { showCitations: false } }));

    const hidden = lastReply('收到商品後幾天內可以退貨？');
    expect(hidden.kind).toBe('company-data');
    if (hidden.kind === 'company-data') {
      expect(hidden.citations).toHaveLength(0);
      expect(hidden.citationNotice).toContain('組織資料');
    }
  });

  it('still refuses to answer from company data the assistant is not connected to when citations are hidden', () => {
    // 內部助理沒有連接退貨政策知識庫，關閉引用出處也不會讓它假裝有答案。
    expect(lastReply('收到商品後幾天內可以退貨？', 'assistant-internal-onboarding').kind).toBe('no-result');
  });

  it('lets 嚴格回答 stop the assistant from adding general knowledge in chat', async () => {
    expect(lastReplyKind('保養皮革要注意什麼？')).toBe('general-knowledge');

    await firstValueFrom(repository.updateAssistantSettings(SEEDED, { rules: { knowledgeScope: 'company-data-only' } }));

    expect(lastReplyKind('保養皮革要注意什麼？')).not.toBe('general-knowledge');
  });

  describe('deleteAssistant', () => {
    it('is a cold Observable: nothing is deleted until subscribed', async () => {
      const deletion = repository.deleteAssistant(SEEDED);

      expect(await assistantIds(repository)).toContain(SEEDED);

      await firstValueFrom(deletion);
      expect(await assistantIds(repository)).not.toContain(SEEDED);
    });

    it('lets the owner delete a seeded assistant, removing it from every list and settings', async () => {
      // 刪除前：擁有者與被分享的員工都能看到／使用這個助理。
      expect(await assistantIds(repository)).toContain(SEEDED);
      expect(await usableAssistantIds(createRepository({ storage, viewer: EMPLOYEE }))).toContain(SEEDED);

      const result = await firstValueFrom(repository.deleteAssistant(SEEDED));

      expect(result).toEqual({ status: 'ready', data: null });
      expect(await assistantIds(createRepository({ storage }))).not.toContain(SEEDED);
      expect(await usableAssistantIds(createRepository({ storage }))).not.toContain(SEEDED);
      expect(
        await usableAssistantIds(createRepository({ storage, viewer: EMPLOYEE })),
      ).not.toContain(SEEDED);
      expect(await firstValueFrom(createRepository({ storage }).getAssistantSettings(SEEDED))).toMatchObject({
        status: 'permission-denied',
        reason: 'assistant-configuration',
      });
    });

    it('refuses deletion from a non-owner and changes nothing', async () => {
      const before = await assistantIds(repository);

      const result = await firstValueFrom(
        createRepository({ storage, viewer: EMPLOYEE }).deleteAssistant(SEEDED),
      );

      expect(result).toMatchObject({ status: 'permission-denied', reason: 'assistant-configuration' });
      expect(await assistantIds(createRepository({ storage }))).toEqual(before);
      expect(await firstValueFrom(createRepository({ storage }).getAssistantSettings(SEEDED))).toMatchObject({
        status: 'ready',
      });
    });

    it('deletes a wizard-created assistant, too', async () => {
      const draftCreated = await firstValueFrom(repository.createNamedAssistantDraft());
      if (draftCreated.status !== 'ready') throw new Error('expected ready');
      const draft = {
        ...createEmptyAssistantDraft(),
        templateId: 'blank' as const,
        name: '臨時助理',
        purpose: '測試用途',
        audience: 'account-members' as const,
        sources: [{ id: 'knowledge-product-guide', type: 'knowledge-base' as const }],
        hasTrialAnswer: true,
        currentStep: 'test' as const,
      };
      const created = await firstValueFrom(repository.createAssistantFromDraft(draftCreated.data.id, draft));
      if (created.status !== 'ready') throw new Error('expected ready');

      expect(await assistantIds(repository)).toContain(created.data.id);

      const result = await firstValueFrom(repository.deleteAssistant(created.data.id));

      expect(result).toEqual({ status: 'ready', data: null });
      expect(await assistantIds(createRepository({ storage }))).not.toContain(created.data.id);
    });
  });
});

describe('MockDemoRepository operations summary (#178)', () => {
  it('counts in-chat database query answers on their own, like the API', async () => {
    const result = await firstValueFrom(createRepository().getOperationsSummary());
    if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
    const queries = result.data.databaseQueries;
    expect(queries.answeredCount + queries.notPermittedCount + queries.insufficientRecordsCount + queries.failedCount).toBe(queries.totalCount);
    expect(queries.failureRate).toBeCloseTo(queries.failedCount / queries.totalCount);
    expect(Object.keys(queries).sort()).toEqual(['answeredCount', 'failedCount', 'failureRate', 'insufficientRecordsCount', 'notPermittedCount', 'totalCount']);
  });

  it('refuses an account that cannot manage assistants', async () => {
    const result = await firstValueFrom(createRepository({ viewer: EMPLOYEE }).getOperationsSummary());
    expect(result.status).toBe('permission-denied');
  });
});
