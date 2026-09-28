import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { inject, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { concatMap, firstValueFrom, map, of, Subject, type Observable } from 'rxjs';
import type { AccountId } from '../../../core/domain/account.model';
import { createEmptyAssistantDraft } from '../../../core/domain/assistant-draft.model';
import type { SaveAssistantDraftResult } from '../../../core/repositories/demo-repository';
import {
  API_ASSISTANTS_PATH,
  API_CONNECTABLE_SOURCES_PATH,
  apiAssistantDraftPath,
  HybridDemoRepository,
} from '../../../core/repositories/hybrid-demo-repository';
import { createMemoryStorage } from '../../../core/repositories/memory-storage';
import { MockDemoRepository } from '../../../core/repositories/mock-demo-repository';
import { DEMO_SEED } from '../../../core/repositories/demo-seed';
import { DEMO_REPOSITORY } from '../../../core/repositories/tokens';
import { DemoSessionService } from '../../../core/session/demo-session.service';
import { AssistantDraftStore } from './assistant-draft.store';

/** `repositoryResource` 經由 Promise 回報結果：沒有 component fixture 時用 tick + 微任務等它落地。 */
async function settle(): Promise<void> {
  for (let round = 0; round < 3; round++) {
    await Promise.resolve();
    TestBed.tick();
  }
  await Promise.resolve();
}

async function setup(
  options: { readonly storage?: ReturnType<typeof createMemoryStorage>; readonly draftId?: string } = {},
) {
  const storage = options.storage ?? createMemoryStorage();
  const activeAccountId = signal<AccountId | null>('account-smb-admin');
  const repository = new MockDemoRepository(DEMO_SEED, {
    storage,
    now: () => new Date('2026-09-22T02:00:00.000Z'),
    viewer: () => activeAccountId(),
  });
  let draftId = options.draftId;
  if (draftId === undefined) {
    const created = await firstValueFrom(repository.createNamedAssistantDraft());
    if (created.status !== 'ready') throw new Error('expected a new draft');
    draftId = created.data.id;
  }

  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      AssistantDraftStore,
      { provide: DEMO_REPOSITORY, useValue: repository },
      { provide: DemoSessionService, useValue: { activeAccountId } },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ draftId }) } } },
    ],
  });

  const store = TestBed.inject(AssistantDraftStore);
  await settle();
  return { store, activeAccountId, storage, repository, draftId };
}

