import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { BehaviorSubject, concatMap, firstValueFrom, map, Subject, take, throwError } from 'rxjs';
import type { AccountId } from '../../../core/domain/account.model';
import { DEMO_SEED } from '../../../core/repositories/demo-seed';
import type { DemoKeyValueStorage } from '../../../core/repositories/demo-repository';
import { createMemoryStorage } from '../../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../../core/repositories/mock-demo-repository';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { AssistantSettingsStore } from './assistant-settings.store';

function setup(
  options: { readonly accountId?: AccountId; readonly storage?: DemoKeyValueStorage } = {},
) {
  const storage = options.storage ?? createMemoryStorage();
  const accountId = options.accountId ?? 'account-smb-admin';
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage,
    now: () => new Date('2026-09-23T02:00:00.000Z'),
    // `listConnectableSources()` 是非同步契約，viewer 由 repository 的 `viewer` 選項推導
    // （不再接收呼叫端傳入的帳號），要與下面 `DemoSessionService` 的假身分一致。
    viewer: () => accountId,
  });
  const params = new BehaviorSubject(new Map([['id', 'assistant-customer-service']]));

  TestBed.configureTestingModule({
    providers: [
      AssistantSettingsStore,
      { provide: DEMO_REPOSITORY, useValue: repository },
      {
        provide: DemoSessionService,
        useValue: {
          activeAccountId: signal<AccountId | null>(accountId),
        },
      },
      {
        provide: ActivatedRoute,
        useValue: { snapshot: { paramMap: params.value }, paramMap: params.asObservable() },
      },
    ],
  });

  return { store: TestBed.inject(AssistantSettingsStore), repository, storage };
}

/**
 * `connectableSources` 現在由 `repositoryResource`（`rxResource`）驅動：即使底層是
 * `defer(() => of(...))` 同步發出，`resource()` 內部仍透過 Promise 回報結果，沒有
 * component fixture 可用的 `whenStable()` 時，用 `TestBed.tick()` 搭配一次微任務等待。
 */
async function settleResource(): Promise<void> {
  await Promise.resolve();
  TestBed.tick();
  await Promise.resolve();
}

