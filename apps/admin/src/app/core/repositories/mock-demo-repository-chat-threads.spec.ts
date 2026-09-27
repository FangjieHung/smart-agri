import { firstValueFrom } from 'rxjs';
import type { AccountId } from '../domain/account.model';
import type { AssistantConfigurationView } from '../domain/assistant.model';
import type { AssistantChatView, ChatThreadListView } from '../domain/conversation.model';
import type { RepositoryView, SendChatMessageResult } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';

const ASSISTANT = 'assistant-customer-service';
const CUSTOMER: AccountId = 'account-external-customer';

/**
 * 讓測試可以在同一個 repository 實例上切換「目前帳號」：非同步契約下
 * `listChatThreads`／`createChatThread`／`renameChatThread`／`deleteChatThread`／
 * `getAssistantChat` 不再接收 viewer，改由 `viewer()` 這個回呼推導。
 */
interface ViewerRef {
  current: AccountId | null;
}

function createRepository(storage = createMemoryStorage(), tick = { value: 0 }, viewerRef: ViewerRef = { current: CUSTOMER }) {
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage,
    now: () => new Date(Date.parse('2026-09-22T02:00:00.000Z') + tick.value++ * 1000),
    viewer: () => viewerRef.current,
  });
  return { repository, viewerRef };
}

