import { beforeEach, describe, expect, it } from 'vitest';
import { firstValueFrom } from 'rxjs';
import type { AccountId } from '../domain/account.model';
import type { WebsiteEmbedSettings } from '../domain/publishing.model';
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
  welcomeMessage: '您好！',
  nonTextReply: '目前只能回答文字問題。',
  channelSecret: ('abcdef' + '0123456789').repeat(2),
  accessToken: 'demo-access-token-value-that-is-long-enough-0001',
};

const WEBSITE_SETTINGS: WebsiteEmbedSettings = {
  displayName: '安心商行線上客服',
  welcomeMessage: '您好，我是客服助理。',
  brandColor: 'forest',
  position: 'bottom-right',
  allowedDomains: ['shop.anxin-demo.example'],
};

describe('MockDemoRepository publishing permission', () => {
  let repository: MockDemoRepository;
  /** `getAssistantChat` 的非同步契約不再接收 viewer；用這個 box 切換目前發起者。 */
  let chatViewerBox: { current: AccountId };

  beforeEach(() => {
    chatViewerBox = { current: ADMIN };
    repository = new MockDemoRepository(DEMO_SEED, {
      storage: createMemoryStorage(),
      now: () => new Date('2026-09-23T02:00:00.000Z'),
      viewer: () => ADMIN,
      chatViewer: () => chatViewerBox.current,
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

  it('lets the owner with manage-publishing read and change every channel', async () => {
    expect((await firstValueFrom(repository.getAssistantPublishing(ASSISTANT))).status).toBe('ready');
    expect((await firstValueFrom(repository.updatePlatformSharing(ASSISTANT, []))).status).toBe('ready');
  });

  it('refuses every publishing method once manage-publishing is revoked, with one message', async () => {
    revokePublishing();

    const calls = [
      await firstValueFrom(repository.getAssistantPublishing(ASSISTANT)),
      await firstValueFrom(repository.updatePlatformSharing(ASSISTANT, [])),
      await firstValueFrom(repository.updateWebsiteEmbed(ASSISTANT, WEBSITE_SETTINGS, 1)),
      await firstValueFrom(repository.publishWebsite(ASSISTANT)),
      await firstValueFrom(repository.unpublishWebsite(ASSISTANT)),
      await firstValueFrom(repository.setPublishingChannelPaused(ASSISTANT, 'website', true)),
      await firstValueFrom(repository.saveLineSettings(ASSISTANT, VALID_LINE, 1)),
      await firstValueFrom(repository.testLineConnection(ASSISTANT)),
      await firstValueFrom(repository.publishLine(ASSISTANT)),
      await firstValueFrom(repository.unpublishLine(ASSISTANT)),
      await firstValueFrom(repository.setPublishingChannelPaused(ASSISTANT, 'line', true)),
      await firstValueFrom(repository.setPublishingChannelPaused(ASSISTANT, 'platform', true)),
    ];

    for (const call of calls) {
      expect(call).toMatchObject({ status: 'permission-denied', reason: 'publishing' });
    }
    // 不存在與無權限共用同一句話。
    const unknown = await firstValueFrom(repository.getAssistantPublishing('assistant-nope'));
    if (calls[0].status === 'permission-denied' && unknown.status === 'permission-denied') {
      expect(calls[0].message).toBe(unknown.message);
      expect(calls[0].message).not.toContain('客服助理');
    }
  });

  it('empties the channel overview instead of listing channels it cannot open', async () => {
    revokePublishing();

    expect(await firstValueFrom(repository.listChannelOverview())).toEqual({ status: 'ready', data: [] });
    expect(await firstValueFrom(repository.listPublishingChannels())).toEqual({ status: 'ready', data: [] });
  });

  it('keeps the saved channel settings and restores them when the permission comes back', async () => {
    await firstValueFrom(repository.updatePlatformSharing(ASSISTANT, ['account-internal-employee']));
    revokePublishing();
    repository
      .updateMemberPermissions(ADMIN, [
        'manage-assistants',
        'manage-data-sources',
        'manage-publishing',
        'read-consented-submissions',
      ])
      .subscribe();

    const view = await firstValueFrom(repository.getAssistantPublishing(ASSISTANT));
    expect(view.status).toBe('ready');
    if (view.status === 'ready') {
      expect(view.data.platform.allowedAccountIds).toEqual(['account-internal-employee']);
    }
  });

  it('still lets a shared account open the assistant while the owner cannot edit publishing', async () => {
    revokePublishing();

    // 發布設定與「誰開得了」是兩件事：收回設定權限不等於把使用者踢出去。
    chatViewerBox.current = 'account-internal-employee';
    const chat = await firstValueFrom(repository.getAssistantChat(ASSISTANT));
    expect(chat.status).toBe('ready');
  });
});
