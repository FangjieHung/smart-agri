import type { AccountId } from '../domain/account.model';
import type { AssistantConfigurationView } from '../domain/assistant.model';
import type { AssistantChatView, ChatThreadListView } from '../domain/conversation.model';
import type { RepositoryView } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';

const ASSISTANT = 'assistant-customer-service';
const CUSTOMER: AccountId = 'account-external-customer';

function createRepository(storage = createMemoryStorage(), tick = { value: 0 }) {
  return new MockDemoRepository(DEMO_SEED, {
    storage,
    now: () => new Date(Date.parse('2026-09-22T02:00:00.000Z') + tick.value++ * 1000),
  });
}

function ready<T>(result: RepositoryView<T>): T {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

function chatOf(result: unknown): AssistantChatView {
  const view = result as RepositoryView<AssistantChatView>;
  return ready(view);
}

function listOf(result: unknown): ChatThreadListView {
  return ready(result as RepositoryView<ChatThreadListView>);
}

describe('MockDemoRepository chat threads', () => {
  it('starts with no saved threads and creates one on the first message', () => {
    const repository = createRepository();

    expect(listOf(repository.listChatThreads(CUSTOMER, ASSISTANT))).toMatchObject({
      assistantId: ASSISTANT,
      assistantName: '客服助理',
      historyMode: 'saved',
      threads: [],
    });

    const chat = chatOf(repository.sendChatMessage(CUSTOMER, ASSISTANT, '收到商品後幾天內可以退貨？'));
    expect(chat.threadId).not.toBeNull();

    const list = listOf(repository.listChatThreads(CUSTOMER, ASSISTANT));
    expect(list.threads).toHaveLength(1);
    expect(list.threads[0]).toMatchObject({ id: chat.threadId, messageCount: 2 });
  });

  it('derives the thread title from the first question and keeps it after later messages', () => {
    const repository = createRepository();
    repository.sendChatMessage(CUSTOMER, ASSISTANT, '收到商品後幾天內可以退貨？');
    repository.sendChatMessage(CUSTOMER, ASSISTANT, '皮革商品平常要怎麼保養？');

    const list = listOf(repository.listChatThreads(CUSTOMER, ASSISTANT));
    expect(list.threads).toHaveLength(1);
    expect(list.threads[0].title).toContain('收到商品後幾天內可以退貨');
    expect(list.threads[0].messageCount).toBe(4);
  });

  it('opens a new empty thread and lists threads newest activity first', () => {
    const repository = createRepository();
    const first = chatOf(repository.sendChatMessage(CUSTOMER, ASSISTANT, '收到商品後幾天內可以退貨？'));
    const second = chatOf(repository.createChatThread(CUSTOMER, ASSISTANT));

    expect(second.threadId).not.toBe(first.threadId);
    expect(second.messages).toEqual([]);

    const list = listOf(repository.listChatThreads(CUSTOMER, ASSISTANT));
    expect(list.threads.map((thread) => thread.id)).toEqual([second.threadId, first.threadId]);

    repository.sendChatMessage(CUSTOMER, ASSISTANT, '我要回報訂單問題', first.threadId ?? undefined);
    expect(
      listOf(repository.listChatThreads(CUSTOMER, ASSISTANT)).threads.map((thread) => thread.id),
    ).toEqual([first.threadId, second.threadId]);
  });

  it('opens a thread by id and keeps each thread’s messages apart', () => {
    const repository = createRepository();
    const first = chatOf(repository.sendChatMessage(CUSTOMER, ASSISTANT, '收到商品後幾天內可以退貨？'));
    const second = chatOf(repository.createChatThread(CUSTOMER, ASSISTANT));
    repository.sendChatMessage(CUSTOMER, ASSISTANT, '皮革商品平常要怎麼保養？', second.threadId ?? undefined);

    const reopened = chatOf(repository.getAssistantChat(CUSTOMER, ASSISTANT, first.threadId ?? undefined));
    expect(reopened.messages).toHaveLength(2);
    expect(JSON.stringify(reopened.messages)).not.toContain('保養');
  });

  it('renames a thread and rejects an empty or over-long name', () => {
    const repository = createRepository();
    const chat = chatOf(repository.sendChatMessage(CUSTOMER, ASSISTANT, '收到商品後幾天內可以退貨？'));
    const threadId = chat.threadId ?? '';

    expect(repository.renameChatThread(CUSTOMER, ASSISTANT, threadId, '   ')).toMatchObject({
      status: 'validation-failed',
    });
    expect(repository.renameChatThread(CUSTOMER, ASSISTANT, threadId, 'x'.repeat(61))).toMatchObject({
      status: 'validation-failed',
    });

    expect(repository.renameChatThread(CUSTOMER, ASSISTANT, threadId, '退貨問題')).toMatchObject({
      status: 'ready',
      data: { id: threadId, title: '退貨問題' },
    });
    repository.sendChatMessage(CUSTOMER, ASSISTANT, '我要回報訂單問題', threadId);
    expect(listOf(repository.listChatThreads(CUSTOMER, ASSISTANT)).threads[0].title).toBe('退貨問題');
  });

  it('deletes a thread and returns the remaining list', () => {
    const repository = createRepository();
    const first = chatOf(repository.sendChatMessage(CUSTOMER, ASSISTANT, '收到商品後幾天內可以退貨？'));
    const second = chatOf(repository.createChatThread(CUSTOMER, ASSISTANT));

    const remaining = listOf(
      repository.deleteChatThread(CUSTOMER, ASSISTANT, second.threadId ?? ''),
    );
    expect(remaining.threads.map((thread) => thread.id)).toEqual([first.threadId]);
    expect(repository.getAssistantChat(CUSTOMER, ASSISTANT, second.threadId ?? '')).toMatchObject({
      status: 'permission-denied',
      reason: 'chat-thread',
    });
  });

  it('never exposes another account’s threads, not even to the assistant owner', () => {
    const storage = createMemoryStorage();
    const repository = createRepository(storage);
    const mine = chatOf(repository.sendChatMessage(CUSTOMER, ASSISTANT, '我的私人問題：退貨要幾天？'));

    expect(listOf(repository.listChatThreads('account-smb-admin', ASSISTANT)).threads).toEqual([]);
    const stolen = repository.getAssistantChat('account-smb-admin', ASSISTANT, mine.threadId ?? '');
    expect(stolen).toMatchObject({ status: 'permission-denied', reason: 'chat-thread' });
    expect(JSON.stringify(stolen)).not.toContain('我的私人問題');
    expect(
      repository.renameChatThread('account-smb-admin', ASSISTANT, mine.threadId ?? '', '偷改'),
    ).toMatchObject({ status: 'permission-denied', reason: 'chat-thread' });
    expect(
      repository.deleteChatThread('account-smb-admin', ASSISTANT, mine.threadId ?? ''),
    ).toMatchObject({ status: 'permission-denied', reason: 'chat-thread' });
  });

  it('keeps the owner’s usage summary anonymous while threads grow', () => {
    const repository = createRepository();
    repository.sendChatMessage(CUSTOMER, ASSISTANT, '我的私人問題：退貨要幾天？');
    const analytics = repository.getAssistantAnalytics('account-smb-admin', ASSISTANT);

    expect(analytics).toMatchObject({ status: 'ready', data: { conversationCount: 19 } });
    expect(JSON.stringify(analytics)).not.toContain('我的私人問題');
  });

  it('migrates a version 1 single-conversation record into one thread', () => {
    const storage = createMemoryStorage();
    storage.setItem(
      `sme-demo:chat:${CUSTOMER}:${ASSISTANT}`,
      JSON.stringify({
        version: 1,
        messages: [
          { id: 'chat-message-1', author: 'account', text: '舊版對話', createdAt: '2026-09-20T00:00:00.000Z' },
        ],
      }),
    );
    const repository = createRepository(storage);

    const list = listOf(repository.listChatThreads(CUSTOMER, ASSISTANT));
    expect(list.threads).toHaveLength(1);
    expect(list.threads[0].title).toContain('舊版對話');
    expect(chatOf(repository.getAssistantChat(CUSTOMER, ASSISTANT)).messages).toHaveLength(1);
  });

  it('keeps no history for an assistant whose rules turn conversation saving off', () => {
    const storage = createMemoryStorage();
    const ephemeral: AssistantConfigurationView = {
      id: 'assistant-created-1',
      ownerAccountId: 'account-smb-admin',
      name: '不留紀錄助理',
      purpose: '示範關閉保存對話',
      status: 'ready',
      audience: 'members-and-external-customers',
      // 平台內分享清單的初始值：外部客戶被勾選，所以開得了這個助理。
      sharedWithAccountIds: ['account-external-customer'],
      knowledgeBaseIds: ['knowledge-refund-policy'],
      databaseIds: [],
      keepOwnConversations: false,
    };
    storage.setItem('sme-demo:created-assistants', JSON.stringify([ephemeral]));
    const repository = createRepository(storage);

    const list = listOf(repository.listChatThreads(CUSTOMER, 'assistant-created-1'));
    expect(list.historyMode).toBe('not-saved');
    expect(list.threads).toEqual([]);
    expect(list.historyNotice).not.toBe('');

    const chat = chatOf(repository.sendChatMessage(CUSTOMER, 'assistant-created-1', '收到商品後幾天內可以退貨？'));
    expect(chat.historyMode).toBe('not-saved');
    expect(chat.threadId).toBeNull();
    expect(chat.messages).toHaveLength(2);
    expect(listOf(repository.listChatThreads(CUSTOMER, 'assistant-created-1')).threads).toEqual([]);
    expect(storage.getItem(`sme-demo:chat:${CUSTOMER}:assistant-created-1`)).toBeNull();

    // 同一個 repository 實例內是單一暫時對話；換一個實例（等同重新整理）就消失。
    expect(chatOf(repository.getAssistantChat(CUSTOMER, 'assistant-created-1')).messages).toHaveLength(2);
    expect(chatOf(createRepository(storage).getAssistantChat(CUSTOMER, 'assistant-created-1')).messages).toEqual([]);
  });

  it('denies thread operations for an assistant the account cannot use', () => {
    const repository = createRepository();

    expect(repository.listChatThreads(CUSTOMER, 'assistant-internal-onboarding')).toMatchObject({
      status: 'permission-denied',
      reason: 'assistant-use',
    });
    expect(repository.createChatThread(CUSTOMER, 'assistant-does-not-exist')).toMatchObject({
      status: 'permission-denied',
      reason: 'assistant-use',
    });
  });
});