describe('AssistantDraftStore', () => {
  it('prefills the draft from the customer-question template and autosaves it', async () => {
    const { store } = await setup();

    expect(store.loadStatus()).toBe('ready');
    expect(store.saveStatusLabel()).toContain('已載入先前的草稿');

    store.applyTemplate('answer-customer-questions');

    expect(store.draft()).toMatchObject({
      templateId: 'answer-customer-questions',
      name: '客戶問答助理',
      rules: { knowledgeScope: 'company-data-only' },
    });
    expect(store.draft().purpose).not.toBe('');
    await settle();
    expect(store.saveStatusLabel()).toContain('已自動儲存');
  });

  it('validates only the required fields of the step being left', async () => {
    const { store } = await setup();

    expect(store.errorsFor('purpose')).toEqual([]);
    expect(store.tryAdvance('purpose')).toBe(false);
    expect(store.errorsFor('purpose').map((error) => error.field)).toEqual([
      'name',
      'purpose',
      'audience',
    ]);
    expect(store.errorsFor('sources')).toEqual([]);

    store.applyTemplate('answer-customer-questions');
    store.setAudience({ internal: true, external: true });

    expect(store.draft().audience).toBe('members-and-external-customers');
    expect(store.errorsFor('purpose')).toEqual([]);
    expect(store.tryAdvance('purpose')).toBe(true);
    expect(store.draft().currentStep).toBe('sources');
  });

  it('connects and disconnects mixed sources by connection id only', async () => {
    const { store } = await setup();

    store.toggleSource({ id: 'knowledge-product-guide', type: 'knowledge-base' });
    store.toggleSource({ id: 'knowledge-refund-policy', type: 'knowledge-base' });
    store.toggleSource({ id: 'database-orders', type: 'database' });
    store.toggleSource({ id: 'database-customer-records', type: 'database' });
    store.toggleSource({ id: 'knowledge-product-guide', type: 'knowledge-base' });

    expect(store.draft().sources).toEqual([
      { id: 'knowledge-refund-policy', type: 'knowledge-base' },
      { id: 'database-orders', type: 'database' },
      { id: 'database-customer-records', type: 'database' },
    ]);
  });

  it('clears the data-write target when that database is disconnected', async () => {
    const { store } = await setup();

    store.toggleSource({ id: 'database-orders', type: 'database' });
    store.updateRules({ dataWriteDatabaseId: 'database-orders', dataWritePurpose: '記錄詢問' });
    store.toggleSource({ id: 'database-orders', type: 'database' });

    expect(store.draft().rules.dataWriteDatabaseId).toBeNull();
  });

  it('resumes the saved draft after a reload and does not open it for another account', async () => {
    const storage = createMemoryStorage();
    const first = await setup({ storage });
    first.store.applyTemplate('answer-customer-questions');
    first.store.setAudience({ internal: true, external: false });
    await settle();

    const second = await setup({ storage, draftId: first.draftId });

    expect(second.store.draft().name).toBe('客戶問答助理');
    expect(second.store.draft().audience).toBe('account-members');
    expect(second.store.saveStatusLabel()).toContain('已載入先前的草稿');

    second.activeAccountId.set('account-internal-employee');
    await settle();
    expect(second.store.draft().name).toBe('');
    expect(second.store.canManage()).toBe(false);
    expect(second.store.loadStatus()).toBe('permission-denied');
  });

  it('sends one save at a time, each with the revision the previous save returned, so its own saves never conflict', async () => {
    const { store, repository, draftId } = await setup();
    const sent: { name: string; revision: number }[] = [];
    const save = repository.saveNamedAssistantDraft.bind(repository);
    const firstResponse = new Subject<SaveAssistantDraftResult>();
    repository.saveNamedAssistantDraft = (id, draft, revision): Observable<SaveAssistantDraftResult> => {
      sent.push({ name: draft.name, revision });
      // 第一次保存停在「送出中」，直到測試放行；之後的保存照常。
      if (sent.length === 1) {
        return save(id, draft, revision).pipe(concatMap((result) => firstResponse.pipe(map(() => result))));
      }
      return save(id, draft, revision);
    };

    store.update({ name: '一' });
    await settle();
    expect(sent).toEqual([{ name: '一', revision: 1 }]);

    // 第一次還沒回來：這兩次變更不會各自送出，而是合併成下一次、讀取當下最新的草稿。
    store.update({ name: '一二' });
    store.update({ name: '一二三' });
    await settle();
    expect(sent).toHaveLength(1);

    firstResponse.next({ status: 'loading' });
    firstResponse.complete();
    await settle();

    expect(sent).toEqual([
      { name: '一', revision: 1 },
      { name: '一二三', revision: 2 },
    ]);
    expect(store.saveStatusLabel()).toContain('已自動儲存');
    const saved = await firstValueFrom(repository.getNamedAssistantDraft(draftId));
    expect(saved).toMatchObject({ status: 'ready', data: { draft: { name: '一二三' }, revision: 3 } });
  });

  it('stops autosaving and says so when another tab saved the draft first', async () => {
    const { store, repository, draftId } = await setup();

    // 另一個分頁先存過：這個分頁手上的 revision（1）已經過期。
    await firstValueFrom(
      repository.saveNamedAssistantDraft(draftId, { ...store.draft(), name: '另一個分頁' }, 1),
    );
    store.update({ name: '這個分頁' });
    await settle();

    expect(store.saveStatusLabel()).toBe('這份草稿已在其他分頁被更新過，請重新載入後再修改。');

    // 之後的變更也不再送出，不會蓋掉另一個分頁的內容。
    store.update({ name: '這個分頁又改了' });
    await settle();
    const saved = await firstValueFrom(repository.getNamedAssistantDraft(draftId));
    expect(saved).toMatchObject({ status: 'ready', data: { draft: { name: '另一個分頁' } } });
  });

  it('records trial answers and creates the assistant through the repository, removing the draft', async () => {
    const { store, repository, draftId } = await setup();
    store.applyTemplate('answer-customer-questions');
    store.setAudience({ internal: true, external: true });
    store.toggleSource({ id: 'knowledge-refund-policy', type: 'knowledge-base' });

    expect(await store.create()).toBeNull();

    const answer = store.runTrial('trial-refund-window');

    expect(answer?.kind).toBe('company-data');
    expect(store.draft().testedQuestionIds).toEqual(['trial-refund-window']);

    const assistantId = await store.create();

    expect(assistantId).toMatch(/^assistant-created-\d+$/);
    expect(await firstValueFrom(repository.getNamedAssistantDraft(draftId))).toMatchObject({
      status: 'permission-denied',
      reason: 'assistant-draft',
    });
  });

  it('shows the server’s per-field errors on the steps they belong to when creating is refused', async () => {
    const { store, repository } = await setup();
    store.applyTemplate('answer-customer-questions');
    store.setAudience({ internal: true, external: false });
    store.toggleSource({ id: 'knowledge-refund-policy', type: 'knowledge-base' });
    store.runTrial('trial-refund-window');
    repository.createAssistantFromDraft = () =>
      of({
        status: 'validation-failed',
        errors: [
          { field: 'sources', message: '草稿裡的知識庫無法連接，可能已不存在、屬於其他組織，或已被收回分享。' },
          { field: 'tone', message: '語氣設定不正確。' },
        ],
        message: '草稿裡的知識庫無法連接，可能已不存在、屬於其他組織，或已被收回分享。',
      });

    expect(await store.create()).toBeNull();

    expect(store.createError()).toBe('草稿裡的知識庫無法連接，可能已不存在、屬於其他組織，或已被收回分享。');
    expect(store.fieldError('sources', 'sources')).toBe('草稿裡的知識庫無法連接，可能已不存在、屬於其他組織，或已被收回分享。');
    expect(store.fieldError('purpose', 'tone')).toBe('語氣設定不正確。');

    // 任何編輯都清掉伺服器的錯誤，下一次建立會重新驗證。
    store.update({ name: '改過的名稱' });
    expect(store.fieldError('sources', 'sources')).toBeNull();
  });

  it('does not create when the final save fails', async () => {
    const { store, repository } = await setup();
    store.applyTemplate('answer-customer-questions');
    store.setAudience({ internal: true, external: false });
    store.toggleSource({ id: 'knowledge-refund-policy', type: 'knowledge-base' });
    store.runTrial('trial-refund-window');
    await settle();
    let created = false;
    repository.saveNamedAssistantDraft = () => of({ status: 'conflict', message: '這份草稿已在其他分頁被更新過，請重新載入後再修改。' });
    repository.createAssistantFromDraft = () => {
      created = true;
      return of({ status: 'loading' });
    };

    expect(await store.create()).toBeNull();

    expect(created).toBe(false);
    expect(store.createError()).toBe('這份草稿已在其他分頁被更新過，請重新載入後再修改。');
  });
});

