import { signal, type Provider } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import type { AccountId } from '../../../../core/domain/account.model';
import { DEMO_SEED, type DemoSeed } from '../../../../core/repositories/demo-seed';
import { createMemoryStorage } from '../../../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../../../core/repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../../../core/repositories/tokens';
import { DemoSessionService } from '../../../../core/session/demo-session.service';
import { KeptConversationsComponent, keptConversationsNote } from './kept-conversations.component';

const EMPLOYEE: AccountId = 'account-internal-employee';
const STAFF = 'assistant-created-901';

/** 同仁可以建助理，並擁有「同仁助理」：擁有者不是管理者。 */
const SEED: DemoSeed = {
  ...DEMO_SEED,
  accounts: DEMO_SEED.accounts.map((account) =>
    account.id === EMPLOYEE ? { ...account, permissions: [...account.permissions, 'manage-assistants'] } : account,
  ),
  assistants: [...DEMO_SEED.assistants, { ...DEMO_SEED.assistants[1], id: STAFF, ownerAccountId: EMPLOYEE, name: '同仁助理' }],
};

async function render(accountId: AccountId, assistantId = STAFF, assistantName = '同仁助理') {
  const activeAccountId = signal<AccountId | null>(accountId);
  const storage = createMemoryStorage();
  const threads = (count: number) =>
    JSON.stringify({
      version: 2,
      threads: Array.from({ length: count }, (_, index) => ({
        id: `chat-thread-${index + 1}`,
        title: `對話 ${index + 1}`,
        titleSource: 'derived',
        createdAt: '2026-10-01T00:00:00.000Z',
        updatedAt: '2026-10-01T00:00:00.000Z',
        messages: [],
      })),
    });
  storage.setItem(`sme-demo:chat:${EMPLOYEE}:${STAFF}`, threads(4));
  storage.setItem(`sme-demo:chat:account-smb-admin:${STAFF}`, threads(1));
  const repository = new MockDemoRepository(SEED, { storage, viewer: () => activeAccountId() });
  const providers: Provider[] = [
    { provide: DEMO_REPOSITORY, useValue: repository },
    { provide: DemoSessionService, useValue: { activeAccountId } },
  ];
  TestBed.configureTestingModule({ imports: [KeptConversationsComponent], providers });
  const fixture = TestBed.createComponent(KeptConversationsComponent);
  fixture.componentRef.setInput('assistantId', assistantId);
  fixture.componentRef.setInput('assistantName', assistantName);
  await settle(fixture);
  return { fixture, host: fixture.nativeElement as HTMLElement, repository };
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

describe('KeptConversationsComponent (issue #242)', () => {
  it('tells the owner how many threads stay until the retention and to ask the manager, with no button', async () => {
    const { host } = await render(EMPLOYEE);

    expect(host.querySelector('[data-kept-conversations-note]')?.textContent).toBe(
      '既有的 5 串對話會保留到保存期限；要立即刪除，請聯絡管理者。',
    );
    expect(keptConversationsNote(5)).toBe('既有的 5 串對話會保留到保存期限；要立即刪除，請聯絡管理者。');
    expect(host.querySelector('button')).toBeNull();
  });

  it('gives the manager the counts and the same purge as the settings page', async () => {
    const { fixture, host, repository } = await render('account-smb-admin');
    const purge = vi.spyOn(repository, 'purgeAssistantConversations');

    expect(host.querySelector('[data-kept-conversations-count]')?.textContent).toContain('目前已保存 5 串對話（2 位成員）');
    expect(host.querySelector('[data-kept-conversations-note]')).toBeNull();
    host.querySelector<HTMLButtonElement>('.kept-conversations__purge')?.click();
    await settle(fixture);

    expect(host.querySelector('.confirm-title')?.textContent).toBe('立即刪除「同仁助理」已保存的對話？');
    expect(host.querySelector('.confirm-detail')?.textContent).toContain('2 位成員');
    expect(host.querySelector('.confirm-detail')?.textContent).toContain('5 串對話');
    expect(host.querySelector<HTMLButtonElement>('.confirm-purge')?.disabled).toBe(true);
    const box = host.querySelector<HTMLInputElement>('#purge-acknowledge');
    if (!box) throw new Error('missing checkbox');
    box.checked = true;
    box.dispatchEvent(new Event('change'));
    await settle(fixture);
    host.querySelector<HTMLButtonElement>('.confirm-purge')?.click();
    await settle(fixture);

    expect(purge).toHaveBeenCalledWith(STAFF);
    expect(host.querySelector('[role="status"]')?.textContent).toBe('已刪除 5 串對話。');
    expect(host.querySelector('[data-kept-conversations-count]')?.textContent).toContain('目前已保存 0 串對話');
    expect(host.querySelector<HTMLButtonElement>('.kept-conversations__purge')?.disabled).toBe(true);
    expect(document.activeElement?.id).toBe('kept-conversations-count');
  });

  it('shows nothing when the summary is refused', async () => {
    const { host } = await render('account-external-customer');

    expect(host.textContent?.trim()).toBe('');
  });
});
