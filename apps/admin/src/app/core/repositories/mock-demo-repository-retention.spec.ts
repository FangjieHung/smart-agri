import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import type { ChatModelOptionView, OrganizationRetentionView } from '../domain/organization-settings.model';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import {
  MOCK_RETENTION_OPTIONS,
  MockDemoRepository,
  ORGANIZATION_SETTINGS_DENIED_MESSAGE,
  retentionCutoff,
  UNKNOWN_RETENTION_DAYS_MESSAGE,
} from './mock-demo-repository';

const ADMIN: AccountId = 'account-smb-admin';
const EMPLOYEE: AccountId = 'account-internal-employee';

const TWO_MODELS: readonly ChatModelOptionView[] = [
  { id: 'fake-chat-dev', displayName: 'fake-chat-dev', model: 'fake-chat-dev' },
  { id: 'second', displayName: 'fake-chat-second', model: 'fake-chat-second' },
];

function dataOf<T = OrganizationRetentionView>(result: { status: string }): T {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return (result as unknown as { data: T }).data;
}

/** 直接寫進 mock 的對話儲存（版本 2），最後活動時間是 `updatedAt`。 */
function storeThreads(
  storage: ReturnType<typeof createMemoryStorage>,
  accountId: AccountId,
  assistantId: string,
  updatedAts: readonly string[],
): void {
  const threads = updatedAts.map((updatedAt, index) => ({
    id: `chat-thread-${index + 1}`,
    title: `對話 ${index + 1}`,
    titleSource: 'derived',
    createdAt: updatedAt,
    updatedAt,
    messages: [],
  }));
  storage.setItem(`sme-demo:chat:${accountId}:${assistantId}`, JSON.stringify({ version: 2, threads }));
}

