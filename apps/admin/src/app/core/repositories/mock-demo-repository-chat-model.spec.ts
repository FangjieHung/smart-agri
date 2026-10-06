import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import type { ChatModelOptionView, OrganizationChatModelView } from '../domain/organization-settings.model';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import {
  MOCK_CHAT_MODELS,
  MockDemoRepository,
  ORGANIZATION_SETTINGS_DENIED_MESSAGE,
  UNKNOWN_CHAT_MODEL_MESSAGE,
} from './mock-demo-repository';

const ADMIN: AccountId = 'account-smb-admin';
const EMPLOYEE: AccountId = 'account-internal-employee';

const TWO_MODELS: readonly ChatModelOptionView[] = [
  { id: 'fake-chat-dev', displayName: 'fake-chat-dev', model: 'fake-chat-dev' },
  { id: 'second', displayName: 'fake-chat-second', model: 'fake-chat-second' },
];

function dataOf(result: { status: string }): OrganizationChatModelView {
  if (result.status !== 'ready') throw new Error(`expected ready, got ${result.status}`);
  return (result as unknown as { data: OrganizationChatModelView }).data;
}

describe('MockDemoRepository organization chat model (issue #240)', () => {
  let storage: ReturnType<typeof createMemoryStorage>;
  let viewer: AccountId | null;

  const create = (chatModels?: readonly ChatModelOptionView[]) =>
    new MockDemoRepository(DEMO_SEED, {
      storage,
      now: () => new Date('2026-10-06T03:04:00.000Z'),
      viewer: () => viewer,
      ...(chatModels ? { chatModels } : {}),
    });

  beforeEach(() => {
    storage = createMemoryStorage();
    viewer = ADMIN;
  });

  it('offers a single model by default, used as the deployment default', async () => {
    const view = dataOf(await firstValueFrom(create().getOrganizationChatModel()));

    expect(view).toEqual({
      options: MOCK_CHAT_MODELS,
      selectedId: null,
      effective: MOCK_CHAT_MODELS[0],
      source: 'deployment-default',
      canChange: true,
      lastChange: null,
      revision: 0,
    });
  });

  it('lets any account read it, but only the manager may change it', async () => {
    viewer = EMPLOYEE;
    const repository = create(TWO_MODELS);
    const view = dataOf(await firstValueFrom(repository.getOrganizationChatModel()));
    expect(view.canChange).toBe(false);

    expect(await firstValueFrom(repository.updateOrganizationChatModel('second', 0))).toEqual({
      status: 'permission-denied',
      reason: 'organization-settings',
      message: ORGANIZATION_SETTINGS_DENIED_MESSAGE,
    });
    expect(dataOf(await firstValueFrom(repository.getOrganizationChatModel())).selectedId).toBeNull();
  });

  it('switches models, records the last change and bumps the revision', async () => {
    const repository = create(TWO_MODELS);
    const view = dataOf(await firstValueFrom(repository.updateOrganizationChatModel('second', 0)));

    expect(view.selectedId).toBe('second');
    expect(view.effective).toEqual(TWO_MODELS[1]);
    expect(view.source).toBe('selected');
    expect(view.revision).toBe(1);
    expect(view.lastChange).toEqual({ actorName: '安心商行管理者', at: '2026-10-06T03:04:00.000Z' });
  });

  it('answers the same value again without writing, and a stale revision with a conflict', async () => {
    const repository = create(TWO_MODELS);
    await firstValueFrom(repository.updateOrganizationChatModel('second', 0));

    expect(dataOf(await firstValueFrom(repository.updateOrganizationChatModel('second', 0))).revision).toBe(1);
    expect((await firstValueFrom(repository.updateOrganizationChatModel(null, 0))).status).toBe('conflict');
    const back = dataOf(await firstValueFrom(repository.updateOrganizationChatModel(null, 1)));
    expect(back.source).toBe('deployment-default');
    expect(back.selectedId).toBeNull();
  });

  it('refuses an id the deployment does not offer', async () => {
    expect(await firstValueFrom(create(TWO_MODELS).updateOrganizationChatModel('no-such-model', 0))).toEqual({
      status: 'validation-failed',
      message: UNKNOWN_CHAT_MODEL_MESSAGE,
    });
  });

  it('falls back to the deployment default when the chosen model is no longer offered', async () => {
    await firstValueFrom(create(TWO_MODELS).updateOrganizationChatModel('second', 0));
    const view = dataOf(await firstValueFrom(create().getOrganizationChatModel()));

    expect(view.source).toBe('removed');
    expect(view.selectedId).toBe('second');
    expect(view.effective).toEqual(MOCK_CHAT_MODELS[0]);
  });

  it('records the model the organization uses on each new test run', async () => {
    const repository = create(TWO_MODELS);
    const first = await firstValueFrom(repository.createAssistantTestRun('assistant-customer-service'));
    await firstValueFrom(repository.updateOrganizationChatModel('second', 0));
    const second = await firstValueFrom(repository.createAssistantTestRun('assistant-customer-service'));

    expect(first.status === 'ready' && first.data.model).toBe('fake-chat-dev');
    expect(second.status === 'ready' && second.data.model).toBe('fake-chat-second');
  });
});
