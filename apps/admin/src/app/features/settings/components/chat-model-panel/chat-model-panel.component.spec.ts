import { signal, type Provider } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { AccountId } from '../../../../core/domain/account.model';
import type { ChatModelOptionView } from '../../../../core/domain/organization-settings.model';
import { DEMO_SEED } from '../../../../core/repositories/demo-seed';
import { createMemoryStorage } from '../../../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../../../core/repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../../../core/repositories/tokens';
import { DemoSessionService } from '../../../../core/session/demo-session.service';
import { ChatModelPanelComponent } from './chat-model-panel.component';

const TWO_MODELS: readonly ChatModelOptionView[] = [
  { id: 'fake-chat-dev', displayName: '標準模型', model: 'fake-chat-dev' },
  { id: 'second', displayName: '進階模型', model: 'fake-chat-second' },
];

async function render(options: {
  accountId?: AccountId;
  chatModels?: readonly ChatModelOptionView[];
  storage?: ReturnType<typeof createMemoryStorage>;
} = {}) {
  const activeAccountId = signal<AccountId | null>(options.accountId ?? 'account-smb-admin');
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage: options.storage ?? createMemoryStorage(),
    now: () => new Date('2026-10-06T03:04:00.000Z'),
    viewer: () => activeAccountId(),
    ...(options.chatModels ? { chatModels: options.chatModels } : {}),
  });
  const providers: Provider[] = [
    { provide: DEMO_REPOSITORY, useValue: repository },
    { provide: DemoSessionService, useValue: { activeAccountId } },
  ];
  TestBed.configureTestingModule({ imports: [ChatModelPanelComponent], providers });
  const fixture = TestBed.createComponent(ChatModelPanelComponent);
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, repository };
}

function select(host: HTMLElement): HTMLSelectElement | null {
  return host.querySelector<HTMLSelectElement>('#chat-model-select');
}

function requiredSelect(host: HTMLElement): HTMLSelectElement {
  const field = select(host);
  if (!field) throw new Error('missing select');
  return field;
}

async function choose(
  fixture: { detectChanges(): void; whenStable(): Promise<unknown> },
  host: HTMLElement,
  value: string,
): Promise<void> {
  const field = select(host);
  if (!field) throw new Error('missing select');
  field.value = value;
  field.dispatchEvent(new Event('change'));
  await fixture.whenStable();
  fixture.detectChanges();
}

function status(host: HTMLElement): HTMLElement {
  const region = host.querySelector<HTMLElement>('#chat-model-status');
  if (!region) throw new Error('missing status region');
  return region;
}

