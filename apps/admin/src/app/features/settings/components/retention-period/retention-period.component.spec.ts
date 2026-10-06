import { signal, type Provider } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { AccountId } from '../../../../core/domain/account.model';
import { DEMO_SEED } from '../../../../core/repositories/demo-seed';
import { createMemoryStorage } from '../../../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../../../core/repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../../../core/repositories/tokens';
import { DemoSessionService } from '../../../../core/session/demo-session.service';
import { RetentionPeriodComponent } from './retention-period.component';

type Storage = ReturnType<typeof createMemoryStorage>;

/** 直接寫進 mock 的保存期限紀錄（扮演先前已存好的設定）。 */
function storeRetention(storage: Storage, record: { days: number | null; pending: { days: number; effectiveAt: string } | null }) {
  storage.setItem('sme-demo:organization-retention', JSON.stringify({ ...record, lastChange: null }));
}

/** 兩串很久以前的對話與一串最近的對話：30 天的預覽是 2 串。 */
function storeOldThreads(storage: Storage): void {
  const thread = (id: number, updatedAt: string) => ({
    id: `chat-thread-${id}`,
    title: `對話 ${id}`,
    titleSource: 'derived',
    createdAt: updatedAt,
    updatedAt,
    messages: [],
  });
  storage.setItem(
    'sme-demo:chat:account-internal-employee:assistant-internal-onboarding',
    JSON.stringify({
      version: 2,
      threads: [thread(1, '2026-06-01T00:00:00.000Z'), thread(2, '2026-07-01T00:00:00.000Z'), thread(3, '2026-10-01T00:00:00.000Z')],
    }),
  );
}

async function render(options: { accountId?: AccountId; storage?: Storage } = {}) {
  const activeAccountId = signal<AccountId | null>(options.accountId ?? 'account-smb-admin');
  const storage = options.storage ?? createMemoryStorage();
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage,
    now: () => new Date('2026-10-06T13:00:00.000Z'),
    viewer: () => activeAccountId(),
  });
  const providers: Provider[] = [
    { provide: DEMO_REPOSITORY, useValue: repository },
    { provide: DemoSessionService, useValue: { activeAccountId } },
  ];
  TestBed.configureTestingModule({ imports: [RetentionPeriodComponent], providers });
  const fixture = TestBed.createComponent(RetentionPeriodComponent);
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, repository, storage };
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

function select(host: HTMLElement): HTMLSelectElement | null {
  return host.querySelector<HTMLSelectElement>('#retention-days-select');
}

function requiredSelect(host: HTMLElement): HTMLSelectElement {
  const field = select(host);
  if (!field) throw new Error('missing select');
  return field;
}

async function choose(fixture: ComponentFixture<unknown>, host: HTMLElement, value: string): Promise<void> {
  const field = requiredSelect(host);
  field.value = value;
  field.dispatchEvent(new Event('change'));
  await settle(fixture);
}

function dialog(host: HTMLElement): HTMLElement | null {
  return host.querySelector<HTMLElement>('[role="dialog"]');
}

function status(host: HTMLElement): HTMLElement {
  const region = host.querySelector<HTMLElement>('#retention-status');
  if (!region) throw new Error('missing status region');
  return region;
}

function button(root: HTMLElement, text: string): HTMLButtonElement {
  const found = Array.from(root.querySelectorAll<HTMLButtonElement>('button')).find(
    (candidate) => candidate.textContent?.trim() === text,
  );
  if (!found) throw new Error(`missing button ${text}`);
  return found;
}

