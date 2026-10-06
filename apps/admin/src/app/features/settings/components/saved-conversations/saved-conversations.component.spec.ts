import { signal, type Provider } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { AccountId } from '../../../../core/domain/account.model';
import { DEMO_SEED } from '../../../../core/repositories/demo-seed';
import { createMemoryStorage } from '../../../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../../../core/repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../../../core/repositories/tokens';
import { DemoSessionService } from '../../../../core/session/demo-session.service';
import { SavedConversationsComponent } from './saved-conversations.component';

type Storage = ReturnType<typeof createMemoryStorage>;

function storeThreads(storage: Storage, accountId: AccountId, assistantId: string, updatedAts: readonly string[]): void {
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
}

async function render(accountId: AccountId = 'account-smb-admin') {
  const activeAccountId = signal<AccountId | null>(accountId);
  const storage = createMemoryStorage();
  storeThreads(storage, 'account-smb-admin', 'assistant-customer-service', ['2026-10-01T02:00:00.000Z']);
  storeThreads(storage, 'account-internal-employee', 'assistant-customer-service', [
    '2026-10-03T04:05:00.000Z',
    '2026-09-20T00:00:00.000Z',
  ]);
  const repository = new MockDemoRepository(DEMO_SEED, { storage, viewer: () => activeAccountId() });
  const providers: Provider[] = [
    { provide: DEMO_REPOSITORY, useValue: repository },
    { provide: DemoSessionService, useValue: { activeAccountId } },
  ];
  TestBed.configureTestingModule({ imports: [SavedConversationsComponent], providers });
  const fixture = TestBed.createComponent(SavedConversationsComponent);
  await settle(fixture);
  return { fixture, host: fixture.nativeElement as HTMLElement, repository, storage };
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

function row(host: HTMLElement, assistantId: string): HTMLElement {
  const element = host.querySelector<HTMLElement>(`[data-assistant-row="${assistantId}"]`);
  if (!element) throw new Error(`missing row ${assistantId}`);
  return element;
}

describe('SavedConversationsComponent (issue #242)', () => {
  it('shows each assistant\'s switch, thread and member counts and last activity, without any content', async () => {
    const { host } = await render();

    const customer = row(host, 'assistant-customer-service');
    expect(customer.textContent).toContain('客服助理');
    expect(customer.querySelector('[data-keep-state]')?.textContent).toContain('開啟');
    expect(customer.querySelector('[data-thread-count]')?.textContent).toContain('已保存 3 串對話（2 位成員）');
    expect(customer.querySelector('[data-last-activity]')?.textContent).toMatch(/最後活動：2026\/10\/03 \d\d:05/);
    expect(customer.querySelector<HTMLButtonElement>('button')?.getAttribute('aria-label')).toBe('立即刪除「客服助理」已保存的對話');

    const onboarding = row(host, 'assistant-internal-onboarding');
    expect(onboarding.querySelector('[data-thread-count]')?.textContent).toContain('已保存 0 串對話（0 位成員）');
    expect(onboarding.querySelector('[data-last-activity]')?.textContent).toContain('最後活動：尚無對話');
    // 沒有對話可刪時不提供刪除。
    expect(onboarding.querySelector<HTMLButtonElement>('button')?.disabled).toBe(true);

    expect(host.textContent).not.toContain('退貨問題');
  });

  it('shows nothing at all to a non-manager', async () => {
    const { host } = await render('account-internal-employee');

    expect(host.querySelector('[data-saved-conversations]')).toBeNull();
    expect(host.querySelector('button')).toBeNull();
    expect(host.textContent?.trim()).toBe('');
  });

  it('deletes only after the acknowledgement, then reports, refreshes and moves focus to the heading', async () => {
    const { fixture, host, repository } = await render();
    const purge = vi.spyOn(repository, 'purgeAssistantConversations');

    row(host, 'assistant-customer-service').querySelector<HTMLButtonElement>('button')?.click();
    await settle(fixture);
    const dialog = host.querySelector('[role="dialog"]');
    expect(dialog?.querySelector('.confirm-title')?.textContent).toBe('立即刪除「客服助理」已保存的對話？');
    expect(dialog?.querySelector('.confirm-detail')?.textContent).toContain('2 位成員');
    expect(dialog?.querySelector('.confirm-detail')?.textContent).toContain('3 串對話');
    expect(dialog?.querySelector('[data-purge-issues-note]')?.textContent).toContain('已轉給專人的問答會保留在處理事項中');

    const confirm = host.querySelector<HTMLButtonElement>('.confirm-purge');
    expect(confirm?.disabled).toBe(true);
    confirm?.click();
    expect(purge).not.toHaveBeenCalled();

    const box = host.querySelector<HTMLInputElement>('#purge-acknowledge');
    if (!box) throw new Error('missing checkbox');
    box.checked = true;
    box.dispatchEvent(new Event('change'));
    await settle(fixture);
    host.querySelector<HTMLButtonElement>('.confirm-purge')?.click();
    await settle(fixture);

    expect(purge).toHaveBeenCalledWith('assistant-customer-service');
    expect(host.querySelector('[role="dialog"]')).toBeNull();
    expect(host.querySelector('[data-purge-status]')?.textContent).toBe('已刪除「客服助理」的 3 串對話。');
    expect(row(host, 'assistant-customer-service').querySelector('[data-thread-count]')?.textContent).toContain('已保存 0 串對話');
    expect(document.activeElement?.id).toBe('saved-conversations-title');
  });

  it('cancel deletes nothing and returns focus to the row\'s button', async () => {
    const { fixture, host, repository } = await render();
    const purge = vi.spyOn(repository, 'purgeAssistantConversations');
    const button = row(host, 'assistant-customer-service').querySelector<HTMLButtonElement>('button');

    button?.click();
    await settle(fixture);
    host.querySelector<HTMLButtonElement>('.confirm-cancel')?.click();
    await settle(fixture);

    expect(host.querySelector('[role="dialog"]')).toBeNull();
    expect(purge).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(button);
    expect(row(host, 'assistant-customer-service').querySelector('[data-thread-count]')?.textContent).toContain('已保存 3 串對話');
  });

  it('says nothing was deleted when the request fails', async () => {
    const { fixture, host, repository } = await render();
    vi.spyOn(repository, 'purgeAssistantConversations').mockReturnValue(throwError(() => new Error('offline')));

    row(host, 'assistant-customer-service').querySelector<HTMLButtonElement>('button')?.click();
    await settle(fixture);
    const box = host.querySelector<HTMLInputElement>('#purge-acknowledge');
    if (!box) throw new Error('missing checkbox');
    box.checked = true;
    box.dispatchEvent(new Event('change'));
    await settle(fixture);
    host.querySelector<HTMLButtonElement>('.confirm-purge')?.click();
    await settle(fixture);

    expect(host.querySelector('[role="alert"]')?.textContent).toContain('這次沒有刪除任何對話');
    expect(row(host, 'assistant-customer-service').querySelector('[data-thread-count]')?.textContent).toContain('已保存 3 串對話');
  });
});
