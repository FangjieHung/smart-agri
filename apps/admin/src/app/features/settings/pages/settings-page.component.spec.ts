import { signal, type Provider } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import type { AccountId } from '../../../core/domain/account.model';
import { DEMO_SEED } from '../../../core/repositories/demo-seed';
import { createMemoryStorage } from '../../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../../core/repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { SettingsPageComponent } from './settings-page.component';

function render(accountId: AccountId = 'account-smb-admin') {
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage: createMemoryStorage(),
    now: () => new Date('2026-09-23T02:00:00.000Z'),
    viewer: () => accountId,
  });
  const providers: Provider[] = [
    { provide: DEMO_REPOSITORY, useValue: repository },
    { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>(accountId) } },
  ];
  TestBed.configureTestingModule({ imports: [SettingsPageComponent], providers });
  const fixture = TestBed.createComponent(SettingsPageComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

describe('SettingsPageComponent', () => {
  it('renders the appearance rows through the shared setting row', () => {
    const { host } = render();
    const rows = Array.from(host.querySelectorAll('.appearance-settings lib-setting-row'));

    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('質地');
    expect(rows[1].textContent).toContain('配色');
  });

  it('places the chat model section after the team panel and before appearance (issue #240)', async () => {
    const { fixture, host } = render();
    await fixture.whenStable();
    fixture.detectChanges();
    const order = Array.from(host.querySelectorAll('app-team-panel, app-chat-model-panel, .appearance-settings')).map(
      (element) => element.tagName.toLowerCase(),
    );

    expect(order).toEqual(['app-team-panel', 'app-chat-model-panel', 'div']);
    expect(host.querySelector('app-chat-model-panel')?.textContent).toContain('目前使用：fake-chat-dev');
  });

  it('places the conversation retention section right after the chat model section (issue #243)', async () => {
    const { fixture, host } = render();
    await fixture.whenStable();
    fixture.detectChanges();
    const order = Array.from(
      host.querySelectorAll('app-chat-model-panel, app-conversation-retention, .appearance-settings'),
    ).map((element) => element.tagName.toLowerCase());

    expect(order).toEqual(['app-chat-model-panel', 'app-conversation-retention', 'div']);
    const section = host.querySelector('[data-conversation-retention]');
    expect(section?.querySelector('h2')?.textContent).toBe('對話保存');
    expect(section?.querySelector('[data-retention-period]')).not.toBeNull();
  });

  it('lets the manager switch the chat model right after shortening the retention (shared revision, issue #243)', async () => {
    const repository = new MockDemoRepository(DEMO_SEED, {
      storage: createMemoryStorage(),
      now: () => new Date('2026-10-06T13:00:00.000Z'),
      viewer: () => 'account-smb-admin',
      chatModels: [
        { id: 'fake-chat-dev', displayName: '標準模型', model: 'fake-chat-dev' },
        { id: 'second', displayName: '進階模型', model: 'fake-chat-second' },
      ],
    });
    TestBed.configureTestingModule({
      imports: [SettingsPageComponent],
      providers: [
        { provide: DEMO_REPOSITORY, useValue: repository },
        { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>('account-smb-admin') } },
      ],
    });
    const fixture = TestBed.createComponent(SettingsPageComponent);
    const host = fixture.nativeElement as HTMLElement;
    const settle = async () => {
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    };
    await settle();
    const updateModel = vi.spyOn(repository, 'updateOrganizationChatModel');

    const retention = host.querySelector<HTMLSelectElement>('#retention-days-select');
    if (!retention) throw new Error('missing retention select');
    retention.value = '30';
    retention.dispatchEvent(new Event('change'));
    await settle();
    host.querySelector<HTMLButtonElement>('[role="dialog"] .confirm-retention')?.click();
    await settle();
    expect(host.querySelector('[data-retention-pending]')).not.toBeNull();

    const model = host.querySelector<HTMLSelectElement>('#chat-model-select');
    if (!model) throw new Error('missing chat model select');
    model.value = 'second';
    model.dispatchEvent(new Event('change'));
    await settle();

    expect(updateModel).toHaveBeenCalledWith('second', 1);
    expect(host.querySelector('#chat-model-status')?.textContent).toContain('已改用「進階模型」');
    expect(host.querySelector('app-chat-model-panel [role="alert"]')).toBeNull();
  });
});
