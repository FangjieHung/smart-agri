import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import { DEMO_SEED, type DemoSeed } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository, ORGANIZATION_SETTINGS_DENIED_MESSAGE } from './mock-demo-repository';

const ADMIN: AccountId = 'account-smb-admin';
const EMPLOYEE: AccountId = 'account-internal-employee';
const EXTERNAL: AccountId = 'account-external-customer';
const CUSTOMER_SERVICE = 'assistant-customer-service';
const ONBOARDING = 'assistant-internal-onboarding';
const STAFF = 'assistant-created-901';

/** 同仁也能建助理，並擁有一個不保存對話的助理：擁有者不是管理者（決定 E）。 */
const SEED: DemoSeed = {
  ...DEMO_SEED,
  accounts: DEMO_SEED.accounts.map((account) =>
    account.id === EMPLOYEE ? { ...account, permissions: [...account.permissions, 'manage-assistants'] } : account,
  ),
  assistants: [
    ...DEMO_SEED.assistants,
    { ...DEMO_SEED.assistants[1], id: STAFF, ownerAccountId: EMPLOYEE, name: '同仁助理', keepOwnConversations: false },
  ],
};

function dataOf<T>(result: { status: string }): T {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return (result as unknown as { data: T }).data;
}

describe('MockDemoRepository immediate purge (issue #242)', () => {
  let storage: ReturnType<typeof createMemoryStorage>;
  let viewer: AccountId | null;
  let repository: MockDemoRepository;

  const storeThreads = (accountId: AccountId, assistantId: string, updatedAts: readonly string[]) =>
    storage.setItem(
      `sme-demo:chat:${accountId}:${assistantId}`,
      JSON.stringify({
        version: 2,
        threads: updatedAts.map((updatedAt, index) => ({
          id: `chat-thread-${index + 1}`,
          title: `退貨問題 ${index + 1}`,
          titleSource: 'derived',
          createdAt: updatedAt,
          updatedAt,
          messages: [],
        })),
      }),
    );

  beforeEach(() => {
    storage = createMemoryStorage();
    viewer = ADMIN;
    repository = new MockDemoRepository(SEED, { storage, viewer: () => viewer });
    storeThreads(ADMIN, CUSTOMER_SERVICE, ['2026-10-01T00:00:00.000Z']);
    storeThreads(EMPLOYEE, CUSTOMER_SERVICE, ['2026-10-03T00:00:00.000Z', '2026-09-20T00:00:00.000Z']);
    storeThreads(EMPLOYEE, STAFF, ['2026-10-02T00:00:00.000Z']);
  });

  it('lists every assistant with its threads, members, last activity and switch, numbers only', async () => {
    const rows = dataOf<readonly Record<string, unknown>[]>(await firstValueFrom(repository.listRetentionAssistants()));

    expect(rows).toEqual([
      { assistantId: CUSTOMER_SERVICE, assistantName: '客服助理', keepConversations: true, threadCount: 3, accountCount: 2, lastActivityAt: '2026-10-03T00:00:00.000Z' },
      { assistantId: ONBOARDING, assistantName: '內部教育訓練助理', keepConversations: true, threadCount: 0, accountCount: 0, lastActivityAt: null },
      { assistantId: STAFF, assistantName: '同仁助理', keepConversations: false, threadCount: 1, accountCount: 1, lastActivityAt: '2026-10-02T00:00:00.000Z' },
    ]);
    expect(JSON.stringify(rows)).not.toContain('退貨問題');
  });

  it('purges every member\'s threads on that assistant only, whether the switch is on or off', async () => {
    expect(dataOf(await firstValueFrom(repository.purgeAssistantConversations(CUSTOMER_SERVICE)))).toEqual({ deletedThreadCount: 3 });
    expect(dataOf(await firstValueFrom(repository.purgeAssistantConversations(STAFF)))).toEqual({ deletedThreadCount: 1 });
    expect(dataOf(await firstValueFrom(repository.purgeAssistantConversations(STAFF)))).toEqual({ deletedThreadCount: 0 });

    const rows = dataOf<readonly { threadCount: number }[]>(await firstValueFrom(repository.listRetentionAssistants()));
    expect(rows.map((row) => row.threadCount)).toEqual([0, 0, 0]);
    // 成員自己的對話紀錄裡也不見了。
    viewer = EMPLOYEE;
    const list = dataOf<{ threads: readonly unknown[] }>(await firstValueFrom(repository.listChatThreads(CUSTOMER_SERVICE)));
    expect(list.threads).toEqual([]);
  });

  it('refuses the list and the purge to non-managers, the owner included, with the same denial', async () => {
    const denied = { status: 'permission-denied', reason: 'organization-settings', message: ORGANIZATION_SETTINGS_DENIED_MESSAGE };
    for (const account of [EMPLOYEE, EXTERNAL]) {
      viewer = account;
      expect(await firstValueFrom(repository.listRetentionAssistants())).toEqual(denied);
      expect(await firstValueFrom(repository.purgeAssistantConversations(STAFF))).toEqual(denied);
      expect(await firstValueFrom(repository.purgeAssistantConversations(CUSTOMER_SERVICE))).toEqual(denied);
    }
    viewer = ADMIN;
    expect(await firstValueFrom(repository.purgeAssistantConversations('assistant-unknown'))).toEqual(denied);
    viewer = null;
    expect(await firstValueFrom(repository.purgeAssistantConversations(CUSTOMER_SERVICE))).toEqual(denied);

    viewer = ADMIN;
    const rows = dataOf<readonly { threadCount: number }[]>(await firstValueFrom(repository.listRetentionAssistants()));
    expect(rows.map((row) => row.threadCount)).toEqual([3, 0, 1]);
  });

  it('gives the summary to the manager and the owner, and assistant-configuration to anyone else', async () => {
    expect(dataOf(await firstValueFrom(repository.getAssistantConversationSummary(CUSTOMER_SERVICE)))).toEqual({
      threadCount: 3,
      accountCount: 2,
      canPurge: true,
    });
    expect(dataOf(await firstValueFrom(repository.getAssistantConversationSummary(STAFF)))).toEqual({
      threadCount: 1,
      accountCount: 1,
      canPurge: true,
    });

    viewer = EMPLOYEE;
    expect(dataOf(await firstValueFrom(repository.getAssistantConversationSummary(STAFF)))).toEqual({
      threadCount: 1,
      accountCount: 1,
      canPurge: false,
    });
    const notOwned = await firstValueFrom(repository.getAssistantConversationSummary(CUSTOMER_SERVICE));
    expect(notOwned).toMatchObject({ status: 'permission-denied', reason: 'assistant-configuration' });
    expect(await firstValueFrom(repository.getAssistantConversationSummary('assistant-unknown'))).toEqual(notOwned);

    viewer = EXTERNAL;
    expect(await firstValueFrom(repository.getAssistantConversationSummary(STAFF))).toEqual(notOwned);
  });
});
