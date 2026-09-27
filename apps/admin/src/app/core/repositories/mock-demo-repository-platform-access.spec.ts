import { firstValueFrom } from 'rxjs';
import type { AccountId, ChatViewerId, VisitorId } from '../domain/account.model';
import type { AssistantChatView, ChatThreadListView } from '../domain/conversation.model';
import type { PublishingChannelType } from '../domain/publishing.model';
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

/** `getAssistantChat`／`listChatThreads` 的非同步契約不再接收 viewer；用這個 box 切換。 */
const viewerBoxes = new WeakMap<MockDemoRepository, { current: ChatViewerId | null }>();

function createRepository(storage = createMemoryStorage(), visitorStorage = createMemoryStorage()) {
  const box: { current: ChatViewerId | null } = { current: null };
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage,
    visitorStorage,
    now: () => new Date('2026-09-22T02:00:00.000Z'),
    viewer: () => (typeof box.current === 'string' && box.current.startsWith('account-') ? (box.current as AccountId) : null),
    chatViewer: () => box.current,
  });
  viewerBoxes.set(repository, box);
  return repository;
}

function boxOf(repository: MockDemoRepository) {
  const box = viewerBoxes.get(repository);
  if (box === undefined) throw new Error('unknown repository: use createRepository()');
  return box;
}

async function chatAs(
  repository: MockDemoRepository,
  viewer: ChatViewerId,
  assistantId: string,
): Promise<RepositoryView<AssistantChatView>> {
  boxOf(repository).current = viewer;
  return firstValueFrom(repository.getAssistantChat(assistantId));
}

async function threadsAs(
  repository: MockDemoRepository,
  viewer: AccountId,
  assistantId: string,
): Promise<RepositoryView<ChatThreadListView>> {
  boxOf(repository).current = viewer;
  return firstValueFrom(repository.listChatThreads(assistantId));
}

