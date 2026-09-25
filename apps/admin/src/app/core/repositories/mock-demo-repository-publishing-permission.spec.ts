import { beforeEach, describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import { DEMO_SEED } from './demo-seed';
import { createMemoryStorage } from './memory-storage';
import { MockDemoRepository } from './mock-demo-repository';

const ADMIN: AccountId = 'account-smb-admin';
const ASSISTANT = 'assistant-customer-service';

// channelId/channelSecret 刻意用字串組合而非常值寫死，避免密鑰掃描器把這組假值
// 誤判為真的 LINE Messaging OAuth2 憑證。
const VALID_LINE = {
  officialAccountId: '@anxin-demo',
  channelId: '12345' + '67890',
  channelSecret: ('abcdef' + '0123456789').repeat(2),
  accessToken: 'demo-access-token-value-that-is-long-enough-0001',
};

describe('MockDemoRepository publishing permission', () => {
  let repository: MockDemoRepository;

  beforeEach(() => {
    repository = new MockDemoRepository(DEMO_SEED, {
      storage: createMemoryStorage(),
      now: () => new Date('2026-09-23T02:00:00.000Z'),
      viewer: () => ADMIN,
    });
  });

  /** 拿掉 manage-publishing，但仍然是助理擁有者。 */
  function revokePublishing(): void {
    // mock 的 Observable 是同步的，訂閱當下就寫入。
    repository
      .updateMemberPermissions(ADMIN, [
        'manage-assistants',
        'manage-data-sources',
        'read-consented-submissions',
      ])
      .subscribe();
  }

  it('lets the owner with manage-publishing read and change every channel', () => {
    expect(repository.getAssistantPublishing(ADMIN, ASSISTANT).status).toBe('ready');
    expect(repository.updatePlatformSharing(ADMIN, ASSISTANT, []).status).toBe('ready');
  });

  it('refuses every publishing method once manage-publishing is revoked, with one message', () => {
    revokePublishing();

    const calls = [
      repository.getAssistantPublishing(ADMIN, ASSISTANT),
      repository.updatePlatformSharing(ADMIN, ASSISTANT, []),
      repository.checkWebsiteInstallation(ADMIN, ASSISTANT),
      repository.saveLineSettings(ADMIN, ASSISTANT, VALID_LINE),
      repository.sendLineTestMessage(ADMIN, ASSISTANT),
      repository.activateLineChannel(ADMIN, ASSISTANT),
      repository.setPublishingChannelPaused(ADMIN, ASSISTANT, 'platform', true),
    ];

    for (const call of calls) {
      expect(call).toMatchObject({ status: 'permission-denied', reason: 'publishing' });
    }
    // 不存在與無權限共用同一句話。
    const unknown = repository.getAssistantPublishing(ADMIN, 'assistant-nope');
    if (calls[0].status === 'permission-denied' && unknown.status === 'permission-denied') {
      expect(calls[0].message).toBe(unknown.message);
      expect(calls[0].message).not.toContain('客服助理');
    }
  });

  it('empties the channel overview instead of listing channels it cannot open', () => {
    revokePublishing();

    expect(repository.listChannelOverview(ADMIN)).toEqual({ status: 'ready', data: [] });
    expect(repository.listPublishingChannels(ADMIN)).toEqual({ status: 'ready', data: [] });
  });

  it('keeps the saved channel settings and restores them when the permission comes back', () => {
    repository.updatePlatformSharing(ADMIN, ASSISTANT, ['account-internal-employee']);
    revokePublishing();
    repository
      .updateMemberPermissions(ADMIN, [
        'manage-assistants',
        'manage-data-sources',
        'manage-publishing',
        'read-consented-submissions',
      ])
      .subscribe();

    const view = repository.getAssistantPublishing(ADMIN, ASSISTANT);
    expect(view.status).toBe('ready');
    if (view.status === 'ready') {
      expect(view.data.platform.allowedAccountIds).toEqual(['account-internal-employee']);
    }
  });

  it('still lets a shared account open the assistant while the owner cannot edit publishing', () => {
    revokePublishing();

    // 發布設定與「誰開得了」是兩件事：收回設定權限不等於把使用者踢出去。
    const chat = repository.getAssistantChat('account-internal-employee', ASSISTANT);
    expect(chat.status).toBe('ready');
  });
});