/**
 * API 模式（`HybridDemoRepository` + HTTP）整條走一次精靈：PR #91 的教訓是單元測試與 Cypress
 * 都只跑 mock，真實 GUID 被默默濾掉也不會紅。這裡從 `/connectable-sources` 讀到的知識庫 GUID
 * 一路到「建立助理」送出的草稿內容都要保留同一個 GUID。
 */
describe('AssistantDraftStore in API mode', () => {
  const DRAFT_GUID = '0199c000-0000-7000-8000-0000000000f1';
  const KB_GUID = '0199b000-0000-7000-8000-0000000000b2';
  const ASSISTANT_GUID = '0199c000-0000-7000-8000-0000000000a1';

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('keeps a knowledge base GUID read from the API from the sources step to the created assistant', async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        AssistantDraftStore,
        { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>('account-smb-admin') } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ draftId: DRAFT_GUID }) } } },
        {
          provide: DEMO_REPOSITORY,
          useFactory: () =>
            new HybridDemoRepository(
              DEMO_SEED,
              { storage: createMemoryStorage(), viewer: () => 'account-smb-admin' },
              {
                http: inject(HttpClient),
                // 測試假身分要帶 organizationId，否則 scoped storage 不落地（見 `scoped-storage.ts`）。
                viewerPermissions: () => ({
                  accountId: '0199a000-0000-7000-8000-00000000000a',
                  demoAccountId: 'account-smb-admin',
                  permissions: ['manage-assistants', 'manage-data-sources', 'manage-publishing', 'use-shared-assistants'],
                  organizationId: '0199a000-0000-7000-8000-0000000000aa',
                }),
              },
            ),
        },
      ],
    });
    const controller = TestBed.inject(HttpTestingController);
    const store = TestBed.inject(AssistantDraftStore);
    const draftView = (payload: unknown, revision: number) => ({
      id: DRAFT_GUID,
      payload,
      schemaVersion: 1,
      revision,
      savedAt: '2026-09-28T02:00:00+00:00',
    });

    await settle();
    controller
      .expectOne({ method: 'GET', url: apiAssistantDraftPath(DRAFT_GUID) })
      .flush(draftView(createEmptyAssistantDraft(), 1));
    await settle();
    controller.expectOne({ method: 'GET', url: API_CONNECTABLE_SOURCES_PATH }).flush([
      {
        id: KB_GUID,
        type: 'knowledge-base',
        name: '同仁分享的手冊',
        summary: '2 份文件、1 則 FAQ',
        permission: 'read-only',
        status: 'ready',
        updatedAt: '2026-09-27T02:00:00+00:00',
      },
    ]);
    await settle();
    expect(store.connectableSources().map((source) => source.id)).toEqual([KB_GUID]);

    // 精靈的四個步驟：每次變更都自動保存，一次只送一個 PUT，帶上一次回來的 revision。
    store.applyTemplate('answer-customer-questions');
    store.setAudience({ internal: true, external: false });
    const picked = store.connectableSources()[0];
    store.toggleSource(picked);
    store.runTrial('trial-refund-window');
    await settle();
    const autosave = controller.expectOne({ method: 'PUT', url: apiAssistantDraftPath(DRAFT_GUID) });
    expect(autosave.request.body.revision).toBe(1);
    autosave.flush(draftView(autosave.request.body.payload, 2));
    await settle();

    const creating = store.create();
    await settle();
    const finalSave = controller.expectOne({ method: 'PUT', url: apiAssistantDraftPath(DRAFT_GUID) });
    expect(finalSave.request.body.revision).toBe(2);
    expect(finalSave.request.body.payload.sources).toEqual([{ id: KB_GUID, type: 'knowledge-base' }]);
    finalSave.flush(draftView(finalSave.request.body.payload, 3));
    await settle();

    const post = controller.expectOne({ method: 'POST', url: API_ASSISTANTS_PATH });
    expect(post.request.body).toEqual({ draftId: DRAFT_GUID });
    post.flush(
      {
        id: ASSISTANT_GUID,
        ownerAccountId: '0199a000-0000-7000-8000-00000000000a',
        name: '客戶問答助理',
        purpose: '回答客戶問題',
        status: 'ready',
        viewerCanManage: true,
        createdAt: '2026-09-28T03:00:00+00:00',
        updatedAt: '2026-09-28T03:00:00+00:00',
      },
      { status: 201, statusText: 'Created' },
    );

    expect(await creating).toBe(ASSISTANT_GUID);
  });
});