describe('MockDemoRepository conversation retention (issue #243)', () => {
  let storage: ReturnType<typeof createMemoryStorage>;
  let viewer: AccountId | null;
  let now: Date;

  const create = (chatModels?: readonly ChatModelOptionView[]) =>
    new MockDemoRepository(DEMO_SEED, {
      storage,
      now: () => now,
      viewer: () => viewer,
      ...(chatModels ? { chatModels } : {}),
    });

  beforeEach(() => {
    storage = createMemoryStorage();
    viewer = ADMIN;
    now = new Date('2026-10-06T13:00:00.000Z');
  });

  it('keeps conversations forever by default and offers the four periods', async () => {
    const view = dataOf(await firstValueFrom(create().getOrganizationRetention()));

    expect(view).toEqual({
      days: null,
      pending: null,
      options: [30, 90, 180, 365],
      canChange: true,
      lastChange: null,
      revision: 0,
    });
    expect(MOCK_RETENTION_OPTIONS).toEqual([30, 90, 180, 365]);
  });

  it('lets any account read it, but only the manager may preview or change it', async () => {
    viewer = EMPLOYEE;
    const repository = create();
    const denied = { status: 'permission-denied', reason: 'organization-settings', message: ORGANIZATION_SETTINGS_DENIED_MESSAGE };

    expect(dataOf(await firstValueFrom(repository.getOrganizationRetention())).canChange).toBe(false);
    expect(await firstValueFrom(repository.previewOrganizationRetention(30))).toEqual(denied);
    expect(await firstValueFrom(repository.updateOrganizationRetention(30, 0))).toEqual(denied);
  });

  it('stores a shorter period as pending for 7 days and keeps the current one', async () => {
    const view = dataOf(await firstValueFrom(create().updateOrganizationRetention(30, 0)));

    expect(view.days).toBeNull();
    expect(view.pending).toEqual({ days: 30, effectiveAt: '2026-10-13T13:00:00.000Z' });
    expect(view.lastChange).toEqual({ actorName: '安心商行管理者', at: '2026-10-06T13:00:00.000Z' });
    expect(view.revision).toBe(1);
  });

  it('goes back by sending the current period, and ignores the pending one sent again', async () => {
    const repository = create();
    await firstValueFrom(repository.updateOrganizationRetention(30, 0));

    const same = dataOf(await firstValueFrom(repository.updateOrganizationRetention(30, 1)));
    expect(same.revision).toBe(1);

    const reverted = dataOf(await firstValueFrom(repository.updateOrganizationRetention(null, 1)));
    expect(reverted).toMatchObject({ days: null, pending: null, revision: 2 });
  });

  it('applies a longer period at once and drops the pending one', async () => {
    const repository = create();
    // 先讓 90 天生效：縮短後等緩衝期過去。
    await firstValueFrom(repository.updateOrganizationRetention(90, 0));
    now = new Date('2026-10-14T00:00:00.000Z');
    const effective = dataOf(await firstValueFrom(repository.getOrganizationRetention()));
    expect(effective).toMatchObject({ days: 90, pending: null, revision: 2, lastChange: { actorName: '系統' } });

    await firstValueFrom(repository.updateOrganizationRetention(30, 2));
    const longer = dataOf(await firstValueFrom(repository.updateOrganizationRetention(180, 3)));
    expect(longer).toMatchObject({ days: 180, pending: null, revision: 4 });

    const forever = dataOf(await firstValueFrom(repository.updateOrganizationRetention(null, 4)));
    expect(forever).toMatchObject({ days: null, pending: null, revision: 5 });
  });

  it('turns a pending period current once it is due, which makes an open page stale', async () => {
    const repository = create();
    await firstValueFrom(repository.updateOrganizationRetention(30, 0));
    now = new Date('2026-10-13T13:00:00.000Z');

    expect(await firstValueFrom(repository.updateOrganizationRetention(null, 1))).toEqual({
      status: 'conflict',
      message: expect.any(String),
    });
    expect(dataOf(await firstValueFrom(repository.getOrganizationRetention()))).toMatchObject({
      days: 30,
      pending: null,
      revision: 2,
      lastChange: { actorName: '系統', at: '2026-10-13T13:00:00.000Z' },
    });
  });

  it('checks the days before the revision, like the API', async () => {
    const repository = create();

    expect(await firstValueFrom(repository.updateOrganizationRetention(45, 99))).toEqual({
      status: 'validation-failed',
      message: UNKNOWN_RETENTION_DAYS_MESSAGE,
    });
    expect(await firstValueFrom(repository.updateOrganizationRetention(30, 99))).toMatchObject({ status: 'conflict' });
    expect(await firstValueFrom(repository.previewOrganizationRetention(45))).toEqual({
      status: 'validation-failed',
      message: UNKNOWN_RETENTION_DAYS_MESSAGE,
    });
  });

  it('shares the revision with the chat model', async () => {
    const repository = create(TWO_MODELS);
    await firstValueFrom(repository.updateOrganizationChatModel('second', 0));

    expect(await firstValueFrom(repository.updateOrganizationRetention(30, 0))).toMatchObject({ status: 'conflict' });
    expect(dataOf(await firstValueFrom(repository.updateOrganizationRetention(30, 1))).revision).toBe(2);
    expect(await firstValueFrom(repository.updateOrganizationChatModel(null, 1))).toMatchObject({ status: 'conflict' });
    expect(
      dataOf<{ revision: number }>(await firstValueFrom(repository.getOrganizationChatModel())).revision,
    ).toBe(2);
  });

  it('counts every account’s threads whose last activity is before the cutoff', async () => {
    storeThreads(storage, ADMIN, 'assistant-customer-service', ['2026-06-01T00:00:00.000Z', '2026-10-01T00:00:00.000Z']);
    storeThreads(storage, EMPLOYEE, 'assistant-internal-onboarding', ['2026-08-20T00:00:00.000Z']);
    const repository = create();

    const thirty = dataOf<{ days: number; threadCount: number; cutoff: string }>(
      await firstValueFrom(repository.previewOrganizationRetention(30)),
    );
    expect(thirty).toEqual({ days: 30, threadCount: 2, cutoff: '2026-09-05T16:00:00.000Z' });
    const ninety = dataOf<{ threadCount: number }>(await firstValueFrom(repository.previewOrganizationRetention(90)));
    expect(ninety.threadCount).toBe(1);
  });

  it('computes the cutoff as 00:00 of the statistics day N days ago', () => {
    // 台北 10/07 07:30（UTC 還是 10/06）：今天是 10/07，30 天前是 9/7 的 00:00（UTC 9/6 16:00）。
    expect(retentionCutoff(new Date('2026-10-06T23:30:00.000Z'), 30, 'Asia/Taipei').toISOString()).toBe(
      '2026-09-06T16:00:00.000Z',
    );
    expect(retentionCutoff(new Date('2026-10-06T23:30:00.000Z'), 30, 'UTC').toISOString()).toBe(
      '2026-09-06T00:00:00.000Z',
    );
  });
});