describe('AssistantSettingsStore', () => {
  it('loads the assistant named in the route for its owner', async () => {
    const { store } = setup();
    await settleResource();

    expect(store.settings()?.configuration.name).toBe('客服助理');
    expect(store.canEdit()).toBe(true);
    expect(store.saveStatusLabel()).toContain('尚未編輯');
  });

  it('refuses an account that does not own the assistant, without naming it', async () => {
    const { store } = setup({ accountId: 'account-internal-employee' });
    await settleResource();

    expect(store.canEdit()).toBe(false);
    expect(store.settings()).toBeNull();
    expect(store.deniedMessage()).not.toContain('客服助理');
  });

  it('autosaves the audience and reports when it was saved', async () => {
    const { store, storage } = setup();
    await settleResource();

    store.updateProfile({ audience: 'members-and-external-customers' });

    expect(store.settings()?.configuration.audience).toBe('members-and-external-customers');
    expect(store.saveStatusLabel()).toContain('已自動儲存');
    expect(storage.getItem('sme-demo:assistant-settings:assistant-customer-service')).toContain(
      'members-and-external-customers',
    );
  });

  it('keeps the last valid value and shows a field error when a required field is emptied', async () => {
    const { store } = setup();
    await settleResource();

    store.updateProfile({ name: '   ' });

    expect(store.fieldError('name')).toBe('請輸入助理名稱。');
    // 全有或全無：伺服器沒有寫入，頁面標題仍是上一個有效的值；輸入框保留使用者剛輸入的內容。
    expect(store.savedSettings()?.configuration.name).toBe('客服助理');
    expect(store.settings()?.configuration.name).toBe('   ');
  });

  it('clears an earlier field error once the value is valid again', async () => {
    const { store } = setup();
    await settleResource();
    store.updateProfile({ name: '' });
    expect(store.fieldError('name')).not.toBeNull();

    store.updateProfile({ name: '售後服務助理' });

    expect(store.fieldError('name')).toBeNull();
    expect(store.settings()?.configuration.name).toBe('售後服務助理');
  });

  describe('auto-disabled periodic report (issue #179)', () => {
    const KEY = 'sme-demo:assistant-settings:assistant-customer-service';

    /** 先存一次設定，再把「已自動停用」寫進去（mock 沒有排程工作）。 */
    async function disabledStorage(): Promise<DemoKeyValueStorage> {
      const storage = createMemoryStorage();
      const seeding = new MockDemoRepository(DEMO_SEED, {
        storage,
        now: () => new Date('2026-09-23T02:00:00.000Z'),
        viewer: () => 'account-smb-admin',
      });
      await firstValueFrom(seeding.updateAssistantSettings('assistant-customer-service', { rules: { showCitations: true } }));
      const stored = JSON.parse(storage.getItem(KEY) ?? '{}');
      storage.setItem(KEY, JSON.stringify({
        ...stored,
        periodicReportAutoDisabled: {
          disabledAt: '2026-09-15T00:00:05.000Z',
          reason: 'owner-cannot-read',
          skippedPeriods: 3,
          message: '已自動停用：連續 3 期沒有產生報表，最近一期是因為助理擁有者無法讀取這個數據庫的紀錄。恢復權限後可以重新啟用。',
        },
      }));
      return storage;
    }

    it('re-enables by sending the current frequency and shows the server’s cleared state', async () => {
      const { store, repository } = setup({ storage: await disabledStorage() });
      await settleResource();
      expect(store.settings()?.periodicReportAutoDisabled?.reason).toBe('owner-cannot-read');
      const update = vi.spyOn(repository, 'updateAssistantSettings');

      store.resumePeriodicReport();

      expect(update).toHaveBeenCalledWith('assistant-customer-service', { rules: { periodicReport: 'monthly' } });
      expect(store.settings()?.periodicReportAutoDisabled).toBeNull();
      expect(store.settings()?.rules.periodicReport).toBe('monthly');
    });

    it('does nothing when the schedule is not disabled', async () => {
      const { store, repository } = setup();
      await settleResource();
      const update = vi.spyOn(repository, 'updateAssistantSettings');

      store.resumePeriodicReport();

      expect(update).not.toHaveBeenCalled();
    });
  });

  it('connects and disconnects a data source through the repository', async () => {
    const { store } = setup();
    await settleResource();
    const guide = store
      .connectableSources()
      .find((source) => source.id === 'knowledge-product-guide');
    if (guide === undefined) throw new Error('expected the product guide to be connectable');

    store.toggleSource(guide);
    expect(store.settings()?.configuration.knowledgeBaseIds).not.toContain('knowledge-product-guide');

    store.toggleSource(guide);
    expect(store.settings()?.configuration.knowledgeBaseIds).toContain('knowledge-product-guide');
  });

  it('only offers sources this account can see', async () => {
    const { store } = setup();
    await settleResource();

    expect(store.connectableSources().length).toBeGreaterThan(0);
    for (const source of store.connectableSources()) {
      expect(source.id.startsWith('knowledge-') || source.id.startsWith('database-')).toBe(true);
    }
  });

  it('turns 保存自己的對話 off and on again through the repository', async () => {
    const { store, repository } = setup();
    await settleResource();

    store.updateRules({ keepOwnConversations: false });
    const off = await firstValueFrom(repository.listChatThreads('assistant-customer-service'));
    expect(off.status === 'ready' && off.data.historyMode).toBe('not-saved');

    store.updateRules({ keepOwnConversations: true });
    const on = await firstValueFrom(repository.listChatThreads('assistant-customer-service'));
    expect(on.status === 'ready' && on.data.historyMode).toBe('saved');
  });

  it('shows a note when a change is refused rather than applying it silently', async () => {
    const { store } = setup();
    await settleResource();

    store.note('助理至少要保留一種使用對象，否則沒有人能開啟它。');

    expect(store.noticeMessage()).toContain('至少要保留一種使用對象');
  });

  it('sends one write at a time and never lets an earlier response overwrite what was typed since', async () => {
    const { store, repository } = setup();
    await settleResource();
    const sent: string[] = [];
    const release = new Subject<void>();
    const update = repository.updateAssistantSettings.bind(repository);
    repository.updateAssistantSettings = (assistantId, patch) => {
      sent.push(patch.name ?? '');
      // 第一次 PATCH 停在「送出中」，直到測試放行。
      return sent.length === 1
        ? update(assistantId, patch).pipe(concatMap((result) => release.pipe(take(1), map(() => result))))
        : update(assistantId, patch);
    };

    store.updateProfile({ name: '客' });
    store.updateProfile({ name: '客服' });
    store.updateProfile({ name: '客服小幫手' });

    // 第一次還在送出：後面兩次合併成下一次，畫面先顯示最新輸入的值。
    expect(sent).toEqual(['客']);
    expect(store.busy()).toBe(true);
    expect(store.settings()?.configuration.name).toBe('客服小幫手');

    release.next();

    expect(sent).toEqual(['客', '客服小幫手']);
    expect(store.busy()).toBe(false);
    expect(store.settings()?.configuration.name).toBe('客服小幫手');
    expect(store.savedSettings()?.configuration.name).toBe('客服小幫手');
  });

  it('keeps the typed value and reports a failure when a write fails outright', async () => {
    const { store, repository } = setup();
    await settleResource();
    repository.updateAssistantSettings = () => throwError(() => new Error('offline'));

    store.updateProfile({ name: '售後服務助理' });

    expect(store.saveStatusLabel()).toBe('目前無法儲存這項變更，請稍後再試。');
    expect(store.settings()?.configuration.name).toBe('售後服務助理');
    expect(store.savedSettings()?.configuration.name).toBe('客服助理');
    expect(store.busy()).toBe(false);
  });
});
