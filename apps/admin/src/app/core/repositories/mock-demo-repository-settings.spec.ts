import type { AssistantSettingsView } from '../domain/assistant-settings.model';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';

const SEEDED = 'assistant-customer-service';

describe('MockDemoRepository assistant settings', () => {
  let storage: ReturnType<typeof createMemoryStorage>;
  let repository: MockDemoRepository;

  beforeEach(() => {
    storage = createMemoryStorage();
    repository = new MockDemoRepository(DEMO_SEED, {
      storage,
      now: () => new Date('2026-09-23T02:00:00.000Z'),
    });
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

  function settings(assistantId = SEEDED): AssistantSettingsView {
    const result = repository.getAssistantSettings('account-smb-admin', assistantId);
    if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
    return result.data;
  }

  it('reads a seeded assistant as an editable settings view before anything is edited', () => {
    const view = settings();

    expect(view.configuration.name).toBe('客服助理');
    expect(view.configuration.audience).toBe('members-and-external-customers');
    expect(view.sources).toHaveLength(5);
    expect(view.rules.keepOwnConversations).toBe(true);
    expect(view.savedAt).toBeNull();
  });

  it('gives the same permission-denied answer for another account and for an unknown id', () => {
    const otherAccount = repository.getAssistantSettings('account-internal-employee', SEEDED);
    const unknown = repository.getAssistantSettings('account-smb-admin', 'assistant-created-999');

    expect(otherAccount.status).toBe('permission-denied');
    expect(unknown.status).toBe('permission-denied');
    if (otherAccount.status === 'permission-denied' && unknown.status === 'permission-denied') {
      expect(otherAccount.message).toBe(unknown.message);
      expect(otherAccount.message).not.toContain('客服助理');
    }
  });

  it('refuses to edit a seeded assistant from an account that cannot manage assistants', () => {
    const result = repository.updateAssistantSettings('account-internal-employee', SEEDED, {
      name: '被改掉的名字',
    });

    expect(result.status).toBe('permission-denied');
    expect(settings().configuration.name).toBe('客服助理');
  });

  it('saves the audience of a seeded assistant so a new repository instance reads it back', () => {
    const result = repository.updateAssistantSettings('account-smb-admin', SEEDED, {
      audience: 'account-members',
    });

    expect(result.status).toBe('ready');
    const reopened = new MockDemoRepository(DEMO_SEED, { storage });
    const reread = reopened.getAssistantSettings('account-smb-admin', SEEDED);
    expect(reread.status).toBe('ready');
    if (reread.status === 'ready') {
      expect(reread.data.configuration.audience).toBe('account-members');
      expect(reread.data.savedAt).toBe('2026-09-23T02:00:00.000Z');
    }
  });

  it('shows the edited name everywhere the assistant is listed', () => {
    repository.updateAssistantSettings('account-smb-admin', SEEDED, { name: '售後服務助理' });

    const configurations = repository.listAssistantConfigurations('account-smb-admin');
    expect(configurations.status).toBe('ready');
    if (configurations.status === 'ready') {
      expect(configurations.data.map((assistant) => assistant.name)).toContain('售後服務助理');
    }
  });

  it('rejects an empty name without writing anything', () => {
    const result = repository.updateAssistantSettings('account-smb-admin', SEEDED, { name: '  ' });

    expect(result.status).toBe('validation-failed');
    if (result.status === 'validation-failed') {
      expect(result.errors.map((error) => error.field)).toContain('name');
    }
    expect(settings().configuration.name).toBe('客服助理');
  });

  it('connects a knowledge base by reference only and disconnects it again', () => {
    repository.setAssistantSourceConnection('account-smb-admin', SEEDED, {
      id: 'knowledge-product-guide',
      type: 'knowledge-base',
    }, false);

    const afterDisconnect = settings();
    expect(afterDisconnect.configuration.knowledgeBaseIds).not.toContain('knowledge-product-guide');
    expect(afterDisconnect.sources).toHaveLength(4);

    repository.setAssistantSourceConnection('account-smb-admin', SEEDED, {
      id: 'knowledge-product-guide',
      type: 'knowledge-base',
    }, true);

    expect(settings().configuration.knowledgeBaseIds).toContain('knowledge-product-guide');
  });

  it('refuses to connect a source the account cannot see', () => {
    const result = repository.setAssistantSourceConnection('account-smb-admin', SEEDED, {
      id: 'knowledge-not-mine',
      type: 'knowledge-base',
    } as never, true);

    expect(result.status).toBe('validation-failed');
    expect(settings().sources).toHaveLength(5);
  });

  it('refuses to disconnect the last remaining source', () => {
    const remaining = settings().sources;
    for (const source of remaining.slice(0, remaining.length - 1)) {
      repository.setAssistantSourceConnection('account-smb-admin', SEEDED, source, false);
    }

    const result = repository.setAssistantSourceConnection(
      'account-smb-admin',
      SEEDED,
      settings().sources[0],
      false,
    );

    expect(result.status).toBe('validation-failed');
    expect(settings().sources).toHaveLength(1);
  });

  it('clears the write target when its database is disconnected', () => {
    repository.updateAssistantSettings('account-smb-admin', SEEDED, {
      rules: { dataWriteDatabaseId: 'database-orders', dataWritePurpose: '處理訂單問題回報' },
    });
    expect(settings().rules.dataWriteDatabaseId).toBe('database-orders');

    repository.setAssistantSourceConnection('account-smb-admin', SEEDED, {
      id: 'database-orders',
      type: 'database',
    }, false);

    expect(settings().rules.dataWriteDatabaseId).toBeNull();
  });

  it('keeps existing conversations in storage when 保存自己的對話 is turned off, and shows them again when it is turned back on', () => {
    repository.sendChatMessage('account-smb-admin', SEEDED, '收到商品後幾天內可以退貨？');
    const before = repository.listChatThreads('account-smb-admin', SEEDED);
    expect(before.status).toBe('ready');
    if (before.status === 'ready') expect(before.data.threads).toHaveLength(1);

    repository.updateAssistantSettings('account-smb-admin', SEEDED, {
      rules: { keepOwnConversations: false },
    });

    const off = repository.listChatThreads('account-smb-admin', SEEDED);
    expect(off.status).toBe('ready');
    if (off.status === 'ready') {
      expect(off.data.historyMode).toBe('not-saved');
      expect(off.data.threads).toHaveLength(0);
    }

    repository.updateAssistantSettings('account-smb-admin', SEEDED, {
      rules: { keepOwnConversations: true },
    });

    const back = repository.listChatThreads('account-smb-admin', SEEDED);
    expect(back.status).toBe('ready');
    if (back.status === 'ready') expect(back.data.threads).toHaveLength(1);
  });

  it('seeds one assistant with 顯示引用出處 on and another with it off', () => {
    expect(settings().rules.showCitations).toBe(true);
    expect(settings('assistant-internal-onboarding').rules.showCitations).toBe(false);
  });

  it('keeps the company-data answer but removes its citations when 顯示引用出處 is off', () => {
    const shown = lastReply('收到商品後幾天內可以退貨？');
    expect(shown.kind).toBe('company-data');
    if (shown.kind === 'company-data') {
      expect(shown.citations.length).toBeGreaterThan(0);
      expect(shown.citationNotice).toBeNull();
    }

    repository.updateAssistantSettings('account-smb-admin', SEEDED, {
      rules: { showCitations: false },
    });

    const hidden = lastReply('收到商品後幾天內可以退貨？');
    expect(hidden.kind).toBe('company-data');
    if (hidden.kind === 'company-data') {
      expect(hidden.citations).toHaveLength(0);
      expect(hidden.citationNotice).toContain('公司資料');
    }
  });

  it('still refuses to answer from company data the assistant is not connected to when citations are hidden', () => {
    // 內部助理沒有連接退貨政策知識庫，關閉引用出處也不會讓它假裝有答案。
    expect(lastReply('收到商品後幾天內可以退貨？', 'assistant-internal-onboarding').kind).toBe('no-result');
  });

  it('lets 嚴格回答 stop the assistant from adding general knowledge in chat', () => {
    expect(lastReplyKind('保養皮革要注意什麼？')).toBe('general-knowledge');

    repository.updateAssistantSettings('account-smb-admin', SEEDED, {
      rules: { knowledgeScope: 'company-data-only' },
    });

    expect(lastReplyKind('保養皮革要注意什麼？')).not.toBe('general-knowledge');
  });
});