describe('RetentionPeriodComponent (issue #243)', () => {
  it('gives the manager a labelled menu of the four periods and forever, with the fixed notes', async () => {
    const { host } = await render();
    const field = requiredSelect(host);

    expect(host.querySelector('label[for="retention-days-select"]')?.textContent).toContain('保存期限');
    expect(Array.from(field.options).map((option) => option.textContent?.trim())).toEqual([
      '30 天',
      '90 天',
      '180 天',
      '365 天',
      '永久',
    ]);
    expect(field.value).toBe('forever');
    expect(field.getAttribute('aria-describedby')).toBe('retention-status');
    expect(host.querySelector('[data-retention-issues-note]')?.textContent).toContain(
      '已轉給專人的問答會保留在處理事項中',
    );
    expect(host.querySelector('[data-retention-last-change]')?.textContent).toContain('上次變更：尚未變更過');
    expect(host.querySelector('[data-retention-pending]')).toBeNull();
  });

  it('shortens through a confirmation with the previewed count, then shows the buffer notice until it is reverted', async () => {
    const storage = createMemoryStorage();
    storeOldThreads(storage);
    const { fixture, host, repository } = await render({ storage });
    const preview = vi.spyOn(repository, 'previewOrganizationRetention');
    const update = vi.spyOn(repository, 'updateOrganizationRetention');

    await choose(fixture, host, '30');

    expect(preview).toHaveBeenCalledWith(30);
    expect(update).not.toHaveBeenCalled();
    const confirm = dialog(host);
    if (!confirm) throw new Error('missing dialog');
    expect(confirm.textContent).toContain('把保存期限縮短為 30 天？');
    expect(confirm.querySelector('[data-retention-preview-count]')?.textContent).toContain('大約會刪除 2 串對話');
    expect(confirm.textContent).toContain('7 天緩衝期');
    expect(confirm.textContent).toContain('已轉給專人的問答會保留在處理事項中');

    button(confirm, '縮短為 30 天').click();
    await settle(fixture);

    expect(update).toHaveBeenCalledWith(30, 0);
    expect(dialog(host)).toBeNull();
    expect(host.querySelector('[data-retention-pending]')?.textContent).toContain('將於 10/13 起改為 30 天');
    expect(status(host).getAttribute('aria-live')).toBe('polite');
    expect(status(host).textContent).toContain('已改為 30 天，將於 10/13 起生效');
    expect(requiredSelect(host).value).toBe('30');
    expect(document.activeElement).toBe(requiredSelect(host));
    expect(host.querySelector('[data-retention-last-change]')?.textContent).toContain('上次變更：安心商行管理者，');

    button(host, '改回').click();
    await settle(fixture);

    expect(update).toHaveBeenLastCalledWith(null, 1);
    expect(host.querySelector('[data-retention-pending]')).toBeNull();
    expect(status(host).textContent).toContain('已改回 永久');
    expect(requiredSelect(host).value).toBe('forever');
  });

  it('cancelling the confirmation changes nothing and puts the menu back', async () => {
    const { fixture, host, repository } = await render();
    const update = vi.spyOn(repository, 'updateOrganizationRetention');

    await choose(fixture, host, '90');
    const confirm = dialog(host);
    if (!confirm) throw new Error('missing dialog');
    expect(confirm.querySelector('[data-retention-preview-count]')?.textContent).toContain('大約會刪除 0 串對話');
    button(confirm, '取消').click();
    await settle(fixture);

    expect(update).not.toHaveBeenCalled();
    expect(dialog(host)).toBeNull();
    expect(requiredSelect(host).value).toBe('forever');
    expect(document.activeElement).toBe(requiredSelect(host));
  });

  it('extends at once, without a confirmation or a buffer notice', async () => {
    const storage = createMemoryStorage();
    storeRetention(storage, { days: 90, pending: null });
    const { fixture, host, repository } = await render({ storage });
    const preview = vi.spyOn(repository, 'previewOrganizationRetention');

    await choose(fixture, host, '180');

    expect(preview).not.toHaveBeenCalled();
    expect(dialog(host)).toBeNull();
    expect(host.querySelector('[data-retention-pending]')).toBeNull();
    expect(status(host).textContent).toContain('已改為 180 天，立即生效');
    expect(requiredSelect(host).value).toBe('180');
  });

  it('treats choosing the current period again while one is pending as going back', async () => {
    const storage = createMemoryStorage();
    storeRetention(storage, { days: 90, pending: { days: 30, effectiveAt: '2026-10-13T13:00:00.000Z' } });
    const { fixture, host, repository } = await render({ storage });
    const update = vi.spyOn(repository, 'updateOrganizationRetention');

    expect(requiredSelect(host).value).toBe('30');
    expect(host.querySelector('[data-retention-pending]')?.textContent).toContain('目前是 90 天，將於 10/13 起改為 30 天');

    await choose(fixture, host, '90');

    expect(update).toHaveBeenCalledWith(90, 0);
    expect(dialog(host)).toBeNull();
    expect(host.querySelector('[data-retention-pending]')).toBeNull();
  });

  it('is read-only for anyone but the manager and never asks for a preview', async () => {
    const storage = createMemoryStorage();
    storeRetention(storage, { days: 90, pending: { days: 30, effectiveAt: '2026-10-13T13:00:00.000Z' } });
    const preview = vi.spyOn(MockDemoRepository.prototype, 'previewOrganizationRetention');
    const { host } = await render({ accountId: 'account-internal-employee', storage });

    expect(select(host)).toBeNull();
    expect(host.querySelector('h3')?.textContent).toContain('保存期限：90 天');
    expect(host.textContent).toContain('只有管理者可以變更保存期限');
    expect(host.querySelector('[data-retention-pending]')?.textContent).toContain('將於 10/13 起改為 30 天');
    expect(host.querySelectorAll('button')).toHaveLength(0);
    expect(host.querySelector('[data-retention-issues-note]')?.textContent).toContain(
      '已轉給專人的問答會保留在處理事項中',
    );
    expect(preview).not.toHaveBeenCalled();
    preview.mockRestore();
  });

  it('explains a conflict, reloads and puts the menu back', async () => {
    const { fixture, host, repository } = await render();
    vi.spyOn(repository, 'updateOrganizationRetention').mockReturnValue(
      of({ status: 'conflict', message: '組織設定已在其他分頁或由其他管理者更新過，請重新載入後再修改。' }),
    );
    const reads = vi.spyOn(repository, 'getOrganizationRetention');

    await choose(fixture, host, '30');
    button(dialog(host) as HTMLElement, '縮短為 30 天').click();
    await settle(fixture);

    expect(host.querySelector('[role="alert"]')?.textContent).toContain('組織設定已在其他分頁或由其他管理者更新過');
    expect(host.querySelector('[role="alert"]')?.textContent).toContain('這次沒有變更');
    expect(reads).toHaveBeenCalled();
    expect(dialog(host)).toBeNull();
    expect(requiredSelect(host).value).toBe('forever');
    expect(status(host).textContent?.trim()).toBe('');
  });

  it('explains a period that is not offered (422) and reloads', async () => {
    const storage = createMemoryStorage();
    storeRetention(storage, { days: 30, pending: null });
    const { fixture, host, repository } = await render({ storage });
    vi.spyOn(repository, 'updateOrganizationRetention').mockReturnValue(
      of({ status: 'validation-failed', message: '保存期限只能是 30、90、180、365 天或永久。' }),
    );
    const reads = vi.spyOn(repository, 'getOrganizationRetention');

    await choose(fixture, host, '365');

    expect(host.querySelector('[role="alert"]')?.textContent).toContain('保存期限只能是 30、90、180、365 天或永久');
    expect(reads).toHaveBeenCalled();
    expect(requiredSelect(host).value).toBe('30');
  });

  it('still lets the manager decide when the preview cannot be read', async () => {
    const { fixture, host, repository } = await render();
    vi.spyOn(repository, 'previewOrganizationRetention').mockReturnValue(throwError(() => new Error('offline')));

    await choose(fixture, host, '30');

    expect(dialog(host)?.querySelector('[data-retention-preview-count]')?.textContent).toContain(
      '目前無法計算大約會刪除的對話數',
    );
  });

  it('puts the menu back when saving fails', async () => {
    const storage = createMemoryStorage();
    storeRetention(storage, { days: 90, pending: null });
    const { fixture, host, repository } = await render({ storage });
    vi.spyOn(repository, 'updateOrganizationRetention').mockReturnValue(throwError(() => new Error('offline')));

    await choose(fixture, host, '365');

    expect(host.querySelector('[role="alert"]')?.textContent).toContain('目前無法儲存保存期限');
    expect(requiredSelect(host).value).toBe('90');
  });

  it('shows an error panel when the setting cannot be read', async () => {
    const repository = new MockDemoRepository(DEMO_SEED, { storage: createMemoryStorage() });
    vi.spyOn(repository, 'getOrganizationRetention').mockReturnValue(throwError(() => new Error('500')));
    TestBed.configureTestingModule({
      imports: [RetentionPeriodComponent],
      providers: [
        { provide: DEMO_REPOSITORY, useValue: repository },
        { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>('account-smb-admin') } },
      ],
    });
    const fixture = TestBed.createComponent(RetentionPeriodComponent);
    await fixture.whenStable();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('目前無法載入保存期限');
  });
});