function dataOf<T>(result: RepositoryView<T> | { status: 'validation-failed' }): T {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

async function usableIds(repository: MockDemoRepository, viewer: AccountId): Promise<readonly string[]> {
  boxOf(repository).current = viewer;
  const result = await firstValueFrom(repository.listUsableAssistants());
  return dataOf(result).map((assistant) => assistant.id);
}

async function updatePlatformSharingAs(
  repository: MockDemoRepository,
  viewer: AccountId,
  assistantId: string,
  accountIds: readonly AccountId[],
) {
  boxOf(repository).current = viewer;
  return firstValueFrom(repository.updatePlatformSharing(assistantId, accountIds));
}

async function setPublishingChannelPausedAs(
  repository: MockDemoRepository,
  viewer: AccountId,
  assistantId: string,
  channelType: PublishingChannelType,
  paused: boolean,
) {
  boxOf(repository).current = viewer;
  return firstValueFrom(repository.setPublishingChannelPaused(assistantId, channelType, paused));
}

function threadsOf(result: RepositoryView<ChatThreadListView>): ChatThreadListView {
  return dataOf(result);
}

function chatOf(result: RepositoryView<AssistantChatView>): AssistantChatView {
  return dataOf(result);
}

describe('MockDemoRepository platform sharing decides who may open an assistant', () => {
  it('lets a listed account in and shuts the same account out once it is unticked', async () => {
    const repository = createRepository();

    expect(await usableIds(repository, EMPLOYEE)).toContain(CUSTOMER_SERVICE);
    expect((await chatAs(repository, EMPLOYEE, CUSTOMER_SERVICE)).status).toBe('ready');

    await updatePlatformSharingAs(repository, ADMIN, CUSTOMER_SERVICE, [CUSTOMER]);

    expect(await usableIds(repository, EMPLOYEE)).not.toContain(CUSTOMER_SERVICE);
    const denied = await chatAs(repository, EMPLOYEE, CUSTOMER_SERVICE);
    const unknown = await chatAs(repository, EMPLOYEE, 'assistant-does-not-exist');
    expect(denied).toEqual(unknown);
    expect(denied).toMatchObject({ status: 'permission-denied', reason: 'assistant-use' });
    expect(JSON.stringify(denied)).not.toContain('客服助理');

    await updatePlatformSharingAs(repository, ADMIN, CUSTOMER_SERVICE, [EMPLOYEE, CUSTOMER]);
    expect(await usableIds(repository, EMPLOYEE)).toContain(CUSTOMER_SERVICE);
  });

  it('keeps the conversations of an account that loses access and gives them back on re-adding', async () => {
    const storage = createMemoryStorage();
    const repository = createRepository(storage);

    dataOf(repository.sendChatMessage(EMPLOYEE, CUSTOMER_SERVICE, '收到商品後幾天內可以退貨？'));
    const chatKey = `sme-demo:chat:${EMPLOYEE}:${CUSTOMER_SERVICE}`;
    expect(storage.getItem(chatKey)).not.toBeNull();
    expect(threadsOf(await threadsAs(repository, EMPLOYEE, CUSTOMER_SERVICE)).threads).toHaveLength(1);

    await updatePlatformSharingAs(repository, ADMIN, CUSTOMER_SERVICE, [CUSTOMER]);

    expect(await threadsAs(repository, EMPLOYEE, CUSTOMER_SERVICE)).toMatchObject({
      status: 'permission-denied',
      reason: 'assistant-use',
    });
    // 被取消勾選只收回權限，不刪資料：對話仍留在儲存中。
    expect(storage.getItem(chatKey)).toContain('退貨');

    await updatePlatformSharingAs(repository, ADMIN, CUSTOMER_SERVICE, [EMPLOYEE, CUSTOMER]);
    const restored = threadsOf(await threadsAs(repository, EMPLOYEE, CUSTOMER_SERVICE));
    expect(restored.threads).toHaveLength(1);
    expect(chatOf(await chatAs(repository, EMPLOYEE, CUSTOMER_SERVICE)).messages.length).toBeGreaterThan(0);
  });

  it('keeps the owner in even when nobody is ticked and when the channel is paused', async () => {
    const repository = createRepository();

    dataOf(await updatePlatformSharingAs(repository, ADMIN, CUSTOMER_SERVICE, []));
    expect(await usableIds(repository, ADMIN)).toContain(CUSTOMER_SERVICE);
    expect((await chatAs(repository, ADMIN, CUSTOMER_SERVICE)).status).toBe('ready');

    await setPublishingChannelPausedAs(repository, ADMIN, CUSTOMER_SERVICE, 'platform', true);
    expect((await chatAs(repository, ADMIN, CUSTOMER_SERVICE)).status).toBe('ready');
  });

  it('closes the platform door for everyone but the owner while the channel is paused', async () => {
    const repository = createRepository();

    // 種子資料：內部教育訓練助理的平台內分享已暫停，同仁雖在清單內也開不了。
    expect(await usableIds(repository, EMPLOYEE)).not.toContain(ONBOARDING);
    expect(await usableIds(repository, ADMIN)).toContain(ONBOARDING);

    await setPublishingChannelPausedAs(repository, ADMIN, ONBOARDING, 'platform', false);
    expect(await usableIds(repository, EMPLOYEE)).toContain(ONBOARDING);
  });

  it('lets the audience decide the kind of viewer and the list decide which accounts', async () => {
    const repository = createRepository();
    await setPublishingChannelPausedAs(repository, ADMIN, ONBOARDING, 'platform', false);

    // 內部教育訓練助理的使用對象只有內部員工：外部客戶就算被勾選也開不了。
    dataOf(await updatePlatformSharingAs(repository, ADMIN, ONBOARDING, [EMPLOYEE, CUSTOMER]));
    expect(await usableIds(repository, EMPLOYEE)).toContain(ONBOARDING);
    expect(await usableIds(repository, CUSTOMER)).not.toContain(ONBOARDING);
    expect(await chatAs(repository, CUSTOMER, ONBOARDING)).toMatchObject({
      status: 'permission-denied',
      reason: 'assistant-use',
    });
  });

  it('demonstrates the rule with the seed: the employee can use one assistant and not the other', async () => {
    const repository = createRepository();

    expect(await usableIds(repository, EMPLOYEE)).toEqual([CUSTOMER_SERVICE]);
    expect(await usableIds(repository, CUSTOMER)).toEqual([CUSTOMER_SERVICE]);
  });

  it('leaves anonymous visitors to the external channels, not to the platform list', async () => {
    const repository = createRepository();

    expect((await chatAs(repository, VISITOR, CUSTOMER_SERVICE)).status).toBe('ready');

    // 清空平台內分享清單不影響未登入訪客：他們由官網嵌入／LINE 是否已發布決定。
    dataOf(await updatePlatformSharingAs(repository, ADMIN, CUSTOMER_SERVICE, []));
    expect((await chatAs(repository, VISITOR, CUSTOMER_SERVICE)).status).toBe('ready');

    // 反過來，勾選帳號也不會讓內部助理對外開放。
    await setPublishingChannelPausedAs(repository, ADMIN, ONBOARDING, 'platform', false);
    dataOf(await updatePlatformSharingAs(repository, ADMIN, ONBOARDING, [EMPLOYEE]));
    expect(await chatAs(repository, VISITOR, ONBOARDING)).toMatchObject({
      status: 'permission-denied',
      reason: 'assistant-use',
    });
  });
});