function ready<T>(result: RepositoryView<T>): T {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

/** `sendChatMessage` 仍是同步契約（issue #80 才改），這裡另外收斂它自己的 union。 */
function sentChatOf(result: SendChatMessageResult): AssistantChatView {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

async function chatOf(result: Promise<RepositoryView<AssistantChatView>>): Promise<AssistantChatView> {
  return ready(await result);
}

async function listOf(result: Promise<RepositoryView<ChatThreadListView>>): Promise<ChatThreadListView> {
  return ready(await result);
}

describe('MockDemoRepository chat threads', () => {
  it('starts with no saved threads and creates one on the first message', async () => {
    const { repository } = createRepository();

    expect(await listOf(firstValueFrom(repository.listChatThreads(ASSISTANT)))).toMatchObject({
      assistantId: ASSISTANT,
      assistantName: '客服助理',
      historyMode: 'saved',
      threads: [],
    });

    const chat = sentChatOf(repository.sendChatMessage(CUSTOMER, ASSISTANT, '收到商品後幾天內可以退貨？'));
    expect(chat.threadId).not.toBeNull();

    const list = await listOf(firstValueFrom(repository.listChatThreads(ASSISTANT)));
    expect(list.threads).toHaveLength(1);
    expect(list.threads[0]).toMatchObject({ id: chat.threadId, messageCount: 2 });
  });

  it('derives the thread title from the first question and keeps it after later messages', async () => {
    const { repository } = createRepository();
    repository.sendChatMessage(CUSTOMER, ASSISTANT, '收到商品後幾天內可以退貨？');
    repository.sendChatMessage(CUSTOMER, ASSISTANT, '皮革商品平常要怎麼保養？');

    const list = await listOf(firstValueFrom(repository.listChatThreads(ASSISTANT)));
    expect(list.threads).toHaveLength(1);
    expect(list.threads[0].title).toContain('收到商品後幾天內可以退貨');
    expect(list.threads[0].messageCount).toBe(4);
  });

  it('opens a new empty thread and lists threads newest activity first', async () => {
    const { repository } = createRepository();
    const first = sentChatOf(repository.sendChatMessage(CUSTOMER, ASSISTANT, '收到商品後幾天內可以退貨？'));
    const second = await chatOf(firstValueFrom(repository.createChatThread(ASSISTANT)));

    expect(second.threadId).not.toBe(first.threadId);
    expect(second.messages).toEqual([]);

    const list = await listOf(firstValueFrom(repository.listChatThreads(ASSISTANT)));
    expect(list.threads.map((thread) => thread.id)).toEqual([second.threadId, first.threadId]);

    repository.sendChatMessage(CUSTOMER, ASSISTANT, '我要回報訂單問題', first.threadId ?? undefined);
    expect(
      (await listOf(firstValueFrom(repository.listChatThreads(ASSISTANT)))).threads.map((thread) => thread.id),
    ).toEqual([first.threadId, second.threadId]);
  });

  it('opens a thread by id and keeps each thread’s messages apart', async () => {
    const { repository } = createRepository();
    const first = sentChatOf(repository.sendChatMessage(CUSTOMER, ASSISTANT, '收到商品後幾天內可以退貨？'));
    const second = await chatOf(firstValueFrom(repository.createChatThread(ASSISTANT)));
    repository.sendChatMessage(CUSTOMER, ASSISTANT, '皮革商品平常要怎麼保養？', second.threadId ?? undefined);

    const reopened = await chatOf(firstValueFrom(repository.getAssistantChat(ASSISTANT, first.threadId ?? undefined)));
    expect(reopened.messages).toHaveLength(2);
    expect(JSON.stringify(reopened.messages)).not.toContain('保養');
  });

  it('renames a thread and rejects an empty or over-long name', async () => {
    const { repository } = createRepository();
    const chat = sentChatOf(repository.sendChatMessage(CUSTOMER, ASSISTANT, '收到商品後幾天內可以退貨？'));
    const threadId = chat.threadId ?? '';

    expect(await firstValueFrom(repository.renameChatThread(ASSISTANT, threadId, '   '))).toMatchObject({
      status: 'validation-failed',
    });
    expect(await firstValueFrom(repository.renameChatThread(ASSISTANT, threadId, 'x'.repeat(61)))).toMatchObject({
      status: 'validation-failed',
    });

    expect(await firstValueFrom(repository.renameChatThread(ASSISTANT, threadId, '退貨問題'))).toMatchObject({
      status: 'ready',
      data: { id: threadId, title: '退貨問題' },
    });
    repository.sendChatMessage(CUSTOMER, ASSISTANT, '我要回報訂單問題', threadId);
    expect((await listOf(firstValueFrom(repository.listChatThreads(ASSISTANT)))).threads[0].title).toBe('退貨問題');
  });

  it('deletes a thread and returns the remaining list', async () => {
    const { repository } = createRepository();
    const first = sentChatOf(repository.sendChatMessage(CUSTOMER, ASSISTANT, '收到商品後幾天內可以退貨？'));
    const second = await chatOf(firstValueFrom(repository.createChatThread(ASSISTANT)));

    const remaining = await listOf(
      firstValueFrom(repository.deleteChatThread(ASSISTANT, second.threadId ?? '')),
    );
    expect(remaining.threads.map((thread) => thread.id)).toEqual([first.threadId]);
    expect(await firstValueFrom(repository.getAssistantChat(ASSISTANT, second.threadId ?? ''))).toMatchObject({
      status: 'permission-denied',
      reason: 'chat-thread',
    });
  });

  it('never exposes another account’s threads, not even to the assistant owner', async () => {
    const storage = createMemoryStorage();
    const { repository, viewerRef } = createRepository(storage);
    const mine = sentChatOf(repository.sendChatMessage(CUSTOMER, ASSISTANT, '我的私人問題：退貨要幾天？'));

    viewerRef.current = 'account-smb-admin';
    expect((await listOf(firstValueFrom(repository.listChatThreads(ASSISTANT)))).threads).toEqual([]);
    const stolen = await firstValueFrom(repository.getAssistantChat(ASSISTANT, mine.threadId ?? ''));
    expect(stolen).toMatchObject({ status: 'permission-denied', reason: 'chat-thread' });
    expect(JSON.stringify(stolen)).not.toContain('我的私人問題');
    expect(
      await firstValueFrom(repository.renameChatThread(ASSISTANT, mine.threadId ?? '', '偷改')),
    ).toMatchObject({ status: 'permission-denied', reason: 'chat-thread' });
    expect(
      await firstValueFrom(repository.deleteChatThread(ASSISTANT, mine.threadId ?? '')),
    ).toMatchObject({ status: 'permission-denied', reason: 'chat-thread' });
  });

  it('keeps the owner’s usage summary anonymous while threads grow', () => {
    const { repository } = createRepository();
    repository.sendChatMessage(CUSTOMER, ASSISTANT, '我的私人問題：退貨要幾天？');
    const analytics = repository.getAssistantAnalytics('account-smb-admin', ASSISTANT);

    expect(analytics).toMatchObject({ status: 'ready', data: { conversationCount: 19 } });
    expect(JSON.stringify(analytics)).not.toContain('我的私人問題');
  });

  it('migrates a version 1 single-conversation record into one thread', async () => {
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
    const { repository } = createRepository(storage);

    const list = await listOf(firstValueFrom(repository.listChatThreads(ASSISTANT)));
    expect(list.threads).toHaveLength(1);
    expect(list.threads[0].title).toContain('舊版對話');
    expect((await chatOf(firstValueFrom(repository.getAssistantChat(ASSISTANT)))).messages).toHaveLength(1);
  });

  it('keeps no history for an assistant whose rules turn conversation saving off', async () => {
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
    const { repository } = createRepository(storage);

    const list = await listOf(firstValueFrom(repository.listChatThreads('assistant-created-1')));
    expect(list.historyMode).toBe('not-saved');
    expect(list.threads).toEqual([]);
    expect(list.historyNotice).not.toBe('');

    const chat = sentChatOf(repository.sendChatMessage(CUSTOMER, 'assistant-created-1', '收到商品後幾天內可以退貨？'));
    expect(chat.historyMode).toBe('not-saved');
    expect(chat.threadId).toBeNull();
    expect(chat.messages).toHaveLength(2);
    expect((await listOf(firstValueFrom(repository.listChatThreads('assistant-created-1')))).threads).toEqual([]);
    expect(storage.getItem(`sme-demo:chat:${CUSTOMER}:assistant-created-1`)).toBeNull();

    // 同一個 repository 實例內是單一暫時對話；換一個實例（等同重新整理）就消失。
    expect((await chatOf(firstValueFrom(repository.getAssistantChat('assistant-created-1')))).messages).toHaveLength(2);
    const { repository: reloaded } = createRepository(storage);
    expect((await chatOf(firstValueFrom(reloaded.getAssistantChat('assistant-created-1')))).messages).toEqual([]);
  });

  it('denies thread operations for an assistant the account cannot use', async () => {
    const { repository } = createRepository();

    expect(await firstValueFrom(repository.listChatThreads('assistant-internal-onboarding'))).toMatchObject({
      status: 'permission-denied',
      reason: 'assistant-use',
    });
    expect(await firstValueFrom(repository.createChatThread('assistant-does-not-exist'))).toMatchObject({
      status: 'permission-denied',
      reason: 'assistant-use',
    });
  });
});