describe('ChatModelPanelComponent (issue #240)', () => {
  it('shows only "目前使用" when the deployment offers one model', async () => {
    const { host } = await render();

    expect(select(host)).toBeNull();
    expect(host.textContent).toContain('目前使用：fake-chat-dev');
    expect(host.textContent).toContain('這個部署只提供一個對話模型');
    expect(host.textContent).toContain('上次變更：尚未變更過');
  });

  it('gives the manager a labelled menu with the deployment default first', async () => {
    const { host } = await render({ chatModels: TWO_MODELS });
    const field = requiredSelect(host);

    expect(host.querySelector('label[for="chat-model-select"]')?.textContent).toContain('使用的模型');
    expect(Array.from(field.options).map((option) => option.textContent?.trim())).toEqual([
      '標準模型（部署預設）',
      '進階模型',
    ]);
    expect(field.value).toBe('fake-chat-dev');
    expect(field.getAttribute('aria-describedby')).toBe('chat-model-status');
  });

  it('saves a change at once and announces it in the live status region', async () => {
    const { fixture, host, repository } = await render({ chatModels: TWO_MODELS });
    const update = vi.spyOn(repository, 'updateOrganizationChatModel');

    await choose(fixture, host, 'second');

    expect(update).toHaveBeenCalledWith('second', 0);
    expect(status(host).getAttribute('aria-live')).toBe('polite');
    expect(status(host).textContent).toContain('已改用「進階模型」');
    expect(requiredSelect(host).value).toBe('second');
    expect(host.querySelector('[data-chat-model-last-change]')?.textContent).toContain(
      '上次變更：安心商行管理者，',
    );

    // 改回第一項是「跟著部署預設」：送出 null。
    await choose(fixture, host, 'fake-chat-dev');
    expect(update).toHaveBeenLastCalledWith(null, 1);
  });

  it('is read-only for anyone but the manager', async () => {
    const { host } = await render({ accountId: 'account-internal-employee', chatModels: TWO_MODELS });

    expect(select(host)).toBeNull();
    expect(host.textContent).toContain('目前使用：標準模型');
    expect(host.textContent).toContain('只有管理者可以變更對話模型');
  });

  it('explains when the chosen model is no longer offered', async () => {
    const storage = createMemoryStorage();
    const first = await render({ chatModels: TWO_MODELS, storage });
    await choose(first.fixture, first.host, 'second');
    TestBed.resetTestingModule();

    const { host } = await render({
      chatModels: [TWO_MODELS[0], { id: 'third', displayName: '第三個模型', model: 'third' }],
      storage,
    });

    expect(host.querySelector('[data-chat-model-removed]')?.textContent).toContain(
      '原本選的模型已不再提供，目前使用部署預設 標準模型',
    );
    const field = requiredSelect(host);
    expect(field.value).toBe('__removed__');
    expect(field.options[0].disabled).toBe(true);
  });

  it('shows the removed notice to a read-only viewer as well', async () => {
    const storage = createMemoryStorage();
    const first = await render({ chatModels: TWO_MODELS, storage });
    await choose(first.fixture, first.host, 'second');
    TestBed.resetTestingModule();

    const { host } = await render({ accountId: 'account-internal-employee', storage });

    expect(host.textContent).toContain('目前使用：fake-chat-dev');
    expect(host.textContent).toContain('原本選的模型已不再提供，目前使用部署預設 fake-chat-dev');
  });

  it('explains a conflict, reloads and puts the menu back', async () => {
    const { fixture, host, repository } = await render({ chatModels: TWO_MODELS });
    vi.spyOn(repository, 'updateOrganizationChatModel').mockReturnValue(
      of({ status: 'conflict', message: '組織設定已被其他人更新過，請重新載入後再修改。' }),
    );
    const reads = vi.spyOn(repository, 'getOrganizationChatModel');

    await choose(fixture, host, 'second');

    expect(host.querySelector('[role="alert"]')?.textContent).toContain('組織設定已被其他人更新過');
    expect(host.querySelector('[role="alert"]')?.textContent).toContain('這次沒有變更');
    expect(reads).toHaveBeenCalled();
    expect(requiredSelect(host).value).toBe('fake-chat-dev');
    expect(status(host).textContent?.trim()).toBe('');
  });

  it('explains a model that is not offered any more (422)', async () => {
    const { fixture, host, repository } = await render({ chatModels: TWO_MODELS });
    vi.spyOn(repository, 'updateOrganizationChatModel').mockReturnValue(
      of({ status: 'validation-failed', message: '這個模型不在部署提供的清單中，請重新載入後再選擇。' }),
    );

    await choose(fixture, host, 'second');

    expect(host.querySelector('[role="alert"]')?.textContent).toContain('這個模型不在部署提供的清單中');
    expect(requiredSelect(host).value).toBe('fake-chat-dev');
  });

  it('puts the menu back when saving fails', async () => {
    const { fixture, host, repository } = await render({ chatModels: TWO_MODELS });
    vi.spyOn(repository, 'updateOrganizationChatModel').mockReturnValue(
      throwError(() => new Error('offline')),
    );

    await choose(fixture, host, 'second');

    expect(host.querySelector('[role="alert"]')?.textContent).toContain('目前無法儲存對話模型');
    expect(requiredSelect(host).value).toBe('fake-chat-dev');
  });

  it('shows an error panel when the setting cannot be read', async () => {
    const repository = new MockDemoRepository(DEMO_SEED, { storage: createMemoryStorage() });
    vi.spyOn(repository, 'getOrganizationChatModel').mockReturnValue(throwError(() => new Error('500')));
    TestBed.configureTestingModule({
      imports: [ChatModelPanelComponent],
      providers: [
        { provide: DEMO_REPOSITORY, useValue: repository },
        { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>('account-smb-admin') } },
      ],
    });
    const fixture = TestBed.createComponent(ChatModelPanelComponent);
    await fixture.whenStable();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('目前無法載入對話模型');
  });
});
