import type { AccountId, VisitorId } from '../domain/account.model';
import type { AssistantChatView, ChatThreadListView } from '../domain/conversation.model';
import type { RepositoryView } from './demo-repository';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';

/**
 * 平台內分享的勾選清單就是「誰能在平台內開啟助理」的依據。
 * 使用對象（audience）決定「哪一種人」，勾選清單決定「哪些帳號」，擁有者永遠開得了。
 */

const ADMIN: AccountId = 'account-smb-admin';
const EMPLOYEE: AccountId = 'account-internal-employee';
const CUSTOMER: AccountId = 'account-external-customer';
const VISITOR: VisitorId = 'visitor-aaaaaaaa';
const CUSTOMER_SERVICE = 'assistant-customer-service';
const ONBOARDING = 'assistant-internal-onboarding';

function createRepository(storage = createMemoryStorage(), visitorStorage = createMemoryStorage()) {
  return new MockDemoRepository(DEMO_SEED, {
    storage,
    visitorStorage,
    now: () => new Date('2026-09-22T02:00:00.000Z'),
  });
}

function dataOf<T>(result: RepositoryView<T> | { status: 'validation-failed' }): T {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

function usableIds(repository: MockDemoRepository, viewer: AccountId): readonly string[] {
  return dataOf(repository.listUsableAssistants(viewer)).map((assistant) => assistant.id);
}

function threadsOf(result: unknown): ChatThreadListView {
  return dataOf(result as RepositoryView<ChatThreadListView>);
}

function chatOf(result: unknown): AssistantChatView {
  return dataOf(result as RepositoryView<AssistantChatView>);
}

describe('MockDemoRepository platform sharing decides who may open an assistant', () => {
  it('lets a listed account in and shuts the same account out once it is unticked', () => {
    const repository = createRepository();

    expect(usableIds(repository, EMPLOYEE)).toContain(CUSTOMER_SERVICE);
    expect(repository.getAssistantChat(EMPLOYEE, CUSTOMER_SERVICE).status).toBe('ready');

    repository.updatePlatformSharing(ADMIN, CUSTOMER_SERVICE, [CUSTOMER]);

    expect(usableIds(repository, EMPLOYEE)).not.toContain(CUSTOMER_SERVICE);
    const denied = repository.getAssistantChat(EMPLOYEE, CUSTOMER_SERVICE);
    const unknown = repository.getAssistantChat(EMPLOYEE, 'assistant-does-not-exist');
    expect(denied).toEqual(unknown);
    expect(denied).toMatchObject({ status: 'permission-denied', reason: 'assistant-use' });
    expect(JSON.stringify(denied)).not.toContain('客服助理');

    repository.updatePlatformSharing(ADMIN, CUSTOMER_SERVICE, [EMPLOYEE, CUSTOMER]);
    expect(usableIds(repository, EMPLOYEE)).toContain(CUSTOMER_SERVICE);
  });

  it('keeps the conversations of an account that loses access and gives them back on re-adding', () => {
    const storage = createMemoryStorage();
    const repository = createRepository(storage);

    chatOf(repository.sendChatMessage(EMPLOYEE, CUSTOMER_SERVICE, '收到商品後幾天內可以退貨？'));
    const chatKey = `sme-demo:chat:${EMPLOYEE}:${CUSTOMER_SERVICE}`;
    expect(storage.getItem(chatKey)).not.toBeNull();
    expect(threadsOf(repository.listChatThreads(EMPLOYEE, CUSTOMER_SERVICE)).threads).toHaveLength(1);

    repository.updatePlatformSharing(ADMIN, CUSTOMER_SERVICE, [CUSTOMER]);

    expect(repository.listChatThreads(EMPLOYEE, CUSTOMER_SERVICE)).toMatchObject({
      status: 'permission-denied',
      reason: 'assistant-use',
    });
    // 被取消勾選只收回權限，不刪資料：對話仍留在儲存中。
    expect(storage.getItem(chatKey)).toContain('退貨');

    repository.updatePlatformSharing(ADMIN, CUSTOMER_SERVICE, [EMPLOYEE, CUSTOMER]);
    const restored = threadsOf(repository.listChatThreads(EMPLOYEE, CUSTOMER_SERVICE));
    expect(restored.threads).toHaveLength(1);
    expect(chatOf(repository.getAssistantChat(EMPLOYEE, CUSTOMER_SERVICE)).messages.length).toBeGreaterThan(0);
  });

  it('keeps the owner in even when nobody is ticked and when the channel is paused', () => {
    const repository = createRepository();

    dataOf(repository.updatePlatformSharing(ADMIN, CUSTOMER_SERVICE, []));
    expect(usableIds(repository, ADMIN)).toContain(CUSTOMER_SERVICE);
    expect(repository.getAssistantChat(ADMIN, CUSTOMER_SERVICE).status).toBe('ready');

    repository.setPublishingChannelPaused(ADMIN, CUSTOMER_SERVICE, 'platform', true);
    expect(repository.getAssistantChat(ADMIN, CUSTOMER_SERVICE).status).toBe('ready');
  });

  it('closes the platform door for everyone but the owner while the channel is paused', () => {
    const repository = createRepository();

    // 種子資料：內部教育訓練助理的平台內分享已暫停，同仁雖在清單內也開不了。
    expect(usableIds(repository, EMPLOYEE)).not.toContain(ONBOARDING);
    expect(usableIds(repository, ADMIN)).toContain(ONBOARDING);

    repository.setPublishingChannelPaused(ADMIN, ONBOARDING, 'platform', false);
    expect(usableIds(repository, EMPLOYEE)).toContain(ONBOARDING);
  });

  it('lets the audience decide the kind of viewer and the list decide which accounts', () => {
    const repository = createRepository();
    repository.setPublishingChannelPaused(ADMIN, ONBOARDING, 'platform', false);

    // 內部教育訓練助理的使用對象只有內部員工：外部客戶就算被勾選也開不了。
    dataOf(repository.updatePlatformSharing(ADMIN, ONBOARDING, [EMPLOYEE, CUSTOMER]));
    expect(usableIds(repository, EMPLOYEE)).toContain(ONBOARDING);
    expect(usableIds(repository, CUSTOMER)).not.toContain(ONBOARDING);
    expect(repository.getAssistantChat(CUSTOMER, ONBOARDING)).toMatchObject({
      status: 'permission-denied',
      reason: 'assistant-use',
    });
  });

  it('demonstrates the rule with the seed: the employee can use one assistant and not the other', () => {
    const repository = createRepository();

    expect(usableIds(repository, EMPLOYEE)).toEqual([CUSTOMER_SERVICE]);
    expect(usableIds(repository, CUSTOMER)).toEqual([CUSTOMER_SERVICE]);
  });

  it('leaves anonymous visitors to the external channels, not to the platform list', () => {
    const repository = createRepository();

    expect(repository.getAssistantChat(VISITOR, CUSTOMER_SERVICE).status).toBe('ready');

    // 清空平台內分享清單不影響未登入訪客：他們由官網嵌入／LINE 是否已發布決定。
    dataOf(repository.updatePlatformSharing(ADMIN, CUSTOMER_SERVICE, []));
    expect(repository.getAssistantChat(VISITOR, CUSTOMER_SERVICE).status).toBe('ready');

    // 反過來，勾選帳號也不會讓內部助理對外開放。
    repository.setPublishingChannelPaused(ADMIN, ONBOARDING, 'platform', false);
    dataOf(repository.updatePlatformSharing(ADMIN, ONBOARDING, [EMPLOYEE]));
    expect(repository.getAssistantChat(VISITOR, ONBOARDING)).toMatchObject({
      status: 'permission-denied',
      reason: 'assistant-use',
    });
  });
});
